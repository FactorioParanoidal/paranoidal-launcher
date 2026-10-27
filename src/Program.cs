using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// name and version come from AppInfo only (single source, see AppInfo.Version)
[assembly: AssemblyTitle(Paranoidal.AppInfo.Name)]
[assembly: AssemblyProduct(Paranoidal.AppInfo.Name)]
[assembly: AssemblyDescription("Лаунчер и обновление сборки Factorio Paranoidal")]
[assembly: AssemblyVersion(Paranoidal.AppInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(Paranoidal.AppInfo.AssemblyVersion)]
[assembly: AssemblyInformationalVersion(Paranoidal.AppInfo.Version)]

namespace Paranoidal
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            StartArgs.Parse(args);
            // helper mode (started elevated by SelfUpdate.InstallElevated): only the file swap, no window, no mutex
            if (StartArgs.InstallSrc != null && StartArgs.InstallDir != null)
                return SelfUpdate.HelperInstall(StartArgs.InstallSrc, StartArgs.InstallDir);
            // a restart after an update or a move: the previous process still holds the mutex for a moment
            StartArgs.WaitForPrevious();
            bool created;
            using (var mutex = new Mutex(true, @"Local\ParanoidalLauncher", out created))
            {
                Tr.Init();
                if (!created)
                {
                    MessageBox.Show(AppInfo.Name + Tr.T(" уже запущен.", " is already running."), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Settings.MigratePaths();
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Crash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
                Application.Run(new MainForm());
            }
            return 0;
        }

        static void Crash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                File.AppendAllText(Path.Combine(AppInfo.DataDir, "crash.txt"),
                    DateTime.Now + "  " + AppInfo.Name + " " + AppInfo.Version + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
            MessageBox.Show(Tr.T("Непредвиденная ошибка:\n", "Unexpected error:\n") + (ex == null ? "?" : ex.Message) +
                Tr.T("\n\nПодробности: ", "\n\nDetails: ") + Path.Combine(AppInfo.DataDir, "crash.txt") +
                Tr.T("\nКнопка «Отчёт о проблеме» соберёт его в ZIP для Discord.", "\nThe \"Problem report\" button packs it into a ZIP for Discord."),
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
