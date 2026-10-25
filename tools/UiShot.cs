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
        static void Snap(Control f, string file)
        {
            var h = f.Handle;   // creates the window without showing it
            foreach (Control c in f.Controls) { var ch = c.Handle; }
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(file);
            }
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
            Dialogs.TestSnapshot = f => Snap(f, Path.Combine(a[0], "dialog-" + a[1] + "-" + (++n) + ".png"));
            var fac = FactorioInfo.Inspect(a.Length > 2 ? a[2] : null);
            Dialogs.AskReport(null, fac);
            Dialogs.AskHostLine(null);
            return 0;
        }
    }
}
