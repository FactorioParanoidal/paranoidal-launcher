using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Paranoidal
{
    static class AppInfo
    {
        public const string Name = "Paranoidal Launcher";
        /// <summary>
        /// Launcher version. This is the only place where it is set: the window title, header, log, crash report,
        /// sync log, GitHub User-Agent, assembly/file version and the exe manifest (build.ps1) take it from here.
        /// Every change of the launcher bumps it and gets a section in CHANGELOG.md; build.ps1 refuses to build
        /// a different exe under an already built version (README.md, «Версии»).
        /// </summary>
        public const string Version = "1.1.0";
        public const string AssemblyVersion = Version + ".0";
        public const string Owner = "FactorioParanoidal";
        public const string RepoName = "ParanoidalModpack";
        public const string RepoUrl = "https://github.com/" + Owner + "/" + RepoName + ".git";
        public const string Branch = "pre_release_2.0";
        public const string RequiredFactorio = "2.0.77";
        /// <summary>The game the build needs, worded the same everywhere it is shown to the player.</summary>
        public const string RequiredGame = "Factorio " + RequiredFactorio + " Space Age";
        public const string MinGitVersion = "2.56.0.2-64";

        /// <summary>Tests only: PARANOIDAL_LAUNCHER_TEST_OFFLINE=1 makes git and the GitHub API fail as without a connection.</summary>
        public static readonly bool TestOffline = Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_TEST_OFFLINE") == "1";

        /// <summary>Folder next to the exe that holds all launcher data (settings, MinGit, logs).</summary>
        public const string DataDirName = "ParanoidalLauncher-data";

        /// <summary>
        /// What the startup log should say about DataDir (a fallback or a migration); null when there is nothing to say.
        /// Set by ResolveDataDir, so it is declared without an initializer.
        /// </summary>
        public static string DataDirNote;

        /// <summary>
        /// Settings, MinGit and logs: &lt;folder of the exe&gt;\ParanoidalLauncher-data (players usually keep the exe in the
        /// Factorio folder). Falls back to the pre-1.0.2 folder %LOCALAPPDATA%\ParanoidalUpdater when the exe folder is
        /// not writable (e.g. Program Files) or is a mods folder (the sync would treat the data folder as a foreign mod).
        /// PARANOIDAL_LAUNCHER_DATA_DIR overrides it (tests only).
        /// </summary>
        public static readonly string DataDir = ResolveDataDir();

        public static string LegacyDataDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParanoidalUpdater");

        static string ResolveDataDir()
        {
            var custom = Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try { return Path.GetFullPath(custom.Trim()); } catch { }
            }
            var legacy = LegacyDataDir();
            string reason;
            try
            {
                var exeDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
                if (Path.GetFileName(exeDir).Equals("mods", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(exeDir, "mod-list.json")))
                    reason = "лаунчер лежит в папке модов, а в ней не должно быть посторонних папок. Положите лаунчер в папку игры, рядом с папкой mods";
                else
                {
                    var dir = Path.Combine(exeDir, DataDirName);
                    if (CanWrite(dir))
                    {
                        MigrateFrom(legacy, dir);
                        return dir;
                    }
                    reason = "в папку лаунчера нельзя записывать (" + exeDir + "). Положите лаунчер в папку, куда можно записывать, например в папку игры, если она не в Program Files";
                }
            }
            catch (Exception ex) { reason = ex.Message; }
            DataDirNote = "Данные лаунчера хранятся в " + legacy + ": " + reason + ".";
            return legacy;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>First start of 1.0.2+ next to the exe: take settings.ini of the old %LOCALAPPDATA% folder.</summary>
        static void MigrateFrom(string legacy, string dir)
        {
            try
            {
                var from = Path.Combine(legacy, "settings.ini");
                var to = Path.Combine(dir, "settings.ini");
                if (File.Exists(to) || !File.Exists(from) || string.Equals(legacy, dir, StringComparison.OrdinalIgnoreCase)) return;
                File.Copy(from, to);
                DataDirNote = "Настройки перенесены из " + legacy + " в " + dir + ". Старая папка больше не нужна, её можно удалить.";
            }
            catch { }
        }

        public static string Short(string sha) => string.IsNullOrEmpty(sha) ? "—" : (sha.Length > 10 ? sha.Substring(0, 10) : sha);

        /// <summary>Folder of the exe (no trailing backslash).</summary>
        public static string ExeDir => Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');

        /// <summary>Path where %USERPROFILE% is written instead of the user's profile folder (reports, shared logs).</summary>
        public static string MaskUser(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
                if (profile.Length > 3)
                {
                    text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(profile), "%USERPROFILE%",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(profile.Replace('\\', '/')), "%USERPROFILE%",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                // any other C:\Users\<name> (another profile, a path written with doubled backslashes)
                text = System.Text.RegularExpressions.Regex.Replace(text, @"([A-Za-z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/))(?!Public\b|%)[^\\/""'\r\n:*?<>|]+", "$1%USERNAME%",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(user) + @"(?![\w-])", "%USERNAME%",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { }
            return text;
        }
    }

    /// <summary>Tiny key=value settings file in AppInfo.DataDir.</summary>
    static class Settings
    {
        static readonly string FilePath = Path.Combine(AppInfo.DataDir, "settings.ini");
        static Dictionary<string, string> data;

        static void Load()
        {
            if (data != null) return;
            data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) data[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public static string Get(string key)
        {
            Load();
            string v;
            return data.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        /// <summary>
        /// A folder path: stored relative to the exe folder when it is inside it, so a game folder copied with the launcher
        /// (portable copy, LAN share) still points to its own game and mods.
        /// </summary>
        public static string GetPath(string key)
        {
            var v = Get(key);
            if (v == null) return null;
            try { return Path.IsPathRooted(v) ? Path.GetFullPath(v).TrimEnd('\\') : Path.GetFullPath(Path.Combine(AppInfo.ExeDir, v)).TrimEnd('\\'); }
            catch { return v; }
        }

        public static void SetPath(string key, string fullPath)
        {
            Set(key, Relative(fullPath));
        }

        static string Relative(string full)
        {
            if (string.IsNullOrEmpty(full)) return full;
            try
            {
                full = Path.GetFullPath(full).TrimEnd('\\');
                var exe = AppInfo.ExeDir;
                if (full.Equals(exe, StringComparison.OrdinalIgnoreCase)) return ".";
                if (full.StartsWith(exe + "\\", StringComparison.OrdinalIgnoreCase)) return ".\\" + full.Substring(exe.Length + 1);
            }
            catch { }
            return full;
        }

        /// <summary>1.1.0: absolute paths inside the exe folder written by older versions become relative.</summary>
        public static void MigratePaths()
        {
            foreach (var key in new[] { "factorio", "mods" })
            {
                var v = Get(key);
                if (v == null || !Path.IsPathRooted(v)) continue;
                var rel = Relative(v);
                if (rel != v) Set(key, rel);
            }
        }

        public static void Set(string key, string value)
        {
            Load();
            data[key] = value ?? "";
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var sb = new StringBuilder();
                foreach (var kv in data) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    static class FsUtil
    {
        /// <summary>Recursive delete that also handles read-only files (git packs are read-only).</summary>
        public static void DeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(d, FileAttributes.Directory); } catch { }
            }
            try { File.SetAttributes(dir, FileAttributes.Directory); } catch { }
            Directory.Delete(dir, true);
        }

        public static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }
    }

    static class Fmt
    {
        public static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        static readonly CultureInfo En = CultureInfo.InvariantCulture;
        public static CultureInfo Cur => Tr.En ? En : Ru;

        public static string Size(double bytes) => Size(bytes, Tr.En);

        /// <summary>Size for the journal and files the team reads: always Russian.</summary>
        public static string SizeRu(double bytes) => Size(bytes, false);

        static string Size(double bytes, bool en)
        {
            var c = en ? En : Ru;
            if (bytes < 1024 * 1024) return (bytes / 1024).ToString("0", c) + (en ? " KB" : " КБ");
            if (bytes < 1024.0 * 1024 * 1024) return (bytes / 1024 / 1024).ToString(bytes < 10 * 1024 * 1024 ? "0.0" : "0", c) + (en ? " MB" : " МБ");
            return (bytes / 1024 / 1024 / 1024).ToString("0.00", c) + (en ? " GB" : " ГБ");
        }

        public static string Num(long n) => n.ToString("N0", Cur);

        public static string Duration(double seconds) => Duration(seconds, Tr.En);

        public static string Duration(double seconds, bool en)
        {
            string s = en ? " s" : " с", min = en ? " min" : " мин", h = en ? " h " : " ч ";
            if (seconds < 60) return "~" + Math.Max(1, (int)Math.Round(seconds)) + s;
            if (seconds < 3600) return "~" + (int)Math.Round(seconds / 60) + min;
            return "~" + (int)(seconds / 3600) + h + (int)Math.Round(seconds % 3600 / 60) + min;
        }

        public static string Date(DateTime d) => d.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    static class Res
    {
        static Stream Open(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream("Paranoidal." + name);

        static Image splash, gear;
        public static Image Splash { get { return splash ?? (splash = Load("splash.png")); } }
        public static Image Gear { get { return gear ?? (gear = Load("gear.png")); } }

        static Image Load(string name)
        {
            using (var s = Open(name))
            {
                if (s == null) return null;
                using (var tmp = Image.FromStream(s)) return new Bitmap(tmp);
            }
        }

        public static Icon AppIcon(int size)
        {
            try
            {
                using (var s = Open("app.ico"))
                    if (s != null) return new Icon(s, size, size);
            }
            catch { }
            return null;
        }

        public static Stream MinGitZip() => Open("mingit.zip");
    }
}
