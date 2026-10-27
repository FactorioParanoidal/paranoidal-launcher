// UI snapshot tool (not shipped): draws the launcher's dialogs into PNG files without showing them, so a test never takes
// the focus from the user's game. Usage: UiShot.exe <outDir> <ru|en>
// Set PARANOIDAL_LAUNCHER_DATA_DIR to a test folder: the language choice is written to its settings.ini.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Paranoidal
{
    static class UiShot
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>
        /// Renders the window with PrintWindow (RichTextBox and nested controls included, unlike DrawToBitmap). The window is
        /// shown far off-screen without activation (SW_SHOWNOACTIVATE), so it never takes the focus, then hidden again.
        /// </summary>
        static void Snap(Form f, string file)
        {
            var h = f.Handle;
            SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
            ShowWindow(h, 4);   // SW_SHOWNOACTIVATE
            for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    PrintWindow(h, hdc, 0);   // WM_PRINT path: works off-screen (PW_RENDERFULLCONTENT needs a DWM surface)
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(file);
            }
            ShowWindow(h, 0);   // SW_HIDE
            Console.WriteLine("snap " + file + " " + f.Width + "x" + f.Height);
        }

        [STAThread]
        static int Main(string[] a)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
            Tr.Init();
            Tr.Set(a[1] == "en");
            Directory.CreateDirectory(a[0]);
            int n = 0;
            Dialogs.TestSnapshot = f => { Snap(f, Path.Combine(a[0], "dialog-" + a[1] + "-" + (++n) + ".png")); f.Close(); };
            var fac = FactorioInfo.Inspect(a.Length > 2 ? a[2] : null);
            Dialogs.AskReport(null, fac);
            Dialogs.AskHostLine(null);
            // 1.2.0: self-update (a[3] = a launcher release.json, e.g. of a test stand), choice of installation, main window
            ReleaseInfo rel = null;
            if (a.Length > 3 && File.Exists(a[3]))
                rel = Releases.ParseManifest(File.ReadAllText(a[3]), AppInfo.LauncherRepo, "launcher", AppInfo.ExeName);
            if (rel != null)
            {
                Dialogs.AskSelfUpdate(null, rel, false);
                Dialogs.AskSelfUpdate(null, rel, true);
            }
            Dialogs.ChooseInstall(null, new System.Collections.Generic.List<FactorioInfo>
            {
                new FactorioInfo { Dir = @"C:\Games\Factorio 2.0.77", Version = "2.0.77" },
                new FactorioInfo { Dir = @"D:\SteamLibrary\steamapps\common\Factorio", Version = "2.0.77" },
            });
            var main = new MainForm();
            var t = typeof(MainForm);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            t.GetMethod("AppendLog", flags).Invoke(main, new object[] { "Готово. Моды соответствуют последней версии сборки", LogKind.Ok });
            t.GetMethod("AppendLogHighlight", flags).Invoke(main, new object[] { "Обновился гайд по сборке Paranoidal: версия 1.3.4 — C:\\Games\\Factorio\\Guide_to_the_Paranoidal.html" });
            t.GetMethod("AppendLog", flags).Invoke(main, new object[] { "Следующая обычная строка журнала.", LogKind.Info });
            t.GetMethod("SelectTab", flags).Invoke(main, new object[] { 2 });
            Snap(main, Path.Combine(a[0], "main-" + a[1] + ".png"));
            main.ClientSize = new Size(Theme.S(900), Theme.S(740));
            Snap(main, Path.Combine(a[0], "main-min-" + a[1] + ".png"));
            return 0;
        }
    }
}
