using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
        static FButton Buttons(Frame fr, string okText, string cancelText = null)
        {
            Func<float, int> S = Theme.S;
            int bh = S(38), x = fr.Width - fr.Pad;
            cancelText = cancelText ?? Tr.T("Отмена", "Cancel");
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

        /// <summary>
        /// A newer launcher is released: every version between the installed and the latest with its changes, a
        /// recommendation to update; asked on every start until updated (no "never" option, by the owner's decision).
        /// True: update now.
        /// </summary>
        public static bool AskSelfUpdate(IWin32Window owner, ReleaseInfo r, bool needsAdmin)
        {
            Func<float, int> S = Theme.S;
            var since = r.Since(AppInfo.Version);
            var versions = string.Join(", ", since.Select(c => c.Version).Reverse());
            var fr = Open(Tr.T("Обновление лаунчера", "Launcher update"),
                Tr.T("Вышла новая версия лаунчера: ", "A new launcher version is out: ") + r.Version,
                Tr.T("У вас версия ", "You have version ") + AppInfo.Version + ". " +
                (since.Count > 1 ? Tr.T("Новые версии: ", "New versions: ") + versions + ". " : "") +
                Tr.T("Рекомендуем обновиться: исправления и новые возможности приходят только с новой версией. " +
                     "Лаунчер скачает её (", "We recommend updating: fixes and new features come only with the new version. " +
                     "The launcher downloads it (") + Fmt.Size(r.Size) +
                Tr.T("), проверит и перезапустится. Настройки, моды и сейвы не меняются.",
                     "), checks it and restarts. Settings, mods and saves are not changed.") +
                (needsAdmin ? Tr.T("\n\nПапка лаунчера защищена Windows (например, Program Files): Windows попросит права администратора. " +
                                   "Чтобы этот вопрос больше не появлялся, перенесите папку игры вместе с лаунчером в обычную папку, например C:\\Games.",
                                   "\n\nThe launcher's folder is protected by Windows (e.g. Program Files): Windows asks for administrator rights. " +
                                   "To stop this question, move the game folder with the launcher to a normal folder, e.g. C:\\Games.") : ""), 680);
            var box = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Inset,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.25f),
                WordWrap = true,
                DetectUrls = false,
                Bounds = new Rectangle(fr.Pad, fr.Y, fr.Width - 2 * fr.Pad, S(260)),
            };
            Native.DarkScrollbars(box);
            var bold = new Font("Segoe UI Semibold", 10.5f);
            foreach (var c in since)
            {
                box.SelectionFont = bold;
                box.SelectionColor = Theme.Title;
                box.AppendText(AppInfo.Name + " " + c.Version + (c.Date.Length > 0 ? "  ·  " + c.Date : "") + "\n");
                box.SelectionFont = box.Font;
                box.SelectionColor = Theme.Text;
                box.AppendText(PlainMarkdown(c.Text) + "\n\n");
            }
            if (since.Count == 0) box.AppendText(Tr.T("Список изменений недоступен.", "The list of changes is not available."));
            box.SelectionStart = 0;
            fr.F.Controls.Add(box);
            fr.Y += S(260) + S(12);
            var ok = Buttons(fr, Tr.T("Обновить сейчас", "Update now"), Tr.T("Позже", "Later"));
            bool result = false;
            ok.Click += (s, e) => { result = true; fr.F.DialogResult = DialogResult.OK; fr.F.Close(); };
            Show(fr.F, owner);
            fr.F.Dispose();
            return result;
        }

        /// <summary>CHANGELOG markdown as plain text: bullets, no ** and backticks.</summary>
        public static string PlainMarkdown(string md)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var raw in (md ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Replace("**", "").Replace("`", "");
                var t = line.TrimStart();
                if (t.StartsWith("- ")) { if (sb.Length > 0) sb.Append('\n'); sb.Append("• ").Append(t.Substring(2)); }
                else if (t.Length > 0) sb.Append(sb.Length > 0 ? " " : "").Append(t);   // wrapped continuation of a bullet
            }
            return sb.ToString();
        }

        /// <summary>
        /// Several suitable game installations: which one is the player's (the launcher moves itself there and uses it).
        /// Null: not now.
        /// </summary>
        public static FactorioInfo ChooseInstall(IWin32Window owner, List<FactorioInfo> list)
        {
            Func<float, int> S = Theme.S;
            var fr = Open(Tr.T("Папка игры", "Game folder"), Tr.T("Найдено несколько подходящих установок ", "Several suitable installations of ") + AppInfo.RequiredGame,
                Tr.T("Лаунчер работает правильно, только когда лежит в папке игры, в которую вы играете со сборкой. " +
                     "Выберите её — лаунчер перенесёт себя туда, оставит ярлык на рабочем столе и запустится оттуда.",
                     "The launcher works properly only from the folder of the game you play the modpack with. " +
                     "Choose it: the launcher moves itself there, leaves a desktop shortcut and starts from there."), 640);
            var radios = new List<RadioButton>();
            foreach (var f in list)
            {
                var rb = Styled(new RadioButton { Text = f.Dir, Checked = radios.Count == 0 });
                rb.Bounds = new Rectangle(fr.TextX, fr.Y, fr.TextW, S(26));
                fr.F.Controls.Add(rb);
                radios.Add(rb);
                fr.Y += S(27);
            }
            fr.Y += S(8);
            var ok = Buttons(fr, Tr.T("Перенести сюда", "Move here"), Tr.T("Не сейчас", "Not now"));
            FactorioInfo result = null;
            ok.Click += (s, e) =>
            {
                int i = radios.FindIndex(r => r.Checked);
                if (i < 0) return;
                result = list[i];
                fr.F.DialogResult = DialogResult.OK;
                fr.F.Close();
            };
            Show(fr.F, owner);
            fr.F.Dispose();
            return result;
        }

        /// <summary>
        /// «Paranoidal 2.1»: the folder of the 2.1 game, from the installations found or chosen by hand. Returns the folder
        /// (checked by the caller); null: cancelled.
        /// </summary>
        public static string ChooseMigrationTarget(IWin32Window owner, List<FactorioInfo> list)
        {
            Func<float, int> S = Theme.S;
            var l = Lines.L21;
            var fr = Open("Paranoidal 2.1", Tr.T("Укажите папку Factorio 2.1", "Choose the Factorio 2.1 folder"),
                Tr.T("Лаунчер перенесёт в неё сейвы, сборку, настройки и себя, а потом докачает только то, чем сборка 2.1 отличается от вашей. " +
                     "Перед переездом он покажет, что именно и куда перенесёт.\nНужна Factorio " + l.VersionWords(false) +
                     " с дополнением Space Age (space-age, quality, elevated-rails, recycler).",
                     "The launcher moves the saves, the build, the settings and itself there, then downloads only what the 2.1 build changes. " +
                     "Before the move it shows what goes where.\nIt needs Factorio " + l.VersionWords(true) +
                     " with the Space Age expansion (space-age, quality, elevated-rails, recycler)."), 660);
            var radios = new List<RadioButton>();
            if (list.Count == 0)
                AddText(fr, Tr.T("Подходящая Factorio 2.1 не найдена — укажите её папку сами.\n", "No suitable Factorio 2.1 was found: choose its folder yourself.\n") +
                    FactorioInfo.HowToGet21(), Theme.Orange);
            foreach (var f in list)
            {
                var rb = Styled(new RadioButton { Text = f.Dir + "  —  " + f.Describe(null, l), Checked = radios.Count == 0 });
                rb.Bounds = new Rectangle(fr.TextX, fr.Y, fr.TextW, S(26));
                fr.F.Controls.Add(rb);
                radios.Add(rb);
                fr.Y += S(27);
            }
            fr.Y += S(8);
            string result = null;
            Action browse = () =>
            {
                using (var dlg = new FolderBrowserDialog
                {
                    Description = Tr.T("Папка Factorio 2.1 (в ней есть bin\\x64\\factorio.exe)", "The Factorio 2.1 folder (it has bin\\x64\\factorio.exe)"),
                    ShowNewFolderButton = false,
                })
                {
                    if (dlg.ShowDialog(fr.F) != DialogResult.OK) return;
                    result = dlg.SelectedPath;
                    fr.F.DialogResult = DialogResult.OK;
                    fr.F.Close();
                }
            };
            int by = fr.Y;
            var ok = Buttons(fr, list.Count > 0 ? Tr.T("Далее", "Next") : Tr.T("Выбрать папку…", "Choose a folder…"));
            ok.Click += (s, e) =>
            {
                if (list.Count == 0) { browse(); return; }
                int i = radios.FindIndex(r => r.Checked);
                if (i < 0) return;
                result = list[i].Dir;
                fr.F.DialogResult = DialogResult.OK;
                fr.F.Close();
            };
            if (list.Count > 0)
            {
                var other = new FButton(Tr.T("Другая папка…", "Another folder…"), ButtonKind.Normal) { Bounds = new Rectangle(fr.Pad, by, S(150), S(38)) };
                other.Click += (s, e) => browse();
                fr.F.Controls.Add(other);
            }
            Show(fr.F, owner);
            fr.F.Dispose();
            return result;
        }

        /// <summary>The move to Paranoidal 2.1: what moves where, what is deleted, that there is no way back. True: move.</summary>
        public static bool ConfirmMigration(IWin32Window owner, MigrationPlan p)
        {
            Func<float, int> S = Theme.S;
            var sb = new System.Text.StringBuilder();
            Func<string, string, string> T = Tr.T;
            if (p.InPlace)
                sb.Append(T("Игра в папке ", "The game in ")).Append(p.NewDir).Append(T(" уже версии ", " is version ")).Append(p.NewVersion)
                  .Append(T(". Файлы не переносятся: лаунчер переключит сборку на 2.1 и докачает только разницу.",
                               " already. Nothing is moved: the launcher switches the build to 2.1 and downloads only the difference."));
            else
            {
                sb.Append(T("Из: ", "From: ")).Append(p.OldDir).Append('\n').Append(T("В:   ", "To:   ")).Append(p.NewDir).Append("\n\n");
                sb.Append(T("Переедут (перемещением, без копий):", "What moves (moved, not copied):")).Append('\n');
                if (p.SharedWd)
                    sb.Append(T("• сейвы, чертежи и настройки игры переносить не нужно: обе игры пользуются одной папкой ", "• saves, blueprints and game settings stay: both games use one folder ")).Append(p.NewWd).Append('\n');
                else
                {
                    sb.Append(T("• сейвы: ", "• saves: ")).Append(p.Saves).Append(p.Saves > 0 ? " (" + Fmt.Size(p.SavesBytes) + ")" : "").Append(" → ").Append(Path.Combine(p.NewWd, "saves")).Append('\n');
                    if (p.PlayerItems.Count > 0) sb.Append("• ").Append(string.Join(", ", p.PlayerItems)).Append(" → ").Append(p.NewWd).Append('\n');
                    if (p.Steps.Any(s => s.Kind == "config"))
                        sb.Append(T("• настройки игры (графика, клавиши): копия config.ini игры 2.0 → ", "• game settings (graphics, controls): a copy of the 2.0 config.ini → "))
                          .Append(p.NewCfg).Append(T(", пути — от игры 2.1", ", paths of the 2.1 game")).Append('\n');
                }
                if (p.ModsStay)
                    sb.Append(T("• своя папка модов ", "• your own mods folder ")).Append(p.ModsDir).Append(T(" остаётся на месте и переходит на 2.1", " stays where it is and switches to 2.1")).Append('\n');
                else
                    sb.Append(T("• сборка и ваши моды (", "• the build and your mods (")).Append(Fmt.Size(p.ModsBytes)).Append(") → ").Append(p.NewMods)
                      .Append(T(", потом докачается только разница со сборкой 2.1", ", then only the difference to the 2.1 build is downloaded")).Append('\n');
                sb.Append(T("• лаунчер, его данные и гайд → ", "• the launcher, its data and the guide → ")).Append(p.NewDir).Append(T("; ярлыки будут вести туда", "; the shortcuts will point there"));
            }
            if (p.Foreign.Count > 0)
                sb.Append("\n\n").Append(T("Моды не из сборки (", "Mods not from the build (")).Append(p.Foreign.Count).Append("): ")
                  .Append(string.Join(", ", p.Foreign.Take(12))).Append(p.Foreign.Count > 12 ? " …" : "")
                  .Append(p.InPlace ? T(" — останутся как есть", ": they stay as they are") : T(" — переедут как есть", ": they move as they are"))
                  .Append(T("; при обновлении лаунчер спросит, оставить ли их: многие моды для 2.0 в 2.1 не загрузятся.",
                             "; the update asks whether to keep them, many mods for 2.0 do not load in 2.1."));
            var fr = Open(T("Переезд на Paranoidal 2.1", "Moving to Paranoidal 2.1"),
                p.InPlace ? T("Перейти на Paranoidal 2.1?", "Switch to Paranoidal 2.1?")
                          : T("Перенести сейвы, сборку и настройки в Factorio ", "Move the saves, the build and the settings to Factorio ") + p.NewVersion + "?",
                sb.ToString(), 760);
            AddText(fr, T("⚠ Переезд необратим. Сейв, сохранённый в 2.1, в Factorio 2.0 не откроется, а настройки модов (mod-settings.dat) после 2.1 игра 2.0 не прочитает. " +
                            "Вернуться на Paranoidal 2.0 этим лаунчером нельзя. Играйте на сервере? Переезжайте вместе с хостом.",
                            "⚠ The move cannot be undone. A save made in 2.1 does not open in Factorio 2.0, and the 2.0 game cannot read the mod settings (mod-settings.dat) after 2.1. " +
                            "This launcher cannot go back to Paranoidal 2.0. Playing on a server? Move together with the host."), Theme.Red);
            string removed;
            if (p.InPlace)
                removed = T("⚠ Сбрасываются: откат сборки 2.0, «Не менять версию» и ответы «Оставить» про лишние моды.",
                            "⚠ Reset: the 2.0 build rollback, \"Keep this version\" and the \"Keep\" answers about extra mods.");
            else
                removed = T("⚠ Из папки 2.0 удалится всё, что принёс лаунчер: сам лаунчер, его данные (в том числе откат сборки 2.0), гайд, ответы «Оставить» про лишние моды",
                            "⚠ Everything the launcher brought is removed from the 2.0 folder: the launcher, its data (with the 2.0 build rollback), the guide, the \"Keep\" answers about extra mods") +
                    (p.CacheItems.Count > 0 ? T(" и кэш загрузки игры 2.0 (", " and the load cache of the 2.0 game (") + string.Join(", ", p.CacheItems) + " — " + Fmt.Size(p.CacheBytes) + ")" : "") +
                    T(". В папке 2.0 останутся файлы игры, её config.ini и логи.", ". The 2.0 folder keeps the game files, its config.ini and logs.");
            AddText(fr, removed, Theme.Orange);
            if (p.Asides.Count > 0)
                AddText(fr, T("Уже есть в папке 2.1 и не будет перезаписано, а останется рядом под другим именем: ", "Already in the 2.1 folder and not overwritten, kept next to it under another name: ") +
                    string.Join("; ", p.Asides), Theme.Muted);
            var box = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Inset,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 8.5f),
                WordWrap = false,
                DetectUrls = false,
                Bounds = new Rectangle(fr.Pad, fr.Y, fr.Width - 2 * fr.Pad, S(130)),
                Text = string.Join("\n", p.Steps.Select(s => Migration.Describe(s, Tr.En))),
            };
            Native.DarkScrollbars(box);
            fr.F.Controls.Add(box);
            fr.Y += S(130) + S(12);
            var ok = Buttons(fr, p.InPlace ? T("Перейти на 2.1", "Switch to 2.1") : T("Перенести на 2.1", "Move to 2.1"));
            bool result = false;
            ok.Click += (s, e) => { result = true; fr.F.DialogResult = DialogResult.OK; fr.F.Close(); };
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
