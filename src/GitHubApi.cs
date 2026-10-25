using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Paranoidal
{
    sealed class CommitInfo
    {
        public string Sha, Message, Author;
        public DateTime Date;
    }

    /// <summary>Read-only GitHub REST calls for the change list (anonymous, 60 requests/hour is plenty).</summary>
    static class GitHubApi
    {
        const string Api = "https://api.github.com/repos/" + AppInfo.Owner + "/" + AppInfo.RepoName;

        static object Get(string url, int timeoutMs = 20000)
        {
            if (AppInfo.TestOffline) throw new Exception("нет связи (PARANOIDAL_LAUNCHER_TEST_OFFLINE)");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "ParanoidalLauncher/" + AppInfo.Version;
            req.Accept = "application/vnd.github+json";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(r.ReadToEnd());
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && ((int)hr.StatusCode == 403 || (int)hr.StatusCode == 429))
                    throw new Exception("GitHub временно ограничил запросы, попробуйте позже");
                if (hr != null) throw new Exception("HTTP " + (int)hr.StatusCode);
                throw new Exception(ex.Message);
            }
        }

        public static List<CommitInfo> Compare(string baseSha, string headSha)
        {
            var d = Get(Api + "/compare/" + baseSha + "..." + headSha + "?per_page=250") as IDictionary<string, object>;
            var list = Parse(d == null ? null : d["commits"]);
            list.Reverse();   // newest first
            return list;
        }

        /// <summary>Blob id -> size for every file in mods/ of a commit (one request), used to size download batches.</summary>
        public static Dictionary<string, long> BlobSizes(string commit)
        {
            var d = Get(Api + "/git/trees/" + commit + ":mods?recursive=1", 90000) as IDictionary<string, object>;
            var res = new Dictionary<string, long>();
            var items = d == null ? null : d["tree"] as IEnumerable;
            if (items == null) return res;
            foreach (var o in items)
            {
                var e = o as IDictionary<string, object>;
                if (e == null || !"blob".Equals(e["type"] as string)) continue;
                object s;
                if (e.TryGetValue("size", out s) && s != null) res[(string)e["sha"]] = Convert.ToInt64(s, CultureInfo.InvariantCulture);
            }
            return res;
        }

        /// <summary>A commit by its full or abbreviated id (the host's version line has 10 characters).</summary>
        public static CommitInfo Commit(string sha)
        {
            var list = Parse(new object[] { Get(Api + "/commits/" + Uri.EscapeDataString(sha)) });
            return list.Count == 1 && list[0].Sha != null ? list[0] : null;
        }

        public static List<CommitInfo> Recent(string branch, int count)
        {
            return Parse(Get(Api + "/commits?sha=" + Uri.EscapeDataString(branch) + "&per_page=" + count));
        }

        static List<CommitInfo> Parse(object arr)
        {
            var list = new List<CommitInfo>();
            var items = arr as IEnumerable;
            if (items == null) return list;
            foreach (var o in items)
            {
                var c = o as IDictionary<string, object>;
                if (c == null) continue;
                var commit = c["commit"] as IDictionary<string, object>;
                var author = commit == null ? null : commit["author"] as IDictionary<string, object>;
                var ci = new CommitInfo { Sha = c["sha"] as string };
                var msg = (commit == null ? "" : commit["message"] as string) ?? "";
                int nl = msg.IndexOf('\n');
                ci.Message = (nl >= 0 ? msg.Substring(0, nl) : msg).Trim();
                if (author != null)
                {
                    ci.Author = author["name"] as string;
                    DateTime dt;
                    if (DateTime.TryParse(author["date"] as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        ci.Date = dt.ToLocalTime();
                }
                list.Add(ci);
            }
            return list;
        }
    }

    /// <summary>The last list of build changes, shown when GitHub cannot be reached.</summary>
    static class CommitsCache
    {
        static string FilePath => Path.Combine(AppInfo.DataDir, "commits-cache.txt");

        public static void Save(List<CommitInfo> list)
        {
            if (list == null || list.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var c in list)
                    sb.Append(c.Sha).Append('\t').Append(c.Date == default(DateTime) ? 0 : new DateTimeOffset(c.Date).ToUnixTimeSeconds())
                      .Append('\t').Append((c.Message ?? "").Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
                Directory.CreateDirectory(AppInfo.DataDir);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static List<CommitInfo> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var list = new List<CommitInfo>();
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = line.Split(new[] { '\t' }, 3);
                    long t;
                    if (f.Length < 3 || f[0].Length != 40 || !long.TryParse(f[1], out t)) continue;
                    list.Add(new CommitInfo { Sha = f[0], Message = f[2], Date = t == 0 ? default(DateTime) : DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime });
                }
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }
    }
}
