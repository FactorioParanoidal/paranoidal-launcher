using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Paranoidal
{
    /// <summary>
    /// One step back without internet: the files a version change replaced or deleted, and the list of files it added.
    /// Not a copy of the whole build. Kept in .paranoidal-git\rollback\ (slot.txt + the saved files under mods\…).
    /// </summary>
    sealed class RollbackSlot
    {
        /// <summary>The version the slot leads to, and the version installed now (the slot is valid only for it).</summary>
        public string Target, Cur;
        /// <summary>Paths "mods/…": saved here with the target's content / to delete (not in the target) / could not be saved.</summary>
        public readonly List<string> Saved = new List<string>(), Added = new List<string>(), Missing = new List<string>();
        public string Dir;
        public long Bytes;
    }

    sealed partial class SyncEngine
    {
        static string RbDir(SyncPaths p) => Path.Combine(p.GitDir, "rollback");
        static string RbNewDir(SyncPaths p) => Path.Combine(p.GitDir, "rollback-new");
        static string SlotFile(string dir, string path) => Path.Combine(dir, path.Replace('/', '\\'));
        static string WorkFile(SyncPaths p, string path) => Path.Combine(p.Root, path.Replace('/', '\\'));

        static RollbackSlot ReadSlot(string dir)
        {
            try
            {
                var file = Path.Combine(dir, "slot.txt");
                if (!File.Exists(file)) return null;
                var s = new RollbackSlot { Dir = dir };
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int sp = line.IndexOf(' ');
                    if (sp <= 0) continue;
                    string k = line.Substring(0, sp), v = line.Substring(sp + 1);
                    switch (k)
                    {
                        case "target": s.Target = v.Trim(); break;
                        case "cur": s.Cur = v.Trim(); break;
                        case "saved": s.Saved.Add(v); break;
                        case "added": s.Added.Add(v); break;
                        case "missing": s.Missing.Add(v); break;
                    }
                }
                return s.Target != null && s.Cur != null ? s : null;
            }
            catch { return null; }
        }

        static void WriteSlot(RollbackSlot s, string dir)
        {
            var sb = new StringBuilder();
            sb.Append("# Paranoidal Launcher rollback: the files of version 'target' that version 'cur' replaced or deleted\n");
            sb.Append("target ").Append(s.Target).Append('\n').Append("cur ").Append(s.Cur).Append('\n');
            foreach (var x in s.Saved) sb.Append("saved ").Append(x).Append('\n');
            foreach (var x in s.Added) sb.Append("added ").Append(x).Append('\n');
            foreach (var x in s.Missing) sb.Append("missing ").Append(x).Append('\n');
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, "slot.txt.tmp");
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            var file = Path.Combine(dir, "slot.txt");
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        static void CopyFile(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (File.Exists(to)) File.SetAttributes(to, FileAttributes.Normal);
            File.Copy(from, to, true);
            File.SetAttributes(to, FileAttributes.Normal);
        }

        /// <summary>
        /// Before an update OLD → NEW: keeps the files of OLD that NEW replaces or deletes. The slot leads back to the version
        /// the game was last started with: if OLD was never played (two updates in a row), the slot keeps leading to the
        /// earlier version and only adds the files it does not have yet.
        /// </summary>
        RollbackSlot SaveForRollback(SyncPaths p, string OLD, string NEW, HashSet<string> localEdits)
        {
            try
            {
                var played = p.ReadPlayed();
                var slot = ReadSlot(RbDir(p));
                bool fresh = slot == null || slot.Cur != OLD || slot.Target == NEW || played == OLD || !CommitExists(slot.Target);
                if (fresh)
                {
                    if (Directory.Exists(RbNewDir(p))) FsUtil.DeleteDir(RbNewDir(p));
                    slot = new RollbackSlot { Target = OLD, Dir = RbNewDir(p) };
                }
                var known = new HashSet<string>(slot.Saved.Concat(slot.Added).Concat(slot.Missing), StringComparer.Ordinal);
                var r = git.Run(new[] { "diff", "--no-renames", "--name-status", "-z", OLD, NEW, "--", "mods" });
                if (r.Code != 0) { ui.Log("Откат к прошлой версии будет недоступен: " + r.LastErrorLine(), LogKind.Warn); return null; }
                Stage("Сохраняю файлы текущей версии для отката…", "Keeping the current files for a rollback…");
                ui.Progress(null, "");
                var parts = SplitZ(r.Out);
                for (int i = 0; i + 1 < parts.Count; i += 2)
                {
                    var status = parts[i];
                    var path = parts[i + 1];
                    if (!known.Add(path)) continue;
                    if (status.StartsWith("A")) { slot.Added.Add(path); continue; }
                    var full = WorkFile(p, path);
                    if (localEdits.Contains(path) || !File.Exists(full)) { slot.Missing.Add(path); continue; }
                    try
                    {
                        CopyFile(full, SlotFile(slot.Dir, path));
                        slot.Saved.Add(path);
                        slot.Bytes += new FileInfo(full).Length;
                    }
                    catch { slot.Missing.Add(path); }
                }
                slot.Cur = NEW;
                ui.Log((fresh ? "Для отката на версию " : "Откат по-прежнему ведёт на версию ") + AppInfo.Short(slot.Target) + ": сохранено файлов " +
                    Fmt.Num(slot.Saved.Count) + " (" + Fmt.SizeRu(slot.Bytes) + ")" + (slot.Missing.Count > 0 ? ", не сохранено (были изменены вручную) " + slot.Missing.Count : "") + ".", LogKind.Muted);
                return slot;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ui.Log("Откат к прошлой версии будет недоступен: " + ex.Message, LogKind.Warn);
                return null;
            }
        }

        /// <summary>After a successful update: the new slot replaces the old one.</summary>
        void CommitRollback(SyncPaths p, RollbackSlot slot)
        {
            try
            {
                if (string.Equals(slot.Dir, RbNewDir(p), StringComparison.OrdinalIgnoreCase))
                {
                    WriteSlot(slot, slot.Dir);
                    if (Directory.Exists(RbDir(p))) FsUtil.DeleteDir(RbDir(p));
                    Directory.Move(RbNewDir(p), RbDir(p));
                }
                else WriteSlot(slot, slot.Dir);
            }
            catch (Exception ex) { ui.Log("Не удалось записать данные для отката: " + ex.Message, LogKind.Warn); }
        }

        void DropRollback(SyncPaths p)
        {
            try { if (Directory.Exists(RbDir(p))) FsUtil.DeleteDir(RbDir(p)); } catch { }
            try { if (Directory.Exists(RbNewDir(p))) FsUtil.DeleteDir(RbNewDir(p)); } catch { }
        }

        void ReadRollback(SyncPaths p, StatusInfo st)
        {
            var slot = ReadSlot(RbDir(p));
            if (slot == null || slot.Cur != st.Installed || !CommitExists(slot.Target)) return;
            st.RollbackTarget = slot.Target;
            st.RollbackDate = CommitDate(slot.Target);
            st.RollbackIsNewer = CommitTime(slot.Target) > CommitTime(st.Installed);
        }

        /// <summary>
        /// Switches mods\ to the slot's version without internet. The files it replaces are kept in a new slot, so the way
        /// back is one click as well. Files changed by hand before the update could not be kept: «Проверить файлы» restores them.
        /// </summary>
        public UpdateResult Rollback(SyncPaths p)
        {
            git.ResetCancel();
            ui.CanCancel(false);
            RefuseWhileRunning("откат", "the rollback");
            Bind(p);
            var cur = p.ReadSynced();
            var slot = ReadSlot(RbDir(p));
            if (slot == null || cur == null || slot.Cur != cur || !CommitExists(slot.Target))
                throw new SyncException("Откат недоступен: нет сохранённых файлов прошлой версии для установленной сейчас версии.",
                    "Rollback is not available: there are no saved files of the previous version for the installed one.");
            bool newer = CommitTime(slot.Target) > CommitTime(cur);
            if (newer) Stage("Возвращаю версию сборки от " + CommitDate(slot.Target) + "…", "Returning to the build of " + CommitDate(slot.Target) + "…");
            else Stage("Откатываю сборку на версию от " + CommitDate(slot.Target) + "…", "Rolling the build back to " + CommitDate(slot.Target) + "…");
            ui.Progress(null, Tr.T("Не закрывайте программу.", "Do not close the launcher."));

            // 1. the current files, for the way back
            if (Directory.Exists(RbNewDir(p))) FsUtil.DeleteDir(RbNewDir(p));
            var back = new RollbackSlot { Target = cur, Cur = slot.Target, Dir = RbNewDir(p) };
            Action<string> keep = path =>
            {
                var full = WorkFile(p, path);
                if (!File.Exists(full)) { back.Missing.Add(path); return; }
                try { CopyFile(full, SlotFile(back.Dir, path)); back.Saved.Add(path); }
                catch { back.Missing.Add(path); }
            };
            foreach (var path in slot.Saved)
            {
                if (File.Exists(WorkFile(p, path))) keep(path);
                else back.Added.Add(path);   // deleted by the current version: delete it again on the way back
            }
            foreach (var path in slot.Added) keep(path);
            foreach (var path in slot.Missing)
            {
                if (File.Exists(WorkFile(p, path))) keep(path);
                else back.Added.Add(path);
            }

            // 2. the target's files
            int failed = 0;
            foreach (var path in slot.Saved)
            {
                try { CopyFile(SlotFile(slot.Dir, path), WorkFile(p, path)); }
                catch (Exception ex) { failed++; ui.Log("Не удалось вернуть " + path + ": " + ex.Message, LogKind.Warn); }
            }
            foreach (var path in slot.Added)
            {
                var full = WorkFile(p, path);
                try
                {
                    if (File.Exists(full)) { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); }
                    // empty folders left behind (a mod the current version added)
                    for (var d = Path.GetDirectoryName(full); d != null && d.Length > p.Mods.Length &&
                         d.StartsWith(p.Mods + "\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(d) &&
                         !Directory.EnumerateFileSystemEntries(d).Any(); d = Path.GetDirectoryName(d))
                        Directory.Delete(d);
                }
                catch (Exception ex) { failed++; ui.Log("Не удалось удалить " + path + ": " + ex.Message, LogKind.Warn); }
            }

            // 3. installed version and index
            // the restored files are in the recognition index already: reset keeps their hashes and does not read them again
            try
            {
                string tree;
                LocalTree(p, slot.Target, out tree);
                Bind(p);
                File.Copy(FpIndex(p), Path.Combine(p.GitDir, "index"), true);
            }
            catch (Exception ex) { ui.Log("Индекс распознавания не обновлён (" + ex.Message + "), git перечитает файлы.", LogKind.Muted); Bind(p); }
            ResetIndex(p, slot.Target);
            File.WriteAllText(p.SyncedFile, slot.Target + "\n");
            try
            {
                WriteSlot(back, back.Dir);
                FsUtil.DeleteDir(RbDir(p));
                Directory.Move(RbNewDir(p), RbDir(p));
            }
            catch (Exception ex) { ui.Log("Обратный путь не сохранён: " + ex.Message, LogKind.Warn); }

            var sample = new List<string>();
            var res = new UpdateResult { Old = cur, New = slot.Target, Mods = SummaryBetween(cur, slot.Target), Rollback = true };
            res.NotRestored = Math.Max(0, LocalChanges(p, slot.Target, sample));
            ui.Log((newer ? "Возвращена версия сборки " : "Сборка откачена на версию ") + AppInfo.Short(slot.Target) + " (было " + AppInfo.Short(cur) +
                "): возвращено файлов " + Fmt.Num(slot.Saved.Count) + ", удалено " + Fmt.Num(slot.Added.Count) + ".", LogKind.Ok);
            if (res.NotRestored > 0)
                ui.Log("Не совпадают со сборкой файлов: " + res.NotRestored + " (" + string.Join(", ", sample.Take(5)) + (sample.Count > 5 ? " …" : "") +
                    "). Их исправит «Проверить файлы» (нужен интернет).", LogKind.Warn);
            WriteSyncLog(p, cur, slot.Target, res);
            return res;
        }
    }
}
