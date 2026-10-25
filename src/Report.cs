using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Paranoidal
{
    /// <summary>
    /// «Отчёт о проблеме»: one ZIP for Discord with what the team needs to look into a problem. Never includes
    /// player-data.json (Factorio account token). The Windows user name in paths is replaced by %USERPROFILE%/%USERNAME%.
    /// </summary>
    static class Report
    {
        /// <summary>Discord upload limit agreed with the modpack team (2026-10-22): the whole archive stays under it.</summary>
        public const long Limit = 20L * 1000 * 1000;

        public enum SaveChoice { None, LastManual, LastAutosave, Chosen }

        public sealed class Input
        {
            public FactorioInfo Fac;
            public string ModsDir;
            public string VersionLine;
            /// <summary>Russian text about the build state (version, offline, changes, rollback), from the window.</summary>
            public string State;
            public string Journal;
            public string LoadDiag;
            public List<string> ModifiedFiles;
            public List<string> ForeignMods;
            public SaveChoice Save;
            public string SavePath;
            public string OutFile;
        }

        public sealed class Result
        {
            public string Zip;
            public long Size;
            public readonly List<string> Entries = new List<string>();
            /// <summary>The save that was asked for but not added (path + size + reason, Russian and English).</summary>
            public string SaveSkippedRu, SaveSkippedEn;
            public string SaveAdded;
        }

        public static string WriteData(FactorioInfo fac) => fac == null ? null : GameConfig.ResolveWriteData(fac.Dir, fac.Exe);

        /// <summary>Newest save of the game: a manual one or an autosave (_autosave*.zip). Null when there is none.</summary>
        public static FileInfo LastSave(FactorioInfo fac, bool autosave)
        {
            try
            {
                var dir = Path.Combine(WriteData(fac), "saves");
                if (!Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir).GetFiles("*.zip")
                    .Where(f => f.Name.StartsWith("_autosave", StringComparison.OrdinalIgnoreCase) == autosave)
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            }
            catch { return null; }
        }

        public static Result Create(Input inp)
        {
            var res = new Result { Zip = inp.OutFile };
            var wd = WriteData(inp.Fac);
            var tmp = inp.OutFile + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            {
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    Action<string, string> text = (name, content) =>
                    {
                        if (content == null) return;
                        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                        using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(AppInfo.MaskUser(content));
                        res.Entries.Add(name);
                    };
                    Action<string, string, bool> file = (name, path, mask) =>
                    {
                        try
                        {
                            if (path == null || !File.Exists(path)) return;
                            var bytes = ReadShared(path);
                            if (mask)
                            {
                                var s = Encoding.UTF8.GetString(bytes);
                                // journals can grow for years: the end is what matters
                                if (s.Length > 2 * 1024 * 1024) s = "…(начало обрезано)…\n" + s.Substring(s.Length - 2 * 1024 * 1024);
                                text(name, s);
                                return;
                            }
                            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                            e.LastWriteTime = File.GetLastWriteTime(path);
                            using (var o = e.Open()) o.Write(bytes, 0, bytes.Length);
                            res.Entries.Add(name);
                        }
                        catch (Exception ex) { text(name + ".error.txt", "Не прочитан: " + ex.Message); }
                    };

                    text("README.txt", Readme());
                    text("version.txt", VersionText(inp, wd));
                    if (wd != null)
                    {
                        file("factorio/factorio-current.log", Path.Combine(wd, "factorio-current.log"), true);
                        file("factorio/factorio-previous.log", Path.Combine(wd, "factorio-previous.log"), true);
                    }
                    if (inp.Fac != null)
                    {
                        string cfg = null;
                        try { cfg = GameConfig.ResolveConfigPath(inp.Fac.Dir, inp.Fac.Exe); } catch { }
                        file("factorio/config.ini", cfg, true);
                    }
                    if (inp.ModsDir != null)
                    {
                        file("mods/mod-list.json", Path.Combine(inp.ModsDir, "mod-list.json"), false);
                        file("mods/mod-settings.dat", Path.Combine(inp.ModsDir, "mod-settings.dat"), false);
                    }
                    if (inp.ForeignMods != null) text("mods/mods-not-from-build.txt", inp.ForeignMods.Count == 0 ? "нет\n" : string.Join("\n", inp.ForeignMods) + "\n");
                    if (inp.ModifiedFiles != null)
                        text("mods/changed-build-files.txt", inp.ModifiedFiles.Count == 0 ? "нет: файлы модов сборки совпадают с установленной версией\n" : string.Join("\n", inp.ModifiedFiles) + "\n");
                    file("launcher/sync-log.txt", Path.Combine(AppInfo.DataDir, "sync-log.txt"), true);
                    file("launcher/crash.txt", Path.Combine(AppInfo.DataDir, "crash.txt"), true);
                    file("launcher/settings.ini", Path.Combine(AppInfo.DataDir, "settings.ini"), true);
                    file("launcher/load-diagnostics.txt", Path.Combine(AppInfo.DataDir, "load-diagnostics.txt"), true);
                    text("launcher/journal.txt", inp.Journal);

                    // the save goes first among the big files: the player asked for it
                    if (inp.Save != SaveChoice.None)
                    {
                        string path = inp.SavePath;
                        if (inp.Save == SaveChoice.LastManual || inp.Save == SaveChoice.LastAutosave)
                        {
                            var f = LastSave(inp.Fac, inp.Save == SaveChoice.LastAutosave);
                            path = f == null ? null : f.FullName;
                        }
                        if (path == null || !File.Exists(path))
                        {
                            res.SaveSkippedRu = inp.Save == SaveChoice.LastAutosave ? "автосохранений нет" : "сейвов нет";
                            res.SaveSkippedEn = inp.Save == SaveChoice.LastAutosave ? "there are no autosaves" : "there are no saves";
                        }
                        else
                        {
                            long size = new FileInfo(path).Length;
                            fs.Flush();
                            // saves are compressed already: stored as is, plus the zip directory
                            if (fs.Length + size + 64 * 1024 > Limit)
                            {
                                res.SaveSkippedRu = "сейв «" + Path.GetFileName(path) + "» (" + Fmt.SizeRu(size) + ") не помещается в 20 МБ — приложите его отдельно: " + AppInfo.MaskUser(path);
                                res.SaveSkippedEn = "the save \"" + Path.GetFileName(path) + "\" (" + Fmt.Size(size) + ") does not fit into 20 MB — attach it separately: " + AppInfo.MaskUser(path);
                            }
                            else
                            {
                                var e = zip.CreateEntry("saves/" + Path.GetFileName(path), CompressionLevel.NoCompression);
                                e.LastWriteTime = File.GetLastWriteTime(path);
                                using (var o = e.Open())
                                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                    src.CopyTo(o);
                                res.Entries.Add("saves/" + Path.GetFileName(path));
                                res.SaveAdded = path;
                            }
                        }
                        if (res.SaveSkippedRu != null) text("saves/NOT-INCLUDED.txt", "Сейв не добавлен: " + res.SaveSkippedRu + "\n");
                    }

                    // fresh crash dumps of the game, while they fit
                    if (wd != null && Directory.Exists(wd))
                    {
                        foreach (var dmp in new DirectoryInfo(wd).GetFiles("*.dmp").Where(f => f.LastWriteTime > DateTime.Now.AddDays(-3)).OrderByDescending(f => f.LastWriteTime))
                        {
                            fs.Flush();
                            if (fs.Length + dmp.Length / 2 + 64 * 1024 > Limit) continue;
                            file("factorio/" + dmp.Name, dmp.FullName, false);
                        }
                    }
                }
                res.Size = fs.Length;
            }
            if (res.Size > Limit)
            {
                File.Delete(tmp);
                throw new SyncException("Отчёт получился больше 20 МБ (" + Fmt.SizeRu(res.Size) + "): так быть не должно, сообщите об этом.",
                    "The report is larger than 20 MB (" + Fmt.Size(res.Size) + "): this should not happen, please tell us.");
            }
            if (File.Exists(inp.OutFile)) File.Delete(inp.OutFile);
            File.Move(tmp, inp.OutFile);
            return res;
        }

        /// <summary>"Windows 10 Enterprise LTSC 2019 (1809, build 17763.1234)": Environment.OSVersion says 6.2 without a manifest entry.</summary>
        static string WindowsVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        var name = k.GetValue("ProductName") as string;
                        var rel = (k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId")) as string;
                        var build = k.GetValue("CurrentBuild") as string;
                        var ubr = k.GetValue("UBR");
                        int b;
                        // Windows 11 still writes "Windows 10" into ProductName
                        if (name != null && int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                        return (name ?? "Windows") + " (" + (rel != null ? rel + ", " : "") + "build " + build + (ubr != null ? "." + ubr : "") + ")";
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        static byte[] ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string Readme() =>
            "Отчёт о проблеме Paranoidal Launcher " + AppInfo.Version + ", создан " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ".\n\n" +
            "version.txt — строка версии (как «Скопировать версию»), состояние сборки, игра и пути.\n" +
            "factorio/ — логи игры (текущий и предыдущий запуск), config.ini игры, свежие дампы падений.\n" +
            "mods/ — mod-list.json, mod-settings.dat, моды не из сборки, изменённые файлы сборки.\n" +
            "launcher/ — журнал лаунчера этого сеанса, sync-log.txt, crash.txt, settings.ini, история диагностики загрузки.\n" +
            "saves/ — сейв, если его выбрали и он поместился в 20 МБ.\n\n" +
            "Не включено: player-data.json (в нём токен аккаунта Factorio). Имя пользователя Windows в путях заменено на %USERPROFILE% / %USERNAME%.\n";

        static string VersionText(Input inp, string wd)
        {
            var sb = new StringBuilder();
            sb.Append(inp.VersionLine ?? "строка версии недоступна").Append("\n\n");
            if (inp.State != null) sb.Append(inp.State.TrimEnd()).Append("\n\n");
            if (inp.LoadDiag != null) sb.Append("Диагностика загрузки:\n").Append(inp.LoadDiag.TrimEnd()).Append("\n\n");
            if (inp.Fac != null)
            {
                sb.Append("Factorio: ").Append(inp.Fac.Describe(false)).Append(inp.Fac.Ok ? " (подходит)" : " (НЕ подходит: нужна " + AppInfo.RequiredGame + ")").Append('\n');
                sb.Append("Папка игры: ").Append(inp.Fac.Dir).Append('\n');
                sb.Append("Установка: ").Append(GameConfig.InstallKind(inp.Fac.Dir, inp.Fac.Exe)).Append('\n');
                try { sb.Append("config.ini: ").Append(GameConfig.ResolveConfigPath(inp.Fac.Dir, inp.Fac.Exe)).Append('\n'); } catch { }
                sb.Append("Папка данных игры: ").Append(wd ?? "?").Append('\n');
                var cache = GameConfig.CacheEnabled(inp.Fac.Dir, inp.Fac.Exe);
                sb.Append("Кэш загрузки в config.ini: ").Append(cache == null ? "нет config.ini" : cache.Value ? "включён" : "выключен").Append('\n');
            }
            else sb.Append("Factorio: не найдена\n");
            sb.Append("Папка модов: ").Append(inp.ModsDir ?? "не выбрана").Append('\n');
            if (inp.ModsDir != null)
            {
                try { sb.Append("Отпечаток всего mod-settings.dat: ").Append(ModSettingsFile.FileFingerprint(inp.ModsDir) ?? "нет файла").Append('\n'); }
                catch (Exception ex) { sb.Append("mod-settings.dat не прочитан: ").Append(ex.Message).Append('\n'); }
            }
            sb.Append("Данные лаунчера: ").Append(AppInfo.DataDir).Append('\n');
            sb.Append("Лаунчер: ").Append(AppInfo.Name).Append(' ').Append(AppInfo.Version).Append(", язык интерфейса ").Append(Tr.En ? "EN" : "RU").Append('\n');
            sb.Append("Windows: ").Append(WindowsVersion()).Append(Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit").Append('\n');
            return sb.ToString();
        }
    }
}
