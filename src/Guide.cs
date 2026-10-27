using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Paranoidal
{
    /// <summary>
    /// The illustrated modpack guide: one HTML file (Guide_to_the_Paranoidal.html, every picture inside) from the
    /// releases of FactorioParanoidal/guide-to-the-paranoidal. It lives next to the exe, in the game folder, so a player
    /// sees it and can open it without the launcher. If the exe folder is read-only (Program Files) it goes to DataDir.
    /// It is downloaded together with a build update and by the «Гайд» button when there is no copy yet.
    /// </summary>
    static class GuideFile
    {
        public static string NextToExe => Path.Combine(AppInfo.ExeDir, AppInfo.GuideFile);
        public static string InDataDir => Path.Combine(AppInfo.DataDir, AppInfo.GuideFile);

        /// <summary>Where a download goes.</summary>
        public static string Target => AppInfo.ExeDirWritable ? NextToExe : InDataDir;

        /// <summary>The local copy to open (the newer one if both places have one); null when there is none.</summary>
        public static string Local()
        {
            string a = File.Exists(NextToExe) ? NextToExe : null, b = File.Exists(InDataDir) ? InDataDir : null;
            if (a == null || b == null) return a ?? b;
            return Releases.Compare(Version(b), Version(a)) > 0 ? b : a;
        }

        /// <summary>Version from &lt;meta name="guide-version"&gt; in the first bytes of the file; null if unknown.</summary>
        public static string Version(string path)
        {
            try
            {
                var buf = new byte[8192];
                int n;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) n = f.Read(buf, 0, buf.Length);
                var m = Regex.Match(Encoding.UTF8.GetString(buf, 0, n), "<meta name=\"guide-version\" content=\"([0-9.]+)\">");
                return m.Success && Releases.IsVersion(m.Groups[1].Value) ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>The release is newer than the local copy, or there is no usable local copy.</summary>
        public static bool NeedsUpdate(ReleaseInfo r, string localVersion) => r != null && Releases.Compare(r.Version, localVersion) > 0;

        /// <summary>Downloads and checks the guide of the release; returns the path of the new copy.</summary>
        public static string Install(ReleaseInfo r, Action<long, long> progress)
        {
            var target = Target;
            Releases.Download(r, target, progress);
            var v = Version(target);
            if (v != r.Version) throw new Exception(AppInfo.GuideFile + ": в скачанном файле версия " + (v ?? "?") + ", ожидалась " + r.Version);
            // an older copy in the other place would only confuse
            var other = target == NextToExe ? InDataDir : NextToExe;
            try { if (File.Exists(other) && Releases.Compare(Version(other), v) < 0) File.Delete(other); } catch { }
            return target;
        }

        public static void Open(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
        }
    }
}
