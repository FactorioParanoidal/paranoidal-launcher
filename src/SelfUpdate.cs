using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Paranoidal
{
    /// <summary>
    /// Replacing the launcher's own exe: self-update from the launcher's GitHub Releases and copying the exe into the game
    /// folder (Relocation). A running exe cannot be overwritten but can be renamed: exe -> exe.old, new file -> exe,
    /// restart; the new process deletes exe.old. Where the folder needs administrator rights, the same exe runs elevated
    /// in helper mode (--install-exe, Program.cs) and does only the file swap.
    /// </summary>
    static class SelfUpdate
    {
        public const string OldSuffix = ".old", NewSuffix = ".new";

        /// <summary>Path of the running exe.</summary>
        public static string CurrentExe
        {
            get
            {
                try { return Path.GetFullPath(Assembly.GetEntryAssembly().Location); }
                catch { return Path.Combine(AppInfo.ExeDir, AppInfo.ExeName); }
            }
        }

        /// <summary>Product version of an exe (x.y.z) or null.</summary>
        public static string ExeVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var v = (FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "").Trim();
                return Releases.IsVersion(v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>Deletes exe.old left by an update (the old process may still be exiting: a few tries).</summary>
        public static void CleanupOld(string exe)
        {
            var old = exe + OldSuffix;
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); }
                catch { Thread.Sleep(300); }
            }
            try { if (File.Exists(exe + NewSuffix)) File.Delete(exe + NewSuffix); } catch { }
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(exe), Path.GetFileName(exe) + ".*" + OldSuffix))
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            }
            catch { }
        }

        /// <summary>
        /// exe -> exe.old, newFile -> exe. Works while exe is running. On a failure the old exe is put back.
        /// Throws UnauthorizedAccessException when the folder needs administrator rights.
        /// </summary>
        public static void Swap(string newFile, string exe)
        {
            var old = exe + OldSuffix;
            if (File.Exists(old))
            {
                try { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); }
                catch { old = exe + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + OldSuffix; }   // an old exe still running
            }
            bool had = File.Exists(exe);
            if (had) File.Move(exe, old);
            try { File.Move(newFile, exe); }
            catch
            {
                if (had) try { File.Move(old, exe); } catch { }
                throw;
            }
        }

        /// <summary>Copies srcExe into targetDir as ParanoidalLauncher.exe (replacing one that is there).</summary>
        public static string InstallCopy(string srcExe, string targetDir)
        {
            var target = Path.Combine(targetDir, AppInfo.ExeName);
            if (string.Equals(Path.GetFullPath(srcExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return target;
            var tmp = target + NewSuffix;
            File.Copy(srcExe, tmp, true);
            try { Swap(tmp, target); }
            catch { try { File.Delete(tmp); } catch { } throw; }
            return target;
        }

        /// <summary>
        /// Runs `helperExe --install-exe src targetDir` with administrator rights (UAC) and waits for it.
        /// False with error = null: the player declined the UAC question.
        /// </summary>
        public static bool InstallElevated(string helperExe, string src, string targetDir, out string error)
        {
            error = null;
            var psi = new ProcessStartInfo(helperExe, "--install-exe " + FsUtil.QuoteArg(src) + " " + FsUtil.QuoteArg(targetDir))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) { error = "процесс не запустился"; return false; }
                    p.WaitForExit();
                    if (p.ExitCode == 0) return true;
                    error = "помощник с правами администратора завершился с кодом " + p.ExitCode;
                    return false;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }   // ERROR_CANCELLED: UAC declined
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Helper mode (elevated): put src as targetDir\ParanoidalLauncher.exe. Exit code 0 = done.</summary>
        public static int HelperInstall(string src, string targetDir)
        {
            try
            {
                InstallCopy(src, targetDir);
                return 0;
            }
            catch (UnauthorizedAccessException) { return 5; }
            catch { return 1; }
        }

        /// <summary>Starts exe (normal rights) with the arguments; it waits for this process to exit (--wait-pid).</summary>
        public static void Restart(string exe, string args)
        {
            Process.Start(new ProcessStartInfo(exe, "--wait-pid " + Process.GetCurrentProcess().Id + (string.IsNullOrEmpty(args) ? "" : " " + args))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
        }
    }

    /// <summary>Command line of the launcher (Program.Main): restarts after an update or a move, and the elevated helper.</summary>
    static class StartArgs
    {
        public static int WaitPid;
        public static string UpdatedFrom, MovedFrom, InstallSrc, InstallDir;

        public static void Parse(string[] a)
        {
            for (int i = 0; i < a.Length; i++)
            {
                Func<string> next = () => i + 1 < a.Length ? a[++i] : null;
                switch (a[i])
                {
                    case "--wait-pid": int.TryParse(next(), out WaitPid); break;
                    case "--updated-from": UpdatedFrom = next(); break;
                    case "--moved-from": MovedFrom = next(); break;
                    case "--install-exe": InstallSrc = next(); InstallDir = next(); break;
                }
            }
        }

        /// <summary>Waits until the previous launcher process has exited (it holds the single-instance mutex).</summary>
        public static void WaitForPrevious()
        {
            if (WaitPid <= 0) return;
            try { using (var p = Process.GetProcessById(WaitPid)) p.WaitForExit(30000); }
            catch { }   // already gone
        }
    }
}
