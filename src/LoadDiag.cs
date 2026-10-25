using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Paranoidal
{
    /// <summary>
    /// What one factorio-current/previous.log says about loading (Factorio 2.0.77 markers, measured in
    /// docs/factorio-load-cache.md). Read only.
    /// </summary>
    sealed class LoadLog
    {
        public enum DataState { None, Hit, Rebuilt, NotUsed }
        public enum AtlasState { None, Hit, Miss }

        public string Header;          // "2026-10-21 22:36:40; Factorio 2.0.77 (build 84539, win64, full, space-age)"
        public string Version;         // "2.0.77 (build 84539, win64, full, space-age)"
        public string ModDir, BinPath, WriteData, Graphics;
        public bool Headless;
        public double? Initialised;
        public DataState Data;
        public string RebuildReason;
        public bool DataWritten, AtlasWritten;
        public AtlasState Atlas;
        public bool Crop;
        public readonly Dictionary<string, string> Checksums = new Dictionary<string, string>(StringComparer.Ordinal);

        static readonly Regex Line = new Regex(@"^\s*(\d+\.\d+)\s(.*)$");

        public static LoadLog Read(string path)
        {
            if (path == null || !File.Exists(path)) return null;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
            return Parse(text);
        }

        public static LoadLog Parse(string text)
        {
            var log = new LoadLog();
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Line.Match(lines[i].TrimEnd('\r'));
                if (!m.Success) continue;
                double t = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var s = m.Groups[2].Value.Trim();
                if (log.Header == null)
                {
                    log.Header = s;
                    var v = Regex.Match(s, @"Factorio (\d+\.\d+\.\d+ \([^)]*\))");
                    if (v.Success) log.Version = v.Groups[1].Value;
                    continue;
                }
                if (s.StartsWith("Program arguments:"))
                {
                    var args = Regex.Matches(s, "\"([^\"]*)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
                    int k = args.FindIndex(a => a == "--mod-directory");
                    if (k >= 0 && k + 1 < args.Count) log.ModDir = Norm(args[k + 1]);
                    log.Headless = args.Any(a => a == "--create" || a == "--start-server" || a == "--start-server-load-latest" ||
                        a == "--dump-data" || a == "--benchmark" || a == "--start-server-load-scenario");
                }
                else if (s.StartsWith("Binaries path:")) log.BinPath = Norm(s.Substring(14));
                else if (s.StartsWith("Write data path:")) log.WriteData = Norm(Regex.Replace(s.Substring(16), @"\s*\[[^\]]*\]\s*$", ""));
                else if (s.StartsWith("Graphics options:"))
                {
                    log.Graphics = s.Substring(17).Trim();
                    // the options continue on the next line, which has only the timestamp and spaces before "["
                    if (i + 1 < lines.Length)
                    {
                        var n = Regex.Match(lines[i + 1].TrimEnd('\r'), @"^\s*\d+\.\d+\s{2,}(\[.*)$");
                        if (n.Success) log.Graphics += " " + n.Groups[1].Value.Trim();
                    }
                }
                else if (s.StartsWith("Data stage cache loaded in")) { if (log.Data == DataState.None) log.Data = DataState.Hit; }
                else if (s.StartsWith("Rebuilding data stage cache because"))
                {
                    log.Data = DataState.Rebuilt;
                    log.RebuildReason = s.Substring(35).Trim().TrimEnd('.');
                }
                else if (s.StartsWith("Data stage cache not used")) log.Data = DataState.NotUsed;
                else if (s.StartsWith("Data stage cached in")) log.DataWritten = true;
                else if (s.StartsWith("Atlases loaded from disk cache")) log.Atlas = AtlasState.Hit;
                else if (s.StartsWith("Atlases were not loaded from disk cache")) log.Atlas = AtlasState.Miss;
                else if (s.StartsWith("Atlas cache saved")) log.AtlasWritten = true;
                else if (s.StartsWith("Crop bitmaps")) log.Crop = true;
                else if (s.StartsWith("Factorio initialised")) { if (log.Initialised == null) log.Initialised = t; }
                else if (s.StartsWith("Checksum of "))
                {
                    var c = Regex.Match(s, @"^Checksum of (.+): (\d+)$");
                    if (c.Success) log.Checksums[c.Groups[1].Value] = c.Groups[2].Value;
                }
            }
            // the data cache was hit but written again: the data stage ran (a reason the game did not name)
            if (log.Data == DataState.Hit && log.DataWritten) log.Data = DataState.Rebuilt;
            return log.Header == null ? null : log;
        }

        static string Norm(string path)
        {
            var p = path.Trim().Replace('/', '\\').TrimEnd('\\');
            try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p; }
        }
    }

    /// <summary>Context the log does not have: what the launcher knows about the run.</summary>
    sealed class LoadContext
    {
        /// <summary>Enabled mods with a "~" dependency (GameConfig.TildeMods).</summary>
        public List<string> Tilde = new List<string>();
        /// <summary>The launcher changed the build version between the two launches.</summary>
        public bool BuildChanged;
        /// <summary>The mods folder of the launcher (to tell a game started with other mods).</summary>
        public string ModsDir;
    }

    sealed class LoadVerdict
    {
        public string Key;
        public double? Seconds;
        public LoadLog.DataState Data;
        public LoadLog.AtlasState Atlas;
        public bool Headless, OtherMods, DataWritten, AtlasWritten;
        /// <summary>Reasons as {ru, en}.</summary>
        public readonly List<string[]> DataCauses = new List<string[]>(), AtlasCauses = new List<string[]>();

        static string Pick(string[] s, bool en) => en ? s[1] : s[0];

        /// <summary>One line for the window: time, both caches, the first reason.</summary>
        public string Line(bool en)
        {
            var sb = new StringBuilder(en ? "Last game start: " : "Последний запуск игры: ");
            if (OtherMods) sb.Append(en ? "(other mods folder) " : "(другая папка модов) ");
            sb.Append(Seconds == null ? (en ? "closed before the menu" : "закрыта до меню")
                : (en ? "to the menu " : "до меню ") + Math.Round(Seconds.Value).ToString(CultureInfo.InvariantCulture) + (en ? " s" : " с"));
            sb.Append(" · ").Append(DataPart(en));
            if (!Headless) sb.Append(" · ").Append(AtlasPart(en));
            var first = DataCauses.Count > 0 && Data != LoadLog.DataState.Hit ? DataCauses[0] : AtlasCauses.Count > 0 && Atlas == LoadLog.AtlasState.Miss ? AtlasCauses[0] : null;
            if (first != null) sb.Append(" — ").Append(Pick(first, en));
            return sb.ToString();
        }

        string DataPart(bool en)
        {
            switch (Data)
            {
                case LoadLog.DataState.Hit: return en ? "data cache ✔" : "кэш данных ✔";
                case LoadLog.DataState.None: return en ? "data cache off" : "кэш данных выключен";
                default: return en ? "data cache ✖" : "кэш данных ✖";
            }
        }

        string AtlasPart(bool en)
        {
            switch (Atlas)
            {
                case LoadLog.AtlasState.Hit: return en ? "atlas cache ✔" : "кэш атласа ✔";
                case LoadLog.AtlasState.None: return en ? "atlas cache off" : "кэш атласа выключен";
                default: return en ? "atlas cache ✖" : "кэш атласа ✖";
            }
        }

        /// <summary>Several lines: every reason and what happens next time.</summary>
        public string Full(bool en)
        {
            var sb = new StringBuilder(Line(en).Split(new[] { " — " }, StringSplitOptions.None)[0]);
            if (Data != LoadLog.DataState.Hit)
            {
                sb.Append(en ? "\nData cache: " : "\nКэш данных: ");
                sb.Append(string.Join("; ", DataCauses.Select(c => Pick(c, en))));
                if (DataWritten) sb.Append(en ? ". It was built again: the next start will be faster." : ". Он построен заново: следующий запуск будет быстрее.");
            }
            if (!Headless && Atlas != LoadLog.AtlasState.Hit)
            {
                sb.Append(en ? "\nAtlas cache: " : "\nКэш атласа: ");
                sb.Append(string.Join("; ", AtlasCauses.Select(c => Pick(c, en))));
                if (AtlasWritten && Atlas == LoadLog.AtlasState.Miss) sb.Append(en ? ". It was saved again." : ". Он сохранён заново.");
            }
            if (OtherMods)
                sb.Append(en ? "\nThe game was started with another mods folder, not the launcher's one." : "\nИгра запускалась не с папкой модов лаунчера.");
            return sb.ToString();
        }
    }

    static class LoadDiag
    {
        static string[] P(string ru, string en) => new[] { ru, en };

        static string Names(IEnumerable<string> names)
        {
            var list = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return string.Join(", ", list.Take(6)) + (list.Count > 6 ? " (+" + (list.Count - 6) + ")" : "");
        }

        /// <summary>Why the current launch did (not) use the caches: the current log compared with the previous one.</summary>
        public static LoadVerdict Analyze(LoadLog cur, LoadLog prev, LoadContext ctx)
        {
            ctx = ctx ?? new LoadContext();
            var v = new LoadVerdict
            {
                Key = cur.Header,
                Seconds = cur.Initialised,
                Data = cur.Data,
                Atlas = cur.Atlas,
                Headless = cur.Headless,
                DataWritten = cur.DataWritten,
                AtlasWritten = cur.AtlasWritten,
            };
            if (ctx.ModsDir != null && cur.ModDir != null && !string.Equals(Norm(ctx.ModsDir), cur.ModDir, StringComparison.OrdinalIgnoreCase))
                v.OtherMods = true;
            if (ctx.ModsDir != null && cur.ModDir == null) v.OtherMods = true;   // started without --mod-directory

            bool otherInstall = prev != null && prev.BinPath != null && cur.BinPath != null && !string.Equals(prev.BinPath, cur.BinPath, StringComparison.OrdinalIgnoreCase);
            string[] otherInstallText = otherInstall
                ? P("другая установка игры (" + prev.BinPath + ") пользуется той же папкой данных: кэш у них общий и перестраивается при каждой смене установки",
                    "another game installation (" + prev.BinPath + ") uses the same data folder: they share one cache, it is rebuilt every time the installation changes")
                : null;

            switch (cur.Data)
            {
                case LoadLog.DataState.None:
                    v.DataCauses.Add(P("кэш выключен в config.ini игры (cache-prototype-data)", "the cache is off in the game's config.ini (cache-prototype-data)"));
                    break;
                case LoadLog.DataState.Rebuilt:
                    if (cur.RebuildReason != null && cur.RebuildReason.Contains("mod settings changed"))
                        v.DataCauses.Add(P("изменились настройки модов (любые, в том числе личные, которые меняются в игре)",
                            "mod settings changed (any of them, also the per-player ones changed in game)"));
                    else
                        v.DataCauses.Add(P("игра пересобрала кэш: «" + (cur.RebuildReason ?? "причина не указана") + "»",
                            "the game rebuilt the cache: \"" + (cur.RebuildReason ?? "no reason given") + "\""));
                    break;
                case LoadLog.DataState.NotUsed:
                    if (ctx.Tilde.Count > 0)
                        v.DataCauses.Add(P("у модов " + Names(ctx.Tilde) + " в info.json зависимость с «~»: из-за ошибки Factorio 2.0 кэш данных с ними не работает никогда",
                            "mods " + Names(ctx.Tilde) + " have a \"~\" dependency in info.json: because of a Factorio 2.0 bug the data cache never works with them"));
                    if (prev == null)
                    {
                        v.DataCauses.Add(P("предыдущего лога игры нет: первый запуск с этой папкой данных, или кэш ещё не построен",
                            "there is no previous game log: the first start with this data folder, or the cache was not built yet"));
                        break;
                    }
                    if (!string.Equals(prev.ModDir, cur.ModDir, StringComparison.OrdinalIgnoreCase))
                        v.DataCauses.Add(P("между запусками игра запускалась с другой папкой модов (" + (prev.ModDir ?? "папка модов игры по умолчанию") + ")",
                            "in between the game was started with another mods folder (" + (prev.ModDir ?? "the game's default mods folder") + ")"));
                    if (otherInstall) v.DataCauses.Add(otherInstallText);
                    if (prev.Version != null && cur.Version != null && prev.Version != cur.Version)
                        v.DataCauses.Add(P("сменилась версия игры (" + prev.Version + " → " + cur.Version + ")", "the game version changed (" + prev.Version + " → " + cur.Version + ")"));
                    if (ctx.BuildChanged)
                        v.DataCauses.Add(P("лаунчер сменил версию сборки после прошлого запуска", "the launcher changed the build version after the previous start"));
                    if (prev.Checksums.Count > 0 && cur.Checksums.Count > 0)
                    {
                        var added = cur.Checksums.Keys.Where(k => !prev.Checksums.ContainsKey(k)).ToList();
                        var removed = prev.Checksums.Keys.Where(k => !cur.Checksums.ContainsKey(k)).ToList();
                        var changed = cur.Checksums.Where(kv => prev.Checksums.ContainsKey(kv.Key) && prev.Checksums[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
                        if (added.Count > 0) v.DataCauses.Add(P("добавлены моды: " + Names(added), "mods added: " + Names(added)));
                        if (removed.Count > 0) v.DataCauses.Add(P("убраны моды: " + Names(removed), "mods removed: " + Names(removed)));
                        if (changed.Count > 0) v.DataCauses.Add(P("изменились файлы модов: " + Names(changed), "mod files changed: " + Names(changed)));
                    }
                    if (v.DataCauses.Count == 0)
                        v.DataCauses.Add(P("причина не определена: файл кэша удалён или повреждён, либо мод изменился без изменения своих Lua-файлов",
                            "unknown reason: the cache file was deleted or damaged, or a mod changed without a change of its Lua files"));
                    break;
            }

            switch (cur.Atlas)
            {
                case LoadLog.AtlasState.None:
                    if (!cur.Headless) v.AtlasCauses.Add(P("кэш выключен в config.ini игры (cache-sprite-atlas)", "the cache is off in the game's config.ini (cache-sprite-atlas)"));
                    break;
                case LoadLog.AtlasState.Miss:
                    if (cur.Crop)
                        v.AtlasCauses.Add(P("новая или изменённая графика модов: атлас собран заново, следующий запуск соберёт его ещё раз (так устроена игра), дальше быстро",
                            "new or changed mod graphics: the atlas was built again, the next start builds it once more (the game works so), then it is fast"));
                    else if (prev != null && prev.Crop)
                        v.AtlasCauses.Add(P("второй запуск после новой графики: атлас пересобирается ещё раз, дальше быстро",
                            "the second start after new graphics: the atlas is rebuilt once more, then it is fast"));
                    if (prev != null && prev.Graphics != null && cur.Graphics != null && prev.Graphics != cur.Graphics)
                        v.AtlasCauses.Add(P("изменились настройки графики", "graphics settings changed"));
                    if (otherInstall && v.AtlasCauses.Count == 0) v.AtlasCauses.Add(otherInstallText);
                    if (v.AtlasCauses.Count == 0)
                        v.AtlasCauses.Add(P("причина не определена (например, удалён файл кэша)", "unknown reason (e.g. the cache file was deleted)"));
                    break;
            }
            return v;
        }

        static string Norm(string path)
        {
            try { return Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\'); } catch { return path; }
        }

        /// <summary>The run history the report includes: one Russian entry per analysed launch.</summary>
        public static void Remember(LoadVerdict v)
        {
            try
            {
                var file = Path.Combine(AppInfo.DataDir, "load-diagnostics.txt");
                Directory.CreateDirectory(AppInfo.DataDir);
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024)
                {
                    var keep = File.ReadAllText(file, Encoding.UTF8);
                    File.WriteAllText(file, keep.Substring(keep.Length - 128 * 1024), new UTF8Encoding(false));
                }
                File.AppendAllText(file, "===== " + v.Key + "\r\n" + v.Full(false).Replace("\n", "\r\n") + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
