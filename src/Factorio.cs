using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Paranoidal
{
    sealed class FactorioInfo
    {
        public string Dir, Exe, Version;

        /// <summary>
        /// Expansion mods shipped with Space Age (2.1 adds recycler). A line needs its BuildLine.Dlc: quality,
        /// elevated-rails (and recycler) are used, space-age itself is switched off in mod-list.json.
        /// </summary>
        static readonly string[] AllDlc = { "space-age", "quality", "elevated-rails", "recycler" };
        /// <summary>DLC folders in data\ that exist with the game's version.</summary>
        readonly HashSet<string> dlcOk = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Test snapshots only: a game folder that is not inspected but has every expansion mod.</summary>
        public FactorioInfo WithAllDlc() { foreach (var d in AllDlc) dlcOk.Add(d); return this; }

        /// <summary>DLC of the line that are missing or of another version than the game.</summary>
        public List<string> MissingDlcFor(BuildLine l) => l.Dlc.Where(d => !dlcOk.Contains(d)).ToList();
        public List<string> MissingDlc => MissingDlcFor(Lines.Current);

        /// <summary>Space Age edition: every expansion mod the line needs is installed with the game's version.</summary>
        public bool SpaceAgeFor(BuildLine l) => Version != null && MissingDlcFor(l).Count == 0;
        public bool SpaceAge => SpaceAgeFor(Lines.Current);
        public bool VersionOkFor(BuildLine l) => l.Fits(Version);
        public bool OkFor(BuildLine l) => VersionOkFor(l) && SpaceAgeFor(l);
        public bool VersionOk => VersionOkFor(Lines.Current);
        public bool Ok => OkFor(Lines.Current);

        /// <summary>The game found, in the interface language (en = null) or in the given one (the journal is Russian).</summary>
        public string Describe(bool? en = null, BuildLine l = null)
        {
            bool e = en ?? Tr.En;
            l = l ?? Lines.Of(Version) ?? Lines.Current;
            var v = "Factorio " + (Version ?? "?");
            var missing = MissingDlcFor(l);
            if (Version != null && missing.Count == 0) return v + " Space Age";
            if (Version == null || missing.Count == l.Dlc.Length) return v + (e ? " (no Space Age)" : " (без Space Age)");
            return v + (e ? " (Space Age incomplete: no " : " (Space Age не полностью: нет ") + string.Join(", ", missing) + ")";
        }

        /// <summary>Why this game does not fit the line (interface language); null when it fits.</summary>
        public string Problem(BuildLine l = null)
        {
            l = l ?? Lines.Current;
            if (!VersionOkFor(l))
                return Tr.T("Найдена ", "Found ") + Describe() + Tr.T(". Для сборки " + l.Name + " нужна Factorio " + l.VersionWords(false) + " с дополнением Space Age.",
                    ". The " + l.Name + " build needs Factorio " + l.VersionWords(true) + " with the Space Age expansion.") +
                    (l == Lines.L21 ? "\n\n" + HowToGet21() : "");
            if (!SpaceAgeFor(l))
            {
                var found = MissingDlcFor(l).Count == l.Dlc.Length
                    ? Tr.T("Найдена обычная Factorio " + Version + " без дополнения Space Age.", "Found the base game Factorio " + Version + " without the Space Age expansion.")
                    : Tr.T("Найдена ", "Found ") + Describe(null, l) + Tr.T(". Проверьте целостность файлов игры.", ". Verify the integrity of the game files.");
                return found + Tr.T("\nДля сборки нужна ", "\nThe build needs ") + l.RequiredGame + ".";
            }
            return null;
        }

        /// <summary>Where a player gets Factorio 2.1 (interface language).</summary>
        public static string HowToGet21() => Tr.T(
            "Как получить Factorio 2.1: Steam — Библиотека → Factorio → Свойства → «Бета-версии» (если 2.1 ещё не основная версия); " +
            "GOG Galaxy — настройки игры → «Бета-каналы»; сайт factorio.com — раздел Download (там же экспериментальные версии). " +
            "Удобно поставить 2.1 в отдельную обычную папку, например C:\\Games\\Factorio 2.1: лаунчер сам перенесёт в неё сейвы и сборку.",
            "How to get Factorio 2.1: Steam — Library → Factorio → Properties → \"Betas\" (while 2.1 is not the main version); " +
            "GOG Galaxy — the game's settings → \"Beta channels\"; factorio.com — Download (experimental versions are there too). " +
            "It is handy to put 2.1 into its own normal folder, e.g. C:\\Games\\Factorio 2.1: the launcher moves the saves and the build there itself.");

        /// <summary>Accepts the Factorio folder itself or any folder inside it (bin, bin\x64, mods…).</summary>
        public static FactorioInfo Inspect(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var d = new DirectoryInfo(path);
                for (int i = 0; i < 3 && d != null; i++, d = d.Parent)
                {
                    if (!d.Exists) continue;
                    var exe = Path.Combine(d.FullName, "bin", "x64", "factorio.exe");
                    if (File.Exists(exe))
                    {
                        var fi = new FactorioInfo { Dir = d.FullName.TrimEnd('\\'), Exe = exe, Version = ReadVersion(d.FullName, exe) };
                        foreach (var dlc in AllDlc)
                            if (fi.Version != null && fi.Version == ReadModVersion(Path.Combine(d.FullName, "data", dlc, "info.json")))
                                fi.dlcOk.Add(dlc);
                        return fi;
                    }
                }
            }
            catch { }
            return null;
        }

        static string ReadModVersion(string infoJson)
        {
            try
            {
                if (!File.Exists(infoJson)) return null;
                var m = Regex.Match(File.ReadAllText(infoJson), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            catch { }
            return null;
        }

        static string ReadVersion(string dir, string exe)
        {
            var v0 = ReadModVersion(Path.Combine(dir, "data", "base", "info.json"));
            if (v0 != null) return v0;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!string.IsNullOrEmpty(v)) return v.Trim();
            }
            catch { }
            return null;
        }

        /// <summary>Finds an installed Factorio; prefers the required version.</summary>
        public static FactorioInfo Detect(string near)
        {
            FactorioInfo first = null;
            foreach (var fi in Candidates(near))
            {
                if (fi.Ok) return fi;
                if (first == null) first = fi;
            }
            return first;
        }

        /// <summary>Every installation found (near first, then Steam, GOG, typical folders), each folder once.</summary>
        public static List<FactorioInfo> DetectAll(string near)
        {
            var res = new List<FactorioInfo>();
            foreach (var fi in Candidates(near))
                if (!res.Any(x => x.Dir.Equals(fi.Dir, StringComparison.OrdinalIgnoreCase))) res.Add(fi);
            return res;
        }

        static IEnumerable<FactorioInfo> Candidates(string near)
        {
            var c = new List<string>();
            if (near != null) c.Add(near);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var sp = k == null ? null : k.GetValue("SteamPath") as string;
                    if (sp != null)
                    {
                        sp = sp.Replace('/', '\\');
                        c.Add(Path.Combine(sp, "steamapps", "common", "Factorio"));
                        var vdf = Path.Combine(sp, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(vdf))
                            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                                c.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Factorio"));
                    }
                }
            }
            catch { }
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\1238653230", @"SOFTWARE\GOG.com\Games\1238653230" })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(key))
                    {
                        var p = k == null ? null : k.GetValue("path") as string;
                        if (p != null) c.Add(p);
                    }
                }
                catch { }
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    var r = drive.RootDirectory.FullName;
                    foreach (var parent in new[] { r, Path.Combine(r, "Games"), Path.Combine(r, "Program Files"), Path.Combine(r, "Program Files (x86)") })
                    {
                        if (!Directory.Exists(parent)) continue;
                        try { c.AddRange(Directory.GetDirectories(parent, "*actorio*")); } catch { }
                    }
                }
                catch { }
            }

            foreach (var path in c.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fi = Inspect(path);
                if (fi != null) yield return fi;
            }
        }

        public static bool IsRunning() => Process.GetProcessesByName("factorio").Length > 0;
    }
}
