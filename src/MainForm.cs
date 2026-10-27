using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Paranoidal
{
    sealed class MainForm : Form, ISyncUi
    {
        readonly HeaderPanel header = new HeaderPanel();
        readonly Card cardBuild = new Card("");
        readonly Card cardGame = new Card("");
        readonly Label lblInstalled, lblRemote, lblStatus, lblFacPath, lblFacVer, lblModsPath, lblModsNote, lblLoad, lblStage, lblDetail;
        readonly FButton btnPickFac, btnPickMods, btnOpenMods, btnUpdate, btnPlay, btnCopy, btnVerify, btnCheck, btnCancel;
        readonly FButton btnLang, btnRollback, btnHost, btnReport, btnGuide, btnShortcuts;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 100 };
        readonly CheckBox chkCache, chkPin;
        readonly StripeProgress progress = new StripeProgress();
        readonly FButton[] tabs;
        readonly Control[] pages;
        readonly ListView lvCommits, lvMods;
        readonly RichTextBox rtbLog;
        readonly Font fontBold = new Font("Segoe UI Semibold", 9.75f);
        readonly Font fontStatus = new Font("Segoe UI Semibold", 13f);
        readonly Timer gameWatch = new Timer { Interval = 3000 };

        GitRunner git;
        SyncEngine engine;
        FactorioInfo fac;
        string modsDir, installed, installedDate, remote, remoteDate;
        bool busy, cancelable, offline, adopted, gameRunning, pinSetting;
        int modified = -1;
        BuildFound found;
        string rollbackTarget, rollbackDate;
        bool rollbackNewer;
        List<CommitInfo> shownCommits;
        bool shownCommitsNew, shownCommitsCached;
        List<ModChange> shownMods;
        LoadVerdict lastLoad;
        // releases of the launcher and of the guide (checked together with the build, Releases.CheckAll)
        ReleaseInfo launcherRel, guideRel;
        string guideLocal, guideLocalVersion;
        bool selfUpdatePending, restarting;

        /// <summary>
        /// Tests only: PARANOIDAL_LAUNCHER_TEST_AUTO=&lt;journal file&gt; runs the window far off-screen without activation,
        /// answers "yes" to the self-update and the move questions (first installation), skips the administrator path,
        /// appends the journal to that file on close and closes after the first status check.
        /// </summary>
        static readonly string TestAuto = Environment.GetEnvironmentVariable("PARANOIDAL_LAUNCHER_TEST_AUTO");
        static bool Auto => !string.IsNullOrWhiteSpace(TestAuto);
        protected override bool ShowWithoutActivation => Auto;

        public MainForm()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
            Text = AppInfo.Name + " " + AppInfo.Version;
            var icon = Res.AppIcon(32);
            if (icon != null) Icon = icon;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            StartPosition = Auto ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            if (Auto) Location = new Point(-32000, -32000);
            ClientSize = new Size(Theme.S(1000), Theme.S(800));
            MinimumSize = SizeFromClientSize(new Size(Theme.S(900), Theme.S(740)));

            Controls.Add(header);
            Controls.Add(cardBuild);
            Controls.Add(cardGame);
            btnLang = B(header, "", ButtonKind.Normal, () => Tr.Set(!Tr.En));
            btnGuide = B(header, "", ButtonKind.Normal, OpenGuide);
            btnShortcuts = B(header, "", ButtonKind.Normal, MakeShortcuts);

            lblInstalled = L(cardBuild, Theme.Text);
            lblRemote = L(cardBuild, Theme.Text);
            lblStatus = L(cardBuild, Theme.Muted, fontStatus);
            pinSetting = Settings.Get("pin") == "1";
            chkPin = Check(cardBuild, pinSetting);
            chkPin.CheckedChanged += (s, e) =>
            {
                if (chkPin.Checked == pinSetting) return;
                pinSetting = chkPin.Checked;
                Settings.Set("pin", pinSetting ? "1" : "0");
                AppendLog(pinSetting ? "Версия сборки закреплена: лаунчер не обновляет сборку и не предлагает обновление."
                    : "Версия сборки больше не закреплена.", LogKind.Info);
                UpdateUi();
            };
            btnRollback = B(cardBuild, "", ButtonKind.Normal, DoRollback);
            btnHost = B(cardBuild, "", ButtonKind.Normal, DoHost);

            lblFacPath = L(cardGame, Theme.Text);
            lblFacVer = L(cardGame, Theme.Muted, fontBold);
            lblModsPath = L(cardGame, Theme.Text);
            lblModsNote = L(cardGame, Theme.Muted);
            lblLoad = L(cardGame, Theme.Muted);
            btnPickFac = B(cardGame, "", ButtonKind.Normal, PickFactorio);
            btnPickMods = B(cardGame, "", ButtonKind.Normal, PickMods);
            btnOpenMods = B(cardGame, "", ButtonKind.Normal, OpenMods);
            chkCache = Check(cardGame, Settings.Get("game-cache") != "0");
            chkCache.CheckedChanged += (s, e) =>
            {
                Settings.Set("game-cache", chkCache.Checked ? "1" : "0");
                AppendLog(chkCache.Checked ? "Кэш загрузки игры будет включаться перед каждым запуском."
                    : "Кэш загрузки игры лаунчер больше не включает (config.ini игры не меняется).", LogKind.Info);
            };

            btnUpdate = B(this, "", ButtonKind.Primary, () => DoUpdate(false, null));
            btnPlay = B(this, "", ButtonKind.Success, Play);
            btnCopy = B(this, "", ButtonKind.Normal, CopyCommit);
            btnVerify = B(this, "", ButtonKind.Normal, DoVerify);
            btnCheck = B(this, "", ButtonKind.Normal, CheckStatus);
            btnReport = B(this, "", ButtonKind.Normal, DoReport);
            btnCancel = B(this, "", ButtonKind.Normal, CancelOp);
            btnCancel.Visible = false;

            lblStage = L(this, Theme.Title, fontBold);
            lblDetail = L(this, Theme.Muted);
            Controls.Add(progress);

            lvCommits = MakeList(new[] { 140, 600 }, 1);
            lvMods = MakeList(new[] { 360, 220, 100 }, 0);
            rtbLog = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Inset,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 9.5f),
                WordWrap = true,
                DetectUrls = false,
            };
            Native.DarkScrollbars(rtbLog);
            Controls.Add(rtbLog);

            pages = new Control[] { lvCommits, lvMods, rtbLog };
            tabs = new[] { new FButton("", ButtonKind.Tab), new FButton("", ButtonKind.Tab), new FButton("", ButtonKind.Tab) };
            for (int i = 0; i < tabs.Length; i++)
            {
                int idx = i;
                tabs[i].Click += (s, e) => SelectTab(idx);
                Controls.Add(tabs[i]);
            }
            SelectTab(0);

            gameWatch.Tick += (s, e) => WatchGame();
            Tr.Changed += () =>
            {
                ApplyTexts();
                UpdateUi();
                DoLayout();
                // the line of the last operation is in the old language: show the state instead
                if (!busy && engine != null) { Idle(StatusIdleText()); lblDetail.Text = ""; }
            };
            Resize += (s, e) => DoLayout();
            HandleCreated += (s, e) => Native.DarkTitle(Handle);
            Shown += (s, e) => Startup();
            ApplyTexts();
            DoLayout();
            SetBusy(false);
            Idle(Tr.T("Запуск…", "Starting…"));
            UpdateUi();
        }

        // ------------------------------------------------------------------ building blocks

        Label L(Control parent, Color fore, Font font = null)
        {
            var l = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = parent.BackColor,
                ForeColor = fore,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
            };
            if (font != null) l.Font = font;
            parent.Controls.Add(l);
            return l;
        }

        FButton B(Control parent, string text, ButtonKind kind, Action click)
        {
            var b = new FButton(text, kind);
            b.Click += (s, e) => click();
            parent.Controls.Add(b);
            return b;
        }

        CheckBox Check(Control parent, bool value)
        {
            var c = new CheckBox
            {
                Checked = value,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat,
                UseMnemonic = false,
            };
            c.FlatAppearance.BorderColor = Theme.CardHi;
            c.FlatAppearance.CheckedBackColor = Theme.Inset;
            parent.Controls.Add(c);
            return c;
        }

        ListView MakeList(int[] widths, int fillCol)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Inset,
                ForeColor = Theme.Text,
                OwnerDraw = true,
                MultiSelect = false,
            };
            for (int i = 0; i < widths.Length; i++) lv.Columns.Add("", Theme.S(widths[i]));
            var hdrFont = fontBold;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Theme.Header)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Theme.CardBorder))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
                TextRenderer.DrawText(e.Graphics, e.Header.Text, hdrFont, Rectangle.Inflate(e.Bounds, -Theme.S(6), 0), Theme.Title,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            lv.DrawItem += (s, e) => { e.DrawDefault = true; };
            lv.DrawSubItem += (s, e) => { e.DrawDefault = true; };
            lv.Resize += (s, e) =>
            {
                int other = 0;
                for (int i = 0; i < lv.Columns.Count; i++) if (i != fillCol) other += lv.Columns[i].Width;
                lv.Columns[fillCol].Width = Math.Max(Theme.S(120), lv.ClientSize.Width - other - 2);
            };
            Native.DarkScrollbars(lv);
            Controls.Add(lv);
            return lv;
        }

        void SelectTab(int idx)
        {
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].Checked = i == idx;
                pages[i].Visible = i == idx;
            }
        }

        /// <summary>Every fixed text of the window in the interface language (called again when it changes).</summary>
        void ApplyTexts()
        {
            cardBuild.Title = Tr.T("СБОРКА", "BUILD");
            cardGame.Title = Tr.T("FACTORIO И ПАПКА МОДОВ", "FACTORIO AND MODS FOLDER");
            cardBuild.Invalidate();
            cardGame.Invalidate();
            header.Invalidate();
            btnLang.Text = Tr.T("Язык: RU", "Language: EN");
            tips.SetToolTip(btnLang, Tr.T("Switch to English", "Переключить на русский"));
            btnGuide.Text = Tr.T("📖 Гайд", "📖 Guide");
            btnShortcuts.Text = Tr.T("Ярлыки", "Shortcuts");
            tips.SetToolTip(btnShortcuts, Tr.T(
                "Создать ярлыки лаунчера на рабочем столе и в меню «Пуск» (папка Paranoidal, там же гайд по сборке).\n" +
                "Сам лаунчер остаётся в папке игры: там он работает правильно.",
                "Create launcher shortcuts on the desktop and in the Start menu (Paranoidal folder, with the modpack guide).\n" +
                "The launcher itself stays in the game folder, where it works properly."));
            ShowGuideState();
            btnPickFac.Text = Tr.T("Выбрать…", "Choose…");
            btnPickMods.Text = Tr.T("Выбрать…", "Choose…");
            btnOpenMods.Text = Tr.T("Открыть", "Open");
            chkCache.Text = Tr.T("Кэш загрузки игры (быстрый запуск)", "Game load cache (fast start)");
            tips.SetToolTip(chkCache, Tr.T(
                "Перед каждым запуском игры лаунчер ставит в config.ini игры:\n" +
                "cache-prototype-data, cache-sprite-atlas и compress-sprite-atlas-cache = true.\n" +
                "Первые два запуска после обновления сборки долгие (строится кэш),\n" +
                "дальше игра грузится до меню примерно за 25 секунд вместо 1,5–2 минут.\n" +
                "Остальные настройки config.ini не трогаются; копия до первой правки — config.ini.paranoidal-backup.\n" +
                "Если снять галочку, лаунчер перестанет менять config.ini, но уже включённый кэш останется.",
                "Before every game start the launcher sets in the game's config.ini:\n" +
                "cache-prototype-data, cache-sprite-atlas and compress-sprite-atlas-cache = true.\n" +
                "The first two starts after a build update are long (the cache is built),\n" +
                "then the game loads to the menu in about 25 seconds instead of 1.5–2 minutes.\n" +
                "Other settings of config.ini are not touched; a copy before the first change: config.ini.paranoidal-backup.\n" +
                "Clearing the box stops the launcher from changing config.ini; a cache that is already on stays on."));
            chkPin.Text = Tr.T("Не менять версию", "Keep this version");
            tips.SetToolTip(chkPin, Tr.T(
                "Закрепить установленную версию сборки: лаунчер не обновляет её и не предлагает обновление перед игрой.\n" +
                "Когда это нужно:\n" +
                "• сервер, где хост ещё не обновился: версии сборки у всех игроков должны совпадать байт в байт;\n" +
                "• обновление сломало сейв или прохождение — подождать исправления;\n" +
                "• идёт совместная игра или серия — все остаются на одной версии до конца;\n" +
                "• мобильный или лимитный интернет — обновиться позже;\n" +
                "• запись или стрим — без неожиданных изменений посреди сессии;\n" +
                "• нужно воспроизвести баг на конкретной версии для отчёта.\n" +
                "«Проверить файлы» продолжает работать: сверяет файлы с закреплённой версией.",
                "Keep the installed build version: the launcher does not update it and does not offer the update before playing.\n" +
                "When it helps:\n" +
                "• a server whose host has not updated yet: every player needs the same build, byte for byte;\n" +
                "• an update broke a save or the playthrough: wait for the fix;\n" +
                "• a co-op game or a series: everybody stays on one version to the end;\n" +
                "• mobile or metered internet: update later;\n" +
                "• recording or streaming: no surprises in the middle of a session;\n" +
                "• reproducing a bug on a specific version for a report.\n" +
                "\"Check files\" keeps working: it checks the files against the kept version."));
            btnHost.Text = Tr.T("Версия хоста…", "Host's version…");
            tips.SetToolTip(btnHost, Tr.T(
                "Перейти ровно на версию сборки хоста по его строке «Скопировать версию».\n" +
                "Нужен интернет; скачивается только разница. После перехода включается «Не менять версию».",
                "Switch to exactly the host's build version by his \"Copy version\" line.\n" +
                "Needs internet; only the difference is downloaded. \"Keep this version\" is turned on afterwards."));
            btnPlay.Text = Tr.T("▶  ИГРАТЬ", "▶  PLAY");
            btnCopy.Text = Tr.T("Скопировать версию", "Copy version");
            btnVerify.Text = Tr.T("Проверить файлы", "Check files");
            btnCheck.Text = Tr.T("Проверить обновления", "Check for updates");
            btnReport.Text = Tr.T("Отчёт о проблеме", "Problem report");
            btnCancel.Text = Tr.T("Отмена", "Cancel");
            tips.SetToolTip(btnCopy, Tr.T(
                "Скопировать строку для сверки с хостом и для сообщений о багах:\n" +
                "версия сборки, отпечаток стартовых настроек модов, включённые моды не из сборки, версия лаунчера.\n" +
                "Если игра пишет «Содержимое мода отличается», строки у хоста и игрока должны совпасть.\n" +
                "Эту строку игрок вставляет в «Версия хоста…», чтобы перейти на версию хоста.",
                "Copy the line to compare with the host and to put into bug reports:\n" +
                "build version, fingerprint of the startup mod settings, enabled mods not from the build, launcher version.\n" +
                "If the game says \"mod content differs\", the host's and the player's lines must be equal.\n" +
                "A player pastes this line into \"Host's version…\" to switch to the host's version."));
            tips.SetToolTip(btnVerify, Tr.T(
                "Прочитать заново все файлы установленной сборки, сравнить их с GitHub побайтно\n" +
                "и перекачать отличающиеся. Версию сборки не меняет. Помогает при ошибке\n" +
                "«Содержимое мода отличается», если файлы модов копировались или правились вручную.\n" +
                "Без интернета работает, пока все файлы целы.",
                "Read every file of the installed build again, compare it with GitHub byte for byte\n" +
                "and download the ones that differ. Does not change the build version. Helps with\n" +
                "\"mod content differs\" when mod files were copied or edited by hand.\n" +
                "Works offline as long as no file is broken."));
            tips.SetToolTip(btnCheck, Tr.T("Сравнить установленную версию сборки с последней на GitHub. Файлы не меняются.",
                "Compare the installed build version with the latest one on GitHub. Files are not changed."));
            tips.SetToolTip(btnReport, Tr.T(
                "Собрать ZIP на рабочий стол для Discord сборки: логи игры, строка версии, mod-list.json,\n" +
                "mod-settings.dat, журналы лаунчера, диагностика загрузки и по желанию сейв (до 20 МБ всего).",
                "Put a ZIP on the desktop for the modpack Discord: game logs, the version line, mod-list.json,\n" +
                "mod-settings.dat, launcher logs, the load diagnostics and optionally a save (20 MB in total)."));
            tips.SetToolTip(lblModsNote, Tr.T(
                "Перед обновлением и запуском игры лаунчер ставит в mod-list.json:\n" +
                "quality и elevated-rails включены, space-age выключен — как на сервере.\n" +
                "Остальные строки mod-list.json и настройки модов (mod-settings.dat) не трогаются.",
                "Before an update and a game start the launcher sets in mod-list.json:\n" +
                "quality and elevated-rails on, space-age off, as on the server.\n" +
                "Other lines of mod-list.json and the mod settings (mod-settings.dat) are not touched."));
            lvCommits.Columns[0].Text = Tr.T("Дата", "Date");
            lvCommits.Columns[1].Text = Tr.T("Что изменилось", "What changed");
            lvMods.Columns[0].Text = Tr.T("Мод", "Mod");
            lvMods.Columns[1].Text = Tr.T("Что произошло", "What happened");
            lvMods.Columns[2].Text = Tr.T("Файлов", "Files");
            lvCommits.Invalidate();
            lvMods.Invalidate();
            tabs[2].Text = Tr.T("Журнал", "Journal");
            tips.SetToolTip(tabs[2], Tr.T("Журнал всегда на русском: его читает команда сборки.", "The journal is always in Russian: the modpack team reads it."));
            FillCommits(shownCommits, shownCommitsNew, shownCommitsCached);
            FillMods(shownMods);
            ShowLoad();
        }

        void DoLayout()
        {
            Func<float, int> S = Theme.S;
            int W = ClientSize.Width, H = ClientSize.Height, m = S(14);
            if (W < 10) return;
            header.Bounds = new Rectangle(0, 0, W, S(132));
            // left column of the header: language, guide, shortcuts (the logo is centred, ~150 px from the edge at the minimal width)
            int hw = Math.Max(S(92), new[] { btnLang, btnGuide, btnShortcuts }.Max(b => TextRenderer.MeasureText(b.Text, b.Font).Width) + S(20));
            btnLang.Bounds = new Rectangle(S(10), S(8), hw, S(26));
            btnGuide.Bounds = new Rectangle(S(10), S(40), hw, S(26));
            btnShortcuts.Bounds = new Rectangle(S(10), S(72), hw, S(26));
            int y = header.Bottom + m;
            int cw = (W - 3 * m) * 45 / 100, ch = S(218);
            cardBuild.Bounds = new Rectangle(m, y, cw, ch);
            cardGame.Bounds = new Rectangle(2 * m + cw, y, W - 3 * m - cw, ch);

            int ix = S(14), iw = cw - 2 * ix;
            lblInstalled.Bounds = new Rectangle(ix, S(46), iw, S(24));
            lblRemote.Bounds = new Rectangle(ix, S(74), iw, S(24));
            lblStatus.Bounds = new Rectangle(ix, S(110), iw, S(32));
            int bh = S(28), by = S(176);
            int wHost = TextRenderer.MeasureText(btnHost.Text, btnHost.Font).Width + S(22);
            int wRb = Math.Max(S(84), TextRenderer.MeasureText(btnRollback.Text, btnRollback.Font).Width + S(22));
            btnHost.Bounds = new Rectangle(cw - ix - wHost, by, wHost, bh);
            btnRollback.Bounds = new Rectangle(btnHost.Left - S(8) - wRb, by, wRb, bh);
            chkPin.Bounds = new Rectangle(ix, by + S(2), Math.Max(S(60), btnRollback.Left - ix - S(8)), S(24));

            int gw = cardGame.Width, bw = S(104);
            btnPickFac.Bounds = new Rectangle(gw - ix - bw, S(42), bw, S(28));
            lblFacPath.Bounds = new Rectangle(ix, S(42), gw - 2 * ix - bw - S(8), S(24));
            lblFacVer.Bounds = new Rectangle(ix, S(66), gw - 2 * ix - bw - S(8), S(22));
            btnPickMods.Bounds = new Rectangle(gw - ix - bw, S(96), bw, S(28));
            btnOpenMods.Bounds = new Rectangle(gw - ix - bw, S(126), bw, S(24));
            lblModsPath.Bounds = new Rectangle(ix, S(96), gw - 2 * ix - bw - S(8), S(24));
            lblModsNote.Bounds = new Rectangle(ix, S(120), gw - 2 * ix - bw - S(8), S(30));
            chkCache.Bounds = new Rectangle(ix, S(154), gw - 2 * ix, S(24));
            lblLoad.Bounds = new Rectangle(ix, S(180), gw - 2 * ix, S(26));

            y = cardBuild.Bottom + m;
            int ah = S(48), sh = ah - S(12), gap = S(10);
            int wCopy = S(165), wVerify = S(150), wCheck = S(180);
            // the two main buttons give up width first, so the row fits the minimal window
            int pw = Math.Max(S(150), Math.Min(S(220), (W - 2 * m - wCopy - wVerify - wCheck - 4 * gap) / 2));
            btnUpdate.Bounds = new Rectangle(m, y, pw, ah);
            btnPlay.Bounds = new Rectangle(btnUpdate.Right + gap, y, pw, ah);
            btnCopy.Bounds = new Rectangle(btnPlay.Right + gap, y + S(6), wCopy, sh);
            btnVerify.Bounds = new Rectangle(btnCopy.Right + gap, y + S(6), wVerify, sh);
            btnCheck.Bounds = new Rectangle(W - m - wCheck, y + S(6), wCheck, sh);
            y += ah + m;

            lblStage.Bounds = new Rectangle(m, y, W - 2 * m, S(22));
            y += S(24);
            btnCancel.Bounds = new Rectangle(W - m - S(110), y, S(110), S(28));
            progress.Bounds = new Rectangle(m, y, (btnCancel.Visible ? btnCancel.Left - S(10) : W - m) - m, S(28));
            y += S(30);
            lblDetail.Bounds = new Rectangle(m, y, W - 2 * m, S(22));
            y += S(22) + S(8);

            int tw = S(190);
            for (int i = 0; i < tabs.Length; i++) tabs[i].Bounds = new Rectangle(m + i * (tw + S(3)), y, tw, S(32));
            int wRep = Math.Max(S(170), TextRenderer.MeasureText(btnReport.Text, btnReport.Font).Width + S(24));
            btnReport.Bounds = new Rectangle(W - m - wRep, y + S(2), wRep, S(28));
            y += S(32);
            foreach (var p in pages) p.Bounds = new Rectangle(m, y, W - 2 * m, Math.Max(S(60), H - y - m));
        }

        // ------------------------------------------------------------------ state → UI

        bool Pinned => pinSetting && installed != null;

        /// <summary>A Paranoidal 2.0 build is in mods\ even if its version is unknown: the game can be started.</summary>
        bool BuildPresent => installed != null || (found != null && found.Present && !found.Legacy);

        void UpdateUi()
        {
            if (installed != null)
                lblInstalled.Text = Tr.T("Установлена версия:  ", "Installed version:  ") + (installedDate != null ? Tr.T("от ", "of ") + installedDate : Tr.T("есть", "yes"));
            else if (found != null && found.Present && found.Legacy)
                lblInstalled.Text = Tr.T("Установлена версия:  старая, для Factorio ", "Installed version:  old, for Factorio ") + found.FactorioVersion +
                    (found.ZzzVersion != null ? " (zzzparanoidal " + found.ZzzVersion + ")" : "");
            else if (found != null && found.Present && found.Near != null)
                lblInstalled.Text = Tr.T("Установлена версия:  похожа на версию от ", "Installed version:  close to the build of ") + found.NearDate +
                    Tr.T(" (отличаются модов: ", " (mods differ: ") + found.NearDiff + Tr.T(" из ", " of ") + found.NearTotal + ")";
            else if (found != null && found.Present)
                lblInstalled.Text = Tr.T("Установлена версия:  не определена", "Installed version:  unknown") +
                    (found.Fingerprint != null ? Tr.T(" · отпечаток ", " · fingerprint ") + found.Fingerprint : "");
            else
                lblInstalled.Text = Tr.T("Установлена версия:  —", "Installed version:  —");

            lblRemote.Text = Tr.T("Последняя версия:  ", "Latest version:  ") + (remote == null
                ? (offline ? Tr.T("нет связи с GitHub", "no connection to GitHub") : Tr.T("не проверено", "not checked"))
                : remoteDate != null ? Tr.T("от ", "of ") + remoteDate : installed == remote && installedDate != null ? Tr.T("от ", "of ") + installedDate : Tr.T("есть", "yes"));

            string statusTip = null;
            if (modsDir == null)
                Status(Tr.T("Укажите папку Factorio", "Choose the Factorio folder"), Theme.Orange);
            else if (installed == null)
            {
                if (found == null || !found.Present) Status(Tr.T("● Сборка ещё не установлена", "● The build is not installed yet"), Theme.Orange);
                else if (found.Legacy) Status(Tr.T("● В папке mods старая сборка (Factorio 1.1)", "● The mods folder has the old build (Factorio 1.1)"), Theme.Red);
                else if (found.Near != null)
                {
                    Status(Tr.T("● Сборка Paranoidal с изменениями", "● Paranoidal build with changes"), Theme.Orange);
                    statusTip = Tr.T("Отличаются моды: ", "Mods that differ: ") + string.Join(", ", found.NearMods.Take(30)) + (found.NearMods.Count > 30 ? " …" : "");
                }
                else Status(Tr.T("● Сборка найдена, версия не определена", "● Build found, version unknown"), Theme.Orange);
            }
            else if (modified > 0)
            {
                Status(Tr.T("⚠ Изменено файлов сборки: ", "⚠ Build files changed: ") + modified, Theme.Orange);
                statusTip = Tr.T("Файлы модов сборки в папке mods отличаются от установленной версии.\n«Проверить файлы» вернёт их (для исправления нужен интернет).",
                    "Files of the build's mods differ from the installed version.\n\"Check files\" restores them (fixing needs internet).");
            }
            else if (remote == null)
                Status(offline ? Tr.T("● Нет связи с GitHub", "● No connection to GitHub") : Tr.T("● Обновления не проверены", "● Updates not checked"), Theme.Muted);
            else if (installed == remote && GuideUpdate && !Pinned)
            {
                Status(Tr.T("⬆ Доступно обновление гайда", "⬆ A guide update is available"), Theme.Orange);
                statusTip = GuideUpdateText();
            }
            else if (installed == remote)
                Status(Tr.T("✔ Установлена последняя версия", "✔ The latest version is installed"), Theme.Green);
            else if (Pinned)
                Status(Tr.T("■ Версия закреплена · есть обновление", "■ Version kept · an update exists"), Theme.Title);
            else
                Status(Tr.T("⬆ Доступно обновление", "⬆ An update is available"), Theme.Orange);
            tips.SetToolTip(lblStatus, statusTip);

            btnUpdate.Text = installed == null ? Tr.T("УСТАНОВИТЬ", "INSTALL") : Tr.T("ОБНОВИТЬ", "UPDATE");
            tips.SetToolTip(btnUpdate, Pinned ? Tr.T("Версия закреплена: снимите «Не менять версию», чтобы обновиться.", "The version is kept: clear \"Keep this version\" to update.") : null);

            if (fac == null)
            {
                lblFacPath.Text = Tr.T("Factorio не найдена — нажмите «Выбрать…»", "Factorio not found: press \"Choose…\"");
                lblFacPath.ForeColor = Theme.Orange;
                lblFacVer.Text = Tr.T("Нужна ", "Needs ") + AppInfo.RequiredGame;
                lblFacVer.ForeColor = Theme.Muted;
            }
            else
            {
                lblFacPath.Text = fac.Dir;
                lblFacPath.ForeColor = Theme.Text;
                if (fac.Ok)
                {
                    lblFacVer.Text = "✔ " + fac.Describe();
                    lblFacVer.ForeColor = Theme.Green;
                }
                else
                {
                    lblFacVer.Text = "✖ " + fac.Describe() + Tr.T(" — нужна ", " — needs ") + AppInfo.RequiredGame;
                    lblFacVer.ForeColor = Theme.Red;
                }
            }
            lblModsPath.Text = modsDir == null ? Tr.T("Папка модов не выбрана", "No mods folder selected") : Tr.T("Моды: ", "Mods: ") + modsDir;
            lblModsNote.Text = modsDir != null && fac != null && !modsDir.Equals(Path.Combine(fac.Dir, "mods"), StringComparison.OrdinalIgnoreCase)
                ? Tr.T("Своя папка модов: игра запускается с --mod-directory", "Own mods folder: the game starts with --mod-directory")
                : Tr.T("Quality, Elevated Rails — вкл., Space Age — выкл.", "Quality, Elevated Rails on, Space Age off");

            btnRollback.Text = rollbackTarget != null && rollbackNewer ? Tr.T("Вернуть", "Return") : Tr.T("Откатить", "Roll back");
            tips.SetToolTip(btnRollback, rollbackTarget == null
                ? Tr.T("Отката пока нет: лаунчер сохраняет прошлую версию при обновлении сборки\n(только изменённые файлы, не всю сборку).",
                       "No rollback yet: the launcher keeps the previous version when it updates the build\n(only the changed files, not the whole build).")
                : (rollbackNewer
                    ? Tr.T("Вернуть версию сборки от ", "Return to the build of ") + rollbackDate
                    : Tr.T("Откатить сборку на версию от ", "Roll the build back to the version of ") + rollbackDate) +
                  Tr.T(" — без интернета: её файлы сохранены при обновлении.\nВернуться обратно можно этой же кнопкой.",
                       " — without internet: its files were kept at the update.\nThe same button switches back."));

            bool modsOk = modsDir != null && Directory.Exists(modsDir);
            bool playOk = fac != null && fac.Ok && BuildPresent && modsOk;
            btnUpdate.Enabled = !busy && modsDir != null && !Pinned;
            btnPlay.Enabled = !busy && playOk;
            btnCopy.Enabled = !busy && engine != null && (installed != null || (found != null && found.Present && found.Fingerprint != null));
            btnVerify.Enabled = !busy && installed != null && engine != null && modsOk;
            btnCheck.Enabled = !busy && git != null;
            btnRollback.Enabled = !busy && rollbackTarget != null && engine != null && modsOk;
            btnHost.Enabled = !busy && engine != null && modsDir != null;
            btnReport.Enabled = !busy;
            btnGuide.Enabled = !busy;
            btnShortcuts.Enabled = !busy;
            btnPickFac.Enabled = !busy;
            btnPickMods.Enabled = !busy;
            btnOpenMods.Enabled = modsOk;
            chkCache.Enabled = !busy;
            chkPin.Enabled = !busy && installed != null;
        }

        void Status(string text, Color c)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = c;
        }

        void Idle(string text)
        {
            lblStage.Text = text;
            progress.SetActive(false);
            progress.Indeterminate = false;
        }

        void SetBusy(bool b)
        {
            busy = b;
            cancelable = b;
            btnCancel.Visible = b;
            btnCancel.Enabled = b;
            progress.SetActive(b);
            DoLayout();
            UpdateUi();
        }

        void AppendLog(string text, LogKind kind)
        {
            Color c = kind == LogKind.Ok ? Theme.Green : kind == LogKind.Warn ? Theme.Orange : kind == LogKind.Error ? Theme.Red : kind == LogKind.Muted ? Theme.Muted : Theme.Text;
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = Theme.Muted;
            rtbLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
            rtbLog.SelectionColor = c;
            rtbLog.AppendText(text + "\n");
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------------ ISyncUi (called from worker threads)

        public void Log(string text, LogKind kind = LogKind.Info) => Ui(() => AppendLog(text, kind));

        public void Stage(string ru, string en) => Ui(() => { lblStage.Text = Tr.T(ru, en); AppendLog(ru, LogKind.Muted); });

        public void Progress(double? fraction, string detail) => Ui(() =>
        {
            progress.Indeterminate = fraction == null;
            if (fraction != null) progress.Value = fraction.Value;
            if (detail != null) lblDetail.Text = detail;
        });

        public void CanCancel(bool value) => Ui(() => { cancelable = value; btnCancel.Enabled = value && busy; });

        public bool ConfirmExtras(List<string> names)
        {
            return (bool)Invoke(new Func<bool>(() =>
            {
                var details = names.Take(400).ToList();
                if (names.Count > 400) details.Add(Tr.T("… и ещё ", "… and ") + Fmt.Num(names.Count - 400) + Tr.T("", " more"));
                AppendLog("В папке mods не из сборки: " + Fmt.Num(names.Count), LogKind.Warn);
                var r = DarkDialog.Ask(this, Tr.T("Лишние моды", "Extra mods"),
                    Tr.T("В папке mods есть моды и файлы, которых нет в сборке (", "The mods folder has mods and files that are not in the build (") + Fmt.Num(names.Count) + ").",
                    Tr.T("Удалить их для полного соответствия сборке? Если нет — они останутся как есть, и больше про них не спросим.",
                         "Delete them to match the build exactly? If not, they stay as they are and the launcher will not ask again."),
                    details, Tr.T("Удалить", "Delete"), Tr.T("Оставить", "Keep"), Theme.Title);
                return r == DialogResult.Yes;
            }));
        }

        public bool ConfirmOverwrite(int files, List<string> sample, BuildFound f)
        {
            return (bool)Invoke(new Func<bool>(() =>
            {
                string message;
                if (f != null && f.Present && f.Legacy)
                    message = Tr.T("В папке mods старая сборка Paranoidal для Factorio ", "The mods folder has the old Paranoidal build for Factorio ") + f.FactorioVersion +
                        (f.ZzzVersion != null ? " (zzzparanoidal " + f.ZzzVersion + ")" : "") + ".";
                else if (f != null && f.Present)
                    message = Tr.T("В папке mods уже есть сборка Paranoidal, но её файлы отличаются от устанавливаемой версии.",
                        "The mods folder already has a Paranoidal build, but its files differ from the version being installed.");
                else
                    message = Tr.T("В папке mods уже есть моды сборки, но их файлы отличаются от устанавливаемой версии.",
                        "The mods folder already has mods of the build, but their files differ from the version being installed.");
                AppendLog("Первая синхронизация: отличаются от сборки файлов " + files + ". Спрашиваю подтверждение.", LogKind.Warn);
                var details = sample.ToList();
                if (files > sample.Count) details.Add(Tr.T("… и ещё ", "… and ") + Fmt.Num(files - sample.Count));
                var r = DarkDialog.Ask(this, Tr.T("Файлы в папке mods", "Files in the mods folder"), message,
                    Tr.T("Отличающихся файлов: ", "Files that differ: ") + Fmt.Num(files) +
                    Tr.T(". Установка заменит их файлами версии с GitHub (скачивается только разница). Моды не из сборки, mod-list.json и mod-settings.dat не меняются. " +
                         "Если эту сборку вам дал хост и с ним нужно совпадать, нажмите «Отмена» и используйте «Версия хоста…».",
                         ". Installing replaces them with the files of the GitHub version (only the difference is downloaded). Mods not from the build, mod-list.json and mod-settings.dat are not changed. " +
                         "If the host gave you this build and you must match him, press Cancel and use \"Host's version…\"."),
                    details, Tr.T("Заменить", "Replace"), Tr.T("Отмена", "Cancel"), Theme.Title);
                return r == DialogResult.Yes;
            }));
        }

        // ------------------------------------------------------------------ operations

        void RunOp<T>(Func<T> work, Action<T> done)
        {
            SetBusy(true);
            Task.Run(work).ContinueWith(t => Ui(() =>
            {
                SetBusy(false);
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    if (ex is OperationCanceledException)
                    {
                        Idle(Tr.T("Отменено.", "Cancelled."));
                        lblDetail.Text = Tr.T("Файлы модов не изменены. Уже скачанное сохранено — следующее обновление продолжит с этого места.",
                            "The mod files were not changed. What was downloaded is kept: the next update continues from here.");
                        AppendLog("Операция отменена.", LogKind.Warn);
                    }
                    else
                    {
                        var se = ex as SyncException;
                        var shown = se != null ? se.Display : ex.Message;
                        Idle(Tr.T("Ошибка", "Error"));
                        lblDetail.Text = shown.Split('\n')[0];
                        AppendLog(se != null ? ex.Message : ex.ToString(), LogKind.Error);
                        DarkDialog.Info(this, Tr.T("Ошибка", "Error"), shown, Theme.Red);
                    }
                    UpdateUi();
                    return;
                }
                done(t.Result);
                UpdateUi();
            }));
        }

        sealed class StartData { public GitRunner Git; public FactorioInfo Fac; public string Mods; public bool FromSettings; public List<FactorioInfo> All; }

        void Startup()
        {
            AppendLog(AppInfo.Name + " " + AppInfo.Version, LogKind.Muted);
            AppendLog("Данные лаунчера: " + AppInfo.DataDir, LogKind.Muted);
            if (AppInfo.DataDirNote != null) AppendLog(AppInfo.DataDirNote, LogKind.Warn);
            if (AppInfo.TestOffline) AppendLog("Тестовый режим: связь с GitHub отключена (PARANOIDAL_LAUNCHER_TEST_OFFLINE=1).", LogKind.Warn);
            if (Releases.Base != "https://github.com") AppendLog("Тестовый режим: релизы лаунчера и гайда берутся с " + Releases.Base + ".", LogKind.Warn);
            var exePath = SelfUpdate.CurrentExe;
            SelfUpdate.CleanupOld(exePath);
            if (StartArgs.UpdatedFrom != null)
            {
                AppendLog("Лаунчер обновлён: " + StartArgs.UpdatedFrom + " → " + AppInfo.Version + ".", LogKind.Ok);
                lblDetail.Text = Tr.T("Лаунчер обновлён до ", "The launcher is updated to ") + AppInfo.Version;
            }
            if (StartArgs.MovedFrom != null)
            {
                var removed = Relocation.CleanupOldPlace(StartArgs.MovedFrom, exePath, AppInfo.DataDir);
                AppendLog("Лаунчер перенесён в папку игры: " + AppInfo.ExeDir + ". Со старого места удалено: " +
                    (removed.Count == 0 ? "ничего" : string.Join(", ", removed)) + ". На рабочем столе — ярлык «" + Path.GetFileNameWithoutExtension(Shortcuts.LauncherLink) + "».", LogKind.Ok);
                lblDetail.Text = Tr.T("Лаунчер перенесён в папку игры, на рабочем столе — ярлык для запуска", "The launcher moved into the game folder; a shortcut is on the desktop");
            }
            RefreshGuideLocal();
            RunOp(() =>
            {
                Stage("Ищу Factorio…", "Looking for Factorio…");
                Progress(null, "");
                var sd = new StartData();
                sd.Fac = FactorioInfo.Inspect(Settings.GetPath("factorio"));
                sd.FromSettings = sd.Fac != null;
                if (sd.Fac == null)
                {
                    sd.All = FactorioInfo.DetectAll(AppDomain.CurrentDomain.BaseDirectory);
                    sd.Fac = sd.All.FirstOrDefault(x => x.Ok) ?? sd.All.FirstOrDefault();
                }
                return sd;
            }, sd =>
            {
                fac = sd.Fac;
                if (MaybeRelocate(sd.All, sd.FromSettings)) return;   // the copy in the game folder takes over
                StartEngine();
            });
        }

        /// <summary>Second part of the start: MinGit (unpacked only where the launcher stays), the engine, the status check.</summary>
        void StartEngine()
        {
            var f0 = fac;
            RunOp(() =>
            {
                var exe = GitBootstrap.Ensure(Stage);
                var g = new GitRunner(exe, Path.Combine(AppInfo.DataDir, "no-global-config"));
                var mods = Settings.GetPath("mods");
                // a game folder copied elsewhere: the old absolute path of the mods folder points nowhere
                if (mods != null && !Directory.Exists(Path.GetDirectoryName(mods) ?? mods)) mods = null;
                return new StartData { Git = g, Fac = f0, Mods = mods };
            }, sd =>
            {
                git = sd.Git;
                engine = new SyncEngine(git, this);
                fac = sd.Fac;
                modsDir = sd.Mods ?? (fac != null ? Path.Combine(fac.Dir, "mods") : null);
                if (fac != null) { Settings.SetPath("factorio", fac.Dir); AppendLog("Factorio: " + fac.Dir + " (" + fac.Describe(false) + ")", fac.Ok ? LogKind.Ok : LogKind.Warn); }
                else AppendLog("Factorio не найдена автоматически — укажите папку кнопкой «Выбрать…».", LogKind.Warn);
                if (modsDir != null) Settings.SetPath("mods", modsDir);
                UpdateUi();
                if (fac != null && !fac.Ok)
                    DarkDialog.Info(this, Tr.T("Версия Factorio", "Factorio version"), fac.Problem() +
                        Tr.T("\n\nЗапуск игры из программы будет недоступен. Укажите другую папку Factorio кнопкой «Выбрать…».",
                             "\n\nThe game cannot be started from the launcher. Choose another Factorio folder with \"Choose…\"."), Theme.Red);
                gameRunning = FactorioInfo.IsRunning();
                gameWatch.Start();
                if (!gameRunning) AnalyzeLoad();
                CheckStatus();
            });
        }

        /// <summary>The line under the buttons after a status check, from the current state (also after a language switch).</summary>
        string StatusIdleText()
        {
            if (remote == null && offline && !BuildPresent) return Tr.T("Нет связи с GitHub — установить сборку сейчас нельзя", "No connection to GitHub: the build cannot be installed now");
            else if (remote == null && offline) return Tr.T("Нет связи с GitHub — играть можно, обновление недоступно", "No connection to GitHub: you can play, updating is not possible");
            else if (remote == null) return Tr.T("Не удалось проверить обновления", "Could not check for updates");
            else if (installed == null)
            {
                if (found != null && found.Present && found.Legacy)
                    return Tr.T("В папке mods старая сборка для Factorio 1.1 — «Установить» заменит её", "The mods folder has the old build for Factorio 1.1: \"Install\" replaces it");
                else if (found != null && found.Present)
                    return Tr.T("Версия сборки не определена — «Установить» докачает отличающиеся файлы", "The build version is unknown: \"Install\" downloads the files that differ");
                else return Tr.T("Сборка не установлена — нажмите «Установить»", "The build is not installed: press \"Install\"");
            }
            else if (modified > 0) return Tr.T("Файлы сборки изменены — «Проверить файлы» вернёт их", "Build files were changed: \"Check files\" restores them");
            else if (installed == remote && GuideUpdate && !Pinned) return GuideUpdateText() + Tr.T(" — нажмите «Обновить»", ": press \"Update\"");
            else if (installed == remote) return Tr.T("Установлена последняя версия", "The latest version is installed");
            else if (Pinned) return Tr.T("Версия закреплена — обновление не устанавливается", "The version is kept: the update is not installed");
            else return Tr.T("Доступно обновление сборки", "A build update is available") + (remoteDate != null ? Tr.T(" от ", " of ") + remoteDate : "");
        }

        void CheckStatus()
        {
            if (engine == null) return;
            var mods = modsDir;
            // the launcher's and the guide's releases are checked at the same time as the build
            var releases = Task.Run(() => Releases.CheckAll());
            RunOp(() =>
            {
                var s = engine.CheckStatus(mods);
                ReleaseCheck rc;
                try { rc = releases.Result; } catch (Exception ex) { rc = new ReleaseCheck { LauncherError = ex.GetBaseException().Message, GuideError = ex.GetBaseException().Message }; }
                return Tuple.Create(s, rc);
            }, res =>
            {
                var st = res.Item1;
                ApplyStatus(st, false);
                ApplyReleases(res.Item2, st.Offline);
                Idle(StatusIdleText());
                progress.Value = installed != null && installed == remote ? 1 : 0;
                if (adopted)
                    lblDetail.Text = Tr.T("Сборка в папке mods опознана: версия от ", "The build in the mods folder was recognised: the version of ") + installedDate;
                else
                    lblDetail.Text = st.CommitsAreNew && st.Commits != null
                        ? Tr.T("Новых изменений в сборке: ", "New changes in the build: ") + st.Commits.Count + Tr.T(" — смотрите вкладку «Что нового»", ": see the \"What's new\" tab")
                        : lblDetail.Text.StartsWith(Tr.T("Лаунчер ", "The launcher ")) ? lblDetail.Text : "";
                OfferSelfUpdate();
                if (Auto && !restarting && !busy && guideLocal == null && guideRel != null) OpenGuide();   // tests: the download path of «Гайд»
                else if (Auto && !restarting && !busy) BeginInvoke(new Action(Close));
            });
        }

        // ------------------------------------------------------------------ launcher and guide releases

        void ApplyReleases(ReleaseCheck rc, bool offline)
        {
            var kind = offline ? LogKind.Muted : LogKind.Warn;
            if (rc.Launcher != null) launcherRel = rc.Launcher;
            else AppendLog("Не удалось проверить новую версию лаунчера: " + rc.LauncherError, kind);
            if (rc.Guide != null) guideRel = rc.Guide;
            else AppendLog("Не удалось проверить гайд по сборке: " + rc.GuideError, kind);
            RefreshGuideLocal();
            if (launcherRel != null && Releases.Compare(launcherRel.Version, AppInfo.Version) <= 0)
                AppendLog("Лаунчер " + AppInfo.Version + " — последняя версия.", LogKind.Muted);
            if (guideRel != null)
                AppendLog(GuideUpdate ? "Вышла новая версия гайда по сборке: " + guideRel.Version + (guideLocalVersion != null ? " (у вас " + guideLocalVersion + ")" : " (гайд ещё не скачан)") +
                    ". Он скачается вместе с обновлением сборки." : "Гайд по сборке " + guideLocalVersion + " — последняя версия.",
                    GuideUpdate ? LogKind.Info : LogKind.Muted);
        }

        /// <summary>A newer guide is released (or none is downloaded yet): it is downloaded with the next build update.</summary>
        bool GuideUpdate => GuideFile.NeedsUpdate(guideRel, guideLocalVersion);

        string GuideUpdateText() => guideLocalVersion == null
            ? Tr.T("Гайд по сборке ещё не скачан", "The modpack guide is not downloaded yet")
            : Tr.T("Вышла новая версия гайда по сборке: ", "A new version of the modpack guide: ") + guideRel.Version;

        void RefreshGuideLocal()
        {
            guideLocal = GuideFile.Local();
            guideLocalVersion = guideLocal == null ? null : GuideFile.Version(guideLocal);
            ShowGuideState();
        }

        void ShowGuideState()
        {
            if (btnGuide == null) return;
            tips.SetToolTip(btnGuide, Tr.T(
                "Иллюстрированный гайд по сборке для игроков ванильной Factorio. Один файл " + AppInfo.GuideFile + "\n" +
                "рядом с лаунчером, открывается в браузере и работает без интернета. Новая версия скачивается вместе с обновлением сборки.\n",
                "Illustrated modpack guide for vanilla Factorio players. One file " + AppInfo.GuideFile + "\n" +
                "next to the launcher: opens in the browser and works offline. A new version is downloaded with the build update.\n") +
                (guideLocal == null ? Tr.T("Гайд ещё не скачан: кнопка скачает его (нужен интернет).", "Not downloaded yet: the button downloads it (needs internet).")
                    : Tr.T("Версия ", "Version ") + (guideLocalVersion ?? "?") + ": " + guideLocal));
        }

        /// <summary>Bold journal line with a book sign (the guide was updated).</summary>
        void AppendLogHighlight(string text)
        {
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = Theme.Muted;
            rtbLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
            var normal = rtbLog.SelectionFont;
            rtbLog.SelectionFont = new Font("Segoe UI Symbol", 10.5f, FontStyle.Bold);
            rtbLog.SelectionColor = Theme.Title;
            rtbLog.AppendText("📖 ");
            rtbLog.SelectionFont = new Font(rtbLog.Font, FontStyle.Bold);
            rtbLog.AppendText(text + "\n");
            rtbLog.SelectionFont = rtbLog.Font;
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }

        /// <summary>«Гайд»: opens the local copy; downloads it first only when there is none.</summary>
        void OpenGuide()
        {
            RefreshGuideLocal();
            if (guideLocal != null)
            {
                try { GuideFile.Open(guideLocal); AppendLog("Открыт гайд по сборке " + (guideLocalVersion ?? "?") + ": " + guideLocal, LogKind.Muted); }
                catch (Exception ex) { DarkDialog.Info(this, Tr.T("Гайд по сборке", "Modpack guide"), Tr.T("Не удалось открыть ", "Could not open ") + guideLocal + ":\n" + ex.Message, Theme.Red); }
                return;
            }
            var known = guideRel;
            RunOp(() =>
            {
                Stage("Скачиваю гайд по сборке…", "Downloading the modpack guide…");
                Progress(null, "");
                var r = known ?? Releases.Latest(AppInfo.GuideRepo, "guide", AppInfo.GuideFile);
                return InstallGuide(r);
            }, r =>
            {
                guideRel = r;
                Idle(Tr.T("Гайд по сборке скачан ✔", "The modpack guide is downloaded ✔"));
                lblDetail.Text = guideLocal;
                AppendLogHighlight("Скачан гайд по сборке Paranoidal " + r.Version + ": " + guideLocal);
                if (Auto) { BeginInvoke(new Action(Close)); return; }
                try { GuideFile.Open(guideLocal); } catch { }
            });
        }

        /// <summary>Worker thread: downloads the guide of the release with progress; returns it.</summary>
        ReleaseInfo InstallGuide(ReleaseInfo r)
        {
            Releases.Cancel = false;
            CanCancel(true);
            try
            {
                GuideFile.Install(r, (d, t) => Progress(t > 0 ? d / (double)t : (double?)null, Fmt.Size(d) + " / " + Fmt.Size(t)));
            }
            catch (OperationCanceledException) { throw new SyncException("Скачивание гайда отменено.", "The guide download was cancelled."); }
            catch (SyncException) { throw; }
            catch (Exception ex) { throw new SyncException("Гайд не скачан: " + ex.Message, "The guide was not downloaded: " + ex.Message); }
            Ui(RefreshGuideLocal);
            return r;
        }

        void MakeShortcuts()
        {
            try
            {
                RefreshGuideLocal();
                var made = Shortcuts.All(SelfUpdate.CurrentExe, guideLocal);
                AppendLog("Созданы ярлыки: " + string.Join(", ", made), LogKind.Ok);
                DarkDialog.Info(this, Tr.T("Ярлыки", "Shortcuts"), Tr.T("Ярлыки созданы: на рабочем столе и в меню «Пуск» (папка Paranoidal).", "Shortcuts created: on the desktop and in the Start menu (Paranoidal folder).") +
                    (AppInfo.ExeDir == (fac != null ? fac.Dir : null) || fac == null ? "" : Tr.T("\n\nЛаунчер лежит не в папке игры: ярлыки ведут сюда, пока он не перенесён.", "\n\nThe launcher is not in the game folder: the shortcuts point here until it is moved.")),
                    Theme.Green);
            }
            catch (Exception ex)
            {
                AppendLog("Ярлыки не созданы: " + ex.Message, LogKind.Error);
                DarkDialog.Info(this, Tr.T("Ярлыки", "Shortcuts"), Tr.T("Не удалось создать ярлыки: ", "Could not create the shortcuts: ") + ex.Message, Theme.Red);
            }
        }

        // ------------------------------------------------------------------ self-update

        /// <summary>A newer launcher: asked on every start (and every manual check) until updated; after the game exits if it runs.</summary>
        void OfferSelfUpdate()
        {
            var r = launcherRel;
            if (r == null || restarting || Releases.Compare(r.Version, AppInfo.Version) <= 0) return;
            if (busy || gameRunning)
            {
                if (!selfUpdatePending) AppendLog("Вышла новая версия лаунчера " + r.Version + ": предложу обновиться после выхода из игры.", LogKind.Info);
                selfUpdatePending = true;
                return;
            }
            selfUpdatePending = false;
            AppendLog("Вышла новая версия лаунчера: " + r.Version + " (установлена " + AppInfo.Version + ").", LogKind.Warn);
            if (!Auto && !Dialogs.AskSelfUpdate(this, r, !AppInfo.ExeDirWritable))
            {
                AppendLog("Обновление лаунчера отложено: лаунчер спросит снова при следующем запуске.", LogKind.Muted);
                return;
            }
            DoSelfUpdate(r);
        }

        void DoSelfUpdate(ReleaseInfo r)
        {
            var exe = SelfUpdate.CurrentExe;
            bool direct = AppInfo.ExeDirWritable;
            var file = direct ? exe + SelfUpdate.NewSuffix : Path.Combine(Path.GetTempPath(), "ParanoidalLauncher-" + r.Version + ".exe");
            RunOp(() =>
            {
                Stage("Скачиваю лаунчер " + r.Version + "…", "Downloading launcher " + r.Version + "…");
                Releases.Cancel = false;
                CanCancel(true);
                try { Releases.Download(r, file, (d, t) => Progress(t > 0 ? d / (double)t : (double?)null, Fmt.Size(d) + " / " + Fmt.Size(t))); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { throw new SyncException("Новая версия лаунчера не скачана: " + ex.Message, "The new launcher version was not downloaded: " + ex.Message); }
                var v = SelfUpdate.ExeVersion(file);
                if (v != r.Version)
                {
                    try { File.Delete(file); } catch { }
                    throw new SyncException("Скачанный лаунчер имеет версию " + (v ?? "?") + ", ожидалась " + r.Version + ".",
                        "The downloaded launcher has version " + (v ?? "?") + ", expected " + r.Version + ".");
                }
                return true;
            }, _ =>
            {
                try
                {
                    if (direct) SelfUpdate.Swap(file, exe);
                    else
                    {
                        string err;
                        if (!SelfUpdate.InstallElevated(exe, file, AppInfo.ExeDir, out err))
                        {
                            AppendLog(err == null ? "Права администратора не выданы — лаунчер не обновлён." : "Лаунчер не обновлён: " + err, LogKind.Warn);
                            Idle(Tr.T("Лаунчер не обновлён", "The launcher was not updated"));
                            try { File.Delete(file); } catch { }
                            return;
                        }
                        try { File.Delete(file); } catch { }
                    }
                    AppendLog("Лаунчер " + r.Version + " установлен, перезапускаюсь.", LogKind.Ok);
                    SelfUpdate.Restart(exe, "--updated-from " + AppInfo.Version);
                    restarting = true;
                    BeginInvoke(new Action(Close));
                }
                catch (Exception ex)
                {
                    AppendLog("Лаунчер не обновлён: " + ex, LogKind.Error);
                    try { File.Delete(file); } catch { }
                    DarkDialog.Info(this, Tr.T("Обновление лаунчера", "Launcher update"), Tr.T("Не удалось заменить лаунчер: ", "Could not replace the launcher: ") + ex.Message +
                        Tr.T("\nСкачайте новую версию вручную: ", "\nDownload the new version by hand: ") + Releases.PageUrl(AppInfo.LauncherRepo), Theme.Red);
                }
            });
        }

        // ------------------------------------------------------------------ the launcher's place: the game folder

        /// <summary>
        /// The launcher works properly only from the game folder. When it is elsewhere and the game (2.0.77 Space Age) is
        /// known, it moves itself there silently; if that fails it asks for administrator rights. True: the copy in the
        /// game folder was started and this process closes.
        /// </summary>
        bool MaybeRelocate(List<FactorioInfo> all, bool fromSettings)
        {
            var why = Relocation.DisabledReason();
            if (why != null) { AppendLog("Перенос лаунчера в папку игры выключен: " + why + ".", LogKind.Muted); return false; }
            var target = fac;
            if (!fromSettings && all != null)
            {
                var ok = all.Where(x => x.Ok).ToList();
                if (ok.Any(x => Relocation.SameDir(x.Dir, AppInfo.ExeDir))) return false;
                if (ok.Count > 1)
                {
                    AppendLog("Найдено несколько подходящих установок игры: " + string.Join("; ", ok.Select(x => x.Dir)), LogKind.Info);
                    var chosen = Auto ? ok[0] : Dialogs.ChooseInstall(this, ok);
                    if (chosen == null)
                    {
                        AppendLog("Перенос лаунчера отложен (установка не выбрана). Лаунчер спросит снова при следующем запуске.", LogKind.Warn);
                        return false;
                    }
                    target = fac = chosen;
                }
            }
            if (target == null || !target.Ok)
            {
                if (!Relocation.SameDir(AppInfo.ExeDir, target != null ? target.Dir : ""))
                    AppendLog("Лаунчер лежит не в папке игры (" + AppInfo.ExeDir + "): он перенесёт себя туда, когда будет выбрана " + AppInfo.RequiredGame + ".", LogKind.Warn);
                return false;
            }
            if (Relocation.SameDir(target.Dir, AppInfo.ExeDir)) return false;
            return RelocateTo(target);
        }

        bool RelocateTo(FactorioInfo target)
        {
            var exe = SelfUpdate.CurrentExe;
            var log = new List<string>();
            AppendLog("Лаунчер лежит не в папке игры (" + AppInfo.ExeDir + "). Переношу его в " + target.Dir + ".", LogKind.Info);
            string newExe = null, error = null;
            if (AppInfo.CanWrite(target.Dir))
            {
                try { newExe = Relocation.MoveTo(exe, AppInfo.DataDir, target.Dir, log); }
                catch (Exception ex) { error = ex.Message; }
            }
            else error = "в папку игры нельзя записывать без прав администратора";
            foreach (var l in log) AppendLog("Перенос: " + l + ".", LogKind.Muted);
            if (newExe == null)
            {
                AppendLog("Перенести лаунчер автоматически не удалось: " + error + ".", LogKind.Warn);
                var r = Auto ? DialogResult.No : DarkDialog.Ask(this, Tr.T("Папка игры", "Game folder"),
                    Tr.T("Лаунчер нужно перенести в папку игры", "The launcher must be moved into the game folder"),
                    Tr.T("Правильная работа лаунчера гарантируется, только когда он лежит в папке игры, с которой вы играете:\n",
                         "The launcher is guaranteed to work properly only from the folder of the game you play:\n") + target.Dir + "\n\n" +
                    Tr.T("Сам он туда перенестись не смог (", "It could not move itself there (") + (Tr.En ? EnError(error) : error) +
                    Tr.T("). По кнопке «Перенести» Windows попросит права администратора; лаунчер скопирует себя в папку игры, оставит ярлык на рабочем столе и запустится оттуда.\n\n" +
                         "Совет: если игра стоит в Program Files, перенесите всю её папку в обычную папку, например C:\\Games\\Factorio. " +
                         "Тогда права администратора не понадобятся ни для переноса, ни для обновлений, и это сообщение больше не появится.",
                         "). With \"Move\" Windows asks for administrator rights; the launcher copies itself into the game folder, leaves a desktop shortcut and starts from there.\n\n" +
                         "Tip: if the game is in Program Files, move its whole folder to a normal folder, e.g. C:\\Games\\Factorio. " +
                         "Then neither the move nor updates need administrator rights, and this message does not appear again."),
                    null, Tr.T("Перенести", "Move"), Tr.T("Не сейчас", "Not now"), Theme.Orange);
                if (r != DialogResult.Yes)
                {
                    AppendLog("Перенос лаунчера отложен. Правильная работа гарантируется только из папки игры; лаунчер спросит снова при следующем запуске.", LogKind.Warn);
                    lblDetail.Text = Tr.T("Лаунчер не в папке игры — правильная работа не гарантируется", "The launcher is not in the game folder: proper work is not guaranteed");
                    return false;
                }
                string err2 = null;
                if (!Relocation.TargetIsSameOrNewer(target.Dir) && !SelfUpdate.InstallElevated(exe, exe, target.Dir, out err2))
                {
                    AppendLog(err2 == null ? "Права администратора не выданы — перенос отложен." : "Перенос не удался: " + err2, LogKind.Warn);
                    if (err2 != null) DarkDialog.Info(this, Tr.T("Папка игры", "Game folder"), Tr.T("Перенос не удался: ", "The move failed: ") + err2, Theme.Red);
                    return false;
                }
                newExe = Path.Combine(target.Dir, AppInfo.ExeName);
                try { Relocation.PrepareLegacyData(AppInfo.DataDir, AppInfo.ExeDir); }
                catch (Exception ex) { AppendLog("Данные лаунчера не перенесены: " + ex.Message, LogKind.Warn); }
            }
            try { AppendLog("Ярлык на рабочем столе: " + Shortcuts.Desktop(newExe), LogKind.Ok); }
            catch (Exception ex) { AppendLog("Ярлык на рабочем столе не создан: " + ex.Message, LogKind.Warn); }
            try { SelfUpdate.Restart(newExe, "--moved-from " + FsUtil.QuoteArg(exe)); }
            catch (Exception ex)
            {
                AppendLog("Не удалось запустить лаунчер из папки игры: " + ex.Message, LogKind.Error);
                return false;
            }
            restarting = true;
            BeginInvoke(new Action(Close));
            return true;
        }

        static string EnError(string ru) => ru != null && ru.StartsWith("в папку игры нельзя") ? "the game folder cannot be written without administrator rights" : ru;

        /// <summary>«Проверить файлы»: read every file of the installed build again and restore what differs.</summary>
        void DoVerify()
        {
            if (engine == null || installed == null) return;
            var err = SyncPaths.Validate(modsDir);
            if (err != null) { DarkDialog.Info(this, Tr.T("Папка модов", "Mods folder"), err, Theme.Red); return; }
            if (FactorioInfo.IsRunning()) { DarkDialog.Info(this, Tr.T("Factorio запущена", "Factorio is running"), Tr.T("Закройте игру и запустите проверку снова.", "Close the game and start the check again."), Theme.Orange); return; }
            var mods = modsDir;
            var before = installed;
            RunOp(() => engine.Update(new SyncPaths(mods), true), res =>
            {
                installed = res.New;
                progress.Value = 1;
                int files = res.Mods.Sum(x => x.Files);
                if (res.UpToDate)
                {
                    Idle(Tr.T("Проверка файлов: всё в порядке ✔", "File check: all good ✔"));
                    lblDetail.Text = Tr.T("Все файлы модов побайтно совпадают с установленной версией сборки", "Every mod file matches the installed build version byte for byte");
                    AppendLog("Проверка файлов: расхождений нет.", LogKind.Ok);
                }
                else
                {
                    Idle(Tr.T("Проверка файлов: исправлено ✔", "File check: fixed ✔"));
                    lblDetail.Text = Tr.T("Исправлено или удалено файлов: ", "Files fixed or removed: ") + Fmt.Num(files) + Tr.T(" — список на вкладке «Изменённые моды»", ": see the \"Changed mods\" tab");
                    AppendLog("Проверка файлов: исправлено " + Fmt.Num(files) + " файл(ов), теперь они совпадают со сборкой.", LogKind.Ok);
                    FillMods(res.Mods);
                    if (res.Mods.Count > 0) SelectTab(1);
                }
                modified = 0;
                RefreshAfterUpdate();
            });
        }

        void ApplyStatus(StatusInfo st, bool keepRemote)
        {
            installed = st.Installed;
            installedDate = st.InstalledDate;
            remote = keepRemote ? (st.Remote ?? remote) : st.Remote;
            offline = st.Remote == null ? (keepRemote ? offline && remote == null : st.Offline) : false;
            modified = st.Modified;
            found = st.Found;
            adopted = st.Adopted;
            rollbackTarget = st.RollbackTarget;
            rollbackDate = st.RollbackDate;
            rollbackNewer = st.RollbackIsNewer;
            remoteDate = null;
            if (remote != null && st.Commits != null)
                foreach (var c in st.Commits)
                    if (c.Sha == remote && c.Date != default(DateTime)) { remoteDate = Fmt.Date(c.Date); break; }
            FillCommits(st.Commits, st.CommitsAreNew, st.CommitsCached);
        }

        void FillCommits(List<CommitInfo> commits, bool areNew, bool cached)
        {
            shownCommits = commits;
            shownCommitsNew = areNew;
            shownCommitsCached = cached;
            lvCommits.BeginUpdate();
            lvCommits.Items.Clear();
            if (commits != null)
            {
                foreach (var c in commits)
                {
                    var it = new ListViewItem(c.Date == default(DateTime) ? "" : Fmt.Date(c.Date));
                    it.SubItems.Add(c.Message);
                    it.ForeColor = areNew ? Theme.Orange : (c.Sha == installed ? Theme.Green : Theme.Text);
                    if (!areNew && c.Sha == installed) it.SubItems[1].Text = "✔ " + c.Message + Tr.T("   (установлено)", "   (installed)");
                    lvCommits.Items.Add(it);
                }
            }
            lvCommits.EndUpdate();
            tabs[0].Text = areNew && commits != null ? Tr.T("Что нового (", "What's new (") + commits.Count + ")"
                : cached ? Tr.T("Изменения (сохранённые)", "Changes (saved list)") : Tr.T("Последние изменения", "Latest changes");
            tips.SetToolTip(tabs[0], cached ? Tr.T("Нет связи с GitHub: показан список, сохранённый при последней проверке.", "No connection to GitHub: the list saved at the last check.") : null);
        }

        void DoUpdate(bool thenPlay, string target)
        {
            if (engine == null) return;
            if (target == null && Pinned) return;
            var err = SyncPaths.Validate(modsDir);
            if (err != null) { DarkDialog.Info(this, Tr.T("Папка модов", "Mods folder"), err, Theme.Red); return; }
            if (FactorioInfo.IsRunning()) { DarkDialog.Info(this, Tr.T("Factorio запущена", "Factorio is running"), Tr.T("Закройте игру и запустите обновление снова.", "Close the game and start the update again."), Theme.Orange); return; }
            var mods = modsDir;
            // the guide goes together with the build update; only the guide when the build is current already
            var guide = GuideUpdate ? guideRel : null;
            bool build = !(guide != null && target == null && installed != null && installed == remote && modified <= 0);
            RunOp(() =>
            {
                var o = new UpdateOutcome();
                if (build) o.Res = engine.Update(new SyncPaths(mods), false, target);
                if (guide != null && (o.Res == null || !o.Res.Declined))
                {
                    Stage("Скачиваю гайд по сборке " + guide.Version + "…", "Downloading the modpack guide " + guide.Version + "…");
                    try { InstallGuide(guide); o.Guide = guide.Version; }
                    catch (Exception ex) { o.GuideError = ex is SyncException ? ((SyncException)ex).Message : ex.Message; }
                }
                return o;
            }, o =>
            {
                var res = o.Res;
                if (res != null && res.Declined)
                {
                    Idle(Tr.T("Установка отменена, файлы не тронуты.", "Installation cancelled, files untouched."));
                    lblDetail.Text = "";
                    AppendLog("Обновление отменено пользователем.", LogKind.Warn);
                    return;
                }
                if (res == null)
                {
                    progress.Value = 1;
                    Idle(o.Guide != null ? Tr.T("Гайд по сборке обновлён ✔", "The modpack guide is updated ✔") : Tr.T("Гайд по сборке не обновлён", "The modpack guide was not updated"));
                    lblDetail.Text = o.Guide != null ? guideLocal : o.GuideError;
                    ReportGuide(o);
                    if (thenPlay) Launch();
                    return;
                }
                installed = res.New;
                installedDate = null;
                modified = 0;
                progress.Value = 1;
                if (res.UpToDate)
                {
                    Idle(target != null ? Tr.T("Уже установлена версия хоста ✔", "The host's version is installed already ✔")
                        : Tr.T("Изменений нет — установлена последняя версия ✔", "No changes: the latest version is installed ✔"));
                    lblDetail.Text = target != null ? "" : Tr.T("Моды соответствуют последней версии сборки", "The mods match the latest build version");
                    AppendLog("Изменений нет, всё уже актуально.", LogKind.Ok);
                }
                else
                {
                    if (res.FullCheck && res.Old != null) AppendLog("Служебный индекс не найден — все файлы модов сверены заново побайтно.", LogKind.Muted);
                    Idle(target != null ? Tr.T("Установлена версия хоста ✔", "The host's version is installed ✔")
                        : res.Old == null ? Tr.T("Сборка установлена ✔", "The build is installed ✔") : Tr.T("Готово! Моды обновлены до последней версии ✔", "Done! The mods are updated to the latest version ✔"));
                    lblDetail.Text = Tr.T("Скачано файлов: ", "Files downloaded: ") + Fmt.Num(res.Downloaded) +
                        (res.Old != null ? Tr.T(" · прошлая версия сохранена для отката", " · the previous version is kept for a rollback") : "");
                    AppendLog(target != null ? "Готово. Моды соответствуют версии хоста " + AppInfo.Short(res.New) : "Готово. Моды соответствуют последней версии сборки", LogKind.Ok);
                    FillMods(res.Mods);
                    if (res.Mods.Count > 0) SelectTab(1);
                }
                if (target != null && !chkPin.Checked)
                {
                    chkPin.Checked = true;   // also sets the setting and logs it
                    AppendLog("После перехода на версию хоста включено «Не менять версию». Снимите галочку, когда хост обновится.", LogKind.Info);
                }
                ReportGuide(o);
                if (thenPlay) Launch();
                else RefreshAfterUpdate();
            });
        }

        sealed class UpdateOutcome { public UpdateResult Res; public string Guide, GuideError; }

        void ReportGuide(UpdateOutcome o)
        {
            RefreshGuideLocal();
            if (o.Guide != null) AppendLogHighlight("Обновился гайд по сборке Paranoidal: версия " + o.Guide + " — " + guideLocal);
            else if (o.GuideError != null) AppendLog(o.GuideError + " Сборка от этого не зависит; гайд скачается при следующем обновлении.", LogKind.Warn);
        }

        void RefreshAfterUpdate()
        {
            var stage = lblStage.Text;
            var detail = lblDetail.Text;
            var mods = modsDir;
            RunOp(() => engine.CheckStatus(mods), st =>
            {
                ApplyStatus(st, true);
                Idle(stage);
                progress.Value = 1;
                lblDetail.Text = detail;
            });
        }

        void FillMods(List<ModChange> mods)
        {
            shownMods = mods;
            lvMods.BeginUpdate();
            lvMods.Items.Clear();
            if (mods != null)
                foreach (var m in mods)
                {
                    var it = new ListViewItem(m.Mod);
                    it.SubItems.Add(m.Display);
                    it.SubItems.Add(Fmt.Num(m.Files));
                    it.ForeColor = m.Kind.StartsWith("нов") ? Theme.Green : m.Kind.Contains("удал") ? Theme.Orange : Theme.Text;
                    lvMods.Items.Add(it);
                }
            lvMods.EndUpdate();
            tabs[1].Text = Tr.T("Изменённые моды", "Changed mods") + (mods != null ? " (" + mods.Count + ")" : "");
        }

        // ------------------------------------------------------------------ rollback and the host's version

        void DoRollback()
        {
            if (engine == null || rollbackTarget == null) return;
            if (FactorioInfo.IsRunning()) { DarkDialog.Info(this, Tr.T("Factorio запущена", "Factorio is running"), Tr.T("Закройте игру и повторите.", "Close the game and try again."), Theme.Orange); return; }
            var r = DarkDialog.Ask(this, rollbackNewer ? Tr.T("Вернуть версию", "Return to a version") : Tr.T("Откат сборки", "Build rollback"),
                rollbackNewer ? Tr.T("Вернуть версию сборки от ", "Return to the build of ") + rollbackDate + "?"
                              : Tr.T("Откатить сборку на версию от ", "Roll the build back to the version of ") + rollbackDate + "?",
                Tr.T("Интернет не нужен: файлы этой версии сохранены при обновлении. Сейчас установлена версия от ", "No internet needed: the files of that version were kept at the update. Installed now: the version of ") +
                (installedDate ?? AppInfo.Short(installed)) + ".\n" +
                (rollbackNewer ? "" : Tr.T("Сейв, сделанный на текущей версии, может не загрузиться на старой. ", "A save made on the current version may not load on the old one. ")) +
                Tr.T("mod-settings.dat и моды не из сборки не меняются. Вернуться можно этой же кнопкой.",
                     "mod-settings.dat and mods not from the build are not changed. The same button switches back."),
                null, rollbackNewer ? Tr.T("Вернуть", "Return") : Tr.T("Откатить", "Roll back"), Tr.T("Отмена", "Cancel"), Theme.Title);
            if (r != DialogResult.Yes) return;
            var mods = modsDir;
            bool back = !rollbackNewer;
            RunOp(() => engine.Rollback(new SyncPaths(mods)), res =>
            {
                installed = res.New;
                installedDate = null;
                progress.Value = 1;
                Idle(back ? Tr.T("Сборка откачена ✔", "The build was rolled back ✔") : Tr.T("Версия возвращена ✔", "The version is back ✔"));
                FillMods(res.Mods);
                if (res.Mods.Count > 0) SelectTab(1);
                if (res.NotRestored > 0)
                {
                    lblDetail.Text = Tr.T("Не совпадают со сборкой файлов: ", "Files that do not match the build: ") + res.NotRestored + Tr.T(" — нажмите «Проверить файлы» (нужен интернет)", ": press \"Check files\" (needs internet)");
                    DarkDialog.Info(this, Tr.T("Откат", "Rollback"), Tr.T("Готово, но файлов, которые были изменены вручную до обновления, нет в сохранённой копии: ", "Done, but files changed by hand before the update are not in the saved copy: ") +
                        res.NotRestored + Tr.T(".\nНажмите «Проверить файлы», когда будет интернет.", ".\nPress \"Check files\" when you have internet."), Theme.Orange);
                }
                else lblDetail.Text = back && !pinSetting
                    ? Tr.T("Чтобы лаунчер не предлагал обновиться перед игрой, включите «Не менять версию»", "To stop the launcher offering the update before playing, turn on \"Keep this version\"")
                    : "";
                RefreshAfterUpdate();
            });
        }

        void DoHost()
        {
            if (engine == null || modsDir == null) return;
            var line = Dialogs.AskHostLine(this);
            if (line == null) return;
            var sha = VersionLine.ParseHost(line);
            if (sha == null) return;
            if (installed != null && installed.StartsWith(sha))
            {
                DarkDialog.Info(this, Tr.T("Версия хоста", "Host's version"), Tr.T("У вас уже установлена эта версия сборки.", "You already have this build version."), Theme.Green);
                if (!chkPin.Checked) chkPin.Checked = true;
                return;
            }
            AppendLog("Версия хоста: " + line, LogKind.Info);
            RunOp(() =>
            {
                Stage("Ищу версию хоста на GitHub…", "Looking the host's version up on GitHub…");
                Progress(null, "");
                try { return GitHubApi.Commit(sha); }
                catch (Exception ex) { throw new SyncException("Версия " + sha + " не найдена на GitHub: " + ex.Message, "Version " + sha + " was not found on GitHub: " + ex.Message); }
            }, c =>
            {
                Idle("");
                if (c == null) { DarkDialog.Info(this, Tr.T("Версия хоста", "Host's version"), Tr.T("Версия не найдена на GitHub.", "The version was not found on GitHub."), Theme.Red); return; }
                var r = DarkDialog.Ask(this, Tr.T("Версия хоста", "Host's version"),
                    Tr.T("Перейти на версию сборки от ", "Switch to the build of ") + (c.Date == default(DateTime) ? "?" : Fmt.Date(c.Date)) + "?",
                    "«" + c.Message + "» (" + AppInfo.Short(c.Sha) + ")\n" +
                    Tr.T("Сейчас установлена: ", "Installed now: ") + (installed == null ? Tr.T("неизвестная версия", "an unknown version") : installedDate ?? AppInfo.Short(installed)) + ".\n" +
                    Tr.T("Скачается только разница. Текущая версия сохранится для отката.", "Only the difference is downloaded. The current version is kept for a rollback."),
                    null, Tr.T("Перейти", "Switch"), Tr.T("Отмена", "Cancel"), Theme.Title);
                if (r == DialogResult.Yes) DoUpdate(false, c.Sha);
            });
        }

        // ------------------------------------------------------------------ game start

        void Play()
        {
            if (fac == null || !fac.Ok)
            {
                DarkDialog.Info(this, Tr.T("Версия Factorio", "Factorio version"), fac == null ? Tr.T("Для сборки нужна ", "The build needs ") + AppInfo.RequiredGame + "." : fac.Problem(), Theme.Red);
                return;
            }
            if (FactorioInfo.IsRunning()) { DarkDialog.Info(this, "Factorio", Tr.T("Factorio уже запущена.", "Factorio is already running."), Theme.Orange); return; }
            if (!Pinned && remote != null && installed != null && remote != installed)
            {
                var r = DarkDialog.Ask(this, Tr.T("Доступно обновление", "An update is available"), Tr.T("Вышла новая версия сборки. Обновить перед запуском?", "A new build version is out. Update before playing?"),
                    Tr.T("Если вы играете на сервере, где хост ещё не обновился, выберите «Играть без обновления» и включите «Не менять версию».",
                         "If you play on a server whose host has not updated yet, choose \"Play without updating\" and turn on \"Keep this version\"."), null,
                    Tr.T("Обновить и играть", "Update and play"), Tr.T("Играть без обновления", "Play without updating"), Theme.Title);
                if (r == DialogResult.Yes) { DoUpdate(true, null); return; }
                if (r != DialogResult.No) return;
            }
            Launch();
        }

        void Launch()
        {
            if (fac == null || !fac.Ok || modsDir == null || engine == null) return;
            var mods = modsDir;
            var f = fac;
            var build = installed;
            bool cache = chkCache.Checked;
            RunOp(() =>
            {
                var p = new SyncPaths(mods);
                engine.PrepareForPlay(p);
                engine.MarkPlayed(p, build);
                if (!cache) return true;
                // Factorio writes config.ini when it exits: change it only while the game is closed
                if (FactorioInfo.IsRunning()) return true;
                ReportCache(GameConfig.EnableCache(f.Dir, f.Exe), GameConfig.TildeMods(mods));
                return true;
            }, _ => StartGame());
        }

        /// <summary>Worker thread: logs what GameConfig did.</summary>
        void ReportCache(GameConfig.Result r, List<string> tilde)
        {
            if (r.Error != null)
                Log("Не удалось включить кэш загрузки в " + (r.ConfigPath ?? "config.ini") + ": " + r.Error, LogKind.Warn);
            else if (r.Created)
                Log("Создан " + r.ConfigPath + " с включённым кэшем загрузки (остальное игра допишет сама).", LogKind.Ok);
            else if (r.Changed.Count > 0)
                Log("Кэш загрузки включён в " + r.ConfigPath + ": " + string.Join(", ", r.Changed) +
                    ". Первые два запуска будут долгими, дальше — быстрые.", LogKind.Ok);
            if (tilde.Count > 0)
                Log("Кэш данных игры работать не будет: у модов " + string.Join(", ", tilde) +
                    " в info.json есть зависимость с «~» (ошибка Factorio 2.0: игра каждый раз пересобирает данные). Подробности — FAQ сборки, вопрос о долгой загрузке.", LogKind.Warn);
        }

        void StartGame()
        {
            if (fac == null || !fac.Ok || modsDir == null) return;
            if (FactorioInfo.IsRunning()) { DarkDialog.Info(this, "Factorio", Tr.T("Factorio уже запущена.", "Factorio is already running."), Theme.Orange); return; }
            try
            {
                Process.Start(new ProcessStartInfo(fac.Exe, "--mod-directory " + FsUtil.QuoteArg(modsDir))
                {
                    WorkingDirectory = Path.GetDirectoryName(fac.Exe),
                    UseShellExecute = false,
                });
                // the load diagnostics compare this start with the previous one: did the launcher change the build in between
                Settings.Set("launch-build-prev", Settings.Get("launch-build"));
                Settings.Set("launch-build", installed ?? (found != null ? "fp:" + found.Fingerprint : ""));
                gameRunning = true;
                AppendLog("Factorio запущена: " + fac.Exe + " --mod-directory " + modsDir, LogKind.Ok);
                lblDetail.Text = Tr.T("Factorio запускается… После выхода из игры здесь появится диагностика загрузки.", "Factorio is starting… After you quit the game, the load diagnostics appear here.");
            }
            catch (Exception ex)
            {
                DarkDialog.Info(this, Tr.T("Не удалось запустить Factorio", "Could not start Factorio"), ex.Message, Theme.Red);
            }
        }

        // ------------------------------------------------------------------ load diagnostics

        void WatchGame()
        {
            bool running = FactorioInfo.IsRunning();
            if (gameRunning && !running) AnalyzeLoad();
            gameRunning = running;
            if (!running && selfUpdatePending && !busy) OfferSelfUpdate();
        }

        /// <summary>Reads the game's last log (the game must be closed) and shows how the load went and why.</summary>
        void AnalyzeLoad()
        {
            var f = fac;
            var mods = modsDir;
            if (f == null) return;
            string prevBuild = Settings.Get("launch-build-prev"), curBuild = Settings.Get("launch-build");
            Task.Run(() =>
            {
                var wd = GameConfig.ResolveWriteData(f.Dir, f.Exe);
                var cur = LoadLog.Read(Path.Combine(wd, "factorio-current.log"));
                if (cur == null) return null;
                var prev = LoadLog.Read(Path.Combine(wd, "factorio-previous.log"));
                var ctx = new LoadContext
                {
                    Tilde = GameConfig.TildeMods(mods),
                    BuildChanged = !string.IsNullOrEmpty(prevBuild) && !string.IsNullOrEmpty(curBuild) && prevBuild != curBuild,
                    ModsDir = mods,
                };
                return LoadDiag.Analyze(cur, prev, ctx);
            }).ContinueWith(t => Ui(() =>
            {
                if (t.IsFaulted || t.Result == null) return;
                var v = t.Result;
                lastLoad = v;
                ShowLoad();
                if (v.Key != Settings.Get("load-diag-key"))
                {
                    Settings.Set("load-diag-key", v.Key);
                    LoadDiag.Remember(v);
                    bool good = v.Data == LoadLog.DataState.Hit && (v.Headless || v.Atlas == LoadLog.AtlasState.Hit);
                    AppendLog(v.Full(false), good ? LogKind.Ok : LogKind.Warn);
                }
            }));
        }

        void ShowLoad()
        {
            if (lastLoad == null)
            {
                lblLoad.Text = Tr.T("Последний запуск игры: нет данных", "Last game start: no data");
                lblLoad.ForeColor = Theme.Muted;
                tips.SetToolTip(lblLoad, Tr.T("После выхода из игры здесь появится, сколько шла загрузка и сработал ли кэш.",
                    "After you quit the game, this line shows how long loading took and whether the cache worked."));
                return;
            }
            bool good = lastLoad.Data == LoadLog.DataState.Hit && (lastLoad.Headless || lastLoad.Atlas == LoadLog.AtlasState.Hit);
            lblLoad.Text = lastLoad.Line(Tr.En);
            lblLoad.ForeColor = good ? Theme.Green : Theme.Orange;
            tips.SetToolTip(lblLoad, lastLoad.Full(Tr.En));
        }

        // ------------------------------------------------------------------ version line and report

        /// <summary>The version line in the given language; null when there is nothing to describe.</summary>
        string BuildLine(bool en)
        {
            if (engine == null || modsDir == null) return null;
            if (installed == null && (found == null || !found.Present || found.Fingerprint == null)) return null;
            List<string> foreign = null;
            try { foreign = installed != null ? engine.ForeignEntries(new SyncPaths(modsDir)) : null; } catch { }
            return VersionLine.Build(installed, installedDate, modsDir, foreign, modified, found != null ? found.Fingerprint : null, en);
        }

        void CopyCommit()
        {
            string text, ru;
            try { text = BuildLine(Tr.En); ru = Tr.En ? BuildLine(false) : text; }
            catch (Exception ex) { lblDetail.Text = Tr.T("Не удалось собрать строку версии: ", "Could not build the version line: ") + ex.Message; return; }
            if (text == null) return;
            AppendLog(ru, LogKind.Info);
            try { Clipboard.SetText(text); lblDetail.Text = Tr.T("Скопировано (строка в «Журнале») — сверьте её с хостом или вставьте в сообщение о баге", "Copied (the line is in the Journal): compare it with the host or paste it into a bug report"); }
            catch (Exception ex) { lblDetail.Text = Tr.T("Не удалось скопировать: ", "Could not copy: ") + ex.Message; }
        }

        /// <summary>The state of the window, in Russian, for the report.</summary>
        string StateText()
        {
            var sb = new StringBuilder();
            sb.Append("Установлена версия: ").Append(installed == null ? "не записана" : AppInfo.Short(installed) + " от " + (installedDate ?? "?")).Append('\n');
            if (found != null && found.Present)
            {
                sb.Append("В папке mods: zzzparanoidal ").Append(found.ZzzVersion ?? "?").Append(", factorio_version ").Append(found.FactorioVersion ?? "?");
                if (found.Fingerprint != null) sb.Append(", отпечаток файлов ").Append(found.Fingerprint);
                if (found.Near != null) sb.Append(", ближе всего версия ").Append(AppInfo.Short(found.Near)).Append(" от ").Append(found.NearDate)
                    .Append(" (отличаются ").Append(found.NearDiff).Append(" из ").Append(found.NearTotal).Append(": ").Append(string.Join(", ", found.NearMods)).Append(')');
                sb.Append(", сравнено с известными версиями: ").Append(found.Known).Append('\n');
            }
            sb.Append("Последняя версия на GitHub: ").Append(remote == null ? (offline ? "нет связи" : "не проверено") : AppInfo.Short(remote) + (remoteDate != null ? " от " + remoteDate : "")).Append('\n');
            sb.Append("Изменено файлов сборки: ").Append(modified < 0 ? "не проверено" : modified.ToString()).Append('\n');
            sb.Append("Не менять версию: ").Append(pinSetting ? "да" : "нет").Append('\n');
            sb.Append("Откат: ").Append(rollbackTarget == null ? "нет" : (rollbackNewer ? "вперёд на " : "назад на ") + AppInfo.Short(rollbackTarget) + " от " + rollbackDate).Append('\n');
            sb.Append("Кэш загрузки (галочка лаунчера): ").Append(chkCache.Checked ? "включается" : "не трогается").Append('\n');
            sb.Append("Лаунчер: ").Append(AppInfo.Version).Append(", папка ").Append(AppInfo.ExeDir).Append(AppInfo.ExeDirWritable ? "" : " (только чтение)")
              .Append(", последняя версия на GitHub: ").Append(launcherRel == null ? "не проверено" : launcherRel.Version).Append('\n');
            sb.Append("Гайд по сборке: ").Append(guideLocal == null ? "не скачан" : (guideLocalVersion ?? "?") + " (" + guideLocal + ")")
              .Append(", последняя версия: ").Append(guideRel == null ? "не проверено" : guideRel.Version).Append('\n');
            return sb.ToString();
        }

        void DoReport()
        {
            var inp = Dialogs.AskReport(this, fac);
            if (inp == null) return;
            inp.Fac = fac;
            inp.ModsDir = modsDir;
            try { inp.VersionLine = BuildLine(false); } catch (Exception ex) { inp.VersionLine = "не собрана: " + ex.Message; }
            inp.State = StateText();
            inp.Journal = rtbLog.Text;
            inp.LoadDiag = lastLoad != null ? lastLoad.Full(false) : null;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            inp.OutFile = Path.Combine(Directory.Exists(desktop) ? desktop : AppInfo.DataDir, "Paranoidal-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            var mods = modsDir;
            bool haveBuild = installed != null;
            RunOp(() =>
            {
                Stage("Собираю отчёт о проблеме…", "Building the problem report…");
                Progress(null, "");
                if (mods != null && engine != null && haveBuild)
                {
                    var p = new SyncPaths(mods);
                    try { inp.ModifiedFiles = engine.ChangedBuildFiles(p); } catch { }
                    try { var fe = engine.ForeignEntries(p); if (fe != null) inp.ForeignMods = VersionLine.Describe(mods, fe); } catch { }
                }
                return Report.Create(inp);
            }, res =>
            {
                Idle(Tr.T("Отчёт готов ✔", "The report is ready ✔"));
                lblDetail.Text = res.Zip + " (" + Fmt.Size(res.Size) + ")";
                AppendLog("Отчёт о проблеме: " + res.Zip + " (" + Fmt.SizeRu(res.Size) + ", файлов " + res.Entries.Count + ")" +
                    (res.SaveAdded != null ? ", сейв " + Path.GetFileName(res.SaveAdded) : "") + (res.SaveSkippedRu != null ? "; сейв не добавлен: " + res.SaveSkippedRu : ""), LogKind.Ok);
                if (res.SaveSkippedRu != null)
                    DarkDialog.Info(this, Tr.T("Отчёт о проблеме", "Problem report"), Tr.T("Архив создан, но сейв не добавлен: ", "The archive is ready, but the save was not added: ") +
                        Tr.T(res.SaveSkippedRu, res.SaveSkippedEn) + ".", Theme.Orange);
                try { Process.Start("explorer.exe", "/select," + FsUtil.QuoteArg(res.Zip)); } catch { }
            });
        }

        // ------------------------------------------------------------------ folders

        void OpenMods()
        {
            if (modsDir != null && Directory.Exists(modsDir)) Process.Start("explorer.exe", FsUtil.QuoteArg(modsDir));
        }

        void PickFactorio()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = Tr.T("Выберите папку ", "Choose the folder of ") + AppInfo.RequiredGame + Tr.T(" (в ней есть bin\\x64\\factorio.exe)", " (it has bin\\x64\\factorio.exe)"),
                ShowNewFolderButton = false,
                SelectedPath = fac != null ? fac.Dir : "",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var f = FactorioInfo.Inspect(dlg.SelectedPath);
                if (f == null)
                {
                    DarkDialog.Info(this, Tr.T("Factorio не найдена", "Factorio not found"), Tr.T("В папке\n", "The folder\n") + dlg.SelectedPath + Tr.T("\nнет bin\\x64\\factorio.exe.", "\nhas no bin\\x64\\factorio.exe."), Theme.Red);
                    return;
                }
                if (!f.Ok)
                    DarkDialog.Info(this, Tr.T("Версия Factorio", "Factorio version"), f.Problem() + Tr.T("\nЗапуск игры будет недоступен.", "\nThe game cannot be started."), Theme.Red);
                var oldDefault = fac != null ? Path.Combine(fac.Dir, "mods") : null;
                fac = f;
                Settings.SetPath("factorio", f.Dir);
                AppendLog("Выбрана Factorio: " + f.Dir + " (" + f.Describe(false) + ")", f.Ok ? LogKind.Ok : LogKind.Warn);
                if (modsDir == null || modsDir.Equals(oldDefault, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(f.Dir, "mods");
                    if (SyncPaths.Validate(candidate) == null)
                    {
                        modsDir = candidate;
                        Settings.SetPath("mods", modsDir);
                    }
                }
                lastLoad = null;
                ShowLoad();
                UpdateUi();
                if (f.Ok && MaybeRelocate(null, true)) return;
                if (!FactorioInfo.IsRunning()) AnalyzeLoad();
                CheckStatus();
            }
        }

        void PickMods()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = Tr.T("Выберите папку mods, которую нужно держать в соответствии со сборкой", "Choose the mods folder to keep in line with the build"),
                ShowNewFolderButton = true,
                SelectedPath = modsDir ?? (fac != null ? fac.Dir : ""),
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sel = dlg.SelectedPath.TrimEnd('\\');
                if (!Path.GetFileName(sel).Equals("mods", StringComparison.OrdinalIgnoreCase))
                {
                    var r = DarkDialog.Ask(this, Tr.T("Папка модов", "Mods folder"), Tr.T("Папка модов должна называться mods.", "The mods folder must be named mods."),
                        Tr.T("Использовать папку\n", "Use the folder\n") + Path.Combine(sel, "mods") + Tr.T("\n(она будет создана при обновлении)?", "\n(it is created by the update)?"),
                        null, Tr.T("Да", "Yes"), Tr.T("Нет", "No"), Theme.Title);
                    if (r != DialogResult.Yes) return;
                    sel = Path.Combine(sel, "mods");
                }
                var err = SyncPaths.Validate(sel);
                if (err != null) { DarkDialog.Info(this, Tr.T("Папка модов", "Mods folder"), err, Theme.Red); return; }
                modsDir = sel;
                Settings.SetPath("mods", modsDir);
                AppendLog("Папка модов: " + modsDir, LogKind.Info);
                UpdateUi();
                CheckStatus();
            }
        }

        void CancelOp()
        {
            if (!busy || !cancelable) return;
            btnCancel.Enabled = false;
            lblStage.Text = Tr.T("Отменяю…", "Cancelling…");
            Releases.Cancel = true;
            if (git != null) Task.Run(() => git.Cancel());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Auto)
                try { File.AppendAllText(TestAuto, "=== " + AppInfo.Version + " pid " + Process.GetCurrentProcess().Id + " " + SelfUpdate.CurrentExe + "\r\n" + rtbLog.Text.Replace("\n", "\r\n") + "\r\n"); } catch { }
            if (restarting) { gameWatch.Stop(); base.OnFormClosing(e); return; }
            if (busy)
            {
                if (!cancelable)
                {
                    DarkDialog.Info(this, Tr.T("Подождите", "Please wait"), Tr.T("Идёт раскладка файлов в папке mods. Закройте программу, когда она закончит.",
                        "Files are being written into the mods folder. Close the launcher when it finishes."), Theme.Orange);
                    e.Cancel = true;
                    return;
                }
                var r = DarkDialog.Ask(this, Tr.T("Идёт обновление", "An update is running"), Tr.T("Прервать обновление и закрыть программу?", "Stop the update and close the launcher?"),
                    Tr.T("Файлы модов не изменятся, уже скачанное сохранится.", "The mod files do not change; what was downloaded is kept."),
                    null, Tr.T("Прервать и закрыть", "Stop and close"), Tr.T("Продолжить", "Continue"), Theme.Title);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                if (git != null) git.Cancel();
            }
            gameWatch.Stop();
            base.OnFormClosing(e);
        }
    }
}
