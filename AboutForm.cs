// About / how-to-use dialog for the mini player.
//
// Every control on the band is an unlabeled gesture (invisible click zones on the
// title, wheel = volume, bottom-edge = seek), so a plain text list is hard to map
// onto the real thing. This dialog draws a static *mock* of the band -- painted the
// same taskbar color, with a sample title/artist and a half-filled progress bar --
// and labels the left/middle/right zones directly on it, so the user can picture
// where each gesture lives. Owner-drawn (like MarqueeLabel) with one real OK button.
//
// Static mock only: a live PlayerControl would read real SMTC and its click zones
// would fire transport commands inside the dialog.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    sealed class AboutForm : Form
    {
        const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags FWrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

        const string SampleTitle = "Bohemian Rhapsody";  // sample data, not translated
        const string SampleArtist = "Queen";

        readonly Color _bg, _fg, _fgDim;
        readonly string _heading;
        readonly string[] _captions;   // one gesture per line, last may be the host note

        readonly Font _headingFont;
        readonly Font _bandFont;        // matches the real title font (Segoe UI 9pt)
        readonly Button _ok;

        public AboutForm(Color bg, Color fg, Color fgDim, Version version, string hostNote)
        {
            _bg = bg;
            _fg = fg;
            _fgDim = fgDim;
            _heading = Loc.AppName + " " + version;

            _captions = BuildCaptions(hostNote);

            Font = SystemFonts.MessageBoxFont;  // clean dialog look (Segoe UI 9pt)
            _headingFont = new Font(Font.FontFamily, Font.SizeInPoints + 2f, FontStyle.Bold);
            _bandFont = new Font("Segoe UI", 9f);

            Text = Loc.AppName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            TopMost = true;                        // above the always-on-top floating host
            StartPosition = FormStartPosition.CenterScreen;
            // Layout is driven entirely by font metrics below (LineH/Scale), which
            // already track DPI via the DPI-scaled system font -- so keep auto-scale
            // off, or the form gets scaled a second time and the OK button drifts.
            AutoScaleMode = AutoScaleMode.None;
            BackColor = SystemColors.Control;
            DoubleBuffered = true;

            int pad = LineH();
            _ok = new Button
            {
                Text = Loc.S.Ok,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Size = new Size(Scale(84), LineH() + Scale(10)),
            };
            Controls.Add(_ok);
            AcceptButton = _ok;
            CancelButton = _ok;                    // Esc closes too

            // Size the window to the measured content, then pin the OK button.
            int width = pad * 2 + Scale(430);
            int height = RenderContent(null, width);   // measure pass: returns content bottom
            ClientSize = new Size(width, height + pad + _ok.Height + pad);
            _ok.Location = new Point(ClientSize.Width - pad - _ok.Width,
                                     ClientSize.Height - pad - _ok.Height);
        }

        static string[] BuildCaptions(string hostNote)
        {
            string[] baseLines =
            {
                Loc.S.CheatVolume,
                Loc.S.CheatMute,
                Loc.S.CheatSeek,
                Loc.S.CheatMenu,
            };
            if (string.IsNullOrEmpty(hostNote)) return baseLines;
            var all = new string[baseLines.Length + 1];
            Array.Copy(baseLines, all, baseLines.Length);
            all[baseLines.Length] = hostNote;
            return all;
        }

        int LineH() => TextRenderer.MeasureText("Ag", Font).Height;
        int Scale(int px) => (int)Math.Round(px * (LineH() / 15.0));  // 15px = the ~9pt line height at 96dpi

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderContent(e.Graphics, ClientSize.Width);
        }

        // Single top-to-bottom layout walk used for both sizing (g == null, measure
        // only) and drawing. y advances identically either way, so the measured
        // height always matches what gets painted. Returns the content's bottom y.
        int RenderContent(Graphics g, int width)
        {
            int lineH = LineH();
            int pad = lineH;
            int x = pad;
            int contentW = width - pad * 2;
            int y = pad;

            // Heading + one-line description.
            if (g != null) TextRenderer.DrawText(g, _heading, _headingFont, new Point(x, y), SystemColors.ControlText, F);
            y += TextRenderer.MeasureText("Ag", _headingFont).Height + lineH / 3;

            string desc = Loc.S.AboutDesc;
            Size descSize = TextRenderer.MeasureText(desc, Font, new Size(contentW, int.MaxValue), FWrap);
            if (g != null) TextRenderer.DrawText(g, desc, Font, new Rectangle(x, y, contentW, descSize.Height), SystemColors.ControlText, FWrap);
            y += descSize.Height + lineH;

            // ---- the mock band ----
            int bandH = Scale(46);
            if (g != null) DrawBand(g, new Rectangle(x, y, contentW, bandH));
            y += bandH + lineH / 2;

            // Zone labels centered under each click zone, with a fraction sub-caption.
            int prevC = x + contentW / 8;
            int playC = x + contentW / 2;
            int nextC = x + contentW * 7 / 8;
            if (g != null)
            {
                DrawCentered(g, Loc.S.ZonePrev, Font, prevC, y, SystemColors.ControlText);
                DrawCentered(g, Loc.S.ZonePlay, Font, playC, y, SystemColors.ControlText);
                DrawCentered(g, Loc.S.ZoneNext, Font, nextC, y, SystemColors.ControlText);
            }
            y += lineH;
            if (g != null)
            {
                DrawCentered(g, Loc.S.FracLeft, Font, prevC, y, SystemColors.GrayText);
                DrawCentered(g, Loc.S.FracMiddle, Font, playC, y, SystemColors.GrayText);
                DrawCentered(g, Loc.S.FracRight, Font, nextC, y, SystemColors.GrayText);
            }
            y += lineH + lineH;

            // ---- gesture cheat sheet ----
            foreach (string line in _captions)
            {
                if (g != null) TextRenderer.DrawText(g, line, Font, new Point(x, y), SystemColors.ControlText, F);
                y += lineH + lineH / 4;
            }

            return y;
        }

        // Paint the mock band: taskbar-color fill, sample title/artist, a ~45% progress
        // bar, and faint dividers at 1/4 and 3/4 marking the click zones.
        void DrawBand(Graphics g, Rectangle band)
        {
            using (var bg = new SolidBrush(_bg))
                g.FillRectangle(bg, band);

            int inner = Math.Max(3, band.Height / 12);
            int barH = Math.Max(2, band.Height / 16);
            int textH = band.Height - barH - inner;   // rows sit above the bar
            int rowH = textH / 2;
            int tx = band.X + inner;

            int titleY = band.Y + (rowH - _bandFont.Height) / 2;
            int artistY = band.Y + rowH + (rowH - _bandFont.Height) / 2;
            TextRenderer.DrawText(g, SampleTitle, _bandFont, new Point(tx, titleY), _fg, F);
            TextRenderer.DrawText(g, SampleArtist, _bandFont, new Point(tx, artistY), _fgDim, F);

            // Faint zone dividers, only across the text area (not over the bar).
            using (var pen = new Pen(Color.FromArgb(90, _fg)))
            {
                int d1 = band.X + band.Width / 4;
                int d3 = band.X + band.Width * 3 / 4;
                g.DrawLine(pen, d1, band.Y + inner, d1, band.Y + textH);
                g.DrawLine(pen, d3, band.Y + inner, d3, band.Y + textH);
            }

            // Progress bar along the bottom edge: dim track + brighter played portion.
            int by = band.Bottom - barH;
            using (var track = new SolidBrush(PlayerControl.Shade(_bg, 24)))
                g.FillRectangle(track, band.X, by, band.Width, barH);
            using (var fill = new SolidBrush(_fg))
                g.FillRectangle(fill, band.X, by, (int)(band.Width * 0.45), barH);
        }

        static void DrawCentered(Graphics g, string s, Font f, int cx, int y, Color c)
        {
            int w = TextRenderer.MeasureText(g, s, f, Size.Empty, F).Width;
            TextRenderer.DrawText(g, s, f, new Point(cx - w / 2, y), c, F);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _headingFont?.Dispose(); _bandFont?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
