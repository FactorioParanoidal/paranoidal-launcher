using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Paranoidal
{
    /// <summary>One step of the move to Paranoidal 2.1. Done steps are skipped when an interrupted move continues.</summary>
    sealed class MigStep
    {
        /// <summary>move | delete | config | exe | settings | shortcuts</summary>
        public string Kind, Src, Dst, Extra;
        public bool Done;
        /// <summary>Size of what a move carries (the free space check); not stored.</summary>
        public long Bytes;
    }

    /// <summary>
    /// What the move to Paranoidal 2.1 does (Migration.Plan), kept in ParanoidalLauncher-migration.txt in the new game
    /// folder while it runs: the old launcher (settings migrating=…) and the new one both continue an interrupted move.
    /// </summary>
    sealed class MigrationPlan
    {
        public const string FileName = "ParanoidalLauncher-migration.txt";
        public string OldDir, NewDir, NewVersion, OldWd, NewWd, OldCfg, NewCfg, OldExe, OldData, NewData, ModsDir, NewMods;
        /// <summary>The game folder itself is 2.1 now (Steam updated it): nothing moves, the build switches its line.</summary>
        public bool InPlace;
        /// <summary>Both games use one data folder (Steam, two installer games): saves and settings are there already.</summary>
        public bool SharedWd;
        /// <summary>The mods folder is not the old game's own (an own mods folder): it stays where it is.</summary>
        public bool ModsStay;
        public readonly List<MigStep> Steps = new List<MigStep>();

        // for the confirmation dialog only
        public int Saves;
        public long SavesBytes, ModsBytes, CacheBytes;
        public readonly List<string> PlayerItems = new List<string>(), Asides = new List<string>(), CacheItems = new List<string>(), Foreign = new List<string>();
        public string Error, ErrorEn;

        public string PlanFile => Path.Combine(NewDir, FileName);
        public string NewExe => Path.Combine(NewDir, AppInfo.ExeName);
        public bool Finished => Steps.All(s => s.Done);
    }

    static class Migration
    {
        /// <summary>Files of the player in the game's data folder that go with him (besides the folders below).</summary>
        static readonly string[] PlayerFiles =
        {
            "player-data.json", "achievements.dat", "achievements-modded.dat",
            "server-adminlist.json", "server-banlist.json", "server-whitelist.json", "server-settings.json", "server-id.json",
        };
        /// <summary>Folders whose entries move one by one (a name taken in the new folder gets " (2.0)").</summary>
        static readonly string[] PlayerDirs = { "saves", "scenarios", "script-output" };
        /// <summary>The 2.0 game's load cache: useless for 2.1, about 1 GB (owner's decision: deleted).</summary>
        static readonly string[] CacheFiles = { "atlas-cache.dat", "data-cache.dat", "crop-cache.dat", "temp" };

        /// <summary>Tests only: PARANOIDAL_LAUNCHER_TEST_MIGRATE_STOP=n stops the move after n steps (an interruption).</summary>
        static int TestStop
        {
            get { int n; return int.TryParse(Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_TEST_MIGRATE_STOP"), out n) ? n : 0; }
        }

        static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\');
        static bool Same(string a, string b) => Relocation.SameDir(a, b);
        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        static bool SameVolume(string a, string b)
        {
            try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static long Size(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (!Directory.Exists(path)) return 0;
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        /// <summary>A name that is free on disk and not taken by an earlier step: "a.zip" -> "a (2.0).zip", "a (2.0) 2.zip"…</summary>
        static string Free(string path, string suffix, HashSet<string> taken, bool keepExtension)
        {
            if (!Exists(path) && !taken.Contains(path)) { taken.Add(path); return path; }
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            var ext = keepExtension ? Path.GetExtension(name) : "";
            var stem = name.Substring(0, name.Length - ext.Length);
            for (int i = 1; ; i++)
            {
                var c = Path.Combine(dir, stem + suffix + (i > 1 ? " " + i : "") + ext);
                if (!Exists(c) && !taken.Contains(c)) { taken.Add(c); return c; }
            }
        }

        static MigStep Add(MigrationPlan p, string kind, string src, string dst = null, string extra = null, long bytes = 0)
        {
            var s = new MigStep { Kind = kind, Src = src, Dst = dst, Extra = extra, Bytes = bytes };
            p.Steps.Add(s);
            return s;
        }

        /// <summary>Something of the new game folder is in the way: it is renamed to *.before-paranoidal, never overwritten.</summary>
        static void Aside(MigrationPlan p, string path, HashSet<string> taken)
        {
            if (!Exists(path)) return;
            var to = Free(path + ".before-paranoidal", "", taken, false);
            Add(p, "move", path, to);
            p.Asides.Add(path + " → " + Path.GetFileName(to));
        }

        /// <summary>
        /// The plan of the move from the 2.0 game `oldFac` (its mods folder, the running exe and its data folder) to the 2.1
        /// game `newFac`. Nothing is changed yet; Error is set when the move cannot be done.
        /// </summary>
        public static MigrationPlan Plan(FactorioInfo oldFac, string modsDir, string oldExe, string oldData, FactorioInfo newFac, IEnumerable<string> foreign)
        {
            var p = new MigrationPlan
            {
                OldDir = Full(oldFac.Dir),
                NewDir = Full(newFac.Dir),
                NewVersion = newFac.Version,
                OldExe = Full(oldExe),
                OldData = Full(oldData),
                ModsDir = Full(modsDir),
            };
            if (foreign != null) p.Foreign.AddRange(foreign.Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            p.InPlace = Same(p.OldDir, p.NewDir);
            p.OldWd = GameConfig.ResolveWriteData(oldFac.Dir, oldFac.Exe);
            p.NewWd = GameConfig.ResolveWriteData(newFac.Dir, newFac.Exe);
            p.OldCfg = GameConfig.ResolveConfigPath(oldFac.Dir, oldFac.Exe);
            p.NewCfg = GameConfig.ResolveConfigPath(newFac.Dir, newFac.Exe);
            p.SharedWd = p.InPlace || Same(p.OldWd, p.NewWd);
            var oldRoot = Path.GetDirectoryName(p.ModsDir);
            p.ModsStay = p.InPlace || !Same(oldRoot, p.OldDir);
            p.NewMods = p.ModsStay ? p.ModsDir : Path.Combine(p.NewDir, "mods");
            p.NewData = p.InPlace ? p.OldData : Path.Combine(p.NewDir, AppInfo.DataDirName);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (FactorioInfo.IsRunning())
            {
                p.Error = "Factorio запущена. Закройте игру и начните переезд снова.";
                p.ErrorEn = "Factorio is running. Close the game and start the move again.";
                return p;
            }
            if (!p.InPlace && !AppInfo.CanWrite(p.NewDir))
            {
                p.Error = "В папку " + p.NewDir + " нельзя записывать без прав администратора (например, это Program Files). " +
                    "Перенесите игру 2.1 в обычную папку, например C:\\Games\\Factorio 2.1, и выберите её снова.";
                p.ErrorEn = "The folder " + p.NewDir + " cannot be written without administrator rights (e.g. Program Files). " +
                    "Move the 2.1 game to a normal folder, e.g. C:\\Games\\Factorio 2.1, and choose it again.";
                return p;
            }
            if (!p.SharedWd && !AppInfo.CanWrite(p.NewWd))
            {
                p.Error = "В папку данных игры 2.1 (" + p.NewWd + ") нельзя записывать.";
                p.ErrorEn = "The data folder of the 2.1 game (" + p.NewWd + ") cannot be written.";
                return p;
            }

            // the launcher's state of the 2.0 build: no rollback to 2.0, «Оставить» answers start over (owner's decision)
            var gitDir = Path.Combine(oldRoot, ".paranoidal-git");
            foreach (var n in new[] { "rollback", "rollback-new", "blobs-tmp", "kept-extras.txt", "played-commit" })
                if (Exists(Path.Combine(gitDir, n))) Add(p, "delete", Path.Combine(gitDir, n));

            if (p.InPlace)
            {
                Add(p, "settings", p.OldData);
                Add(p, "delete", Path.Combine(p.OldData, "commits-cache.txt"));
                return p;
            }

            // 1. the player's files, when the 2.1 game keeps its data in another folder
            if (!p.SharedWd)
            {
                foreach (var d in PlayerDirs)
                {
                    var from = Path.Combine(p.OldWd, d);
                    if (!Directory.Exists(from)) continue;
                    int n = 0;
                    foreach (var e in Directory.GetFileSystemEntries(from).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        bool file = File.Exists(e);
                        var to = Free(Path.Combine(p.NewWd, d, Path.GetFileName(e)), " (2.0)", taken, file);
                        long b = Size(e);
                        Add(p, "move", e, to, null, b);
                        n++;
                        if (d == "saves") { p.Saves++; p.SavesBytes += b; }
                    }
                    if (n > 0 && d != "saves") p.PlayerItems.Add(d + " (" + n + ")");
                }
                var files = PlayerFiles.Where(f => File.Exists(Path.Combine(p.OldWd, f))).ToList();
                try
                {
                    files.AddRange(Directory.GetFiles(p.OldWd, "blueprint-storage*.dat").Select(Path.GetFileName)
                        .Where(f => !files.Contains(f, StringComparer.OrdinalIgnoreCase)));
                }
                catch { }
                foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var to = Path.Combine(p.NewWd, f);
                    Aside(p, to, taken);
                    Add(p, "move", Path.Combine(p.OldWd, f), to, null, Size(Path.Combine(p.OldWd, f)));
                    p.PlayerItems.Add(f);
                }
                if (!Same(p.OldCfg, p.NewCfg) && File.Exists(p.OldCfg))
                {
                    string aside = "";
                    if (File.Exists(p.NewCfg))
                    {
                        aside = Free(p.NewCfg + ".before-paranoidal", "", taken, false);
                        Add(p, "move", p.NewCfg, aside);
                        p.Asides.Add(p.NewCfg + " → " + Path.GetFileName(aside));
                    }
                    Add(p, "config", p.OldCfg, p.NewCfg, aside);
                }
            }

            // 2. the build: the mods folder with the launcher's git folder next to it (only the difference is downloaded then)
            if (!p.ModsStay)
            {
                var newGit = Path.Combine(p.NewDir, ".paranoidal-git");
                Aside(p, p.NewMods, taken);
                Aside(p, newGit, taken);
                if (Directory.Exists(p.ModsDir))
                {
                    p.ModsBytes = Size(p.ModsDir);
                    Add(p, "move", p.ModsDir, p.NewMods, null, p.ModsBytes);
                }
                if (Directory.Exists(gitDir))
                {
                    long b = Size(gitDir);
                    p.ModsBytes += b;
                    Add(p, "move", gitDir, newGit, null, b);
                }
            }

            // 3. the guide next to the exe
            var exeDir = Path.GetDirectoryName(p.OldExe);
            var guide = Path.Combine(exeDir, AppInfo.GuideFile);
            if (File.Exists(guide))
            {
                var tg = Path.Combine(p.NewDir, AppInfo.GuideFile);
                if (!File.Exists(tg)) Add(p, "move", guide, tg);
                else if (Releases.Compare(GuideFile.Version(guide), GuideFile.Version(tg)) > 0) { Add(p, "delete", tg); Add(p, "move", guide, tg); }
                else Add(p, "delete", guide);
            }

            // 4. the 2.0 load cache and the launcher's leftovers in the old place
            if (!p.SharedWd)
                foreach (var c in CacheFiles)
                {
                    var path = Path.Combine(p.OldWd, c);
                    if (!Exists(path)) continue;
                    p.CacheBytes += Size(path);
                    p.CacheItems.Add(c);
                    Add(p, "delete", path);
                }
            if (!Same(Path.GetDirectoryName(p.OldCfg), Path.GetDirectoryName(p.NewCfg)) && File.Exists(p.OldCfg + ".paranoidal-backup"))
                Add(p, "delete", p.OldCfg + ".paranoidal-backup");
            try
            {
                foreach (var f in Directory.GetFiles(exeDir))
                {
                    var n = Path.GetFileName(f);
                    if ((n.StartsWith("ParanoidalLauncher", StringComparison.OrdinalIgnoreCase) && (n.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ||
                         n.EndsWith(".new", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".download", StringComparison.OrdinalIgnoreCase))) ||
                        (n.StartsWith("Guide_to_the_Paranoidal", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".download", StringComparison.OrdinalIgnoreCase)))
                        Add(p, "delete", f);
                }
            }
            catch { }

            // 5. the launcher: the exe first (a launcher started from the old place then finds it and hands over), then its data
            Add(p, "exe", p.OldExe, p.NewDir);
            if (!Same(p.OldData, p.NewData))
            {
                Aside(p, p.NewData, taken);
                if (Directory.Exists(p.OldData)) Add(p, "move", p.OldData, p.NewData, null, Size(p.OldData));
            }
            Add(p, "settings", p.NewData);
            Add(p, "delete", Path.Combine(p.NewData, "commits-cache.txt"));
            Add(p, "shortcuts", p.OldExe, p.NewExe);

            // free space: what is copied to another disk (a move inside one disk takes no space)
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in p.Steps.Where(s => s.Kind == "move" && s.Bytes > 0 && !SameVolume(s.Src, s.Dst)))
            {
                var root = Path.GetPathRoot(Path.GetFullPath(s.Dst));
                long v;
                need.TryGetValue(root, out v);
                need[root] = v + s.Bytes;
            }
            foreach (var kv in need)
            {
                try
                {
                    var free = new DriveInfo(kv.Key).AvailableFreeSpace;
                    if (free < kv.Value + 300L * 1024 * 1024)
                    {
                        p.Error = "На диске " + kv.Key + " не хватает места: нужно " + Fmt.SizeRu(kv.Value + 300L * 1024 * 1024) + ", свободно " + Fmt.SizeRu(free) + ".";
                        p.ErrorEn = "Not enough space on " + kv.Key + ": " + Fmt.Size(kv.Value + 300L * 1024 * 1024, true) + " needed, " + Fmt.Size(free, true) + " free.";
                    }
                }
                catch { }
            }
            return p;
        }

        // ------------------------------------------------------------------ running

        /// <summary>
        /// Runs the steps that are not done yet, saving the plan after each one (an interrupted move continues from the
        /// next start of either launcher). Throws on an error; the steps done stay done.
        /// </summary>
        public static void Run(MigrationPlan p, Action<string> log, Action<double?, string> progress)
        {
            Save(p);
            var todo = p.Steps.Where(s => !s.Done).ToList();
            int i = 0, stop = TestStop;
            foreach (var s in todo)
            {
                progress(todo.Count == 0 ? 1 : i / (double)todo.Count, Tr.T("Шаг ", "Step ") + (p.Steps.IndexOf(s) + 1) + Tr.T(" из ", " of ") + p.Steps.Count + ": " + Short(s));
                Do(p, s, log);
                s.Done = true;
                Save(p);
                i++;
                if (stop > 0 && i >= stop) throw new SyncException("Тестовая остановка переезда после шага " + i + " (PARANOIDAL_LAUNCHER_TEST_MIGRATE_STOP).");
            }
            progress(1, "");
        }

        static string Short(MigStep s)
        {
            switch (s.Kind)
            {
                case "move": return Path.GetFileName(s.Src) + " → " + s.Dst;
                case "delete": return Tr.T("удаление ", "deleting ") + s.Src;
                case "config": return "config.ini → " + s.Dst;
                case "exe": return AppInfo.ExeName + " → " + s.Dst;
                case "settings": return "settings.ini";
                default: return Tr.T("ярлыки", "shortcuts");
            }
        }

        /// <summary>The step for the journal (Russian, en = false) or the confirmation dialog.</summary>
        public static string Describe(MigStep s, bool en = false)
        {
            Func<string, string, string> t = (ru, e) => en ? e : ru;
            switch (s.Kind)
            {
                case "move": return t("перенос: ", "move: ") + s.Src + " → " + s.Dst;
                case "delete": return t("удаление: ", "delete: ") + s.Src;
                case "config": return t("настройки игры: ", "game settings: ") + s.Src + " → " + s.Dst + t(" (пути [path] — от игры 2.1)", " ([path] of the 2.1 game)");
                case "exe": return t("лаунчер: ", "launcher: ") + s.Src + " → " + s.Dst;
                case "settings": return t("настройки лаунчера: ", "launcher settings: ") + Path.Combine(s.Src, "settings.ini") + t(" (линия 2.1)", " (line 2.1)");
                default: return t("ярлыки: ", "shortcuts: ") + s.Dst;
            }
        }

        static void Do(MigrationPlan p, MigStep s, Action<string> log)
        {
            switch (s.Kind)
            {
                case "move": MovePath(s.Src, s.Dst); break;
                case "delete": DeletePath(s.Src); break;
                case "config": WriteConfig(s.Src, s.Dst, s.Extra); break;
                case "exe":
                    if (File.Exists(s.Src) && !Relocation.TargetIsSameOrNewer(s.Dst)) SelfUpdate.InstallCopy(s.Src, s.Dst);
                    break;
                case "settings": WriteSettings(p, s.Src); break;
                case "shortcuts": Repoint(p, log); break;
                default: throw new SyncException("Неизвестный шаг переезда: " + s.Kind);
            }
            log(Describe(s));
        }

        const string CopyMark = ".paranoidal-copy-complete";

        /// <summary>
        /// Moves a file or a folder. A folder on another disk is copied to dst.partial, marked complete, renamed to dst and
        /// only then deleted at its old place, so an interruption at any moment loses nothing and continues. Something
        /// that appeared at dst after the plan was made is renamed to *.before-paranoidal.
        /// </summary>
        static void MovePath(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (File.Exists(src))
            {
                if (Exists(dst)) MoveAside(dst);
                File.Move(src, dst);   // across disks too (copy + delete by Windows)
                return;
            }
            var mark = Path.Combine(dst, CopyMark);
            if (!Directory.Exists(src))
            {
                if (File.Exists(mark)) File.Delete(mark);
                return;   // moved already
            }
            if (Directory.Exists(dst))
            {
                if (File.Exists(mark)) { FsUtil.DeleteDir(src); File.Delete(mark); return; }   // copied already, the source was left
                MoveAside(dst);
            }
            if (SameVolume(src, dst))
            {
                try { Directory.Move(src, dst); return; }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 17) { }   // ERROR_NOT_SAME_DEVICE: a mount point of another disk
            }
            var partial = dst + ".partial";
            if (Directory.Exists(partial)) FsUtil.DeleteDir(partial);
            CopyTree(src, partial);
            File.WriteAllText(Path.Combine(partial, CopyMark), src);
            Directory.Move(partial, dst);
            FsUtil.DeleteDir(src);
            File.Delete(mark);
        }

        static void MoveAside(string path)
        {
            var to = Free(path + ".before-paranoidal", "", new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
            if (File.Exists(path)) File.Move(path, to); else Directory.Move(path, to);
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static void DeletePath(string path)
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            else if (Directory.Exists(path)) FsUtil.DeleteDir(path);
        }

        /// <summary>config.ini of the 2.0 game for the 2.1 one: its settings, the [path] of the 2.1 game (GameConfig.MergeForOtherInstall).</summary>
        static void WriteConfig(string oldCfg, string newCfg, string aside)
        {
            if (!File.Exists(oldCfg)) return;
            var text = File.ReadAllText(oldCfg, Encoding.UTF8);
            string newText = null;
            if (!string.IsNullOrEmpty(aside) && File.Exists(aside)) newText = File.ReadAllText(aside, Encoding.UTF8);
            Directory.CreateDirectory(Path.GetDirectoryName(newCfg));
            var tmp = newCfg + ".paranoidal-tmp";
            File.WriteAllText(tmp, GameConfig.MergeForOtherInstall(text, newText), new UTF8Encoding(false));
            if (File.Exists(newCfg)) { File.SetAttributes(newCfg, FileAttributes.Normal); File.Delete(newCfg); }
            File.Move(tmp, newCfg);
        }

        /// <summary>settings.ini of the moved launcher: the 2.1 line, the new game and mods folder, no pin, no 2.0 start history.</summary>
        static void WriteSettings(MigrationPlan p, string dataDir)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("line", Lines.L21.Id),
                new KeyValuePair<string, string>("factorio", p.NewDir),
                new KeyValuePair<string, string>("mods", p.NewMods),
                new KeyValuePair<string, string>("pin", "0"),
                new KeyValuePair<string, string>("launch-build", ""),
                new KeyValuePair<string, string>("launch-build-prev", ""),
                new KeyValuePair<string, string>("load-diag-key", ""),
                new KeyValuePair<string, string>("migrating", ""),
            };
            if (Same(dataDir, AppInfo.DataDir))
            {
                // the data of this very process (a game updated to 2.1 in place, or the new launcher continuing the move)
                foreach (var kv in values)
                    if (kv.Key == "factorio" || kv.Key == "mods") Settings.SetPath(kv.Key, kv.Value);
                    else Settings.Set(kv.Key, kv.Value);
                return;
            }
            Directory.CreateDirectory(dataDir);
            var ini = Path.Combine(dataDir, "settings.ini");
            var lines = File.Exists(ini) ? File.ReadAllLines(ini, Encoding.UTF8).ToList() : new List<string>();
            foreach (var kv in values)
            {
                int i = lines.FindIndex(l => { int e = l.IndexOf('='); return e > 0 && l.Substring(0, e).Trim().Equals(kv.Key, StringComparison.OrdinalIgnoreCase); });
                var line = kv.Key + "=" + kv.Value;
                if (i >= 0) lines[i] = line; else lines.Add(line);
            }
            File.WriteAllText(ini, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }

        /// <summary>The desktop shortcut (made by the move into the game folder) and the Start menu ones that exist.</summary>
        static void Repoint(MigrationPlan p, Action<string> log)
        {
            if (!File.Exists(p.NewExe)) return;
            try { Shortcuts.Desktop(p.NewExe); } catch (Exception ex) { log("ярлык на рабочем столе не создан: " + ex.Message); }
            try
            {
                var l = Path.Combine(Shortcuts.StartMenuDir, Shortcuts.LauncherLink);
                if (File.Exists(l)) Shortcuts.Create(l, p.NewExe, p.NewDir, AppInfo.Name);
                var g = Path.Combine(Shortcuts.StartMenuDir, Shortcuts.GuideLink);
                var guide = Path.Combine(p.NewDir, AppInfo.GuideFile);
                if (File.Exists(g) && File.Exists(guide)) Shortcuts.Create(g, guide, p.NewDir, "Guide to the Paranoidal");
            }
            catch (Exception ex) { log("ярлыки в меню «Пуск» не обновлены: " + ex.Message); }
        }

        // ------------------------------------------------------------------ the plan file

        static readonly string[] Keys = { "old-dir", "new-dir", "new-version", "old-wd", "new-wd", "old-cfg", "new-cfg", "old-exe", "old-data", "new-data", "mods", "new-mods" };

        public static void Save(MigrationPlan p)
        {
            var sb = new StringBuilder();
            sb.Append("# Paranoidal Launcher ").Append(AppInfo.Version).Append(": переезд на Paranoidal 2.1. Лаунчер удалит этот файл, когда переезд закончится.\r\n");
            string[] v = { p.OldDir, p.NewDir, p.NewVersion, p.OldWd, p.NewWd, p.OldCfg, p.NewCfg, p.OldExe, p.OldData, p.NewData, p.ModsDir, p.NewMods };
            for (int i = 0; i < Keys.Length; i++) sb.Append(Keys[i]).Append('=').Append(v[i] ?? "").Append("\r\n");
            sb.Append("in-place=").Append(p.InPlace ? "1" : "0").Append("\r\nshared-wd=").Append(p.SharedWd ? "1" : "0").Append("\r\nmods-stay=").Append(p.ModsStay ? "1" : "0").Append("\r\n");
            foreach (var s in p.Steps)
                sb.Append("step=").Append(s.Done ? "done" : "todo").Append('|').Append(s.Kind).Append('|').Append(s.Src ?? "").Append('|').Append(s.Dst ?? "").Append('|').Append(s.Extra ?? "").Append("\r\n");
            Directory.CreateDirectory(p.NewDir);
            var tmp = p.PlanFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(p.PlanFile)) File.Replace(tmp, p.PlanFile, null);
            else File.Move(tmp, p.PlanFile);
        }

        /// <summary>The plan of an unfinished move; null when there is no (readable) file.</summary>
        public static MigrationPlan Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var p = new MigrationPlan();
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (line.StartsWith("#")) continue;
                    int e = line.IndexOf('=');
                    if (e <= 0) continue;
                    var k = line.Substring(0, e);
                    var v = line.Substring(e + 1);
                    if (k == "step")
                    {
                        var f = v.Split('|');
                        if (f.Length < 5) continue;
                        p.Steps.Add(new MigStep { Done = f[0] == "done", Kind = f[1], Src = f[2], Dst = f[3].Length > 0 ? f[3] : null, Extra = f[4] });
                    }
                    else d[k] = v;
                }
                Func<string, string> g = k => { string v; return d.TryGetValue(k, out v) && v.Length > 0 ? v : null; };
                p.OldDir = g("old-dir"); p.NewDir = g("new-dir"); p.NewVersion = g("new-version"); p.OldWd = g("old-wd"); p.NewWd = g("new-wd");
                p.OldCfg = g("old-cfg"); p.NewCfg = g("new-cfg"); p.OldExe = g("old-exe"); p.OldData = g("old-data"); p.NewData = g("new-data");
                p.ModsDir = g("mods"); p.NewMods = g("new-mods");
                p.InPlace = g("in-place") == "1"; p.SharedWd = g("shared-wd") == "1"; p.ModsStay = g("mods-stay") == "1";
                return p.NewDir == null || p.OldExe == null || p.Steps.Count == 0 ? null : p;
            }
            catch { return null; }
        }

        /// <summary>The move is over: the plan file goes (the journal has every step).</summary>
        public static void Finish(MigrationPlan p)
        {
            try { if (File.Exists(p.PlanFile)) File.Delete(p.PlanFile); } catch { }
        }
    }
}
