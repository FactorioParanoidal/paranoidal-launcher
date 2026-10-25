using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Paranoidal
{
    /// <summary>
    /// What is in mods\ when the launcher does not know the installed version (a folder copied from a friend, unpacked
    /// by hand, a game folder shared over the LAN) or when the files differ from the version it installed.
    /// The modpack itself is not changed for this: the build is recognised by the content of its files.
    /// </summary>
    sealed class BuildFound
    {
        /// <summary>mods\zzzparanoidal\info.json exists.</summary>
        public bool Present;
        public string ZzzVersion, FactorioVersion;
        /// <summary>The Factorio 1.1 build (Beta 8 and older): not recognised further, the launcher only warns.</summary>
        public bool Legacy => FactorioVersion != null && !FactorioVersion.StartsWith("2.");
        /// <summary>Short content fingerprint of mods\ (git tree id of the folder; zip files and settings are left out).</summary>
        public string Fingerprint;
        /// <summary>A known build version whose files are all exactly here.</summary>
        public string Match, MatchDate;
        /// <summary>No exact match: the closest known version, how many of its mods differ here and which.</summary>
        public string Near, NearDate;
        public int NearDiff, NearTotal;
        public readonly List<string> NearMods = new List<string>();
        /// <summary>Number of build versions the folder was compared with.</summary>
        public int Known;

        public static BuildFound Scan(string modsDir)
        {
            var f = new BuildFound();
            try
            {
                var info = Path.Combine(modsDir, "zzzparanoidal", "info.json");
                if (!File.Exists(info)) return f;
                f.Present = true;
                var text = File.ReadAllText(info, Encoding.UTF8);
                var m = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) f.ZzzVersion = m.Groups[1].Value.Trim();
                m = Regex.Match(text, "\"factorio_version\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) f.FactorioVersion = m.Groups[1].Value.Trim();
            }
            catch { }
            return f;
        }
    }

    sealed partial class SyncEngine
    {
        /// <summary>
        /// Index of the files as they are in mods\ (not of a build version): .paranoidal-git\fp-index. It keeps the sizes
        /// and times of the files, so only changed files are hashed again next time; the trees written from it go to
        /// fp-objects and never mix with the downloaded build.
        /// </summary>
        static string FpIndex(SyncPaths p) => Path.Combine(p.GitDir, "fp-index");
        static string FpObjects(SyncPaths p) => Path.Combine(p.GitDir, "fp-objects");

        Dictionary<string, string> FpEnv(SyncPaths p) => new Dictionary<string, string>
        {
            { "GIT_INDEX_FILE", FpIndex(p) },
            { "GIT_OBJECT_DIRECTORY", FpObjects(p) },
            { "GIT_ALTERNATE_OBJECT_DIRECTORIES", Path.Combine(p.GitDir, "objects") },
        };

        /// <summary>
        /// Fingerprint of mods\ and an attempt to recognise its version among the versions known locally (the history the
        /// launcher downloaded before). Works without internet. Returns the top-level entries (name, object id) for the
        /// online attempt in CheckStatus, or null when the folder could not be read.
        /// </summary>
        List<KeyValuePair<string, string>> Recognize(SyncPaths p, StatusInfo st)
        {
            Stage("Узнаю сборку в папке mods…", "Recognising the build in the mods folder…");
            ui.Progress(null, Tr.T("Первый раз это может занять минуту: git считает хеши файлов.",
                "The first time this may take a minute: git hashes the files."));
            List<KeyValuePair<string, string>> local;
            string tree;
            try
            {
                Prepare(p);
                local = LocalTree(p, st.Installed, out tree);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ui.Log("Не удалось прочитать папку mods для распознавания сборки: " + ex.Message, LogKind.Warn);
                return null;
            }
            if (local == null) return null;
            st.Found.Fingerprint = tree.Substring(0, 8);
            ui.Log("Отпечаток файлов в папке mods: " + st.Found.Fingerprint + (st.Installed == null ? " (версия сборки не записана)" : ""), LogKind.Muted);
            MatchAndAdopt(p, st, local);
            return local;
        }

        List<KeyValuePair<string, string>> LocalTree(SyncPaths p, string seed, out string treeId)
        {
            treeId = null;
            Bind(p);
            Directory.CreateDirectory(Path.Combine(FpObjects(p), "pack"));
            Directory.CreateDirectory(Path.Combine(FpObjects(p), "info"));
            var env = FpEnv(p);
            if (!File.Exists(FpIndex(p)))
            {
                // the file modes (a few mods have executable files) come from a known version; without one every file is
                // 100644 and the comparison below ignores mode-only differences anyway
                if (seed == null) seed = LocalCommits().Select(c => c.Key).FirstOrDefault();
                if (seed != null) git.Run(new[] { "read-tree", seed }, null, null, env);
            }
            git.Run(new[] { "update-index", "-q", "--refresh" }, null, null, env);
            var exclude = Path.Combine(p.GitDir, "info", "exclude");
            var changed = git.Run(new[] { "ls-files", "-z", "-m", "-d", "--", "mods" }, null, null, env);
            var added = git.Run(new[] { "ls-files", "-z", "-o", "--exclude-from=" + exclude, "--", "mods" }, null, null, env);
            if (changed.Code != 0 || added.Code != 0) throw new SyncException("git ls-files: " + (changed.Code != 0 ? changed : added).LastErrorLine());
            var paths = new HashSet<string>(SplitZ(changed.Out), StringComparer.Ordinal);
            foreach (var a in SplitZ(added.Out)) paths.Add(a);
            if (paths.Count > 0)
            {
                ui.Progress(null, Tr.T("Файлов для проверки: ", "Files to check: ") + Fmt.Num(paths.Count));
                var input = Encoding.UTF8.GetBytes(string.Join("\0", paths) + "\0");
                var r = git.Run(new[] { "update-index", "--add", "--remove", "--info-only", "-z", "--stdin" }, input, null, env);
                if (r.Code != 0) throw new SyncException("git update-index: " + r.LastErrorLine());
            }
            var wt = git.Run(new[] { "write-tree", "--missing-ok" }, null, null, env);
            if (wt.Code != 0 || !Regex.IsMatch(wt.Text, "^[0-9a-f]{40}$")) throw new SyncException("git write-tree: " + wt.LastErrorLine());
            var mods = git.Run(new[] { "rev-parse", wt.Text + ":mods" }, null, null, env);
            if (mods.Code != 0) return null;   // nothing in mods\
            treeId = mods.Text;
            return TreeEntries(treeId, env);
        }

        /// <summary>Entries (name, object id) of one tree.</summary>
        List<KeyValuePair<string, string>> TreeEntries(string tree, IDictionary<string, string> env)
        {
            var res = new List<KeyValuePair<string, string>>();
            var r = git.Run(new[] { "ls-tree", "-z", tree }, null, null, env);
            if (r.Code != 0) return res;
            foreach (var e in SplitZ(r.Out))
            {
                // <mode> SP <type> SP <oid> TAB <name>
                int tab = e.IndexOf('\t');
                if (tab < 0) continue;
                var f = e.Substring(0, tab).Split(' ');
                if (f.Length >= 3) res.Add(new KeyValuePair<string, string>(e.Substring(tab + 1), f[2]));
            }
            return res;
        }

        /// <summary>Commits known locally (downloaded by updates and by FetchHistory) with their time, newest first.</summary>
        List<KeyValuePair<string, long>> LocalCommits()
        {
            var res = new List<KeyValuePair<string, long>>();
            if (!File.Exists(Path.Combine(git.GitDir ?? "", "HEAD"))) return res;
            var all = git.Run(new[] { "cat-file", "--batch-all-objects", "--batch-check=%(objecttype) %(objectname)" });
            if (all.Code != 0) return res;
            var commits = new List<string>();
            foreach (var line in Encoding.ASCII.GetString(all.Out).Split('\n'))
                if (line.StartsWith("commit ")) commits.Add(line.Substring(7).Trim());
            if (commits.Count == 0) return res;
            var log = git.Run(new[] { "log", "--no-walk=unsorted", "--stdin", "--format=%H %ct" }, Encoding.ASCII.GetBytes(string.Join("\n", commits) + "\n"));
            if (log.Code != 0) return res;
            foreach (var line in Encoding.ASCII.GetString(log.Out).Split('\n'))
            {
                var f = line.Trim().Split(' ');
                long t;
                if (f.Length == 2 && f[0].Length == 40 && long.TryParse(f[1], out t)) res.Add(new KeyValuePair<string, long>(f[0], t));
            }
            return res.OrderByDescending(x => x.Value).ToList();
        }

        /// <summary>
        /// Downloads the list of build versions of the last 120 days with their folder trees (commits and trees only, no
        /// files: ~2 MB, ~20 s the first time, then only the new ones). Returns false without a connection.
        /// </summary>
        bool FetchHistory(SyncPaths p)
        {
            Stage("Ищу версию сборки на GitHub…", "Looking the build version up on GitHub…");
            ui.Progress(null, Tr.T("Скачиваю список версий сборки за последние месяцы (без файлов).", "Downloading the list of build versions of the last months (no files)."));
            try { Prepare(p); }
            catch (Exception ex) { ui.Log("Не удалось подготовить папку .paranoidal-git: " + ex.Message, LogKind.Warn); return false; }
            var since = DateTime.UtcNow.AddDays(-120).ToString("yyyy-MM-dd");
            var r = git.Run(new[] { "fetch", "--progress", "--shallow-since=" + since, "--filter=blob:none", "--no-tags", "origin",
                "+refs/heads/" + AppInfo.Branch + ":refs/remotes/origin/" + AppInfo.Branch }, null, line =>
                {
                    var pr = GitProgress.Parse(line);
                    if (pr != null && pr.Phase == "Receiving objects")
                        ui.Progress(pr.Percent / 100.0, Tr.T("Объектов: ", "Objects: ") + Fmt.Num(pr.Done) + Tr.T(" из ", " of ") + Fmt.Num(pr.Total));
                });
            if (r.Code != 0) { ui.Log("Не удалось скачать список версий сборки: " + r.LastErrorLine(), LogKind.Warn); return false; }
            return true;
        }

        /// <summary>
        /// Compares the folder with every known build version. An exact match (all files of every mod of that version are
        /// here; mods not from the build do not matter) becomes the installed version; otherwise the closest one is noted.
        /// </summary>
        void MatchAndAdopt(SyncPaths p, StatusInfo st, List<KeyValuePair<string, string>> local)
        {
            Bind(p);
            var commits = LocalCommits();
            if (commits.Count == 0) return;
            var localMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in local) localMap[kv.Key] = kv.Value;

            // mods tree of every commit, then the content of each distinct tree
            var check = git.Run(new[] { "cat-file", "--batch-check" }, Encoding.ASCII.GetBytes(string.Join("\n", commits.Select(c => c.Key + ":mods")) + "\n"));
            if (check.Code != 0) return;
            var lines = Encoding.UTF8.GetString(check.Out).Split('\n');
            var modsTree = new Dictionary<string, string>();
            for (int i = 0; i < commits.Count && i < lines.Length; i++)
            {
                var f = lines[i].Trim().Split(' ');
                if (f.Length >= 2 && f[1] == "tree") modsTree[commits[i].Key] = f[0];
            }
            var trees = ReadTrees(modsTree.Values.Distinct().ToList());

            var scored = new List<Tuple<string, long, List<string>, int>>();
            foreach (var c in commits)
            {
                string t;
                Dictionary<string, string> entries;
                if (!modsTree.TryGetValue(c.Key, out t) || !trees.TryGetValue(t, out entries) || entries.Count == 0) continue;
                var differ = new List<string>();
                foreach (var e in entries)
                {
                    string mine;
                    if (!localMap.TryGetValue(e.Key, out mine) || mine != e.Value) differ.Add(e.Key);
                }
                scored.Add(Tuple.Create(c.Key, c.Value, differ, entries.Count));
            }
            if (scored.Count == 0) return;
            st.Found.Known = scored.Count;

            // trees differ also when only the executable bit of a file differs (the local one is guessed): look closer at the best ones
            var modeOnly = new Dictionary<string, bool>();
            var env = FpEnv(p);
            Func<string, string, bool> sameContent = (mine, theirs) =>
            {
                bool same;
                var key = mine + theirs;
                if (modeOnly.TryGetValue(key, out same)) return same;
                var d = git.Run(new[] { "diff-tree", "-r", "--raw", "--abbrev=40", "--no-renames", mine, theirs }, null, null, env);
                same = d.Code == 0;
                if (same)
                    foreach (var l in d.Text.Split('\n'))
                    {
                        var f = l.Trim().Split(' ');   // :oldmode newmode oldoid newoid status\tpath
                        if (f.Length >= 4 && f[2] != f[3]) { same = false; break; }
                    }
                modeOnly[key] = same;
                return same;
            };
            foreach (var s in scored.OrderBy(x => x.Item3.Count).ThenByDescending(x => x.Item2).Take(5).ToList())
            {
                if (s.Item3.Count > 5) continue;
                var entries = trees[modsTree[s.Item1]];
                s.Item3.RemoveAll(name => localMap.ContainsKey(name) && sameContent(localMap[name], entries[name]));
            }
            var best = scored.OrderBy(x => x.Item3.Count).ThenByDescending(x => x.Item2).First();
            if (best.Item3.Count == 0)
            {
                Adopt(p, st, best.Item1);
                return;
            }
            st.Found.Near = best.Item1;
            st.Found.NearDate = Fmt.Date(DateTimeOffset.FromUnixTimeSeconds(best.Item2).LocalDateTime);
            st.Found.NearDiff = best.Item3.Count;
            st.Found.NearTotal = best.Item4;
            st.Found.NearMods.AddRange(best.Item3.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            ui.Log("Сборка в папке mods не совпадает ни с одной известной версией (" + scored.Count + "). Ближе всего версия от " + st.Found.NearDate +
                " (" + AppInfo.Short(best.Item1) + "): отличаются моды " + best.Item3.Count + " из " + best.Item4 + ": " +
                string.Join(", ", st.Found.NearMods.Take(12)) + (best.Item3.Count > 12 ? " …" : "") + ".", LogKind.Warn);
        }

        /// <summary>Tree id -> (name -> object id) via one cat-file --batch.</summary>
        Dictionary<string, Dictionary<string, string>> ReadTrees(List<string> ids)
        {
            var res = new Dictionary<string, Dictionary<string, string>>();
            if (ids.Count == 0) return res;
            var r = git.Run(new[] { "cat-file", "--batch" }, Encoding.ASCII.GetBytes(string.Join("\n", ids) + "\n"));
            if (r.Code != 0) return res;
            var d = r.Out;
            int pos = 0;
            while (pos < d.Length)
            {
                int nl = Array.IndexOf(d, (byte)'\n', pos);
                if (nl < 0) break;
                var head = Encoding.ASCII.GetString(d, pos, nl - pos).Split(' ');
                pos = nl + 1;
                if (head.Length < 3 || head[1] != "tree") continue;   // "<id> missing"
                int size = int.Parse(head[2]);
                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                int end = pos + size, i = pos;
                while (i < end)
                {
                    int sp = Array.IndexOf(d, (byte)' ', i, end - i);
                    int nul = Array.IndexOf(d, (byte)0, sp, end - sp);
                    if (sp < 0 || nul < 0 || nul + 21 > end) break;
                    var name = Encoding.UTF8.GetString(d, sp + 1, nul - sp - 1);
                    var sb = new StringBuilder(40);
                    for (int k = 0; k < 20; k++) sb.Append(d[nul + 1 + k].ToString("x2"));
                    entries[name] = sb.ToString();
                    i = nul + 21;
                }
                res[head[0]] = entries;
                pos = end + 1;   // object content is followed by a newline
            }
            return res;
        }

        /// <summary>
        /// The files in mods\ are exactly build version `sha`: record it as installed. No file of mods\ is changed; the
        /// hashes of the recognition index are reused, so git does not read the files again.
        /// </summary>
        void Adopt(SyncPaths p, StatusInfo st, string sha)
        {
            Bind(p);
            var index = Path.Combine(p.GitDir, "index");
            try { if (File.Exists(FpIndex(p))) File.Copy(FpIndex(p), index, true); } catch { }
            ResetIndex(p, sha);
            File.WriteAllText(p.SyncedFile, sha + "\n");
            var before = st.Installed;
            if (before != null && before != sha) DropRollback(p);   // the files were changed outside the launcher
            st.AdoptedFrom = before;
            st.Adopted = before != sha;
            st.Installed = sha;
            st.InstalledDate = CommitDate(sha);
            st.Modified = LocalChanges(p, sha, null);
            st.Found.Match = sha;
            st.Found.MatchDate = st.InstalledDate;
            if (before == null)
                ui.Log("Сборка в папке mods опознана: версия от " + st.InstalledDate + " (" + AppInfo.Short(sha) + "). Файлы не менялись.", LogKind.Ok);
            else if (before != sha)
                ui.Log("Файлы сборки в папке mods заменены не через лаунчер: теперь это версия от " + st.InstalledDate + " (" + AppInfo.Short(sha) +
                    "), раньше была " + AppInfo.Short(before) + ". Лаунчер принял её как установленную; откат к прошлой версии сброшен.", LogKind.Warn);
        }
    }
}
