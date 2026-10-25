using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Paranoidal
{
    /// <summary>
    /// One line that describes the player's setup («Скопировать версию»): the host and a player compare it to find
    /// what Git cannot fix (startup mod settings, extra mods), and it goes into bug reports.
    /// </summary>
    static class VersionLine
    {
        /// <param name="installed">Installed build commit; null when the build in mods\ is not recognised (then `fingerprint`).</param>
        /// <param name="foreign">Top-level entries of mods\ that are not in the build (SyncEngine.ForeignEntries).</param>
        /// <param name="modified">Build files that differ on disk (StatusInfo.Modified; &lt;= 0: none or unknown).</param>
        public static string Build(string installed, string installedDate, string modsDir, List<string> foreign,
            int modified = 0, string fingerprint = null, bool en = false)
        {
            Func<string, string, string> t = (ru, eng) => en ? eng : ru;
            var sb = new StringBuilder();
            if (installed != null)
                sb.Append(t("Paranoidal, сборка от ", "Paranoidal, build of ")).Append(installedDate ?? "?").Append(" (").Append(AppInfo.Short(installed)).Append(')');
            else
                sb.Append(t("Paranoidal, сборка не опознана (отпечаток файлов ", "Paranoidal, build not recognised (file fingerprint ")).Append(fingerprint ?? "?").Append(')');
            if (modified > 0) sb.Append(t(" · изменено файлов сборки: ", " · build files changed: ")).Append(modified);

            string settings;
            try { settings = ModSettingsFile.StartupFingerprint(modsDir) ?? t("нет файла", "no file"); }
            catch (Exception ex) { settings = t("не прочитаны (", "not read (") + ex.Message + ")"; }
            sb.Append(t(" · стартовые настройки модов: ", " · startup mod settings: ")).Append(settings);

            if (foreign != null)
            {
                var names = EnabledModNames(modsDir, foreign);
                sb.Append(t(" · моды не из сборки: ", " · mods not from the build: ")).Append(names.Count == 0 ? t("нет", "none") : string.Join(", ", names));
            }
            sb.Append(" · ").Append(AppInfo.Name).Append(' ').Append(AppInfo.Version);
            return sb.ToString();
        }

        /// <summary>
        /// Build commit from the host's version line (either language; the commit is in parentheses, 7–40 hex digits).
        /// A bare commit id is accepted too. Null when the line has no build version (e.g. the host's build is not recognised).
        /// </summary>
        public static string ParseHost(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, @"\(([0-9a-f]{7,40})\)");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(text.Trim(), @"^([0-9a-f]{7,40})$");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>"name version" of the foreign entries with their state in mod-list.json, for the report.</summary>
        public static List<string> Describe(string modsDir, IEnumerable<string> foreign)
        {
            var enabled = ReadModList(modsDir);
            var res = new List<string>();
            foreach (var full in foreign)
            {
                var name = ModName(full);
                bool on;
                bool isOn = enabled == null || !enabled.TryGetValue(name, out on) || on;
                res.Add(Path.GetFileName(full) + " — " + name + (isOn ? " (включён)" : " (выключен)"));
            }
            res.Sort(StringComparer.OrdinalIgnoreCase);
            return res;
        }

        static readonly Regex VersionSuffix = new Regex(@"_\d+\.\d+\.\d+$");

        /// <summary>Mod names of the foreign entries that Factorio will load (not switched off in mod-list.json).</summary>
        public static List<string> EnabledModNames(string modsDir, IEnumerable<string> foreign)
        {
            var enabled = ReadModList(modsDir);
            var res = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var full in foreign)
            {
                var name = ModName(full);
                bool on;
                // a mod missing from mod-list.json is switched on by Factorio when it finds it
                if (enabled != null && enabled.TryGetValue(name, out on) && !on) continue;
                res.Add(name);
            }
            return res.ToList();
        }

        static string ModName(string full)
        {
            var file = Path.GetFileName(full);
            string infoJson = null;
            try
            {
                if (Directory.Exists(full))
                {
                    var info = Path.Combine(full, "info.json");
                    if (File.Exists(info)) infoJson = File.ReadAllText(info, Encoding.UTF8);
                }
                else if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using (var z = ZipFile.OpenRead(full))
                    {
                        var e = z.Entries.FirstOrDefault(x => x.FullName.Count(c => c == '/') == 1 && x.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase));
                        if (e != null) using (var r = new StreamReader(e.Open(), Encoding.UTF8)) infoJson = r.ReadToEnd();
                    }
                }
            }
            catch { }
            if (infoJson != null)
            {
                var m = Regex.Match(infoJson, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 4);
            return VersionSuffix.Replace(file, "");
        }

        /// <summary>name -> enabled from mods\mod-list.json; null if the file is missing or broken.</summary>
        static Dictionary<string, bool> ReadModList(string modsDir)
        {
            try
            {
                var path = Path.Combine(modsDir, "mod-list.json");
                if (!File.Exists(path)) return null;
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
                object arr;
                if (root == null || !root.TryGetValue("mods", out arr) || !(arr is IEnumerable)) return null;
                var res = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in (IEnumerable)arr)
                {
                    var d = o as IDictionary<string, object>;
                    object n, en;
                    if (d == null || !d.TryGetValue("name", out n) || !(n is string)) continue;
                    res[(string)n] = d.TryGetValue("enabled", out en) && en is bool && (bool)en;
                }
                return res;
            }
            catch { return null; }
        }
    }

    /// <summary>Reads mods\mod-settings.dat (Factorio property tree, read only).</summary>
    static class ModSettingsFile
    {
        /// <summary>
        /// Short hash of the whole file (all sections). Any change of it, also of a per-player setting, makes Factorio 2.0.77
        /// rebuild the data stage cache (docs/factorio-load-cache.md, runs 4–5).
        /// </summary>
        public static string FileFingerprint(string modsDir)
        {
            var path = Path.Combine(modsDir, "mod-settings.dat");
            if (!File.Exists(path)) return null;
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(File.ReadAllBytes(path));
                return h[0].ToString("X2") + h[1].ToString("X2") + "-" + h[2].ToString("X2") + h[3].ToString("X2");
            }
        }

        /// <summary>
        /// Short hash of the startup settings, e.g. "3F2A-9C1B"; null if there is no mod-settings.dat.
        /// Only startup settings must be the same for the host and the players; runtime-per-user ones are personal
        /// and runtime-global ones come from the save, so they are left out.
        /// </summary>
        public static string StartupFingerprint(string modsDir)
        {
            var path = Path.Combine(modsDir, "mod-settings.dat");
            if (!File.Exists(path)) return null;
            var r = new Reader(File.ReadAllBytes(path));
            r.Skip(8 + 1);   // game version (4 x u16) and a reserved byte
            var root = r.Tree(0) as SortedDictionary<string, object>;
            object startup;
            if (root == null || !root.TryGetValue("startup", out startup)) throw new InvalidDataException("нет раздела startup");
            var canon = new StringBuilder();
            Canon(canon, startup);
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(canon.ToString()));
                return h[0].ToString("X2") + h[1].ToString("X2") + "-" + h[2].ToString("X2") + h[3].ToString("X2");
            }
        }

        /// <summary>Order-independent text form of a property tree (dictionaries are sorted by key).</summary>
        static void Canon(StringBuilder sb, object v)
        {
            if (v == null) sb.Append('n');
            else if (v is bool) sb.Append((bool)v ? 'T' : 'F');
            else if (v is double) sb.Append('d').Append(((double)v).ToString("R", CultureInfo.InvariantCulture)).Append(';');
            else if (v is long) sb.Append('i').Append(((long)v).ToString(CultureInfo.InvariantCulture)).Append(';');
            else if (v is ulong) sb.Append('u').Append(((ulong)v).ToString(CultureInfo.InvariantCulture)).Append(';');
            else if (v is string) { var s = (string)v; sb.Append('s').Append(s.Length).Append(':').Append(s); }
            else if (v is SortedDictionary<string, object>)
            {
                sb.Append('{');
                foreach (var kv in (SortedDictionary<string, object>)v)
                {
                    sb.Append(kv.Key.Length).Append(':').Append(kv.Key);
                    Canon(sb, kv.Value);
                }
                sb.Append('}');
            }
            else if (v is List<object>)
            {
                sb.Append('[');
                foreach (var item in (List<object>)v) Canon(sb, item);
                sb.Append(']');
            }
        }

        sealed class Reader
        {
            readonly byte[] d;
            int pos;

            public Reader(byte[] data) { d = data; }

            void Need(int n) { if (n < 0 || pos + n > d.Length) throw new InvalidDataException("файл обрезан или повреждён"); }
            public void Skip(int n) { Need(n); pos += n; }
            byte U8() { Need(1); return d[pos++]; }
            uint U32() { Need(4); var v = BitConverter.ToUInt32(d, pos); pos += 4; return v; }
            uint OptU32() { var b = U8(); return b == 255 ? U32() : b; }

            string Str()
            {
                if (U8() != 0) return "";   // "empty" flag
                var len = OptU32();
                if (len > int.MaxValue) throw new InvalidDataException("неверная длина строки");
                Need((int)len);
                var s = Encoding.UTF8.GetString(d, pos, (int)len);
                pos += (int)len;
                return s;
            }

            public object Tree(int depth)
            {
                if (depth > 64) throw new InvalidDataException("слишком глубокая вложенность");
                var type = U8();
                U8();   // any-type flag
                switch (type)
                {
                    case 0: return null;
                    case 1: return U8() != 0;
                    case 2: { Need(8); var v = BitConverter.ToDouble(d, pos); pos += 8; return v; }
                    case 3: return Str();
                    case 4:
                    {
                        var count = U32();
                        var list = new List<object>();
                        for (uint i = 0; i < count; i++) { Str(); list.Add(Tree(depth + 1)); }
                        return list;
                    }
                    case 5:
                    {
                        var count = U32();
                        var dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                        for (uint i = 0; i < count; i++) { var k = Str(); dict[k] = Tree(depth + 1); }
                        return dict;
                    }
                    case 6: { Need(8); var v = BitConverter.ToInt64(d, pos); pos += 8; return v; }
                    case 7: { Need(8); var v = BitConverter.ToUInt64(d, pos); pos += 8; return v; }
                    default: throw new InvalidDataException("неизвестный тип данных " + type);
                }
            }
        }
    }
}
