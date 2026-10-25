// Console test harness for the launcher engine (not shipped). Usage:
//   Harness.exe <git.exe> <modsDir> <yes|no> [ref|-] [cancelAfterSeconds|-] [options]
//     yes|no          answer to "delete mods that are not in the build?" and to "replace files of a found build?"
//     ref             update to this commit instead of the branch (the host's version)
//     --verify        «Проверить файлы»: full byte-for-byte check of the installed version instead of an update
//     --info          change nothing: print the «Скопировать версию» line (RU and EN)
//     --status        CheckStatus: recognition, offline state, rollback; prints the StatusInfo
//     --rollback      «Откатить» / «Вернуть»
//     --played        mark the installed version as played (as «ИГРАТЬ» does) and exit
//   Harness.exe --loaddiag <current.log> [previous.log] [--tilde=mod1,mod2] [--build-changed]
//   Harness.exe --loaddiag-selftest <load-logs-2.0.77.zip>    checks the verdicts on the reference logs
//   Harness.exe --report <factorioDir> <modsDir> <out.zip> [--save=last|auto|<path>]
//   Harness.exe --parse-host <line>
// PARANOIDAL_LAUNCHER_TEST_OFFLINE=1 makes git and the GitHub API fail as without a connection;
// PARANOIDAL_LAUNCHER_DATA_DIR moves settings and logs.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Paranoidal
{
    sealed class ConsoleUi : ISyncUi
    {
        public string Answer;
        int lastPct = -1;
        DateTime lastDetail = DateTime.MinValue;

        public void Log(string text, LogKind kind = LogKind.Info) { Console.WriteLine("[" + kind + "] " + text); }
        public void Stage(string ru, string en) { Console.WriteLine("== " + ru + "  |  " + en); lastPct = -1; }
        public void Progress(double? f, string detail)
        {
            int pct = f == null ? -1 : (int)(f.Value * 100);
            if (pct != lastPct && (pct % 10 == 0 || (DateTime.Now - lastDetail).TotalSeconds > 20))
            {
                lastPct = pct; lastDetail = DateTime.Now;
                Console.WriteLine("   " + (f == null ? "..." : pct + "%") + " " + detail);
            }
        }
        public void CanCancel(bool v) { Console.WriteLine("   cancel=" + v); }
        public bool ConfirmExtras(List<string> names)
        {
            Console.WriteLine("?? extras=" + names.Count + ": " + string.Join(", ", names));
            return Answer == "yes";
        }
        public bool ConfirmOverwrite(int files, List<string> sample, BuildFound found)
        {
            Console.WriteLine("?? overwrite files=" + files + " found=" + (found == null ? "-" : found.Present + "/" + found.FactorioVersion + "/" + found.ZzzVersion) +
                " sample: " + string.Join(", ", sample.Take(5)));
            return Answer == "yes";
        }
    }

    static class Harness
    {
        static string Opt(string[] argv, string name)
        {
            var a = argv.FirstOrDefault(x => x.StartsWith(name + "="));
            return a == null ? null : a.Substring(name.Length + 1);
        }

        static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            Console.WriteLine("LAUNCHER " + AppInfo.Name + " " + AppInfo.Version + (AppInfo.TestOffline ? " (TEST OFFLINE)" : ""));
            if (argv.Length > 0 && argv[0] == "--loaddiag") return LoadDiagOne(argv);
            if (argv.Length > 0 && argv[0] == "--loaddiag-selftest") return LoadDiagSelfTest(argv[1]);
            if (argv.Length > 0 && argv[0] == "--report") return ReportTest(argv);
            if (argv.Length > 0 && argv[0] == "--parse-host") { Console.WriteLine("HOST " + (VersionLine.ParseHost(argv[1]) ?? "(none)")); return 0; }
            if (argv.Length > 0 && argv[0] == "--paths")
            {
                // settings.ini paths: relative inside the exe folder, absolute outside, 1.0.x absolute values migrated
                var inside = Path.Combine(AppInfo.ExeDir, "game", "mods");
                Settings.Set("mods", inside);
                Settings.Set("factorio", @"D:\Elsewhere\Factorio");
                Settings.MigratePaths();
                Console.WriteLine("RAW mods=" + Settings.Get("mods") + " factorio=" + Settings.Get("factorio"));
                Console.WriteLine("GET mods=" + Settings.GetPath("mods") + " factorio=" + Settings.GetPath("factorio"));
                Settings.SetPath("mods", AppInfo.ExeDir);
                Console.WriteLine("SET exe dir -> " + Settings.Get("mods") + " -> " + Settings.GetPath("mods"));
                return 0;
            }

            bool verify = argv.Contains("--verify"), info = argv.Contains("--info"), status = argv.Contains("--status"),
                 rollback = argv.Contains("--rollback"), played = argv.Contains("--played");
            var a = argv.Where(x => !x.StartsWith("--")).ToArray();
            var ui = new ConsoleUi { Answer = a[2] };
            var git = new GitRunner(a[0], Path.Combine(Path.GetTempPath(), "no-global-config"));
            var eng = new SyncEngine(git, ui) { SkipRunningCheck = true };
            var paths = new SyncPaths(a[1]);
            if (info)
            {
                var synced = paths.ReadSynced();
                if (synced == null) { Console.WriteLine("LINE (not installed)"); return 0; }
                var foreign = eng.ForeignEntries(paths);
                Console.WriteLine("LINE " + VersionLine.Build(synced, null, paths.Mods, foreign));
                Console.WriteLine("LINE-EN " + VersionLine.Build(synced, null, paths.Mods, foreign, 0, null, true));
                return 0;
            }
            if (played) { eng.MarkPlayed(paths, paths.ReadSynced()); Console.WriteLine("PLAYED " + AppInfo.Short(paths.ReadSynced())); return 0; }
            var t0 = DateTime.Now;
            try
            {
                if (status)
                {
                    var st = eng.CheckStatus(a[1]);
                    Console.WriteLine("STATUS installed=" + AppInfo.Short(st.Installed) + " date=" + st.InstalledDate + " remote=" + AppInfo.Short(st.Remote) +
                        " offline=" + st.Offline + " modified=" + st.Modified + " adopted=" + st.Adopted + " from=" + AppInfo.Short(st.AdoptedFrom) +
                        " rollback=" + AppInfo.Short(st.RollbackTarget) + " (" + st.RollbackDate + ", newer=" + st.RollbackIsNewer + ")" +
                        " commits=" + (st.Commits == null ? "null" : st.Commits.Count + (st.CommitsCached ? " cached" : "") + (st.CommitsAreNew ? " new" : "")));
                    var f = st.Found;
                    if (f != null)
                        Console.WriteLine("FOUND present=" + f.Present + " fv=" + f.FactorioVersion + " zzz=" + f.ZzzVersion + " legacy=" + f.Legacy + " fp=" + f.Fingerprint +
                            " match=" + AppInfo.Short(f.Match) + " near=" + AppInfo.Short(f.Near) + " " + f.NearDate + " diff=" + f.NearDiff + "/" + f.NearTotal +
                            " known=" + f.Known + " mods=" + string.Join(",", f.NearMods.Take(10)));
                    Console.WriteLine("TIME " + (int)(DateTime.Now - t0).TotalSeconds + "s");
                    return 0;
                }
                var r = rollback ? eng.Rollback(paths) : eng.Update(paths, verify, a.Length > 3 && a[3] != "-" ? a[3] : null);
                Console.WriteLine("RESULT old=" + AppInfo.Short(r.Old) + " new=" + AppInfo.Short(r.New) + " upToDate=" + r.UpToDate +
                    " fullCheck=" + r.FullCheck + " declined=" + r.Declined + " downloaded=" + r.Downloaded + " notRestored=" + r.NotRestored);
                foreach (var m in r.Mods) Console.WriteLine("   mod " + m.Mod + " | " + m.Kind + " | " + m.Files);
                Console.WriteLine("TIME " + (int)(DateTime.Now - t0).TotalSeconds + "s");
                return 0;
            }
            catch (OperationCanceledException) { Console.WriteLine("CANCELLED after " + (int)(DateTime.Now - t0).TotalSeconds + "s"); return 2; }
            catch (Exception ex) { Console.WriteLine("ERROR " + ex.Message); return 1; }
        }

        // ------------------------------------------------------------------ load diagnostics

        static void PrintVerdict(LoadVerdict v)
        {
            Console.WriteLine("  RU: " + v.Full(false).Replace("\n", "\n      "));
            Console.WriteLine("  EN: " + v.Line(true));
        }

        static int LoadDiagOne(string[] argv)
        {
            var cur = LoadLog.Read(argv[1]);
            var prev = argv.Length > 2 && !argv[2].StartsWith("--") ? LoadLog.Read(argv[2]) : null;
            var ctx = new LoadContext { BuildChanged = argv.Contains("--build-changed") };
            var t = Opt(argv, "--tilde");
            if (t != null) ctx.Tilde = t.Split(',').ToList();
            PrintVerdict(LoadDiag.Analyze(cur, prev, ctx));
            return 0;
        }

        /// <summary>Verdicts on the measured 2.0.77 logs (docs/factorio-load-cache.md) must stay as measured.</summary>
        static int LoadDiagSelfTest(string zipPath)
        {
            var logs = new Dictionary<string, LoadLog>();
            using (var z = ZipFile.OpenRead(zipPath))
                foreach (var e in z.Entries.Where(x => x.Name.EndsWith(".log")))
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8)) logs[e.Name.Substring(0, 2)] = LoadLog.Parse(r.ReadToEnd());
            int fails = 0;
            Action<string, string, LoadContext, Func<LoadVerdict, bool>, string> check = (curK, prevK, ctx, ok, what) =>
            {
                var v = LoadDiag.Analyze(logs[curK], prevK == null ? null : logs[prevK], ctx);
                bool pass = ok(v);
                if (!pass) fails++;
                Console.WriteLine((pass ? "PASS " : "FAIL ") + curK + (prevK != null ? " vs " + prevK : "") + ": " + what);
                PrintVerdict(v);
            };
            Func<LoadVerdict, string, bool> dataCause = (v, s) => v.DataCauses.Any(c => c[0].Contains(s));
            Func<LoadVerdict, string, bool> atlasCause = (v, s) => v.AtlasCauses.Any(c => c[0].Contains(s));
            var none = new LoadContext();
            check("01", null, none, v => v.Data == LoadLog.DataState.NotUsed && v.DataWritten && v.Atlas == LoadLog.AtlasState.Miss && atlasCause(v, "новая или изменённая графика") && Math.Abs(v.Seconds.Value - 105.4) < 0.1, "cold start");
            check("02", "01", none, v => v.Data == LoadLog.DataState.Hit && v.Atlas == LoadLog.AtlasState.Miss && atlasCause(v, "второй запуск"), "second start: atlas once more");
            check("03", "02", none, v => v.Data == LoadLog.DataState.Hit && v.Atlas == LoadLog.AtlasState.Hit && v.DataCauses.Count == 0 && v.AtlasCauses.Count == 0, "warm start");
            check("04", "03", none, v => v.Data == LoadLog.DataState.Rebuilt && dataCause(v, "настройки модов") && v.Atlas == LoadLog.AtlasState.Hit, "per-user setting changed");
            check("05", "04", none, v => v.Data == LoadLog.DataState.Rebuilt && dataCause(v, "настройки модов"), "startup setting changed");
            check("06", "05", none, v => v.Data == LoadLog.DataState.Hit && v.Atlas == LoadLog.AtlasState.Hit, "invalid graphics value: nothing changed");
            check("07", "06", none, v => v.Data == LoadLog.DataState.Hit && v.Atlas == LoadLog.AtlasState.Miss && atlasCause(v, "настройки графики"), "graphics quality changed");
            check("08", "07", none, v => v.Data == LoadLog.DataState.NotUsed && dataCause(v, "изменились файлы модов: zzzparanoidal"), "a Lua file of zzzparanoidal changed");
            check("09", "08", none, v => v.Data == LoadLog.DataState.NotUsed && dataCause(v, "добавлены моды: zz-launcher-cache-probe"), "a foreign mod added");
            check("10", "09", none, v => v.Data == LoadLog.DataState.NotUsed && dataCause(v, "другая установка игры"), "another installation");
            check("11", "10", none, v => v.Data == LoadLog.DataState.None && v.Atlas == LoadLog.AtlasState.None && dataCause(v, "выключен"), "cache off");
            check("13", "12", new LoadContext { Tilde = new List<string> { "tilde-probe" } }, v => v.Data == LoadLog.DataState.NotUsed && dataCause(v, "«~»"), "a mod with ~ (context)");
            check("13", "12", none, v => v.Data == LoadLog.DataState.NotUsed && dataCause(v, "причина не определена"), "a mod with ~ unknown to the launcher");
            check("08", "07", new LoadContext { BuildChanged = true }, v => dataCause(v, "сменил версию сборки"), "build changed by the launcher");
            check("03", "02", new LoadContext { ModsDir = @"C:\elsewhere\mods" }, v => v.OtherMods, "started with another mods folder");
            Console.WriteLine(fails == 0 ? "SELFTEST OK" : "SELFTEST FAILED: " + fails);
            return fails == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ report

        static int ReportTest(string[] argv)
        {
            var fac = FactorioInfo.Inspect(argv[1]);
            var inp = new Report.Input
            {
                Fac = fac,
                ModsDir = argv[2],
                OutFile = Path.GetFullPath(argv[3]),
                VersionLine = "harness",
                State = "harness state\nC:\\Users\\" + Environment.UserName + "\\Desktop",
                Journal = "harness journal " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            var save = Opt(argv, "--save");
            if (save == "last") inp.Save = Report.SaveChoice.LastManual;
            else if (save == "auto") inp.Save = Report.SaveChoice.LastAutosave;
            else if (save != null) { inp.Save = Report.SaveChoice.Chosen; inp.SavePath = save; }
            var res = Report.Create(inp);
            Console.WriteLine("REPORT " + res.Zip + " size=" + res.Size + " entries=" + res.Entries.Count + " save=" + (res.SaveAdded ?? "-") + " skipped=" + (res.SaveSkippedRu ?? "-"));
            foreach (var e in res.Entries) Console.WriteLine("   " + e);
            return 0;
        }
    }
}
