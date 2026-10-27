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
        /// Expansion DLC shipped with Space Age. The build needs all of them: quality and elevated-rails are used,
        /// space-age itself is switched off in mod-list.json.
        /// </summary>
        static readonly string[] Dlc = { "space-age", "quality", "elevated-rails" };
        /// <summary>DLC folders in data\ that are missing or of another version than the game.</summary>
        public readonly List<string> MissingDlc = new List<string>();

        /// <summary>Space Age edition: every expansion DLC of the same version is installed.</summary>
        public bool SpaceAge => Version != null && MissingDlc.Count == 0;
        public bool VersionOk => Version == AppInfo.RequiredFactorio;
        public bool Ok => VersionOk && SpaceAge;

        /// <summary>The game found, in the interface language (en = null) or in the given one (the journal is Russian).</summary>
        public string Describe(bool? en = null)
        {
            bool e = en ?? Tr.En;
            var v = "Factorio " + (Version ?? "?");
            if (SpaceAge) return v + " Space Age";
            if (Version == null || MissingDlc.Count == Dlc.Length) return v + (e ? " (no Space Age)" : " (без Space Age)");
            return v + (e ? " (Space Age incomplete: no " : " (Space Age не полностью: нет ") + string.Join(", ", MissingDlc) + ")";
        }

        public string Problem()
        {
            if (!VersionOk) return Tr.T("Найдена ", "Found ") + Describe() + Tr.T(". Для сборки нужна строго ", ". The build needs exactly ") + AppInfo.RequiredGame + ".";
            if (!SpaceAge)
            {
                var found = MissingDlc.Count == Dlc.Length
                    ? Tr.T("Найдена обычная Factorio " + Version + " без дополнения Space Age.", "Found the base game Factorio " + Version + " without the Space Age expansion.")
                    : Tr.T("Найдена ", "Found ") + Describe() + Tr.T(". Проверьте целостность файлов игры.", ". Verify the integrity of the game files.");
                return found + Tr.T("\nДля сборки нужна ", "\nThe build needs ") + AppInfo.RequiredGame + ".";
            }
            return null;
        }

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
                        foreach (var dlc in Dlc)
                            if (fi.Version == null || fi.Version != ReadModVersion(Path.Combine(d.FullName, "data", dlc, "info.json")))
                                fi.MissingDlc.Add(dlc);
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
