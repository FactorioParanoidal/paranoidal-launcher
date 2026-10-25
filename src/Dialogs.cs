using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Paranoidal
{
    /// <summary>Small dark dialogs with input: the report options and the host's version line.</summary>
    static class Dialogs
    {
        /// <summary>UI tests only: receives the dialog instead of showing it (the dialog then returns "cancelled").</summary>
        public static Action<Form> TestSnapshot;

        static void Show(Form f, IWin32Window owner)
        {
            if (TestSnapshot != null) TestSnapshot(f);
            else f.ShowDialog(owner);
        }

        sealed class Frame
        {
            public Form F;
            public int Width, Pad, TextX, TextW, Y;
        }

        static Frame Open(string title, string message, string sub, int width)
        {
            Func<float, int> S = Theme.S;
            var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.Window,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.75f),
                AutoScaleMode = AutoScaleMode.None,
                KeyPreview = true,
            };
            var icon = Res.AppIcon(32);
            if (icon != null) f.Icon = icon;
            var fr = new Frame { F = f, Width = S(width), Pad = S(18) };
            int iconSize = S(56);
            fr.TextX = fr.Pad + iconSize + S(14);
            fr.TextW = fr.Width - fr.TextX - fr.Pad;
            f.Controls.Add(new PictureBox { Image = Res.Gear, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(fr.Pad, fr.Pad, iconSize, iconSize), BackColor = Theme.Window });
            var mf = new Font("Segoe UI Semibold", 11.5f);
            fr.Y = fr.Pad;
            var mh = TextRenderer.MeasureText(message, mf, new Size(fr.TextW, int.MaxValue), TextFormatFlags.WordBreak).Height;
            f.Controls.Add(new Label { Text = message, Font = mf, ForeColor = Theme.Title, Bounds = new Rectangle(fr.TextX, fr.Y, fr.TextW, mh + S(4)), UseMnemonic = false });
            fr.Y += mh + S(10);
            if (!string.IsNullOrEmpty(sub)) AddText(fr, sub, Theme.Text);
            fr.Y = Math.Max(fr.Y, fr.Pad + iconSize + S(8));
            f.HandleCreated += (s, e) => Native.DarkTitle(f.Handle);
            return fr;
        }

        static void AddText(Frame fr, string text, Color color)
        {
            var h = TextRenderer.MeasureText(text, fr.F.Font, new Size(fr.TextW, int.MaxValue), TextFormatFlags.WordBreak).Height;
            fr.F.Controls.Add(new Label { Text = text, ForeColor = color, Bounds = new Rectangle(fr.TextX, fr.Y, fr.TextW, h + Theme.S(4)), UseMnemonic = false });
            fr.Y += h + Theme.S(8);
        }

        static T Styled<T>(T c) where T : ButtonBase
        {
            c.BackColor = Theme.Window;
            c.ForeColor = Theme.Text;
            c.FlatStyle = FlatStyle.Flat;
            c.UseMnemonic = false;
            c.FlatAppearance.BorderColor = Theme.CardHi;
            c.FlatAppearance.CheckedBackColor = Theme.Inset;
            return c;
        }

        /// <summary>OK / Cancel buttons at the bottom; returns the OK button.</summary>
        static FButton Buttons(Frame fr, string okText)
        {
            Func<float, int> S = Theme.S;
            int bh = S(38), x = fr.Width - fr.Pad;
            var cancelText = Tr.T("Отмена", "Cancel");
            int w = Math.Max(S(120), TextRenderer.MeasureText(cancelText, new Font("Segoe UI Semibold", 9.75f)).Width + S(30));
            x -= w;
            var no = new FButton(cancelText, ButtonKind.Normal) { Bounds = new Rectangle(x, fr.Y, w, bh) };
            no.Click += (s, e) => { fr.F.DialogResult = DialogResult.Cancel; fr.F.Close(); };
            fr.F.Controls.Add(no);
            x -= S(10);
            var yesFont = new Font("Segoe UI Semibold", 11.5f);
            int yw = Math.Max(S(150), TextRenderer.MeasureText(okText, yesFont).Width + S(34));
            x -= yw;
            var yes = new FButton(okText, ButtonKind.Primary) { Bounds = new Rectangle(x, fr.Y, yw, bh) };
            fr.F.Controls.Add(yes);
            fr.Y += bh + fr.Pad;
            fr.F.ClientSize = new Size(fr.Width, fr.Y);
            fr.F.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { fr.F.DialogResult = DialogResult.Cancel; fr.F.Close(); } };
            return yes;
        }

        static string Describe(FileInfo f) => f == null ? Tr.T("нет", "none")
            : f.Name + " (" + Fmt.Size(f.Length) + ", " + Fmt.Date(f.LastWriteTime) + ")" + (f.Length + 64 * 1024 > Report.Limit ? Tr.T(" — больше 20 МБ", " — over 20 MB") : "");

        /// <summary>Report options. Null: cancelled.</summary>
        public static Report.Input AskReport(IWin32Window owner, FactorioInfo fac)
        {
            Func<float, int> S = Theme.S;
            var fr = Open(Tr.T("Отчёт о проблеме", "Problem report"), Tr.T("Отчёт о проблеме", "Problem report"),
                Tr.T("Лаунчер соберёт ZIP на рабочем столе: логи игры (текущий и прошлый запуск), строку версии, mod-list.json, mod-settings.dat, " +
                     "журналы лаунчера (sync-log.txt, crash.txt), config.ini игры и диагностику загрузки. Отправьте его в Discord сборки.\n" +
                     "Токен аккаунта Factorio (player-data.json) в архив не попадает, имя пользователя Windows в путях заменяется.",
                     "The launcher puts a ZIP on the desktop: game logs (this and the previous start), the version line, mod-list.json, mod-settings.dat, " +
                     "launcher logs (sync-log.txt, crash.txt), the game's config.ini and the load diagnostics. Send it to the modpack Discord.\n" +
                     "The Factorio account token (player-data.json) is not included; the Windows user name in paths is replaced."), 600);
            var last = Report.LastSave(fac, false);
            var auto = Report.LastSave(fac, true);
            var chk = Styled(new CheckBox { Text = Tr.T("Добавить сейв", "Add a save"), Checked = false });
            chk.Bounds = new Rectangle(fr.TextX, fr.Y, fr.TextW, S(26));
            fr.F.Controls.Add(chk);
            fr.Y += S(28);
            var rbLast = Styled(new RadioButton { Text = Tr.T("Последний сейв: ", "Latest save: ") + Describe(last), Checked = true });
            var rbAuto = Styled(new RadioButton { Text = Tr.T("Последнее автосохранение: ", "Latest autosave: ") + Describe(auto) });
            var rbPick = Styled(new RadioButton { Text = Tr.T("Выберу сам…", "Choose a file…") });
            foreach (var rb in new[] { rbLast, rbAuto, rbPick })
            {
                rb.Bounds = new Rectangle(fr.TextX + S(22), fr.Y, fr.TextW - S(22), S(26));
                // choosing a variant means "add a save" (disabled radio buttons are unreadable on the dark background)
                rb.Click += (s, e) => chk.Checked = true;
                fr.F.Controls.Add(rb);
                fr.Y += S(27);
            }
            if (last == null && auto != null) { rbAuto.Checked = true; }
            fr.Y += S(4);
            AddText(fr, Tr.T("Архив не больше 20 МБ (лимит Discord). Сейв, который не помещается, не добавится — лаунчер покажет, где он лежит, чтобы приложить его отдельно.",
                "The archive stays under 20 MB (the Discord limit). A save that does not fit is not added: the launcher shows where it is, to attach it separately."), Theme.Muted);
            var ok = Buttons(fr, Tr.T("Создать архив", "Create the archive"));
            Report.Input result = null;
            ok.Click += (s, e) =>
            {
                var inp = new Report.Input { Save = Report.SaveChoice.None };
                if (chk.Checked)
                {
                    if (rbLast.Checked) inp.Save = Report.SaveChoice.LastManual;
                    else if (rbAuto.Checked) inp.Save = Report.SaveChoice.LastAutosave;
                    else
                    {
                        using (var dlg = new OpenFileDialog
                        {
                            Title = Tr.T("Выберите сейв", "Choose a save"),
                            Filter = Tr.T("Сейвы Factorio (*.zip)|*.zip", "Factorio saves (*.zip)|*.zip"),
                        })
                        {
                            var wd = Report.WriteData(fac);
                            if (wd != null && Directory.Exists(Path.Combine(wd, "saves"))) dlg.InitialDirectory = Path.Combine(wd, "saves");
                            if (dlg.ShowDialog(fr.F) != DialogResult.OK) return;
                            inp.Save = Report.SaveChoice.Chosen;
                            inp.SavePath = dlg.FileName;
                        }
                    }
                }
                result = inp;
                fr.F.DialogResult = DialogResult.OK;
                fr.F.Close();
            };
            Show(fr.F, owner);
            fr.F.Dispose();
            return result;
        }

        /// <summary>The host's version line. Null: cancelled.</summary>
        public static string AskHostLine(IWin32Window owner)
        {
            Func<float, int> S = Theme.S;
            var fr = Open(Tr.T("Версия хоста", "Host's version"), Tr.T("Перейти на версию сборки хоста", "Switch to the host's build version"),
                Tr.T("Попросите хоста нажать «Скопировать версию» и вставьте его строку сюда. Лаунчер скачает ровно эту версию сборки " +
                     "(нужен интернет, качается только разница) и включит «Не менять версию», чтобы не предлагать обновление. " +
                     "Когда хост обновится, снимите эту галочку.",
                     "Ask the host to press \"Copy version\" and paste the line here. The launcher downloads exactly that build version " +
                     "(needs internet, only the difference is downloaded) and turns \"Keep this version\" on, so it does not offer the update. " +
                     "When the host updates, clear that check box."), 600);
            var box = new TextBox
            {
                Multiline = true,
                WordWrap = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Inset,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 9.5f),
                Bounds = new Rectangle(fr.Pad, fr.Y, fr.Width - 2 * fr.Pad, S(72)),
            };
            try { if (Clipboard.ContainsText() && VersionLine.ParseHost(Clipboard.GetText()) != null) box.Text = Clipboard.GetText().Trim(); } catch { }
            fr.F.Controls.Add(box);
            fr.Y += S(72) + S(8);
            var hint = new Label { ForeColor = Theme.Muted, Bounds = new Rectangle(fr.Pad, fr.Y, fr.Width - 2 * fr.Pad, S(22)), UseMnemonic = false };
            fr.F.Controls.Add(hint);
            fr.Y += S(28);
            Action check = () =>
            {
                var sha = VersionLine.ParseHost(box.Text);
                hint.Text = box.Text.Trim().Length == 0 ? "" : sha != null ? Tr.T("Версия сборки в строке: ", "Build version in the line: ") + sha
                    : Tr.T("В строке нет версии сборки (номер в скобках).", "The line has no build version (the id in parentheses).");
                hint.ForeColor = sha != null ? Theme.Green : Theme.Orange;
            };
            box.TextChanged += (s, e) => check();
            check();
            var ok = Buttons(fr, Tr.T("Перейти", "Switch"));
            string result = null;
            ok.Click += (s, e) =>
            {
                if (VersionLine.ParseHost(box.Text) == null) { box.Focus(); return; }
                result = box.Text.Trim();
                fr.F.DialogResult = DialogResult.OK;
                fr.F.Close();
            };
            fr.F.Shown += (s, e) => box.Focus();
            Show(fr.F, owner);
            fr.F.Dispose();
            return result;
        }
    }
}
