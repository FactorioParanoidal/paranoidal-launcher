using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Paranoidal
{
    /// <summary>
    /// The launcher belongs in the game folder (next to bin\ and data\ of Factorio): its data, the guide and relative
    /// paths live there and the whole folder can be copied to another PC. Many players start it from Downloads or the
    /// desktop, so from 1.2.0 it moves itself: copies the exe (and its data folder) into the game folder, leaves a desktop
    /// shortcut, starts the copy and exits; the copy deletes the old exe and data (--moved-from). The UI part (when, which
    /// installation, asking for administrator rights) is in MainForm.
    /// </summary>
    static class Relocation
    {
        /// <summary>Why relocation is off on this start (tests, the developer's dist folder); null when it is on.</summary>
        public static string DisabledReason()
        {
            if (Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_NO_RELOCATE") == "1") return "PARANOIDAL_LAUNCHER_NO_RELOCATE=1";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_DATA_DIR"))) return "задана PARANOIDAL_LAUNCHER_DATA_DIR (тестовый режим)";
            if (File.Exists(Path.Combine(AppInfo.ExeDir, "build-info.txt"))) return "лаунчер запущен из папки сборки лаунчера (dist)";
            return null;
        }

        public static bool SameDir(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>The game folder already has a launcher of this or a newer version: start that one instead of copying.</summary>
        public static bool TargetIsSameOrNewer(string gameDir)
        {
            var v = SelfUpdate.ExeVersion(Path.Combine(gameDir, AppInfo.ExeName));
            return v != null && Releases.Compare(v, AppInfo.Version) >= 0;
        }

        /// <summary>
        /// Puts the launcher into gameDir (no elevation): the exe unless a same or newer one is there, the data folder if
        /// it is next to the exe and the game folder has none, the guide if ours is newer. Returns the exe to start.
        /// Throws (UnauthorizedAccessException and others) when the game folder cannot be written.
        /// </summary>
        public static string MoveTo(string srcExe, string dataDir, string gameDir, List<string> log)
        {
            var srcDir = Path.GetDirectoryName(srcExe);
            var target = Path.Combine(gameDir, AppInfo.ExeName);
            if (TargetIsSameOrNewer(gameDir)) log.Add("в папке игры уже есть лаунчер версии " + SelfUpdate.ExeVersion(target) + " — запускается он");
            else { SelfUpdate.InstallCopy(srcExe, gameDir); log.Add("лаунчер скопирован в " + target); }

            var targetData = Path.Combine(gameDir, AppInfo.DataDirName);
            if (SameDir(dataDir, Path.Combine(srcDir, AppInfo.DataDirName)))
            {
                if (!Directory.Exists(targetData))
                {
                    CopyData(dataDir, targetData, srcDir);
                    log.Add("данные лаунчера скопированы в " + targetData);
                }
            }
            else AbsolutizeInPlace(dataDir, srcDir);   // %LOCALAPPDATA% data: the new exe reads it from there

            var guide = Path.Combine(srcDir, AppInfo.GuideFile);
            var targetGuide = Path.Combine(gameDir, AppInfo.GuideFile);
            if (File.Exists(guide) && Releases.Compare(GuideFile.Version(guide), GuideFile.Version(targetGuide)) > 0)
            {
                File.Copy(guide, targetGuide, true);
                log.Add("гайд скопирован в " + targetGuide);
            }
            return target;
        }

        /// <summary>
        /// Copies the launcher's data folder (not MinGit: it is unpacked again from the exe in seconds). settings.ini gets
        /// absolute paths: relative ones pointed from the old exe folder; the new exe makes paths inside its folder relative.
        /// </summary>
        public static void CopyData(string from, string to, string oldExeDir)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("settings.ini", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Copy(f, Path.Combine(to, name), true); } catch { }
            }
            foreach (var d in Directory.GetDirectories(from))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith("mingit-", StringComparison.OrdinalIgnoreCase)) continue;
                try { CopyTree(d, Path.Combine(to, name)); } catch { }
            }
            var ini = Path.Combine(from, "settings.ini");
            if (File.Exists(ini)) File.WriteAllText(Path.Combine(to, "settings.ini"), AbsolutePaths(File.ReadAllText(ini, Encoding.UTF8), oldExeDir), new UTF8Encoding(false));
        }

        /// <summary>
        /// Before an elevated move: the new exe in a read-only game folder keeps its data in %LOCALAPPDATA%\ParanoidalUpdater,
        /// so the current data goes there (settings.ini replaced, paths absolute).
        /// </summary>
        public static void PrepareLegacyData(string dataDir, string oldExeDir)
        {
            var legacy = AppInfo.LegacyDataDir();
            if (SameDir(dataDir, legacy)) AbsolutizeInPlace(dataDir, oldExeDir);
            else CopyData(dataDir, legacy, oldExeDir);
        }

        static void AbsolutizeInPlace(string dataDir, string oldExeDir)
        {
            var ini = Path.Combine(dataDir, "settings.ini");
            try { if (File.Exists(ini)) File.WriteAllText(ini, AbsolutePaths(File.ReadAllText(ini, Encoding.UTF8), oldExeDir), new UTF8Encoding(false)); }
            catch { }
        }

        /// <summary>settings.ini text with the folder paths (factorio, mods) made absolute against the old exe folder.</summary>
        public static string AbsolutePaths(string ini, string oldExeDir)
        {
            var sb = new StringBuilder();
            foreach (var line in ini.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                int i = line.IndexOf('=');
                var key = i > 0 ? line.Substring(0, i).Trim() : "";
                var v = i > 0 ? line.Substring(i + 1).Trim() : "";
                if ((key.Equals("factorio", StringComparison.OrdinalIgnoreCase) || key.Equals("mods", StringComparison.OrdinalIgnoreCase)) && v.Length > 0 && !Path.IsPathRooted(v))
                {
                    try { v = Path.GetFullPath(Path.Combine(oldExeDir, v)).TrimEnd('\\'); } catch { }
                    sb.Append(key).Append('=').Append(v).Append("\r\n");
                }
                else sb.Append(line).Append("\r\n");
            }
            return sb.ToString();
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>
        /// Run by the new copy after a move: deletes the old exe, its data folder and its guide copy. Only these, and the
        /// data folder only when no other launcher exe is left there. Returns what was removed (Russian, for the journal).
        /// </summary>
        public static List<string> CleanupOldPlace(string oldExe, string currentExe, string currentDataDir)
        {
            var done = new List<string>();
            if (string.IsNullOrEmpty(oldExe)) return done;
            oldExe = Path.GetFullPath(oldExe);
            if (string.Equals(oldExe, Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase)) return done;
            if (!oldExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return done;
            var dir = Path.GetDirectoryName(oldExe);
            for (int i = 0; i < 10 && File.Exists(oldExe); i++)
            {
                try { File.SetAttributes(oldExe, FileAttributes.Normal); File.Delete(oldExe); done.Add(oldExe); }
                catch { System.Threading.Thread.Sleep(300); }
            }
            var guide = Path.Combine(dir, AppInfo.GuideFile);
            if (File.Exists(guide) && !SameDir(dir, Path.GetDirectoryName(currentExe)))
                try { File.Delete(guide); done.Add(guide); } catch { }
            var data = Path.Combine(dir, AppInfo.DataDirName);
            bool otherExe = false;
            try { otherExe = Directory.GetFiles(dir, "ParanoidalLauncher*.exe").Length > 0; } catch { }
            if (Directory.Exists(data) && !otherExe && !SameDir(data, currentDataDir))
                try { FsUtil.DeleteDir(data); done.Add(data); } catch { }
            return done;
        }
    }

    /// <summary>
    /// Windows shortcuts (.lnk) through the shell's IShellLink. The desktop path is the real one (also when OneDrive
    /// moved it); the Start menu entry goes to the user's Programs\Paranoidal. PARANOIDAL_LAUNCHER_TEST_SHORTCUTS puts both
    /// into that folder (tests).
    /// </summary>
    static class Shortcuts
    {
        public const string LauncherLink = "Paranoidal Launcher.lnk", GuideLink = "Guide to the Paranoidal.lnk", MenuFolder = "Paranoidal";

        static string Test => Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_TEST_SHORTCUTS");

        public static string DesktopDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Test)) return Path.Combine(Test, "Desktop");
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
        }

        public static string StartMenuDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Test)) return Path.Combine(Test, "Programs", MenuFolder);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), MenuFolder);
            }
        }

        /// <summary>Desktop shortcut to the launcher (after a move: the player clicked the exe on the desktop before).</summary>
        public static string Desktop(string exe)
        {
            var lnk = Path.Combine(DesktopDir, LauncherLink);
            Create(lnk, exe, Path.GetDirectoryName(exe), AppInfo.Name);
            return lnk;
        }

        /// <summary>«Ярлыки»: desktop + Start menu (launcher and, if downloaded, the guide). Returns the created files.</summary>
        public static List<string> All(string exe, string guide)
        {
            var res = new List<string> { Desktop(exe) };
            Directory.CreateDirectory(StartMenuDir);
            var l = Path.Combine(StartMenuDir, LauncherLink);
            Create(l, exe, Path.GetDirectoryName(exe), AppInfo.Name);
            res.Add(l);
            if (guide != null && File.Exists(guide))
            {
                var g = Path.Combine(StartMenuDir, GuideLink);
                Create(g, guide, Path.GetDirectoryName(guide), "Guide to the Paranoidal");
                res.Add(g);
            }
            return res;
        }

        public static void Create(string lnk, string target, string workDir, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workDir);
                link.SetDescription(description);
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        /// <summary>Target of a shortcut (tests and the check whether the desktop shortcut is ours).</summary>
        public static string TargetOf(string lnk)
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
