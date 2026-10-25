using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Paranoidal
{
    static class Theme
    {
        public static readonly Color Window = Color.FromArgb(34, 33, 34);
        public static readonly Color Card = Color.FromArgb(49, 48, 49);
        public static readonly Color CardBorder = Color.FromArgb(16, 16, 16);
        public static readonly Color CardHi = Color.FromArgb(78, 76, 76);
        public static readonly Color Inset = Color.FromArgb(26, 26, 27);
        public static readonly Color Header = Color.FromArgb(56, 55, 56);
        public static readonly Color Text = Color.FromArgb(226, 226, 226);
        public static readonly Color Muted = Color.FromArgb(150, 148, 146);
        public static readonly Color Title = Color.FromArgb(255, 230, 192);
        public static readonly Color Orange = Color.FromArgb(255, 160, 32);
        public static readonly Color Green = Color.FromArgb(96, 190, 96);
        public static readonly Color Red = Color.FromArgb(238, 88, 72);
        public static readonly Color Yellow = Color.FromArgb(255, 212, 0);
        public static readonly Color Grey = Color.FromArgb(86, 85, 86);

        public static float Scale = 1f;
        public static int S(float v) => (int)Math.Round(v * Scale);

        public static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        public static Color Light(Color c, float t) => Mix(c, Color.White, t);
        public static Color Dark(Color c, float t) => Mix(c, Color.Black, t);

        /// <summary>Yellow/black hazard stripes, like the PARANOIDAL logo.</summary>
        public static void Hazard(Graphics g, Rectangle r, int offset, Color a, Color b)
        {
            using (var bb = new SolidBrush(b)) g.FillRectangle(bb, r);
            int w = Math.Max(4, r.Height), step = w * 2;
            var old = g.Clip;
            g.SetClip(r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var ab = new SolidBrush(a))
                for (int x = r.Left - r.Height - step + (offset % step); x < r.Right + step; x += step)
                    g.FillPolygon(ab, new[] { new Point(x, r.Bottom), new Point(x + r.Height, r.Top), new Point(x + r.Height + w, r.Top), new Point(x + w, r.Bottom) });
            g.SmoothingMode = SmoothingMode.Default;
            g.Clip = old;
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public static void DarkTitle(IntPtr hwnd)
        {
            int on = 1;
            try { if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4); } catch { }
        }

        public static void DarkScrollbars(Control c)
        {
            c.HandleCreated += (s, e) => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
        }
    }

    enum ButtonKind { Normal, Primary, Success, Tab }

    /// <summary>Flat Factorio-like button.</summary>
    sealed class FButton : Control
    {
        public ButtonKind Kind;
        bool hover, down, check;

        public bool Checked { get { return check; } set { check = value; Invalidate(); } }

        public FButton(string text, ButtonKind kind)
        {
            Text = text;
            Kind = kind;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", kind == ButtonKind.Primary || kind == ButtonKind.Success ? 11.5f : 9.75f);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            if (Kind == ButtonKind.Tab)
            {
                using (var b = new SolidBrush(check ? Theme.Card : hover ? Theme.Mix(Theme.Inset, Theme.Card, .5f) : Theme.Inset)) g.FillRectangle(b, r);
                if (check) using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, 0, r.Bottom - Theme.S(3), r.Width, Theme.S(3));
                TextRenderer.DrawText(g, Text, Font, r, check ? Theme.Title : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            Color baseC = Kind == ButtonKind.Primary ? Theme.Orange : Kind == ButtonKind.Success ? Theme.Green : Theme.Grey;
            Color fore = Kind == ButtonKind.Normal ? Theme.Text : Color.FromArgb(20, 18, 16);
            if (!Enabled) { baseC = Color.FromArgb(58, 57, 58); fore = Color.FromArgb(118, 116, 114); }
            else if (down) baseC = Theme.Dark(baseC, .18f);
            else if (hover) baseC = Theme.Light(baseC, .14f);

            using (var b = new LinearGradientBrush(r, Theme.Light(baseC, .10f), Theme.Dark(baseC, .12f), 90f)) g.FillRectangle(b, r);
            using (var p = new Pen(Theme.Light(baseC, .30f))) g.DrawLine(p, 1, 1, r.Width - 2, 1);
            using (var p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            var tr = r;
            if (down) tr.Offset(0, 1);
            TextRenderer.DrawText(g, Text, Font, tr, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Card panel with a title, like Factorio's inner frames.</summary>
    sealed class Card : Panel
    {
        public string Title;
        readonly Font titleFont = new Font("Segoe UI Semibold", 10.5f);

        public Card(string title)
        {
            Title = title;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            using (var p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            using (var p = new Pen(Theme.CardHi)) g.DrawLine(p, 1, 1, r.Width - 2, 1);
            TextRenderer.DrawText(g, Title, titleFont, new Point(Theme.S(12), Theme.S(8)), Theme.Title);
            int y = Theme.S(33);
            using (var p = new Pen(Theme.CardBorder)) g.DrawLine(p, Theme.S(10), y, r.Width - Theme.S(10), y);
            using (var p = new Pen(Theme.CardHi)) g.DrawLine(p, Theme.S(10), y + 1, r.Width - Theme.S(10), y + 1);
        }
    }

    /// <summary>Header with the Factorio Paranoidal logo and hazard stripes.</summary>
    sealed class HeaderPanel : Control
    {
        readonly Font small = new Font("Segoe UI", 9f);

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            int stripe = Theme.S(9);
            var top = new Rectangle(0, 0, r.Width, r.Height - stripe);
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, r.Width, Math.Max(1, top.Height)), Color.FromArgb(52, 48, 44), Color.FromArgb(22, 21, 21), 90f))
                g.FillRectangle(b, top);
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(r.Width / 2 - r.Width / 3, -top.Height / 2, r.Width * 2 / 3, top.Height * 2);
                using (var pb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, 255, 150, 30), SurroundColors = new[] { Color.FromArgb(0, 255, 150, 30) } })
                    g.FillPath(pb, glow);
            }
            var img = Res.Splash;
            if (img != null)
            {
                int h = top.Height - Theme.S(26);
                int w = (int)(img.Width * (h / (float)img.Height));
                if (w > r.Width - Theme.S(40)) { w = r.Width - Theme.S(40); h = (int)(img.Height * (w / (float)img.Width)); }
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle((r.Width - w) / 2, (top.Height - h) / 2, w, h));
            }
            TextRenderer.DrawText(g, AppInfo.Name + " " + AppInfo.Version, small, new Rectangle(0, Theme.S(6), r.Width - Theme.S(10), Theme.S(18)), Theme.Muted, TextFormatFlags.Right);
            // the game the build needs (same wording as in the card and dialogs), not the game that was found
            TextRenderer.DrawText(g, Tr.T("для ", "for ") + AppInfo.RequiredGame, small,
                new Rectangle(0, top.Height - Theme.S(20), r.Width - Theme.S(10), Theme.S(18)), Theme.Muted, TextFormatFlags.Right);
            Theme.Hazard(g, new Rectangle(0, top.Bottom, r.Width, stripe), 0, Theme.Yellow, Color.FromArgb(20, 20, 20));
        }
    }

    /// <summary>Progress bar with animated hazard stripes while waiting.</summary>
    sealed class StripeProgress : Control
    {
        double value;
        bool indeterminate;
        int offset;
        readonly Timer timer = new Timer { Interval = 40 };
        readonly Font font = new Font("Segoe UI Semibold", 9f);
        public bool Active;

        public StripeProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            timer.Tick += (s, e) => { offset += Theme.S(2); Invalidate(); };
        }

        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        public bool Indeterminate
        {
            get { return indeterminate; }
            set { indeterminate = value; Sync(); Invalidate(); }
        }

        public void SetActive(bool a) { Active = a; Sync(); Invalidate(); }

        void Sync() { timer.Enabled = Active; }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            using (var b = new SolidBrush(Theme.Inset)) g.FillRectangle(b, r);
            var inner = Rectangle.Inflate(r, -Theme.S(2), -Theme.S(2));
            if (indeterminate && Active)
            {
                Theme.Hazard(g, inner, offset, Color.FromArgb(150, 96, 20), Color.FromArgb(48, 40, 30));
            }
            else if (value > 0)
            {
                var fill = new Rectangle(inner.X, inner.Y, Math.Max(1, (int)(inner.Width * value)), inner.Height);
                using (var b = new LinearGradientBrush(fill, Theme.Light(Theme.Orange, .15f), Theme.Dark(Theme.Orange, .25f), 90f)) g.FillRectangle(b, fill);
                if (Active)
                {
                    var old = g.Clip;
                    g.SetClip(fill);
                    using (var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(40, 0, 0, 0), Color.Transparent))
                    {
                        g.RenderingOrigin = new Point(offset % 16, 0);
                        g.FillRectangle(hb, fill);
                    }
                    g.Clip = old;
                }
                var txt = (value * 100).ToString(value < 0.1 ? "0.0" : "0", Fmt.Ru) + " %";
                TextRenderer.DrawText(g, txt, font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (var p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
        }
    }

    /// <summary>Dark message/question dialog.</summary>
    sealed class DarkDialog : Form
    {
        DarkDialog() { }

        public static DialogResult Ask(IWin32Window owner, string title, string message, string sub, IList<string> details,
            string yesText, string noText, Color accent)
        {
            var S = new Func<float, int>(Theme.S);
            var f = new DarkDialog
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
            int width = S(details != null ? 640 : 520), pad = S(18), iconSize = S(56);
            int textX = pad + iconSize + S(14), textW = width - textX - pad;

            var pic = new PictureBox { Image = Res.Gear, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(pad, pad, iconSize, iconSize), BackColor = Theme.Window };
            f.Controls.Add(pic);

            var mf = new Font("Segoe UI Semibold", 11.5f);
            int y = pad;
            var mh = TextRenderer.MeasureText(message, mf, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak).Height;
            f.Controls.Add(new Label { Text = message, Font = mf, ForeColor = accent, Bounds = new Rectangle(textX, y, textW, mh + S(4)), UseMnemonic = false });
            y += mh + S(10);
            if (!string.IsNullOrEmpty(sub))
            {
                var sh = TextRenderer.MeasureText(sub, f.Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak).Height;
                f.Controls.Add(new Label { Text = sub, ForeColor = Theme.Text, Bounds = new Rectangle(textX, y, textW, sh + S(4)), UseMnemonic = false });
                y += sh + S(8);
            }
            y = Math.Max(y, pad + iconSize + S(8));
            if (details != null && details.Count > 0)
            {
                var box = new RichTextBox
                {
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = Theme.Inset,
                    ForeColor = Theme.Text,
                    Font = new Font("Consolas", 9f),
                    Bounds = new Rectangle(pad, y, width - 2 * pad, S(190)),
                    Text = string.Join("\n", details),
                    WordWrap = false,
                };
                Native.DarkScrollbars(box);
                f.Controls.Add(box);
                y += S(190) + S(12);
            }
            int bh = S(38), x = width - pad;
            FButton noBtn = null;
            if (noText != null)
            {
                int w = Math.Max(S(130), TextRenderer.MeasureText(noText, new Font("Segoe UI Semibold", 9.75f)).Width + S(30));
                x -= w;
                noBtn = new FButton(noText, ButtonKind.Normal) { Bounds = new Rectangle(x, y, w, bh) };
                noBtn.Click += (s, e) => { f.DialogResult = DialogResult.No; f.Close(); };
                f.Controls.Add(noBtn);
                x -= S(10);
            }
            var yesFont = new Font("Segoe UI Semibold", 11.5f);
            int yw = Math.Max(S(150), TextRenderer.MeasureText(yesText, yesFont).Width + S(34));
            x -= yw;
            var yesBtn = new FButton(yesText, ButtonKind.Primary) { Bounds = new Rectangle(x, y, yw, bh) };
            yesBtn.Click += (s, e) => { f.DialogResult = DialogResult.Yes; f.Close(); };
            f.Controls.Add(yesBtn);
            y += bh + pad;

            f.ClientSize = new Size(width, y);
            f.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.Yes; f.Close(); }
                else if (e.KeyCode == Keys.Escape) { f.DialogResult = noText == null ? DialogResult.Yes : DialogResult.Cancel; f.Close(); }
            };
            f.HandleCreated += (s, e) => Native.DarkTitle(f.Handle);
            var res = f.ShowDialog(owner);
            f.Dispose();
            return res;
        }

        public static void Info(IWin32Window owner, string title, string message, Color accent)
        {
            Ask(owner, title, message, null, null, "OK", null, accent);
        }
    }
}
