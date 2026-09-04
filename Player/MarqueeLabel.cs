// Owner-drawn scrolling text, one or two rows (split on '\n'); position is time-based so
// uneven WM_TIMER firing in Explorer doesn't stutter. Owns ZoneAt; hover draws nothing.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    sealed class MarqueeLabel : Control
    {
        // px/second, and also the step rate: the draw offset is an int, so the text moves
        // one whole pixel Speed times a second. Below ~20 the steps become visible.
        const float Speed = 30f;
        const string RowJoin = "  -  ";  // separator when two rows are collapsed onto one
        const TextFormatFlags TFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        readonly Timer _timer = new() { Interval = 8 };  // up to ~120 fps; WM_TIMER floors near 15ms unless the system timer resolution is raised (browsers playing media usually do)
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        Bitmap _buffer;
        string[] _lines = { "" };  // 1 or 2 rows
        int[] _lineW = { 0 };      // measured pixel width per row
        int _lineH;                // single-row text height
        int _gap = 48;             // blank space between repeats, px; scales with the font
        bool _scroll, _overflow, _hover;

        public MarqueeLabel()
        {
            // We draw the frame ourselves (Opaque = system never erases the bg).
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            _timer.Tick += (s, e) => Frame();  // draw directly, not via Invalidate/WM_PAINT
        }

        // 0 = prev, 1 = play/pause, 2 = next, -1 = not laid out yet. The single definition
        // of the split, which PlayerControl.OnTitleClick dispatches on.
        public int ZoneAt(int x)
        {
            int w = Width;
            if (w <= 0) return -1;
            if (x < w / 4) return 0;
            if (x > w * 3 / 4) return 2;
            return 1;
        }

        public override string Text
        {
            get => base.Text;
            set
            {
                if (base.Text == (value ?? "")) return;
                base.Text = value ?? "";
                Measure();
            }
        }

        void Measure()
        {
            string[] lines = (Text ?? "").Split('\n');
            if (lines.Length > 2) lines = new[] { lines[0], lines[1] };  // cap at two rows
            _lineH = TextRenderer.MeasureText("Ag", Font, new System.Drawing.Size(int.MaxValue, 100), TFlags).Height;
            _gap = Math.Max(16, _lineH * 3);  // ~48px at 96dpi, and proportional above it
            // Under "Use small taskbar buttons" a row band is ~11px against a 15px line,
            // so two rows would overlap and clip -- join them onto one instead.
            if (lines.Length == 2 && Height > 0 && Height / 2 < _lineH)
                lines = new[] { lines[1].Length == 0 ? lines[0] : lines[0] + RowJoin + lines[1] };
            _lines = lines;
            _lineW = new int[_lines.Length];
            _overflow = false;
            for (int i = 0; i < _lines.Length; i++)
            {
                _lineW[i] = TextRenderer.MeasureText(_lines[i], Font, new System.Drawing.Size(int.MaxValue, 100), TFlags).Width;
                if (_lineW[i] > Width) _overflow = true;
            }
            UpdateScroll();
        }

        // Scroll only while the mouse is over the label and the text overflows.
        void UpdateScroll()
        {
            bool scroll = _hover && _overflow;
            if (scroll && !_scroll) _clock.Restart();  // start from the left each hover
            _scroll = scroll;
            _timer.Enabled = _scroll && IsHandleCreated;
            Frame();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; UpdateScroll(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; UpdateScroll(); base.OnMouseLeave(e); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Measure(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Measure(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Measure(); }
        protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

        // Render immediately to the control's DC, tied to the timer's steady
        // cadence instead of the starved WM_PAINT queue.
        void Frame()
        {
            if (!IsHandleCreated) return;
            using (var g = CreateGraphics())
                Render(g);
        }

        void Render(Graphics target)
        {
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
            {
                _buffer?.Dispose();
                _buffer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);  // opaque: GDI ClearType text
            }
            using (var g = Graphics.FromImage(_buffer))
            {
                g.Clear(BackColor);
                int rows = _lines.Length;
                int rowH = h / rows;
                int maxW = w;
                foreach (int lwi in _lineW) if (lwi > maxW) maxW = lwi;
                int period = maxW + _gap;  // shared: both rows reset together, short one waits for the long one
                int off = (int)((_clock.Elapsed.TotalSeconds * Speed) % period);
                for (int i = 0; i < rows; i++)
                {
                    string line = _lines[i];
                    int lw = _lineW[i];
                    int y = i * rowH + (rowH - _lineH) / 2;  // center within the row band
                    if (_scroll && lw > w)
                    {
                        TextRenderer.DrawText(g, line, Font, new System.Drawing.Point(-off, y), ForeColor, BackColor, TFlags);
                        TextRenderer.DrawText(g, line, Font, new System.Drawing.Point(-off + period, y), ForeColor, BackColor, TFlags);  // seamless loop
                    }
                    else
                    {
                        // Bounded overload so GDI has an edge to ellipsize against; the Point
                        // one would just clip mid-glyph with nothing saying the title goes on.
                        TextRenderer.DrawText(g, line, Font, new Rectangle(0, y, w, _lineH),
                                              ForeColor, BackColor, TFlags | TextFormatFlags.EndEllipsis);
                    }
                }

                // Marks only while hovered -- at rest the band must read as plain taskbar text.
                // After the text: DrawText passes a backColor, so a glyph run erases what is under.
                if (_hover)
                    using (var divider = new Pen(Color.FromArgb(110, ForeColor)))
                    {
                        g.DrawLine(divider, w / 4, 0, w / 4, h);
                        g.DrawLine(divider, w * 3 / 4, 0, w * 3 / 4, h);
                    }
            }
            target.DrawImageUnscaled(_buffer, 0, 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _buffer?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
