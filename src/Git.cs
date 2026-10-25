using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Paranoidal
{
    sealed class GitResult
    {
        public int Code;
        public byte[] Out = new byte[0];
        public string Err = "";
        public string Text => Encoding.UTF8.GetString(Out).Trim();

        public string LastErrorLine()
        {
            var lines = Err.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (l.StartsWith("fatal:") || l.StartsWith("error:")) return l;
            }
            return lines.Length > 0 ? lines[lines.Length - 1].Trim() : "код " + Code;
        }
    }

    /// <summary>Lets the caller kill one running git process (used by the download watchdog).</summary>
    sealed class RunHandle
    {
        volatile Process proc;
        public volatile bool Killed;

        internal void Attach(Process p) { proc = p; if (Killed) KillTree(p); }

        public void Kill()
        {
            Killed = true;
            var p = proc;
            if (p != null) KillTree(p);
        }

        internal static void KillTree(Process p)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F")
                { CreateNoWindow = true, UseShellExecute = false }))
                    k.WaitForExit(5000);
            }
            catch { }
        }
    }

    /// <summary>Runs the bundled git with an isolated environment (no user/system config, no prompts, no lazy fetch).</summary>
    sealed class GitRunner
    {
        public readonly string Exe;
        readonly string globalConfig;
        public string GitDir;
        public string WorkTree;
        public readonly Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        readonly object gate = new object();
        readonly HashSet<Process> running = new HashSet<Process>();
        volatile bool cancelled;

        public GitRunner(string exe, string globalConfig)
        {
            Exe = exe;
            this.globalConfig = globalConfig;
        }

        public bool Cancelled => cancelled;
        public void ResetCancel() { cancelled = false; }
        public void ThrowIfCancelled() { if (cancelled) throw new OperationCanceledException(); }

        public void Cancel()
        {
            cancelled = true;
            List<Process> list;
            lock (gate) list = new List<Process>(running);
            foreach (var p in list) RunHandle.KillTree(p);
        }

        public GitResult Run(string[] args, byte[] stdin = null, Action<string> onErrLine = null,
            IDictionary<string, string> extraEnv = null, RunHandle handle = null)
        {
            ThrowIfCancelled();
            var cmd = new StringBuilder();
            foreach (var a in args) { if (cmd.Length > 0) cmd.Append(' '); cmd.Append(FsUtil.QuoteArg(a)); }

            var psi = new ProcessStartInfo(Exe, cmd.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = WorkTree != null && Directory.Exists(WorkTree) ? WorkTree : Path.GetTempPath(),
            };
            var env = psi.EnvironmentVariables;
            var drop = new List<string>();
            foreach (string k in env.Keys) if (k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) drop.Add(k);
            foreach (var k in drop) env.Remove(k);
            env["GIT_CONFIG_NOSYSTEM"] = "1";
            env["GIT_CONFIG_GLOBAL"] = globalConfig;
            env["GIT_TERMINAL_PROMPT"] = "0";
            env["GCM_INTERACTIVE"] = "never";
            env["GIT_NO_LAZY_FETCH"] = "1";
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "safe.directory";
            env["GIT_CONFIG_VALUE_0"] = "*";
            if (AppInfo.TestOffline)
            {
                // tests: no connection (a proxy that refuses), without touching the network settings of the computer
                env["GIT_CONFIG_COUNT"] = "2";
                env["GIT_CONFIG_KEY_1"] = "http.proxy";
                env["GIT_CONFIG_VALUE_1"] = "http://127.0.0.1:9";
            }
            if (GitDir != null)
            {
                env["GIT_DIR"] = GitDir;
                env["GIT_WORK_TREE"] = WorkTree;
            }
            lock (gate) foreach (var kv in Env) env[kv.Key] = kv.Value;
            if (extraEnv != null) foreach (var kv in extraEnv) env[kv.Key] = kv.Value;

            var p = new Process { StartInfo = psi };
            p.Start();
            lock (gate) running.Add(p);
            if (cancelled) RunHandle.KillTree(p);
            if (handle != null) handle.Attach(p);

            var outMs = new MemoryStream();
            var tOut = new Thread(() => { try { p.StandardOutput.BaseStream.CopyTo(outMs); } catch { } }) { IsBackground = true };
            tOut.Start();

            var err = new StringBuilder();
            var tErr = new Thread(() =>
            {
                var sb = new StringBuilder();
                Action flush = () =>
                {
                    if (sb.Length == 0) return;
                    var line = sb.ToString(); sb.Clear();
                    lock (err)
                    {
                        err.AppendLine(line);
                        if (err.Length > 40000) err.Remove(0, err.Length - 30000);
                    }
                    if (onErrLine != null) try { onErrLine(line); } catch { }
                };
                try
                {
                    var r = p.StandardError;
                    int c;
                    while ((c = r.Read()) >= 0)
                    {
                        if (c == '\r' || c == '\n') flush(); else sb.Append((char)c);
                    }
                    flush();
                }
                catch { }
            }) { IsBackground = true };
            tErr.Start();

            try
            {
                if (stdin != null)
                {
                    p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                    p.StandardInput.BaseStream.Flush();
                }
                p.StandardInput.Close();
            }
            catch (IOException) { }

            p.WaitForExit();
            tOut.Join();
            tErr.Join();
            lock (gate) running.Remove(p);
            var res = new GitResult { Code = p.ExitCode, Out = outMs.ToArray(), Err = err.ToString() };
            p.Dispose();
            ThrowIfCancelled();
            return res;
        }

        public GitResult Check(string what, params string[] args)
        {
            var r = Run(args);
            if (r.Code != 0) throw new SyncException(what + ": " + r.LastErrorLine());
            return r;
        }
    }

    /// <summary>Parses git progress lines like "Receiving objects:  45% (450/1000), 12.34 MiB | 1.20 MiB/s".</summary>
    sealed class GitProgress
    {
        public string Phase;
        public int Percent, Done, Total;
        public long Bytes = -1;

        static readonly Regex Rx = new Regex(
            @"^(?:remote:\s*)?(?<ph>[A-Za-z][A-Za-z ]*?):\s+(?<pct>\d+)%\s+\((?<d>\d+)/(?<t>\d+)\)(?:,\s*(?<b>[\d.]+)\s*(?<u>bytes|KiB|MiB|GiB))?",
            RegexOptions.Compiled);

        public static GitProgress Parse(string line)
        {
            var m = Rx.Match(line);
            if (!m.Success) return null;
            var p = new GitProgress
            {
                Phase = m.Groups["ph"].Value,
                Percent = int.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture),
                Done = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                Total = int.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture),
            };
            double b;
            if (m.Groups["b"].Success && double.TryParse(m.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out b))
            {
                switch (m.Groups["u"].Value)
                {
                    case "KiB": b *= 1024; break;
                    case "MiB": b *= 1024 * 1024; break;
                    case "GiB": b *= 1024.0 * 1024 * 1024; break;
                }
                p.Bytes = (long)b;
            }
            return p;
        }
    }

    /// <summary>Extracts the embedded MinGit once into AppInfo.DataDir.</summary>
    static class GitBootstrap
    {
        public static string Ensure(Action<string, string> stage)
        {
            var dir = Path.Combine(AppInfo.DataDir, "mingit-" + AppInfo.MinGitVersion);
            var exe = Path.Combine(dir, "cmd", "git.exe");
            var marker = Path.Combine(dir, ".extracted");
            if (File.Exists(exe) && File.Exists(marker)) return exe;

            stage("Подготовка встроенного git (только при первом запуске)…", "Preparing the built-in git (first start only)…");
            Directory.CreateDirectory(AppInfo.DataDir);
            var tmp = dir + ".tmp" + Process.GetCurrentProcess().Id;
            if (Directory.Exists(tmp)) FsUtil.DeleteDir(tmp);
            using (var s = Res.MinGitZip())
            {
                if (s == null) throw new SyncException("Встроенный git не найден внутри программы.", "The built-in git is missing from the launcher.");
                using (var z = new ZipArchive(s, ZipArchiveMode.Read)) z.ExtractToDirectory(tmp);
            }
            File.WriteAllText(Path.Combine(tmp, ".extracted"), AppInfo.MinGitVersion);
            if (Directory.Exists(dir)) FsUtil.DeleteDir(dir);
            Directory.Move(tmp, dir);
            return exe;
        }
    }
}
