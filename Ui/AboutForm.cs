// About / how-to-use dialog. Embeds a second, real PlayerControl as a live demo -- since
// a text list of invisible gestures is hard to map on, showing the actual band (real
// track, real prev/play/next, real hover tooltip) teaches it better than a diagram would.
// That demo is built with firstRunEligible: false, or a genuine first run would open a
// second About on top of this one (see PlayerControl.OnHandleCreated).

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    sealed class AboutForm : Form
    {
        const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags FWrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

        readonly string _heading;
        readonly string[] _captions;   // one gesture per line

        readonly Font _headingFont;
        readonly Button _ok;
        readonly PlayerControl _demoPlayer;  // the live demo band; see file header
        Rectangle _bandRect;                 // where RenderContent lays out the demo

        public AboutForm(Version version)
        {
            _heading = Loc.AppName + " " + version;

            _captions = new[]
            {
                Loc.S.CheatVolume,
                Loc.S.CheatMute,
                Loc.S.CheatSeek,
                Loc.S.CheatMenu,
            };

            Font = SystemFonts.MessageBoxFont;  // clean dialog look (Segoe UI 9pt)
            _headingFont = new Font(Font.FontFamily, Font.SizeInPoints + 2f, FontStyle.Bold);

            Text = Loc.AppName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            TopMost = true;                        // the taskbar is topmost; the dialog must clear it
            StartPosition = FormStartPosition.CenterScreen;
            // Off, because LineH/Scale already track DPI through the system font --
            // auto-scale would apply it a second time and the OK button would drift.
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
            int height = RenderContent(null, width);   // measure pass: also sets _bandRect
            ClientSize = new Size(width, height + pad + _ok.Height + pad);
            _ok.Location = new Point(ClientSize.Width - pad - _ok.Width,
                                     ClientSize.Height - pad - _ok.Height);

            _demoPlayer = new PlayerControl(firstRunEligible: false) { Bounds = _bandRect };
            Controls.Add(_demoPlayer);
        }

        int LineH() => TextRenderer.MeasureText("Ag", Font).Height;
        int Scale(int px) => (int)Math.Round(px * (LineH() / 15.0));  // 15px = the ~9pt line height at 96dpi

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderContent(e.Graphics, ClientSize.Width);
        }

        // One walk serving both sizing (g == null) and drawing, so the measured height
        // cannot drift from what gets painted. Returns the content's bottom y.
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

            // ---- the live demo band -- painted by _demoPlayer itself, not here ----
            int bandH = Scale(46);
            _bandRect = new Rectangle(x, y, contentW, bandH);
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

        static void DrawCentered(Graphics g, string s, Font f, int cx, int y, Color c)
        {
            int w = TextRenderer.MeasureText(g, s, f, Size.Empty, F).Width;
            TextRenderer.DrawText(g, s, f, new Point(cx - w / 2, y), c, F);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _headingFont?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
