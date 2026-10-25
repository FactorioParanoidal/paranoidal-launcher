using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Paranoidal
{
    /// <summary>A problem to show to the player. Message is Russian (journal, sync log); Display follows the interface language.</summary>
    sealed class SyncException : Exception
    {
        readonly string en;
        public SyncException(string ru, string en = null) : base(ru) { this.en = en; }
        public string Display => Tr.En && en != null ? en : Message;
    }

    enum LogKind { Info, Ok, Warn, Error, Muted }

    interface ISyncUi
    {
        /// <summary>Journal line: always Russian.</summary>
        void Log(string text, LogKind kind = LogKind.Info);
        /// <summary>Current step: the interface shows the text in its language, the journal gets the Russian one.</summary>
        void Stage(string ru, string en);
        void Progress(double? fraction, string detail);
        void CanCancel(bool value);
        /// <summary>Mods/files in the mods folder that are not in the build: true = delete them, false = keep as is.</summary>
        bool ConfirmExtras(List<string> names);
        /// <summary>
        /// The first sync finds files of the build's mods that differ from the version being installed (a build copied
        /// by hand, an older or changed one): true = replace them with the files of that version.
        /// </summary>
        bool ConfirmOverwrite(int files, List<string> sample, BuildFound found);
    }

    sealed class ModChange
    {
        public string Mod, Kind, KindEn;
        public int Files;
        public string Display => Tr.T(Kind, KindEn ?? Kind);
    }

    sealed class UpdateResult
    {
        public string Old, New;
        public bool UpToDate, Declined;
        /// <summary>Every file in mods\ was read and compared byte for byte (not only the changed ones).</summary>
        public bool FullCheck;
        public int Downloaded;
        /// <summary>Rollback: files whose version of the target could not be restored (changed by hand before the update).</summary>
        public int NotRestored;
        /// <summary>Done by Rollback (saved files, no download).</summary>
        public bool Rollback;
        public List<ModChange> Mods = new List<ModChange>();
    }

    sealed class StatusInfo
    {
        public string Installed, InstalledDate, Remote;
        public List<CommitInfo> Commits;
        public bool CommitsAreNew, CommitsCached;
        /// <summary>GitHub could not be reached (no internet, blocked); Remote is null then.</summary>
        public bool Offline;
        /// <summary>Files of the installed build that differ from it on disk (-1: not checked).</summary>
        public int Modified = -1;
        /// <summary>What is in mods\ when the installed version is unknown or the files differ from it.</summary>
        public BuildFound Found;
        /// <summary>The installed version was recognised in this check (files match a known version).</summary>
        public string AdoptedFrom;
        public bool Adopted;
        /// <summary>One step back/forward without internet (Rollback.cs); null when there is none.</summary>
        public string RollbackTarget, RollbackDate;
        public bool RollbackIsNewer;
    }

    /// <summary>Layout on disk: &lt;root&gt;\mods is synced, &lt;root&gt;\.paranoidal-git holds the light git metadata.</summary>
    sealed class SyncPaths
    {
        public readonly string Mods, Root, GitDir, Blobs, SyncedFile, PlayedFile;

        public SyncPaths(string mods)
        {
            Mods = Path.GetFullPath(mods).TrimEnd('\\');
            Root = Path.GetDirectoryName(Mods);
            if (Root == null) throw new SyncException("Неверная папка модов: " + mods, "Invalid mods folder: " + mods);
            GitDir = Path.Combine(Root, ".paranoidal-git");
            Blobs = Path.Combine(GitDir, "blobs-tmp");
            SyncedFile = Path.Combine(GitDir, "synced-commit");
            PlayedFile = Path.Combine(GitDir, "played-commit");
        }

        public string ReadSynced() => ReadSha(SyncedFile);

        /// <summary>The build version the game was last started with by the launcher.</summary>
        public string ReadPlayed() => ReadSha(PlayedFile);

        static string ReadSha(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var s = File.ReadAllText(file).Trim();
                return Regex.IsMatch(s, "^[0-9a-f]{40}$") ? s : null;
            }
            catch { return null; }
        }

        /// <summary>Returns an error text (in the interface language) if this folder must not be synced.</summary>
        public static string Validate(string mods)
        {
            if (string.IsNullOrWhiteSpace(mods)) return Tr.T("Папка модов не выбрана.", "The mods folder is not selected.");
            string full;
            try { full = Path.GetFullPath(mods).TrimEnd('\\'); } catch { return Tr.T("Неверный путь: ", "Invalid path: ") + mods; }
            if (!string.Equals(Path.GetFileName(full), "mods", StringComparison.OrdinalIgnoreCase))
                return Tr.T("Папка модов должна называться mods: ", "The mods folder must be named mods: ") + full;
            var root = Path.GetDirectoryName(full);
            if (root == null) return Tr.T("Неверный путь: ", "Invalid path: ") + full;
            if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
                return Tr.T("Рядом с этой папкой mods лежит .git — это чья-то рабочая копия репозитория.\nУтилита не будет её трогать. Выберите другую папку.",
                    "There is a .git folder next to this mods folder: it is someone's working copy of the repository.\nThe launcher will not touch it. Choose another folder.");
            return null;
        }
    }

    sealed partial class SyncEngine
    {
        readonly GitRunner git;
        readonly ISyncUi ui;
        const string Zero = "0000000000000000000000000000000000000000";
        /// <summary>Test harness only: do not refuse while Factorio is running.</summary>
        public bool SkipRunningCheck;

        public SyncEngine(GitRunner git, ISyncUi ui)
        {
            this.git = git;
            this.ui = ui;
        }

        public void Cancel() => git.Cancel();

        void Bind(SyncPaths p)
        {
            git.GitDir = p.GitDir;
            git.WorkTree = p.Root;
            git.Env.Clear();
        }

        void Unbind()
        {
            git.GitDir = null;
            git.WorkTree = null;
            git.Env.Clear();
        }

        void Sleep(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { git.ThrowIfCancelled(); Thread.Sleep(100); }
        }

        void Stage(string ru, string en) => ui.Stage(ru, en);

        void RefuseWhileRunning(string ru, string en)
        {
            if (!SkipRunningCheck && Process.GetProcessesByName("factorio").Length > 0)
                throw new SyncException("Factorio запущена. Закройте игру и запустите " + ru + " снова.", "Factorio is running. Close the game and start " + en + " again.");
        }

        /// <summary>
        /// Mixed reset of the index to `sha` (files are not touched). In this partial clone git reads the object of every
        /// index entry whose id changes, and objects of files are not kept: when one is missing, the index is dropped and
        /// built again from `sha` (every entry is new then, so no object is read; git hashes the files on the next refresh).
        /// </summary>
        void ResetIndex(SyncPaths p, string sha)
        {
            var r = git.Run(new[] { "reset", "-q", sha });
            if (r.Code == 0) return;
            var index = Path.Combine(p.GitDir, "index");
            if (File.Exists(index)) { File.SetAttributes(index, FileAttributes.Normal); File.Delete(index); }
            git.Check("git reset", "reset", "-q", sha);
        }

        bool CommitExists(string sha) => sha != null && git.Run(new[] { "cat-file", "-e", sha + "^{commit}" }).Code == 0;

        // ------------------------------------------------------------------ status

        /// <summary>A git network error that means "no connection" (not "no such branch"): retrying at once does not help.</summary>
        internal static bool IsNetworkError(string err) =>
            err != null && Regex.IsMatch(err, "unable to access|Could not resolve|Failed to connect|Couldn't connect|timed out|Connection (refused|reset|was reset)|Recv failure|SSL|schannel|proxy|getaddrinfo", RegexOptions.IgnoreCase);

        /// <summary>
        /// Latest commit of the branch, or null without a connection (Offline is set then). Gives up fast: one attempt
        /// limited to 20 s, a second one only for errors that are not about the connection.
        /// </summary>
        public string RemoteHead(out bool offline, out string error)
        {
            Unbind();
            offline = false;
            error = null;
            for (int attempt = 1; ; attempt++)
            {
                var h = new RunHandle();
                GitResult r;
                using (new Timer(_ => h.Kill(), null, 20000, Timeout.Infinite))
                    r = git.Run(new[] { "ls-remote", AppInfo.RepoUrl, "refs/heads/" + AppInfo.Branch }, null, null, null, h);
                if (r.Code == 0)
                {
                    var t = r.Text;
                    if (t.Length >= 40 && Regex.IsMatch(t.Substring(0, 40), "^[0-9a-f]{40}$")) return t.Substring(0, 40);
                    error = "ветка " + AppInfo.Branch + " не найдена на GitHub";
                    return null;
                }
                error = h.Killed ? "GitHub не ответил за 20 с" : r.LastErrorLine();
                if (h.Killed || IsNetworkError(r.Err) || attempt >= 2) { offline = h.Killed || IsNetworkError(r.Err); return null; }
                Sleep(2000);
            }
        }

        public StatusInfo CheckStatus(string modsDir)
        {
            git.ResetCancel();
            var st = new StatusInfo();
            SyncPaths p = null;
            List<KeyValuePair<string, string>> local = null;
            if (modsDir != null && SyncPaths.Validate(modsDir) == null)
            {
                p = new SyncPaths(modsDir);
                var synced = p.ReadSynced();
                if (synced != null && File.Exists(Path.Combine(p.GitDir, "HEAD")))
                {
                    Bind(p);
                    if (CommitExists(synced))
                    {
                        st.Installed = synced;
                        st.InstalledDate = CommitDate(synced);
                        Stage("Сверяю файлы сборки в папке mods…", "Checking the build files in the mods folder…");
                        ui.Progress(null, "");
                        st.Modified = LocalChanges(p, synced, null);
                        if (st.Modified > 0) ui.Log("Файлы сборки в папке mods отличаются от установленной версии: " + st.Modified + ".", LogKind.Warn);
                    }
                }
                // the version is unknown or the files were changed outside the launcher: recognise what is there
                if (st.Installed == null || st.Modified > 0)
                {
                    st.Found = BuildFound.Scan(p.Mods);
                    if (st.Found.Present && !st.Found.Legacy) local = Recognize(p, st);
                }
            }

            Stage("Проверяю обновления на GitHub…", "Checking GitHub for updates…");
            ui.Progress(null, "");
            bool offline;
            string err;
            st.Remote = RemoteHead(out offline, out err);
            st.Offline = st.Remote == null && offline;
            if (st.Remote == null) ui.Log((offline ? "Нет связи с GitHub: " : "Не удалось проверить обновления: ") + err + ".", LogKind.Warn);

            // still unknown: compare with the versions of the last months on GitHub (only trees, a few MB)
            if (st.Remote != null && local != null && st.Found != null && st.Found.Match == null)
            {
                Bind(p);
                if (FetchHistory(p)) MatchAndAdopt(p, st, local);
            }

            if (p != null && st.Installed != null) { Bind(p); ReadRollback(p, st); }

            if (st.Remote != null)
            {
                try
                {
                    if (st.Installed != null && st.Installed != st.Remote)
                    {
                        try { st.Commits = GitHubApi.Compare(st.Installed, st.Remote); st.CommitsAreNew = true; }
                        catch (Exception ex) { ui.Log("Не удалось сравнить версии через GitHub API: " + ex.Message, LogKind.Muted); }
                    }
                    if (st.Commits == null)
                    {
                        st.Commits = GitHubApi.Recent(AppInfo.Branch, 20);
                        CommitsCache.Save(st.Commits);
                    }
                }
                catch (Exception ex) { ui.Log("Список изменений недоступен: " + ex.Message, LogKind.Muted); }
            }
            if (st.Commits == null)
            {
                st.Commits = CommitsCache.Load();
                st.CommitsCached = st.Commits != null;
            }
            return st;
        }

        string CommitDate(string sha)
        {
            var r = git.Run(new[] { "show", "-s", "--format=%ad", "--date=format:%d.%m.%Y %H:%M", sha });
            return r.Code == 0 && r.Text.Length > 0 ? r.Text : null;
        }

        long CommitTime(string sha)
        {
            var r = git.Run(new[] { "show", "-s", "--format=%ct", sha });
            long t;
            return r.Code == 0 && long.TryParse(r.Text, out t) ? t : 0;
        }

        /// <summary>
        /// Build files in mods\ that differ from commit `sha` (changed, missing, extra files inside the build's mod folders).
        /// Mods that are not in the build are not counted. `sample` gets a few paths.
        /// </summary>
        int LocalChanges(SyncPaths p, string sha, List<string> sample, int cap = 20)
        {
            var build = TopLevel(sha);
            if (build.Count == 0 || !File.Exists(Path.Combine(p.GitDir, "index"))) return -1;
            git.Run(new[] { "update-index", "-q", "--refresh" });
            var r = git.Run(new[] { "status", "--porcelain=v1", "-z", "-uall", "--no-renames", "--", "mods" });
            if (r.Code != 0) return -1;
            int n = 0;
            foreach (var e in SplitZ(r.Out))
            {
                if (e.Length < 4) continue;
                var rel = e.Substring(3);
                if (rel.StartsWith("mods/")) rel = rel.Substring(5);
                if (!build.Contains(rel.Split('/')[0])) continue;
                n++;
                if (sample != null && sample.Count < cap) sample.Add(rel);
            }
            return n;
        }

        /// <summary>Build files that differ from the installed version (for the report); null when nothing is installed.</summary>
        public List<string> ChangedBuildFiles(SyncPaths p)
        {
            var synced = p.ReadSynced();
            if (synced == null || !File.Exists(Path.Combine(p.GitDir, "HEAD"))) return null;
            git.ResetCancel();
            Bind(p);
            var list = new List<string>();
            return LocalChanges(p, synced, list, int.MaxValue) < 0 ? null : list;
        }

        /// <summary>Remembers the build version the game is started with (rollback goes back to the last played one).</summary>
        public void MarkPlayed(SyncPaths p, string sha)
        {
            if (sha == null || !Directory.Exists(p.GitDir)) return;
            try { File.WriteAllText(p.PlayedFile, sha + "\n"); } catch { }
        }

        // ------------------------------------------------------------------ update

        /// <summary>
        /// verify = false: update mods\ to the latest build («ОБНОВИТЬ»), or to `target` (a commit id: the host's version).
        /// verify = true: «Проверить файлы» — read every file of the installed build again, compare it byte for byte
        /// and restore what differs; the installed version stays (it is the latest one only if nothing is installed yet).
        /// </summary>
        public UpdateResult Update(SyncPaths p, bool verify = false, string target = null)
        {
            git.ResetCancel();
            ui.CanCancel(true);
            RefuseWhileRunning(verify ? "проверку" : "обновление", verify ? "the check" : "the update");

            Stage("Подготовка…", "Preparing…");
            ui.Progress(null, p.Mods);
            Prepare(p);
            ModList.EnsureDlc(p.Mods, (t, k) => ui.Log(t, k));

            var OLD = p.ReadSynced();
            if (OLD != null && !CommitExists(OLD)) OLD = null;

            string NEW;
            if (verify && OLD != null)
            {
                // the list of files of the installed version is already here: nothing to ask GitHub unless a file is broken
                NEW = OLD;
            }
            else
            {
                Stage("Получаю с GitHub список файлов сборки…", "Getting the list of build files from GitHub…");
                FetchWithRetry(new[] { "fetch", "--progress", "--depth", "1", "--filter=blob:none", "--no-tags", "origin", target ?? AppInfo.Branch },
                    "Не удалось получить список файлов с GitHub", "Could not get the file list from GitHub");
                NEW = git.Check("git rev-parse", "rev-parse", "FETCH_HEAD").Text;
                if (!Regex.IsMatch(NEW, "^[0-9a-f]{40}$")) throw new SyncException("Не удалось определить коммит сборки.", "Could not determine the build commit.");
            }

            // mods that are not in the build: ask before anything is downloaded
            HandleExtras(p, TopLevel(NEW));

            // Full byte-for-byte check: first sync, «Проверить файлы», and a missing index (e.g. a check interrupted right
            // after the index was dropped; without an index every file would look deleted and be downloaded again).
            // Normally git trusts the cached size and time of a file and does not read it, so a file changed without
            // changing them (copied over with the original time, damaged on disk) stays unseen until «Проверить файлы».
            var indexFile = Path.Combine(p.GitDir, "index");
            bool fullCheck = OLD == null || verify || !File.Exists(indexFile);
            if (OLD == null)
            {
                Stage("Первая синхронизация: сверяю файлы в папке mods…", "First sync: checking the files in the mods folder…");
                ui.Progress(null, Tr.T("Git считает хеши файлов, которые уже лежат в папке. Это может занять пару минут.",
                    "Git hashes the files already in the folder. This may take a couple of minutes."));
                // the recognition index (Recognize.cs) already knows the hashes of these files: git keeps them for equal files
                if (!File.Exists(indexFile) && File.Exists(FpIndex(p))) { try { File.Copy(FpIndex(p), indexFile); } catch { } }
                ResetIndex(p, NEW);
            }
            else if (fullCheck)
            {
                if (verify) Stage("Проверяю все файлы сборки побайтно…", "Checking every build file byte for byte…");
                else Stage("Служебный индекс не найден: сверяю все файлы заново…", "The index is missing: checking every file again…");
                ui.Progress(null, Tr.T("Git заново читает и сравнивает все файлы в папке mods. Это может занять пару минут.",
                    "Git reads and compares every file in the mods folder again. This may take a couple of minutes."));
                // a new index has no cached sizes and times, so git reads every file again
                if (File.Exists(indexFile)) { File.SetAttributes(indexFile, FileAttributes.Normal); File.Delete(indexFile); }
                git.Check("git reset", "reset", "-q", NEW);
            }
            else
            {
                Stage("Сверяю файлы в папке mods…", "Checking the files in the mods folder…");
                ui.Progress(null, "");
            }
            git.Run(new[] { "update-index", "-q", "--refresh" });

            var st = git.Check("git status", "status", "--porcelain=v1", "-z", "-uall", "--no-renames", "--", "mods");
            var diffs = new List<string>();
            var localEdits = new HashSet<string>(StringComparer.Ordinal);   // build files that differ from OLD (paths "mods/…")
            int differing = 0;
            var sample = new List<string>();
            foreach (var e in SplitZ(st.Out))
            {
                if (e.Length < 4) continue;
                char x = e[0], y = e[1];
                var path = e.Substring(3);
                if ((OLD == null || (fullCheck && OLD != NEW)) && x == ' ' && y == 'D') continue;   // not downloaded yet, not a local edit
                localEdits.Add(path);
                var rel = path.StartsWith("mods/") ? path.Substring(5) : path;
                if (x == '?') diffs.Add("лишний, будет удалён:  " + rel);
                else if (y == 'D' || x == 'D') diffs.Add("удалён, вернётся:  " + rel);
                else { diffs.Add("изменён:  " + rel); differing++; if (sample.Count < 30) sample.Add(rel); }
            }

            // first sync over files that are already there (a build copied by hand, an older or changed one): ask first
            if (OLD == null && differing > 0 && !verify)
            {
                var found = BuildFound.Scan(p.Mods);
                if (!ui.ConfirmOverwrite(differing, sample, found))
                {
                    // nothing was changed in mods\: forget the index, the next attempt starts from scratch
                    try { File.Delete(indexFile); } catch { }
                    return new UpdateResult { Old = OLD, New = NEW, Declined = true };
                }
            }

            var result = new UpdateResult { Old = OLD, New = NEW, FullCheck = fullCheck };
            if (OLD == NEW && diffs.Count == 0)
            {
                CleanupBlobs(p);
                result.UpToDate = true;
                if (fullCheck) WriteSyncLog(p, OLD, NEW, result);
                return result;
            }

            // files of the build's own mods are restored without questions
            if (diffs.Count > 0) ui.Log("Будут заменены версией сборки: " + Fmt.Num(diffs.Count) + " файл(ов)", LogKind.Muted);

            // which blobs of the new build are missing locally
            var di = git.Check("git diff-index", "diff-index", "--raw", "-z", "--no-renames", "--abbrev=40", NEW, "--", "mods");
            var need = new List<string>();
            var needSet = new HashSet<string>();
            var repairedPaths = new List<string>();
            var parts = SplitZ(di.Out);
            for (int i = 0; i + 1 < parts.Count; i += 2)
            {
                var f = parts[i].Split(' ');
                if (f.Length < 5) continue;
                var sha = f[2];
                if (sha == Zero) continue;
                repairedPaths.Add(parts[i + 1]);
                if (needSet.Add(sha)) need.Add(sha);
            }

            Directory.CreateDirectory(Path.Combine(p.Blobs, "pack"));
            Directory.CreateDirectory(Path.Combine(p.Blobs, "info"));
            git.Env["GIT_OBJECT_DIRECTORY"] = p.Blobs;
            git.Env["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(p.GitDir, "objects");
            ConsolidateBlobs(p);   // packs left by an interrupted run count as already downloaded

            var missing = need.Count == 0 ? new List<string>() : MissingObjects(need);
            if (need.Count > missing.Count)
                ui.Log("Уже скачано в прошлый раз: " + Fmt.Num(need.Count - missing.Count) + " файлов, докачиваю остальное.", LogKind.Muted);
            Download(p, NEW, missing, need);
            result.Downloaded = need.Count;

            // the files this version change replaces are kept for a rollback without internet (Rollback.cs)
            RollbackSlot slot = null;
            if (!verify && OLD != null && OLD != NEW && !fullCheck)
            {
                git.Env.Remove("GIT_OBJECT_DIRECTORY");
                git.Env.Remove("GIT_ALTERNATE_OBJECT_DIRECTORIES");
                slot = SaveForRollback(p, OLD, NEW, localEdits);
                git.Env["GIT_OBJECT_DIRECTORY"] = p.Blobs;
                git.Env["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(p.GitDir, "objects");
            }

            ui.CanCancel(false);
            Stage("Раскладываю файлы в папке mods…", "Writing the files into the mods folder…");
            ui.Progress(null, Tr.T("Не закрывайте программу.", "Do not close the launcher."));
            git.Check("git reset --hard", "reset", "-q", "--hard", NEW);
            git.Env.Remove("GIT_OBJECT_DIRECTORY");
            git.Env.Remove("GIT_ALTERNATE_OBJECT_DIRECTORIES");
            var clean = git.Check("git clean", "clean", "-fd", "--", "mods");
            File.WriteAllText(p.SyncedFile, NEW + "\n");
            if (slot != null) CommitRollback(p, slot);
            else if (!verify && OLD != NEW) DropRollback(p);
            CleanupBlobs(p);

            // summary
            if (OLD != null && OLD != NEW) result.Mods = SummaryBetween(OLD, NEW);
            else
            {
                var byMod = new SortedDictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in repairedPaths) Count(byMod, path, 0);
                foreach (var line in clean.Text.Split('\n'))
                {
                    var l = line.Trim();
                    if (l.StartsWith("Removing ")) Count(byMod, l.Substring(9), 1);
                }
                foreach (var kv in byMod)
                {
                    if (kv.Value[0] > 0) result.Mods.Add(new ModChange
                    {
                        Mod = kv.Key, Files = kv.Value[0],
                        Kind = OLD == null ? "скачан / исправлен" : "исправлен", KindEn = OLD == null ? "downloaded / fixed" : "fixed",
                    });
                    if (kv.Value[1] > 0) result.Mods.Add(new ModChange { Mod = kv.Key, Kind = "удалены лишние файлы", KindEn = "extra files removed", Files = kv.Value[1] });
                }
            }
            WriteSyncLog(p, OLD, NEW, result);
            return result;
        }

        static void Count(SortedDictionary<string, int[]> d, string path, int idx)
        {
            var rel = path.Replace('\\', '/');
            if (rel.StartsWith("mods/")) rel = rel.Substring(5);
            var mod = rel.Split('/')[0];
            int[] c;
            if (!d.TryGetValue(mod, out c)) d[mod] = c = new int[2];
            c[idx]++;
        }

        void Prepare(SyncPaths p)
        {
            Directory.CreateDirectory(p.Mods);
            Bind(p);
            if (File.Exists(Path.Combine(p.GitDir, "HEAD")))
            {
                var pc = git.Run(new[] { "config", "--get", "extensions.partialClone" });
                if (pc.Code != 0 || pc.Text.Length == 0)
                {
                    ui.Log("Удаляю старую полную копию репозитория от прошлой версии утилиты (≈1,2 ГБ)…", LogKind.Warn);
                    FsUtil.DeleteDir(p.GitDir);
                }
            }
            if (!File.Exists(Path.Combine(p.GitDir, "HEAD")))
            {
                git.Check("git init", "init", "-q", "-b", AppInfo.Branch);
                try { var d = new DirectoryInfo(p.GitDir); d.Attributes |= FileAttributes.Hidden; } catch { }
            }
            var cfg = new[]
            {
                "core.autocrlf", "false",
                "core.longpaths", "true",
                "core.sparseCheckout", "true",
                "core.sparseCheckoutCone", "false",
                "core.logAllRefUpdates", "false",
                "core.quotepath", "off",
                "remote.origin.url", AppInfo.RepoUrl,
                "remote.origin.fetch", "+refs/heads/" + AppInfo.Branch + ":refs/remotes/origin/" + AppInfo.Branch,
                "remote.origin.promisor", "true",
                "remote.origin.partialclonefilter", "blob:none",
                "extensions.partialClone", "origin",
                "gc.auto", "0",
                "maintenance.auto", "false",
                "diff.renames", "false",
                "status.renames", "false",
                // keep fetched objects as packs (fast, movable) and let curl drop a dead connection itself
                "transfer.unpackLimit", "1",
                "http.lowSpeedLimit", "1024",
                "http.lowSpeedTime", "60",
                "core.fsync", "none",
            };
            for (int i = 0; i < cfg.Length; i += 2)
                git.Check("git config", "config", "--replace-all", cfg[i], cfg[i + 1]);

            var info = Path.Combine(p.GitDir, "info");
            Directory.CreateDirectory(info);
            var utf8 = new UTF8Encoding(false);
            // only mods/ is checked out; the rest of the parent folder is invisible to git
            File.WriteAllText(Path.Combine(info, "sparse-checkout"), "/mods/\n", utf8);
            // Compare files byte for byte. The repository's .gitattributes (text=auto eol=lf) makes git treat a CRLF copy
            // of a file as equal to the LF original, but Factorio compares mods byte for byte ("mod content differs" in
            // multiplayer). Blobs in the repository are LF already, so the files written to mods\ stay the same.
            File.WriteAllText(Path.Combine(info, "attributes"), "* -text\n", utf8);
            WriteExclude(p);

            // leftovers of an interrupted run
            foreach (var f in new[] { "shallow.lock", "index.lock", "config.lock" })
            {
                try { File.Delete(Path.Combine(p.GitDir, f)); } catch { }
            }
            foreach (var dir in new[] { Path.Combine(p.GitDir, "objects", "pack"), Path.Combine(p.Blobs, "pack") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "tmp_*"))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------ extra mods (not in the build)

        static string KeptFile(SyncPaths p) => Path.Combine(p.GitDir, "kept-extras.txt");

        /// <summary>Top-level folders and zip files in mods\ that are not part of the installed build (null: not installed).</summary>
        public List<string> ForeignEntries(SyncPaths p)
        {
            var synced = p.ReadSynced();
            if (synced == null || !File.Exists(Path.Combine(p.GitDir, "HEAD")) || !Directory.Exists(p.Mods)) return null;
            git.ResetCancel();
            Bind(p);
            var build = TopLevel(synced);
            if (build.Count == 0) return null;
            var res = new List<string>();
            foreach (var full in Directory.GetFileSystemEntries(p.Mods))
            {
                var name = Path.GetFileName(full);
                if (build.Contains(name)) continue;
                if (Directory.Exists(full) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) res.Add(full);
            }
            return res;
        }

        static List<string> ReadKept(SyncPaths p)
        {
            try { if (File.Exists(KeptFile(p))) return File.ReadAllLines(KeptFile(p), Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList(); }
            catch { }
            return new List<string>();
        }

        static string IgnoreEscape(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name) { if ("*?[]\\!# ".IndexOf(c) >= 0) sb.Append('\\'); sb.Append(c); }
            return sb.ToString();
        }

        /// <summary>Settings, zip mods and the mods the player chose to keep are never touched by git.</summary>
        static void WriteExclude(SyncPaths p)
        {
            var sb = new StringBuilder("/*\n!/mods/\n/mods/mod-list.json\n/mods/mod-settings.dat\n/mods/*.zip\n");
            foreach (var k in ReadKept(p)) sb.Append("/mods/").Append(IgnoreEscape(k)).Append('\n');
            var info = Path.Combine(p.GitDir, "info");
            Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, "exclude"), sb.ToString(), new UTF8Encoding(false));
        }

        static readonly Regex ZipName = new Regex(@"^(?<n>.+?)(_\d+\.\d+\.\d+)?\.zip$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Top-level entries of mods\ that are not in the build. A zip of a mod that is in the build is removed without
        /// questions (Factorio would see the mod twice); the rest is offered for deletion, refused ones are kept as is.
        /// </summary>
        void HandleExtras(SyncPaths p, HashSet<string> buildMods)
        {
            if (buildMods.Count == 0 || !Directory.Exists(p.Mods)) return;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ask = new List<string>();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = ReadKept(p);
            var keptSet = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
            foreach (var full in Directory.GetFileSystemEntries(p.Mods))
            {
                var name = Path.GetFileName(full);
                present.Add(name);
                if (name.Equals("mod-list.json", StringComparison.OrdinalIgnoreCase) || name.Equals("mod-settings.dat", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mod-list.json.", StringComparison.OrdinalIgnoreCase)) continue;
                if (buildMods.Contains(name)) continue;
                bool isDir = Directory.Exists(full);
                if (!isDir)
                {
                    var m = ZipName.Match(name);
                    if (m.Success && buildMods.Contains(m.Groups["n"].Value))
                    {
                        try { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); ui.Log("Удалён " + name + ": этот мод уже есть в сборке.", LogKind.Warn); }
                        catch (Exception ex) { ui.Log("Не удалось удалить " + name + ": " + ex.Message, LogKind.Warn); }
                        continue;
                    }
                }
                if (keptSet.Contains(name)) continue;
                ask.Add(name);
                if (isDir) dirs.Add(name);
            }
            kept.RemoveAll(k => !present.Contains(k));

            if (ask.Count > 0)
            {
                ask.Sort(StringComparer.OrdinalIgnoreCase);
                if (ui.ConfirmExtras(ask.Select(n => dirs.Contains(n) ? n + "\\" : n).ToList()))
                {
                    foreach (var name in ask)
                    {
                        var full = Path.Combine(p.Mods, name);
                        try
                        {
                            if (dirs.Contains(name)) FsUtil.DeleteDir(full);
                            else { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); }
                        }
                        catch (Exception ex) { ui.Log("Не удалось удалить " + name + ": " + ex.Message, LogKind.Warn); }
                    }
                    ui.Log("Удалено лишних модов и файлов: " + ask.Count, LogKind.Ok);
                }
                else
                {
                    kept.AddRange(ask);
                    ui.Log("Оставлены как есть: " + string.Join(", ", ask), LogKind.Muted);
                }
            }
            try
            {
                Directory.CreateDirectory(p.GitDir);
                File.WriteAllLines(KeptFile(p), kept, new UTF8Encoding(false));
            }
            catch { }
            if (File.Exists(Path.Combine(p.GitDir, "HEAD"))) WriteExclude(p);
        }

        /// <summary>Before starting the game: DLC states in mod-list.json, and the question about extra mods.</summary>
        public void PrepareForPlay(SyncPaths p)
        {
            git.ResetCancel();
            ui.CanCancel(false);
            ModList.EnsureDlc(p.Mods, (t, k) => ui.Log(t, k));
            var synced = p.ReadSynced();
            if (synced == null || !File.Exists(Path.Combine(p.GitDir, "HEAD"))) return;
            Bind(p);
            HandleExtras(p, TopLevel(synced));
        }

        void FetchWithRetry(string[] args, string failRu, string failEn)
        {
            var delays = new[] { 3, 10, 30 };
            for (int attempt = 0; ; attempt++)
            {
                var r = git.Run(args, null, line =>
                {
                    var pr = GitProgress.Parse(line);
                    if (pr != null && pr.Phase == "Receiving objects")
                        ui.Progress(pr.Percent / 100.0, Tr.T("Объектов: ", "Objects: ") + Fmt.Num(pr.Done) + Tr.T(" из ", " of ") + Fmt.Num(pr.Total) + (pr.Bytes >= 0 ? " · " + Fmt.Size(pr.Bytes) : ""));
                });
                if (r.Code == 0) return;
                // the computer is clearly offline: waiting 43 s for three more attempts helps nobody
                bool noHost = Regex.IsMatch(r.Err, "Could not resolve host|getaddrinfo", RegexOptions.IgnoreCase);
                if (attempt >= delays.Length || noHost)
                    throw new SyncException(failRu + ": " + r.LastErrorLine() + "\nФайлы модов НЕ изменены, игру можно запускать. Повторите позже.",
                        failEn + ": " + r.LastErrorLine() + "\nThe mod files were NOT changed, the game can be started. Try again later.");
                ui.Log("Сбой связи (" + r.LastErrorLine() + "), повтор через " + delays[attempt] + " с…", LogKind.Warn);
                Sleep(delays[attempt] * 1000);
            }
        }

        List<string> MissingObjects(List<string> need)
        {
            var r = git.Run(new[] { "cat-file", "--batch-check" }, Encoding.ASCII.GetBytes(string.Join("\n", need) + "\n"));
            if (r.Code != 0) return new List<string>(need);
            var res = new List<string>();
            foreach (var line in Encoding.ASCII.GetString(r.Out).Split('\n'))
            {
                var l = line.Trim();
                if (l.EndsWith(" missing")) res.Add(l.Substring(0, 40));
            }
            return res;
        }

        // ------------------------------------------------------------------ parallel download

        const int Workers = 4;                       // parallel git fetches
        const long BatchBytes = 24L * 1024 * 1024;   // ~24 MB per request: a dropped/slow request loses little
        const int BatchFiles = 400;
        const long UnknownSize = 64 * 1024;

        sealed class Batch
        {
            public List<string> Oids = new List<string>();
            public long Raw;
            public int Fails, Restarts;
        }

        sealed class Slot
        {
            public Batch B;
            public readonly RunHandle H = new RunHandle();
            public long Started, FirstByte = -1, LastByteAt, Bytes;
            public int Objects;
            public bool SlowKill;
            public readonly Queue<KeyValuePair<long, long>> Samples = new Queue<KeyValuePair<long, long>>();
        }

        /// <summary>Moves packs fetched by the workers (w0, w1…) into blobs-tmp; removes leftovers of interrupted fetches.</summary>
        static void ConsolidateBlobs(SyncPaths p)
        {
            var mainPack = Path.Combine(p.Blobs, "pack");
            Directory.CreateDirectory(mainPack);
            foreach (var f in Directory.GetFiles(mainPack, "tmp_*")) { try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { } }
            foreach (var w in Directory.GetDirectories(p.Blobs, "w*"))
            {
                var wp = Path.Combine(w, "pack");
                if (Directory.Exists(wp))
                {
                    // .idx last: git only sees a pack once its index is in place
                    foreach (var f in Directory.GetFiles(wp).OrderBy(f => f.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                    {
                        try
                        {
                            File.SetAttributes(f, FileAttributes.Normal);
                            var name = Path.GetFileName(f);
                            var dest = Path.Combine(mainPack, name);
                            if (name.StartsWith("tmp_") || File.Exists(dest)) File.Delete(f); else File.Move(f, dest);
                        }
                        catch { }
                    }
                }
                foreach (var d in Directory.GetDirectories(w))
                {
                    var n = Path.GetFileName(d);
                    if (n.Length != 2 || !Regex.IsMatch(n, "^[0-9a-f]{2}$")) continue;
                    var md = Path.Combine(p.Blobs, n);
                    Directory.CreateDirectory(md);
                    foreach (var f in Directory.GetFiles(d))
                    {
                        try { var dest = Path.Combine(md, Path.GetFileName(f)); if (File.Exists(dest)) File.Delete(f); else File.Move(f, dest); } catch { }
                    }
                }
                try { FsUtil.DeleteDir(w); } catch { }
            }
        }

        /// <summary>
        /// Downloads missing blobs with several parallel git fetches of ~24 MB each. A request that stalls or slows down
        /// sharply is killed and requested again (a fresh request runs at full speed). Finished requests stay on disk,
        /// so a cancelled/failed update continues where it stopped.
        /// </summary>
        void Download(SyncPaths p, string commit, List<string> missing, List<string> need)
        {
            if (missing.Count == 0) return;

            Dictionary<string, long> sizes = null;
            try
            {
                Stage("Получаю размеры файлов…", "Getting file sizes…");
                ui.Progress(null, "");
                sizes = GitHubApi.BlobSizes(commit);
            }
            catch (Exception ex) { ui.Log("Размеры файлов недоступны (" + ex.Message + "), порции будут примерными.", LogKind.Muted); }
            git.ThrowIfCancelled();
            Func<string, long> size = o => { long s; return sizes != null && sizes.TryGetValue(o, out s) ? s : UnknownSize; };

            var missSet = new HashSet<string>(missing);
            long totalRaw = 0, alreadyRaw = 0;
            foreach (var o in need) { long s = size(o); totalRaw += s; if (!missSet.Contains(o)) alreadyRaw += s; }
            int total = need.Count, already = need.Count - missing.Count;

            var queue = new LinkedList<Batch>();
            var cur = new Batch();
            foreach (var o in missing)
            {
                cur.Oids.Add(o);
                cur.Raw += size(o);
                if (cur.Raw >= BatchBytes || cur.Oids.Count >= BatchFiles) { queue.AddLast(cur); cur = new Batch(); }
            }
            if (cur.Oids.Count > 0) queue.AddLast(cur);

            Stage("Скачиваю файлы сборки: " + Fmt.Num(missing.Count) + " (≈" + Fmt.SizeRu(totalRaw - alreadyRaw) + ")",
                "Downloading build files: " + Fmt.Num(missing.Count) + " (≈" + Fmt.Size(totalRaw - alreadyRaw) + ")");
            var sync = new object();
            var clock = Stopwatch.StartNew();
            int nWorkers = Math.Min(Workers, queue.Count);
            var slots = new Slot[nWorkers];
            var recentRates = new List<double>();
            var aggSamples = new Queue<KeyValuePair<long, long>>();
            long recvTotal = 0, doneRecv = 0, doneRaw = 0;
            int doneFiles = 0, consecutiveFails = 0;
            bool stop = false;
            Exception fatal = null;
            string lastError = null;
            var objects = Path.Combine(p.GitDir, "objects");

            var threads = new List<Thread>();
            for (int wi = 0; wi < nWorkers; wi++)
            {
                int i = wi;
                var wdir = Path.Combine(p.Blobs, "w" + i);
                Directory.CreateDirectory(Path.Combine(wdir, "pack"));
                Directory.CreateDirectory(Path.Combine(wdir, "info"));
                var env = new Dictionary<string, string>
                {
                    { "GIT_OBJECT_DIRECTORY", wdir },
                    { "GIT_ALTERNATE_OBJECT_DIRECTORIES", objects },
                };
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        Batch b;
                        Slot slot;
                        lock (sync)
                        {
                            if (stop || queue.Count == 0) break;
                            b = queue.First.Value;
                            queue.RemoveFirst();
                            slot = new Slot { B = b, Started = clock.ElapsedMilliseconds };
                            slot.LastByteAt = slot.Started;
                            slots[i] = slot;
                        }
                        GitResult r;
                        try
                        {
                            r = git.Run(new[]
                                {
                                    "-c", "fetch.negotiationAlgorithm=noop", "-c", "fetch.unpackLimit=1",
                                    "fetch", "--progress", "--no-tags", "--no-write-fetch-head", "--no-auto-maintenance",
                                    "--filter=blob:none", "--stdin", "origin",
                                },
                                Encoding.ASCII.GetBytes(string.Join("\n", b.Oids) + "\n"),
                                line =>
                                {
                                    var pr = GitProgress.Parse(line);
                                    if (pr == null || pr.Phase != "Receiving objects") return;
                                    lock (sync)
                                    {
                                        long now = clock.ElapsedMilliseconds;
                                        if (pr.Bytes > slot.Bytes)
                                        {
                                            recvTotal += pr.Bytes - slot.Bytes;
                                            slot.Bytes = pr.Bytes;
                                            slot.LastByteAt = now;
                                            if (slot.FirstByte < 0) slot.FirstByte = now;
                                            slot.Samples.Enqueue(new KeyValuePair<long, long>(now, slot.Bytes));
                                            while (slot.Samples.Count > 2 && now - slot.Samples.Peek().Key > 12000) slot.Samples.Dequeue();
                                        }
                                        slot.Objects = Math.Min(pr.Done, b.Oids.Count);
                                    }
                                }, env, slot.H);
                        }
                        catch (OperationCanceledException) { lock (sync) { stop = true; slots[i] = null; } break; }
                        catch (Exception ex) { lock (sync) { if (fatal == null) fatal = ex; stop = true; slots[i] = null; } break; }

                        int wait = 0;
                        lock (sync)
                        {
                            slots[i] = null;
                            if (r.Code == 0)
                            {
                                doneFiles += b.Oids.Count;
                                doneRaw += b.Raw;
                                doneRecv += slot.Bytes;
                                consecutiveFails = 0;
                                long active = slot.FirstByte < 0 ? 0 : clock.ElapsedMilliseconds - slot.FirstByte;
                                if (active > 1500 && slot.Bytes > 512 * 1024)
                                {
                                    recentRates.Add(slot.Bytes * 1000.0 / active);
                                    if (recentRates.Count > 12) recentRates.RemoveAt(0);
                                }
                                continue;
                            }
                            if (slot.H.Killed)
                            {
                                // watchdog: slow or stalled request, ask again right away
                                b.Restarts++;
                                queue.AddFirst(b);
                                continue;
                            }
                            b.Fails++;
                            consecutiveFails++;
                            lastError = r.LastErrorLine();
                            // no connection at all (not a dropped one): three failures in a row are enough
                            bool dead = System.Text.RegularExpressions.Regex.IsMatch(r.Err, "Could not resolve host|Failed to connect|Could not connect|getaddrinfo");
                            if (b.Fails >= 6 || consecutiveFails >= 12 || (dead && consecutiveFails >= 3))
                            {
                                fatal = new SyncException("Связь с GitHub постоянно обрывается (" + lastError + ").\n" +
                                    "Уже скачано " + Fmt.Num(already + doneFiles) + " из " + Fmt.Num(total) + " файлов — это не пропадёт, следующий запуск продолжит с этого места.\n" +
                                    "Файлы модов НЕ изменены, игру можно запускать.",
                                    "The connection to GitHub keeps dropping (" + lastError + ").\n" +
                                    "Already downloaded " + Fmt.Num(already + doneFiles) + " of " + Fmt.Num(total) + " files: they are kept, the next start continues from here.\n" +
                                    "The mod files were NOT changed, the game can be started.");
                                stop = true;
                                break;
                            }
                            if (b.Fails >= 2 && b.Oids.Count > 1)
                            {
                                // a batch that keeps failing is split, so one bad request cannot block the rest
                                int half = b.Oids.Count / 2;
                                var b1 = new Batch { Oids = b.Oids.GetRange(0, half), Fails = b.Fails };
                                var b2 = new Batch { Oids = b.Oids.GetRange(half, b.Oids.Count - half), Fails = b.Fails };
                                foreach (var o in b1.Oids) b1.Raw += size(o);
                                b2.Raw = b.Raw - b1.Raw;
                                queue.AddLast(b1);
                                queue.AddLast(b2);
                            }
                            else queue.AddLast(b);
                            wait = new[] { 2, 5, 10, 20, 30 }[Math.Min(b.Fails - 1, 4)];
                        }
                        ui.Log("Обрыв загрузки (" + lastError + "), повтор через " + wait + " с.", LogKind.Warn);
                        try { Sleep(wait * 1000); }
                        catch (OperationCanceledException) { lock (sync) stop = true; break; }
                    }
                }) { IsBackground = true, Name = "fetch-" + i };
                threads.Add(t);
                t.Start();
            }

            // monitor: progress, speed, watchdog
            long lastUi = -1000;
            while (threads.Any(t => t.IsAlive))
            {
                Thread.Sleep(250);
                var toKill = new List<Slot>();
                string detail;
                double fraction;
                lock (sync)
                {
                    long now = clock.ElapsedMilliseconds;
                    aggSamples.Enqueue(new KeyValuePair<long, long>(now, recvTotal));
                    while (aggSamples.Count > 2 && now - aggSamples.Peek().Key > 8000) aggSamples.Dequeue();

                    double refRate = 0;
                    if (recentRates.Count >= 3) { var s = recentRates.OrderBy(x => x).ToList(); refRate = s[s.Count / 2]; }
                    int busy = slots.Count(s => s != null);

                    long inflightRecv = 0, inflightRaw = 0;
                    int inflightFiles = 0;
                    foreach (var s in slots)
                    {
                        if (s == null) continue;
                        inflightRecv += s.Bytes;
                        inflightFiles += s.Objects;
                        inflightRaw += s.B.Oids.Count == 0 ? 0 : (long)(s.B.Raw * (s.Objects / (double)s.B.Oids.Count));
                        if (s.H.Killed || stop) continue;

                        bool kill = false;
                        string why = null;
                        if (s.FirstByte < 0 && now - s.Started > 120000) { kill = true; why = "GitHub не начал отдавать данные"; }
                        else if (s.FirstByte >= 0 && now - s.LastByteAt > 45000) { kill = true; why = "загрузка зависла"; }
                        else if (refRate > 0 && s.B.Restarts < 3 && s.FirstByte >= 0 && now - s.FirstByte > 15000 && s.Samples.Count >= 2)
                        {
                            var first = s.Samples.Peek();
                            long dt = now - first.Key;
                            double rate = dt >= 8000 ? (s.Bytes - first.Value) * 1000.0 / dt : double.MaxValue;
                            long leftRaw = s.B.Oids.Count == 0 ? 0 : (long)(s.B.Raw * (1 - s.Objects / (double)s.B.Oids.Count));
                            // the request got much slower than usual and has a lot left: a fresh one will be faster
                            if (rate < refRate * 0.3 && leftRaw > 2 * 1024 * 1024)
                            {
                                kill = true;
                                why = "скорость упала до " + Fmt.SizeRu(rate) + "/с";
                            }
                        }
                        if (kill)
                        {
                            s.SlowKill = true;
                            toKill.Add(s);
                            ui.Log("Перезапрашиваю порцию: " + why + ".", LogKind.Muted);
                        }
                    }

                    var a = aggSamples.Peek();
                    double speed = now - a.Key > 800 ? (recvTotal - a.Value) * 1000.0 / (now - a.Key) : 0;
                    long rawProgress = alreadyRaw + doneRaw + inflightRaw;
                    fraction = totalRaw > 0 ? Math.Min(1, rawProgress / (double)totalRaw) : 0;
                    double ratio = doneRaw > 1024 * 1024 ? doneRecv / (double)doneRaw : 1.0;
                    long got = doneRecv + inflightRecv;
                    double left = Math.Max(0, totalRaw - rawProgress) * ratio;

                    var sb = new StringBuilder();
                    sb.Append(Tr.T("Файлы: ", "Files: ")).Append(Fmt.Num(Math.Min(total, already + doneFiles + inflightFiles))).Append(Tr.T(" из ", " of ")).Append(Fmt.Num(total));
                    sb.Append(Tr.T(" · скачано ", " · downloaded ")).Append(Fmt.Size(got)).Append(Tr.T(" из ~", " of ~")).Append(Fmt.Size(got + left));
                    if (speed > 1024)
                    {
                        sb.Append(" · ").Append(Fmt.Size(speed)).Append(Tr.T("/с", "/s"));
                        if (left > 0) sb.Append(Tr.T(" · осталось ", " · left ")).Append(Fmt.Duration(left / speed));
                    }
                    sb.Append(Tr.T(" · потоков: ", " · streams: ")).Append(busy);
                    detail = sb.ToString();
                    if (now - lastUi < 250) detail = null; else lastUi = now;
                }
                foreach (var s in toKill) s.H.Kill();
                if (detail != null) ui.Progress(fraction, detail);
            }
            foreach (var t in threads) t.Join();

            ConsolidateBlobs(p);
            if (git.Cancelled) throw new OperationCanceledException();
            if (fatal != null)
            {
                if (fatal is OperationCanceledException) throw new OperationCanceledException();
                throw fatal as SyncException ?? new SyncException(fatal.Message);
            }
            ui.Progress(1, null);
            ui.Log("Скачано файлов: " + Fmt.Num(missing.Count) + " (" + Fmt.SizeRu(doneRecv) + " за " + Fmt.Duration(clock.ElapsedMilliseconds / 1000.0, false).TrimStart('~') + ")", LogKind.Ok);
        }

        void CleanupBlobs(SyncPaths p)
        {
            try { FsUtil.DeleteDir(p.Blobs); } catch { }
        }

        List<ModChange> SummaryBetween(string oldSha, string newSha)
        {
            var list = new List<ModChange>();
            var oldMods = TopLevel(oldSha);
            var newMods = TopLevel(newSha);
            var r = git.Run(new[] { "diff", "--no-renames", "--name-status", "-z", oldSha, newSha, "--", "mods" });
            if (r.Code != 0) return list;
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var parts = SplitZ(r.Out);
            for (int i = 0; i + 1 < parts.Count; i += 2)
            {
                var rel = parts[i + 1];
                if (rel.StartsWith("mods/")) rel = rel.Substring(5);
                var mod = rel.Split('/')[0];
                int c;
                counts.TryGetValue(mod, out c);
                counts[mod] = c + 1;
            }
            foreach (var kv in counts)
            {
                bool added = !oldMods.Contains(kv.Key), removed = !newMods.Contains(kv.Key);
                list.Add(new ModChange
                {
                    Mod = kv.Key, Files = kv.Value,
                    Kind = added ? "новый мод" : removed ? "мод удалён" : "обновлён",
                    KindEn = added ? "new mod" : removed ? "mod removed" : "updated",
                });
            }
            return list;
        }

        HashSet<string> TopLevel(string sha)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var r = git.Run(new[] { "ls-tree", "-z", "--name-only", sha + ":mods" });
            if (r.Code == 0) foreach (var n in SplitZ(r.Out)) set.Add(n);
            return set;
        }

        void WriteSyncLog(SyncPaths p, string oldSha, string newSha, UpdateResult res)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var sb = new StringBuilder();
                sb.Append("===== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").Append(AppInfo.Branch).Append("  ")
                  .Append(oldSha == null ? "(первая синхронизация)" : AppInfo.Short(oldSha)).Append(" -> ").Append(AppInfo.Short(newSha))
                  .Append("  ").Append(p.Mods).Append("  [лаунчер ").Append(AppInfo.Version)
                  .Append(res.FullCheck ? ", полная проверка" : "").Append(res.Rollback ? ", откат без интернета" : "").Append("]\r\n");
                if (res.UpToDate) sb.Append("  все файлы совпадают со сборкой\r\n");
                if (oldSha != null && oldSha != newSha)
                {
                    var r = git.Run(new[] { "diff", "--no-renames", "--name-status", "-z", oldSha, newSha, "--", "mods" });
                    var parts = SplitZ(r.Out);
                    for (int i = 0; i + 1 < parts.Count; i += 2) sb.Append(parts[i]).Append('\t').Append(parts[i + 1]).Append("\r\n");
                }
                foreach (var m in res.Mods) sb.Append("  ").Append(m.Mod).Append(" — ").Append(m.Kind).Append(" (").Append(m.Files).Append(")\r\n");
                File.AppendAllText(Path.Combine(AppInfo.DataDir, "sync-log.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        static List<string> SplitZ(byte[] data)
        {
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0) continue;
                if (i > start) list.Add(Encoding.UTF8.GetString(data, start, i - start));
                start = i + 1;
            }
            if (start < data.Length)
            {
                var tail = Encoding.UTF8.GetString(data, start, data.Length - start).Trim();
                if (tail.Length > 0) list.Add(tail);
            }
            return list;
        }
    }
}
