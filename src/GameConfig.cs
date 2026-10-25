using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Paranoidal
{
    /// <summary>
    /// Load cache of the game: cache-prototype-data ([other]) and cache-sprite-atlas + compress-sprite-atlas-cache
    /// ([graphics]) in the config.ini the game really reads. With them a repeated start of an unchanged build takes
    /// ~25 s instead of 1.5–2 min. Only these keys are touched; the rest of config.ini stays byte for byte.
    /// Factorio rewrites config.ini when it exits, so this must run while the game is closed.
    /// </summary>
    static class GameConfig
    {
        static readonly string[][] Keys =
        {
            new[] { "other", "cache-prototype-data" },
            new[] { "graphics", "cache-sprite-atlas" },
            new[] { "graphics", "compress-sprite-atlas-cache" },
        };

        public sealed class Result
        {
            public string ConfigPath;
            public bool Created;
            public readonly List<string> Changed = new List<string>();
            public string Error;
        }

        /// <summary>
        /// config.ini of a Factorio started WITHOUT --config (the launcher starts it so): config-path.cfg next to the
        /// game decides, like in the game itself. Without config-path.cfg: the system folder %APPDATA%\Factorio.
        /// </summary>
        public static string ResolveConfigPath(string factorioDir, string exe)
        {
            string configPath = null;
            bool? useSystem = null;
            var cfg = Path.Combine(factorioDir, "config-path.cfg");
            if (File.Exists(cfg))
            {
                foreach (var raw in File.ReadAllLines(cfg))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("#") || line.StartsWith(";")) continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    if (k == "config-path") configPath = v;
                    else if (k == "use-system-read-write-data-directories") useSystem = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (string.IsNullOrEmpty(configPath))
                configPath = useSystem == false ? "__PATH__executable__/../../config" : "__PATH__system-write-data__/config";
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Factorio");
            configPath = configPath
                .Replace("__PATH__executable__", Path.GetDirectoryName(exe))
                .Replace("__PATH__system-write-data__", appData)
                .Replace("__PATH__system-read-data__", Path.Combine(factorioDir, "data"))
                .Replace('/', '\\');
            return Path.Combine(Path.GetFullPath(configPath), "config.ini");
        }

        /// <summary>
        /// The game's data folder (logs, saves, cache): [path] write-data of the config.ini the game reads; without it the
        /// default of the game: the system folder (%APPDATA%\Factorio) for Steam/installer, the game folder for a zip install.
        /// </summary>
        public static string ResolveWriteData(string factorioDir, string exe)
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Factorio");
            string config = null, value = null;
            try
            {
                config = ResolveConfigPath(factorioDir, exe);
                if (File.Exists(config))
                {
                    bool inPath = false;
                    foreach (var raw in File.ReadAllLines(config))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("[")) { inPath = line.Equals("[path]", StringComparison.OrdinalIgnoreCase); continue; }
                        if (!inPath || line.StartsWith(";") || line.StartsWith("#")) continue;
                        int i = line.IndexOf('=');
                        if (i > 0 && line.Substring(0, i).Trim() == "write-data") value = line.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(value))
            {
                bool system = config != null && config.StartsWith(appData + "\\", StringComparison.OrdinalIgnoreCase);
                value = system ? "__PATH__system-write-data__" : "__PATH__executable__/../..";
            }
            value = value
                .Replace("__PATH__executable__", Path.GetDirectoryName(exe))
                .Replace("__PATH__system-write-data__", appData)
                .Replace("__PATH__system-read-data__", Path.Combine(factorioDir, "data"))
                .Replace('/', '\\');
            try { return Path.GetFullPath(value).TrimEnd('\\'); } catch { return value; }
        }

        /// <summary>How the game is installed, for reports: Steam / GOG / zip (portable) / installer.</summary>
        public static string InstallKind(string factorioDir, string exe)
        {
            try
            {
                if (factorioDir.IndexOf("\\steamapps\\", StringComparison.OrdinalIgnoreCase) >= 0) return "Steam";
                if (Directory.GetFiles(factorioDir, "goggame-*.info").Length > 0) return "GOG";
                var config = ResolveConfigPath(factorioDir, exe);
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Factorio");
                return config.StartsWith(appData + "\\", StringComparison.OrdinalIgnoreCase) ? "installer (data in %APPDATA%)" : "zip / portable (data in the game folder)";
            }
            catch { return "?"; }
        }

        /// <summary>The three cache keys are true in config.ini (null: no config.ini).</summary>
        public static bool? CacheEnabled(string factorioDir, string exe)
        {
            try
            {
                var path = ResolveConfigPath(factorioDir, exe);
                if (!File.Exists(path)) return null;
                var changed = new List<string>();
                Apply(File.ReadAllText(path), "\n", changed);
                return changed.Count == 0;
            }
            catch { return null; }
        }

        /// <summary>Switches the load cache on. Never throws: a problem is reported in Result.Error.</summary>
        public static Result EnableCache(string factorioDir, string exe)
        {
            var res = new Result();
            try
            {
                res.ConfigPath = ResolveConfigPath(factorioDir, exe);
                var path = res.ConfigPath;
                if (!File.Exists(path))
                {
                    // Missing [path] values are generated by the game itself, the same way as for a missing file.
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, Apply("", "\r\n", res.Changed), new UTF8Encoding(false));
                    res.Created = true;
                    return res;
                }
                var bytes = File.ReadAllBytes(path);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                var eol = text.Contains("\r\n") ? "\r\n" : "\n";
                var updated = Apply(text, eol, res.Changed);
                if (res.Changed.Count == 0) return res;

                var backup = path + ".paranoidal-backup";
                if (!File.Exists(backup)) File.Copy(path, backup);
                var tmp = path + ".paranoidal-tmp";
                var outBytes = new UTF8Encoding(false).GetBytes(updated);
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    if (bom) f.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                    f.Write(outBytes, 0, outBytes.Length);
                }
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Replace(tmp, path, null);
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
            }
            return res;
        }

        /// <summary>
        /// Sets every key of Keys to true inside its section. Changed gets the keys that really changed. Every existing
        /// line keeps its own line ending (Factorio writes its header with LF and the rest with CRLF); added lines use eol.
        /// </summary>
        internal static string Apply(string text, string eol, List<string> changed)
        {
            var parts = text.Split('\n');
            var lines = new List<string>();
            var ends = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                if (last && parts[i].Length == 0) break;
                var s = parts[i];
                bool cr = s.EndsWith("\r");
                lines.Add(cr ? s.Substring(0, s.Length - 1) : s);
                ends.Add(last ? (cr ? "\r" : "") : (cr ? "\r\n" : "\n"));
            }
            Action<int, string> insert = (at, line) =>
            {
                if (at == lines.Count && at > 0 && !ends[at - 1].EndsWith("\n")) ends[at - 1] += eol;
                lines.Insert(at, line);
                ends.Insert(at, eol);
            };

            foreach (var kv in Keys)
            {
                string section = kv[0], key = kv[1], want = key + "=true";
                int start = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
                if (start < 0)
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) insert(lines.Count, "");
                    insert(lines.Count, "[" + section + "]");
                    insert(lines.Count, want);
                    changed.Add(key);
                    continue;
                }
                int end = start + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;

                var active = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*(.*?)\s*$");
                var commented = new Regex(@"^\s*[;#]\s*" + Regex.Escape(key) + @"\s*=");
                bool found = false, fixedOne = false;
                for (int i = start + 1; i < end; i++)
                {
                    var m = active.Match(lines[i]);
                    if (!m.Success) continue;
                    found = true;
                    if (m.Groups[1].Value != "true") { lines[i] = want; fixedOne = true; }
                }
                if (found) { if (fixedOne) changed.Add(key); continue; }
                int c = -1;
                for (int i = start + 1; i < end && c < 0; i++) if (commented.IsMatch(lines[i])) c = i;
                if (c >= 0) lines[c] = want;
                else
                {
                    int at = end;
                    while (at - 1 > start && lines[at - 1].Trim().Length == 0) at--;
                    insert(at, want);
                }
                changed.Add(key);
            }
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append(ends[i]);
            return sb.ToString();
        }

        /// <summary>
        /// Enabled mods with a "~" dependency. Factorio 2.0.77 never reuses the data stage cache while any enabled mod
        /// has one (it is stored in data-cache.dat as a plain required dependency, so the cache check always fails).
        /// </summary>
        public static List<string> TildeMods(string modsDir)
        {
            var result = new List<string>();
            if (modsDir == null || !Directory.Exists(modsDir)) return result;
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var disabled = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = ser.DeserializeObject(File.ReadAllText(Path.Combine(modsDir, "mod-list.json"), Encoding.UTF8)) as IDictionary<string, object>;
                object arr;
                if (root != null && root.TryGetValue("mods", out arr) && arr is IEnumerable)
                    foreach (var o in (IEnumerable)arr)
                    {
                        var d = o as IDictionary<string, object>;
                        object n, en;
                        if (d != null && d.TryGetValue("name", out n) && d.TryGetValue("enabled", out en) && en is bool && !(bool)en)
                            disabled.Add(n as string ?? "");
                    }
            }
            catch { }

            var infos = new List<string>();
            foreach (var dir in Directory.GetDirectories(modsDir))
            {
                var p = Path.Combine(dir, "info.json");
                try { if (File.Exists(p)) infos.Add(File.ReadAllText(p, Encoding.UTF8)); } catch { }
            }
            foreach (var zip in Directory.GetFiles(modsDir, "*.zip"))
            {
                try
                {
                    using (var z = ZipFile.OpenRead(zip))
                    {
                        var e = z.Entries.FirstOrDefault(x => x.FullName.Count(ch => ch == '/') == 1 && x.FullName.EndsWith("/info.json"));
                        if (e != null) using (var r = new StreamReader(e.Open(), Encoding.UTF8)) infos.Add(r.ReadToEnd());
                    }
                }
                catch { }
            }
            foreach (var json in infos)
            {
                try
                {
                    var info = ser.DeserializeObject(json) as IDictionary<string, object>;
                    object n, deps;
                    if (info == null || !info.TryGetValue("name", out n) || !(n is string) || disabled.Contains((string)n)) continue;
                    if (info.TryGetValue("dependencies", out deps) && deps is IEnumerable && !(deps is string))
                        foreach (var dep in (IEnumerable)deps)
                            if (dep is string && ((string)dep).TrimStart().StartsWith("~")) { result.Add((string)n); break; }
                }
                catch { }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
