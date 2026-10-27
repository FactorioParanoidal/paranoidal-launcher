using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Paranoidal
{
    sealed class ReleaseChange
    {
        public string Version, Date, Text;
    }

    /// <summary>
    /// release.json of a GitHub Release of a FactorioParanoidal repository (the launcher's own releases and the guide's).
    /// Format (README «Где лаунчер ищет обновления»): schema 1, product, version x.y.z, date, file, size, sha256,
    /// changes = every CHANGELOG section, newest first.
    /// </summary>
    sealed class ReleaseInfo
    {
        public string Repo, Product, Version, Date, File, Sha256;
        public long Size;
        public readonly List<ReleaseChange> Changes = new List<ReleaseChange>();

        /// <summary>The file of exactly this version (not "latest": a release published meanwhile cannot mix in).</summary>
        public string FileUrl => Releases.AssetUrl(Repo, "v" + Version, File);

        /// <summary>CHANGELOG sections newer than the given version, newest first.</summary>
        public List<ReleaseChange> Since(string version) => Changes.Where(c => Releases.Compare(c.Version, version) > 0).ToList();
    }

    /// <summary>Result of the release checks that run next to the build check.</summary>
    sealed class ReleaseCheck
    {
        public ReleaseInfo Launcher, Guide;
        public string LauncherError, GuideError;
    }

    /// <summary>
    /// Anonymous downloads from GitHub Releases: https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/latest/download/release.json.
    /// These are plain file downloads (redirects to the release CDN), not REST API calls: the 60 requests/hour API limit
    /// is not spent. PARANOIDAL_LAUNCHER_TEST_GITHUB replaces https://github.com (tests with a local server).
    /// </summary>
    static class Releases
    {
        public const string Manifest = "release.json";

        public static string Base
        {
            get
            {
                var t = Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_TEST_GITHUB");
                return string.IsNullOrWhiteSpace(t) ? "https://github.com" : t.Trim().TrimEnd('/');
            }
        }

        public static string LatestUrl(string repo, string asset) => Base + "/" + AppInfo.Owner + "/" + repo + "/releases/latest/download/" + asset;
        public static string AssetUrl(string repo, string tag, string asset) => Base + "/" + AppInfo.Owner + "/" + repo + "/releases/download/" + tag + "/" + asset;
        /// <summary>Release page for a person (a browser), always the real GitHub.</summary>
        public static string PageUrl(string repo) => "https://github.com/" + AppInfo.Owner + "/" + repo + "/releases/latest";

        /// <summary>Set by «Отмена»: a running download stops with OperationCanceledException.</summary>
        public static volatile bool Cancel;

        public static bool IsVersion(string v) => v != null && Regex.IsMatch(v, @"^\d+\.\d+\.\d+$");

        /// <summary>Compares x.y.z versions numerically; an invalid or missing version is the lowest.</summary>
        public static int Compare(string a, string b)
        {
            var x = Parse(a);
            var y = Parse(b);
            if (x == null || y == null) return (x == null ? 0 : 1) - (y == null ? 0 : 1);
            for (int i = 0; i < 3; i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }

        static int[] Parse(string v)
        {
            if (!IsVersion(v)) return null;
            try { return v.Split('.').Select(int.Parse).ToArray(); } catch { return null; }
        }

        static HttpWebRequest Request(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "ParanoidalLauncher/" + AppInfo.Version;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            return req;
        }

        static Exception Describe(WebException ex, string what)
        {
            var hr = ex.Response as HttpWebResponse;
            if (hr != null && (int)hr.StatusCode == 404) return new Exception(what + ": на GitHub нет такого файла (релиз ещё не опубликован)");
            if (hr != null && ((int)hr.StatusCode == 403 || (int)hr.StatusCode == 429)) return new Exception(what + ": GitHub временно ограничил запросы");
            if (hr != null) return new Exception(what + ": HTTP " + (int)hr.StatusCode);
            return new Exception(what + ": " + ex.Message);
        }

        /// <summary>release.json of the latest release of a repository; checks that it describes the expected file.</summary>
        public static ReleaseInfo Latest(string repo, string product, string file, int timeoutMs = 15000)
        {
            if (AppInfo.TestOffline) throw new Exception("нет связи (PARANOIDAL_LAUNCHER_TEST_OFFLINE)");
            string text;
            try
            {
                using (var resp = (HttpWebResponse)Request(LatestUrl(repo, Manifest), timeoutMs).GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var buf = new char[1 << 20];   // a manifest is a few KB; never read an endless answer
                    int n = r.ReadBlock(buf, 0, buf.Length);
                    text = new string(buf, 0, n);
                }
            }
            catch (WebException ex) { throw Describe(ex, repo + "/" + Manifest); }
            return ParseManifest(text, repo, product, file);
        }

        public static ReleaseInfo ParseManifest(string text, string repo, string product, string file)
        {
            var d = new JavaScriptSerializer().DeserializeObject(text) as IDictionary<string, object>;
            if (d == null) throw new Exception(repo + "/" + Manifest + ": не JSON");
            Func<string, string> S = k => { object o; return d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : null; };
            var info = new ReleaseInfo
            {
                Repo = repo,
                Product = S("product"),
                Version = S("version"),
                Date = S("date") ?? "",
                File = S("file"),
                Sha256 = (S("sha256") ?? "").ToLowerInvariant(),
            };
            long size;
            long.TryParse(S("size"), out size);
            info.Size = size;
            if (S("schema") != "1") throw new Exception(repo + "/" + Manifest + ": неизвестный формат schema=" + S("schema"));
            if (info.Product != product || info.File != file) throw new Exception(repo + "/" + Manifest + ": описан другой файл (" + info.Product + ", " + info.File + ")");
            if (!IsVersion(info.Version)) throw new Exception(repo + "/" + Manifest + ": неверная версия «" + info.Version + "»");
            if (!Regex.IsMatch(info.Sha256, "^[0-9a-f]{64}$") || info.Size <= 0) throw new Exception(repo + "/" + Manifest + ": нет размера или SHA-256 файла");
            object ch;
            if (d.TryGetValue("changes", out ch) && ch is IEnumerable)
                foreach (var o in (IEnumerable)ch)
                {
                    var c = o as IDictionary<string, object>;
                    if (c == null) continue;
                    object v, dt, t;
                    c.TryGetValue("version", out v);
                    c.TryGetValue("date", out dt);
                    c.TryGetValue("text", out t);
                    if (IsVersion(v as string)) info.Changes.Add(new ReleaseChange { Version = (string)v, Date = dt as string ?? "", Text = t as string ?? "" });
                }
            return info;
        }

        /// <summary>The launcher's and the guide's latest releases, both at once (called next to the build check).</summary>
        public static ReleaseCheck CheckAll()
        {
            var res = new ReleaseCheck();
            var tl = Task.Run(() => Latest(AppInfo.LauncherRepo, "launcher", AppInfo.ExeName));
            var tg = Task.Run(() => Latest(AppInfo.GuideRepo, "guide", AppInfo.GuideFile));
            try { res.Launcher = tl.Result; } catch (Exception ex) { res.LauncherError = ex.GetBaseException().Message; }
            try { res.Guide = tg.Result; } catch (Exception ex) { res.GuideError = ex.GetBaseException().Message; }
            return res;
        }

        /// <summary>
        /// Downloads the release file to `target` through target.download, checks its size and SHA-256 and only then
        /// replaces `target`. progress(bytes, total) is called from this thread.
        /// </summary>
        public static void Download(ReleaseInfo r, string target, Action<long, long> progress)
        {
            if (AppInfo.TestOffline) throw new Exception("нет связи (PARANOIDAL_LAUNCHER_TEST_OFFLINE)");
            var tmp = target + ".download";
            try
            {
                using (var resp = (HttpWebResponse)Request(r.FileUrl, 30000).GetResponse())
                using (var s = resp.GetResponseStream())
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    var last = DateTime.MinValue;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (Cancel) throw new OperationCanceledException();
                        fs.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (done > r.Size) throw new Exception(r.File + ": файл больше, чем указано в релизе");
                        if (progress != null && (DateTime.Now - last).TotalMilliseconds > 150) { last = DateTime.Now; progress(done, r.Size); }
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    if (progress != null) progress(done, r.Size);
                    var hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (done != r.Size || hex != r.Sha256)
                        throw new Exception(r.File + ": файл скачался повреждённым (размер или SHA-256 не совпали с релизом)");
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
            catch (WebException ex) { TryDelete(tmp); throw Describe(ex, r.File); }
            catch { TryDelete(tmp); throw; }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
