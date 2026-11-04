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
            // 1.3.0: the move to 2.1 (choice of the folder: found ones / none found; the confirmation: a move / in place)
            var g21 = new System.Collections.Generic.List<FactorioInfo>
            {
                new FactorioInfo { Dir = @"C:\Games\Factorio 2.1.24", Version = "2.1.24" }.WithAllDlc(),
                new FactorioInfo { Dir = @"D:\SteamLibrary\steamapps\common\Factorio", Version = "2.1.25" }.WithAllDlc(),
            };
            Dialogs.ChooseMigrationTarget(null, g21);
            Dialogs.ChooseMigrationTarget(null, new System.Collections.Generic.List<FactorioInfo>());
            Dialogs.ConfirmMigration(null, SamplePlan(false));
            Dialogs.ConfirmMigration(null, SamplePlan(true));
            foreach (var line in new[] { Lines.L20, Lines.L21 })
            {
                Lines.Set(line);
                var main = new MainForm();
                var t = typeof(MainForm);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                t.GetMethod("AppendLog", flags).Invoke(main, new object[] { "Готово. Моды соответствуют последней версии сборки", LogKind.Ok });
                t.GetMethod("AppendLogHighlight", flags).Invoke(main, new object[] { "Обновился гайд по сборке Paranoidal: версия 1.3.4 — C:\\Games\\Factorio\\Guide_to_the_Paranoidal.html" });
                t.GetMethod("AppendLog", flags).Invoke(main, new object[] { "Следующая обычная строка журнала.", LogKind.Info });
                t.GetMethod("SelectTab", flags).Invoke(main, new object[] { 2 });
                ((FButton)t.GetField("btn21", flags).GetValue(main)).Enabled = true;   // enabled once the engine is up
                var id = line.Id.Replace(".", "");
                Snap(main, Path.Combine(a[0], "main-" + id + "-" + a[1] + ".png"));
                main.ClientSize = new Size(Theme.S(900), Theme.S(740));
                Snap(main, Path.Combine(a[0], "main-min-" + id + "-" + a[1] + ".png"));
                main.Dispose();
            }
            return 0;
        }

        /// <summary>A plan as a player with a typical game folder would see it (only for the picture; nothing is planned).</summary>
        static MigrationPlan SamplePlan(bool inPlace)
        {
            var p = new MigrationPlan
            {
                OldDir = @"C:\Games\Factorio 2.0.77", NewDir = inPlace ? @"C:\Games\Factorio 2.0.77" : @"C:\Games\Factorio 2.1.24", NewVersion = "2.1.24",
                InPlace = inPlace, SharedWd = inPlace, ModsStay = inPlace,
            };
            p.OldWd = p.OldDir; p.NewWd = p.NewDir; p.NewCfg = p.NewDir + @"\config\config.ini";
            p.ModsDir = p.OldDir + @"\mods"; p.NewMods = p.NewDir + @"\mods";
            p.Saves = 37; p.SavesBytes = 2300L * 1024 * 1024; p.ModsBytes = 1100L * 1024 * 1024; p.CacheBytes = 1080L * 1024 * 1024;
            if (!inPlace)
            {
                p.PlayerItems.AddRange(new[] { "script-output (4)", "achievements-modded.dat", "blueprint-storage-2.dat", "player-data.json" });
                p.CacheItems.AddRange(new[] { "atlas-cache.dat", "data-cache.dat", "crop-cache.dat", "temp" });
                p.Asides.Add(p.NewDir + @"\mods → mods.before-paranoidal");
                p.Steps.Add(new MigStep { Kind = "move", Src = p.OldDir + @"\saves\base.zip", Dst = p.NewDir + @"\saves\base.zip" });
                p.Steps.Add(new MigStep { Kind = "config", Src = p.OldDir + @"\config\config.ini", Dst = p.NewCfg });
                p.Steps.Add(new MigStep { Kind = "move", Src = p.ModsDir, Dst = p.NewMods });
                p.Steps.Add(new MigStep { Kind = "delete", Src = p.OldDir + @"\atlas-cache.dat" });
                p.Steps.Add(new MigStep { Kind = "exe", Src = p.OldDir + @"\ParanoidalLauncher.exe", Dst = p.NewDir });
                p.Steps.Add(new MigStep { Kind = "move", Src = p.OldDir + @"\ParanoidalLauncher-data", Dst = p.NewDir + @"\ParanoidalLauncher-data" });
                p.Steps.Add(new MigStep { Kind = "shortcuts", Src = p.OldDir + @"\ParanoidalLauncher.exe", Dst = p.NewDir + @"\ParanoidalLauncher.exe" });
            }
            else
            {
                p.Steps.Add(new MigStep { Kind = "delete", Src = p.OldDir + @"\.paranoidal-git\rollback" });
                p.Steps.Add(new MigStep { Kind = "settings", Src = p.OldDir + @"\ParanoidalLauncher-data" });
            }
            p.Foreign.AddRange(new[] { "even-distribution_2.0.2.zip", "my-own-mod", "squeak-through-2_0.1.4.zip" });
            return p;
        }
    }
}
