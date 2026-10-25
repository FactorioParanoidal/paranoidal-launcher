using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Paranoidal
{
    /// <summary>
    /// mods\mod-list.json: the build is played with the Quality and Elevated Rails DLC switched on and Space Age switched
    /// off (decision of the modpack team). A player with another DLC state gets a mod mismatch with the host.
    /// </summary>
    static class ModList
    {
        /// <summary>DLC mod -> the state the build needs. Other entries of mod-list.json are never touched.</summary>
        static readonly KeyValuePair<string, bool>[] Dlc =
        {
            new KeyValuePair<string, bool>("space-age", false),
            new KeyValuePair<string, bool>("quality", true),
            new KeyValuePair<string, bool>("elevated-rails", true),
        };

        /// <summary>Creates mod-list.json if missing; otherwise only sets the enabled flag of the DLC entries.</summary>
        public static void EnsureDlc(string modsDir, Action<string, LogKind> log)
        {
            Directory.CreateDirectory(modsDir);
            var path = Path.Combine(modsDir, "mod-list.json");
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            List<Dictionary<string, object>> mods = null;

            if (File.Exists(path))
            {
                try
                {
                    var root = ser.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
                    object arr;
                    if (root != null && root.TryGetValue("mods", out arr) && arr is IEnumerable)
                    {
                        mods = new List<Dictionary<string, object>>();
                        foreach (var o in (IEnumerable)arr)
                        {
                            var d = o as IDictionary<string, object>;
                            if (d != null) mods.Add(new Dictionary<string, object>(d));
                        }
                    }
                }
                catch { mods = null; }

                if (mods == null)
                {
                    var bak = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    try { File.Copy(path, bak, true); } catch { }
                    log("mod-list.json повреждён — сохранён как " + Path.GetFileName(bak) + ", создаю новый.", LogKind.Warn);
                }
                else
                {
                    var changes = new List<string>();
                    foreach (var want in Dlc)
                    {
                        bool found = false, changed = false;
                        foreach (var m in mods)
                        {
                            if (!want.Key.Equals(m.ContainsKey("name") ? m["name"] as string : null, StringComparison.Ordinal)) continue;
                            found = true;
                            object en;
                            if (!(m.TryGetValue("enabled", out en) && en is bool && (bool)en == want.Value)) { m["enabled"] = want.Value; changed = true; }
                        }
                        if (!found) { mods.Add(Entry(want.Key, want.Value)); changed = true; }
                        if (changed) changes.Add(want.Key + (want.Value ? " включён" : " выключен"));
                    }
                    if (changes.Count == 0) return;
                    Write(path, mods, ser);
                    log("mod-list.json: " + string.Join(", ", changes) + ".", LogKind.Ok);
                    return;
                }
            }

            mods = new List<Dictionary<string, object>> { Entry("base", true) };
            foreach (var name in new[] { "elevated-rails", "quality", "space-age" })
                foreach (var want in Dlc)
                    if (want.Key == name) mods.Add(Entry(want.Key, want.Value));
            Write(path, mods, ser);
            log("Создан mod-list.json: quality и elevated-rails включены, space-age выключен.", LogKind.Ok);
        }

        static Dictionary<string, object> Entry(string name, bool enabled) =>
            new Dictionary<string, object> { { "name", name }, { "enabled", enabled } };

        static void Write(string path, List<Dictionary<string, object>> mods, JavaScriptSerializer ser)
        {
            var sb = new StringBuilder("{\n  \"mods\": [\n");
            for (int i = 0; i < mods.Count; i++)
                sb.Append("    ").Append(ser.Serialize(mods[i])).Append(i + 1 < mods.Count ? ",\n" : "\n");
            sb.Append("  ]\n}\n");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            File.Move(tmp, path);
        }
    }
}
