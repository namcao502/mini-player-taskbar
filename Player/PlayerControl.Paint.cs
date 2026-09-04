// Everything the band draws itself. Nothing here goes through Invalidate(): a posted
// WM_PAINT is starved by the taskbar's pump, so OnPaintBackground never runs.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    partial class PlayerControl
    {
        // Paint the band's own background with the sampled color -- the base
        // control leaves uncovered margins the default gray otherwise.
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(_bg))
                e.Graphics.FillRectangle(b, ClientRectangle);
            DrawProgress(e.Graphics, ClientSize.Height - _barH);
        }

        // Scaled off the title font's line height so the strip and margins follow DPI;
        // 15px is that height at 96dpi (AboutForm.Scale uses the same reference).
        void MeasureMetrics()
        {
            _lineH = Math.Max(1, TextRenderer.MeasureText("Ag", _title.Font).Height);
            _barH = Scale(2);
            _seekH = Scale(8);
            _titlePad = Scale(2);
        }

        int Scale(int px) => Math.Max(1, (int)Math.Round(px * (_lineH / 15.0)));

        // Thin bar along the bottom edge: played portion + dim track for the rest.
        // The two fills don't overlap, so repainting it every second doesn't flicker.
        void DrawProgress(Graphics g, int y)
        {
            int w = ClientSize.Width;
            if (w <= 0 || y < 0) return;
            double f = ProgressFraction();
            if (f < 0)  // no/unknown duration -> no bar, and erase a stale one
            {
                using (var b = new SolidBrush(_bg))
                    g.FillRectangle(b, 0, y, w, _barH);
                return;
            }
            int fw = (int)(w * f);
            using (var fill = new SolidBrush(_fg))
                g.FillRectangle(fill, 0, y, fw, _barH);
            using (var track = new SolidBrush(Shade(_bg, 24)))
                g.FillRectangle(track, fw, y, w - fw, _barH);
        }

        // Opaque 24bpp back-buffer blitted under a clip, not Invalidate: in Explorer an
        // unpainted area stays window-class white and a direct DC fill leaves alpha 0.
        void RepaintChrome()
        {
            if (!IsHandleCreated) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            if (_chromeBuf == null || _chromeBuf.Width != w || _chromeBuf.Height != h)
            {
                _chromeBuf?.Dispose();
                _chromeBuf = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            using (var b = Graphics.FromImage(_chromeBuf))
            {
                using (var bg = new SolidBrush(_bg))
                    b.FillRectangle(bg, 0, 0, w, h);
                DrawProgress(b, h - _barH);
            }
            using (var region = new Region(new Rectangle(0, 0, _titlePad, h)))
            {
                region.Union(new Rectangle(w - _titlePad, 0, _titlePad, h));
                region.Union(new Rectangle(0, h - _seekH, w, _seekH));
                using (var g = CreateGraphics())
                {
                    g.Clip = region;
                    g.DrawImageUnscaled(_chromeBuf, 0, 0);
                }
            }
        }

        // Height-adaptive: the scrolling title fills the full width; the bottom
        // strip is left for the progress bar + its click-to-seek target.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_title == null) return;
            int h = ClientSize.Height, w = ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            _title.SetBounds(_titlePad, 0, Math.Max(0, w - _titlePad * 2), h - _seekH);
            RepaintChrome();  // the new margins/strip won't repaint themselves in Explorer
        }
    }
}
