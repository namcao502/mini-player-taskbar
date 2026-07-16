// Mini media player as a Windows taskbar deskband.
//
// A COM shell extension (CSDeskBand does the IDeskBand2 plumbing) that reads
// the active SMTC session and shows a prev button, the track title + artist (two
// scrolling rows), and a next button in the taskbar; clicking the title toggles
// play/pause. Event-driven, no polling. Long rows scroll smoothly on hover.
// Mouse wheel over the band changes system volume.
//
// Deprecated tech: deskbands work on Windows 10, but were removed in Windows 11.
// Build:     dotnet build -c Release
// Register:  register.bat   (self-elevates; runs RegAsm /codebase)
// Enable:    right-click the taskbar -> Toolbars -> Mini Player
//
// Threading: Explorer hosts the band with no WinForms SynchronizationContext,
// so awaits resume off the UI thread; every UI write goes through UiPost.
// SMTC is started from OnHandleCreated. Layout is height-adaptive so it fits the
// normal taskbar, "Use small taskbar buttons", and DPI scaling.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSDeskBand;
using CSDeskBand.Win;
using Windows.Foundation;
using Windows.Media.Control;

namespace MiniPlayerBand
{
    using Session = GlobalSystemMediaTransportControlsSession;
    using SessionManager = GlobalSystemMediaTransportControlsSessionManager;
    using MediaProps = GlobalSystemMediaTransportControlsSessionMediaProperties;

    [ComVisible(true)]
    [Guid("D7B2E4A1-3F56-4C8B-9E0D-2A6C1F5B8E44")]  // keep stable across rebuilds (it is the COM CLSID)
    [CSDeskBandRegistration(Name = "Mini Player", ShowDeskBand = true)]
    public class Band : CSDeskBandWin
    {
        // Segoe MDL2 Assets glyphs.
        const string GlyphPrev = "\uE892";
        const string GlyphNext = "\uE893";

        static readonly Color Fg = Color.FromArgb(240, 240, 240);
        Color _bg = Color.FromArgb(32, 32, 32);  // taskbar color; sampled from the real taskbar at startup

        internal static Color Lighten(Color c, int d) =>
            Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d));

        readonly MarqueeLabel _title = new();
        readonly IconButton _prev;
        readonly IconButton _next;

        SessionManager _mgr;
        Session _session;
        int _refreshSeq;      // newest RefreshAsync wins; older async reads are dropped
        bool _inited;         // guard so Init runs once
        string _trackTitle = "No media";                        // real title, restored after the volume readout
        readonly Timer _volTimer = new() { Interval = 1200 };   // how long the volume number stays
        readonly Timer _clearTimer = new() { Interval = 800 };  // debounce before falling back to "No media"

        public Band()
        {
            _bg = TaskbarColor();  // sample the taskbar color first, so children are built with it
            Options.Title = "Mini Player";
            Options.ShowTitle = false;
            Options.MinHorizontalSize = new CSDeskBand.Size(150, 20);
            Options.HorizontalSize = new CSDeskBand.Size(150, 40);  // fixed width (min == desired) so it does not auto-resize
            BackColor = _bg;

            _title.BackColor = _bg;
            _title.ForeColor = Fg;
            _title.Font = new Font("Segoe UI", 9f);
            _title.Text = "No media";
            _title.Cursor = Cursors.Hand;
            _title.Click += async (s, e) => await RunCommand(x => x.TryTogglePlayPauseAsync());  // click title toggles too
            Controls.Add(_title);

            _prev = MakeButton(GlyphPrev, x => x.TrySkipPreviousAsync());
            _next = MakeButton(GlyphNext, x => x.TrySkipNextAsync());
            Controls.Add(_prev);
            Controls.Add(_next);

            // Wheel over any part of the band adjusts volume.
            foreach (Control c in new Control[] { this, _title, _prev, _next })
                c.MouseWheel += OnWheel;
            _volTimer.Tick += (s, e) => { _volTimer.Stop(); _title.Text = _trackTitle; };  // restore title
            _clearTimer.Tick += (s, e) => { _clearTimer.Stop(); SetTitle("No media"); };
        }

        // Central title setter: don't overwrite the volume readout while it is showing.
        void SetTitle(string text)
        {
            _trackTitle = text;
            if (!_volTimer.Enabled) _title.Text = text;
        }

        IconButton MakeButton(string glyph, Func<Session, IAsyncOperation<bool>> op)
        {
            var b = new IconButton(glyph) { BackColor = _bg, ForeColor = Fg };
            b.Clicked += async () => await RunCommand(op);
            return b;
        }

        // The base ctor creates the handle before a HandleCreated subscription
        // would catch it, so start SMTC from this virtual instead.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_inited) return;
            _inited = true;
            _ = Init();
        }

        // Paint the band's own background with the sampled color — the base
        // control leaves uncovered margins the default gray otherwise.
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(_bg))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        // Height-adaptive: prev button pinned left, next button pinned right, the
        // scrolling title fills the middle. Buttons vertically centered, never clipped.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_title == null || _prev == null || _next == null) return;
            int h = ClientSize.Height, w = ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            const int pad = 2;
            int bw = Math.Min(34, Math.Max(22, w / 12));  // button width
            int bh = Math.Min(h - pad * 2, 32);           // button height (capped)
            int by = (h - bh) / 2;                        // vertically centered

            _prev.SetBounds(pad, by, bw, bh);
            int nextX = w - pad - bw;
            _next.SetBounds(nextX, by, bw, bh);

            int titleX = pad + bw + 4;
            _title.SetBounds(titleX, 0, Math.Max(0, nextX - titleX - 4), h);
        }

        // ---- SMTC wiring (events, no polling) ----

        async Task Init()
        {
            try
            {
                _mgr = await SessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (s, e) => UiPost(HookSession);
                UiPost(HookSession);
            }
            catch
            {
                UiPost(() => _title.Text = "SMTC unavailable");
            }
        }

        // Runs on the UI thread (always called via UiPost).
        void HookSession()
        {
            if (_session != null)
                _session.MediaPropertiesChanged -= OnMediaProps;
            _session = _mgr.GetCurrentSession();
            if (_session != null)
                _session.MediaPropertiesChanged += OnMediaProps;
            _ = RefreshAsync();
        }

        void OnMediaProps(Session s, MediaPropertiesChangedEventArgs e) => UiPost(() => { _ = RefreshAsync(); });

        // Marshal an action onto the control's UI thread.
        void UiPost(Action a)
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        async Task RefreshAsync()
        {
            int seq = ++_refreshSeq;  // only the newest refresh may touch the UI
            var s = _session;
            if (s == null) { UiPost(() => ScheduleNoMedia(seq)); return; }
            try
            {
                MediaProps props = await s.TryGetMediaPropertiesAsync();
                string title = props.Title ?? "";
                // Empty title (or a null session above) is usually a transient gap while
                // skipping tracks. Don't clear yet — let the debounce decide.
                if (title.Length == 0) { UiPost(() => ScheduleNoMedia(seq)); return; }
                string artist = props.Artist ?? "";
                // Two rows: title on top, artist below (newline = second line in MarqueeLabel).
                string display = artist.Length == 0 ? title : title + "\n" + artist;

                UiPost(() =>
                {
                    if (seq != _refreshSeq) return;  // superseded by a newer refresh
                    _clearTimer.Stop();  // real data arrived; cancel any pending "No media"
                    SetTitle(display);
                });
            }
            catch { }
        }

        // Fall back to "No media" only if no real track shows up within the debounce
        // window, so a brief null/empty gap while skipping doesn't flash the placeholder.
        void ScheduleNoMedia(int seq)
        {
            if (seq != _refreshSeq) return;
            _clearTimer.Stop();
            _clearTimer.Start();
        }

        async Task RunCommand(Func<Session, IAsyncOperation<bool>> op)
        {
            var s = _session;
            if (s == null) return;
            try { await op(s); }
            catch { }  // keep the band alive
        }

        // ---- taskbar color sampling ----

        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        struct RECT { public int Left, Top, Right, Bottom; }

        // Sample the taskbar background: the most common pixel color along its
        // middle row. Falls back to a dark gray if anything looks off.
        static Color TaskbarColor()
        {
            Color fallback = Color.FromArgb(0, 0, 0);  // dark taskbars are near-black
            try
            {
                var tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero || !GetWindowRect(tray, out var r)) return fallback;
                var dc = GetDC(IntPtr.Zero);  // screen DC = final composited pixels (acrylic/blur included)
                if (dc == IntPtr.Zero) return fallback;
                try
                {
                    var counts = new Dictionary<uint, int>();
                    int y = (r.Top + r.Bottom) / 2;
                    for (int x = r.Left + 4; x < r.Right - 4; x += 8)
                    {
                        uint px = GetPixel(dc, x, y);
                        if (px == 0xFFFFFFFF) continue;  // CLR_INVALID
                        counts[px] = counts.TryGetValue(px, out var c) ? c + 1 : 1;
                    }
                    if (counts.Count == 0) return fallback;
                    uint mode = 0; int best = -1;
                    foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; mode = kv.Key; }
                    return Color.FromArgb((int)(mode & 0xFF), (int)((mode >> 8) & 0xFF), (int)((mode >> 16) & 0xFF));
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            }
            catch { return fallback; }
        }

        // ---- volume: wheel over the band, direct Core Audio (no OSD banner) ----

        void OnWheel(object sender, MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;  // stop it bubbling to the parent (double-counts the step)
            int notches = e.Delta / 120;
            if (notches == 0) return;
            int pct = AdjustVolume(0.02f * notches);  // +/- 2 units (0-100 scale) per notch
            if (pct >= 0) ShowVolume(pct);
        }

        // Briefly show the volume number in the title area (right of the art).
        void ShowVolume(int pct)
        {
            _title.Text = "Volume  " + pct;
            _volTimer.Stop();
            _volTimer.Start();  // restart the restore countdown on each notch
        }

        static int AdjustVolume(float delta)
        {
            IAudioEndpointVolume vol = null;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var dev) != 0 || dev == null) return -1;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var o) != 0 || o == null) return -1;
                vol = (IAudioEndpointVolume)o;
                if (vol.GetMasterVolumeLevelScalar(out float cur) != 0) return -1;
                float next = Math.Max(0f, Math.Min(1f, cur + delta));
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                return (int)Math.Round(next * 100);
            }
            catch { return -1; }
            finally { if (vol != null) Marshal.ReleaseComObject(vol); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _volTimer?.Dispose(); _clearTimer?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Smoothly scrolling text, one or two rows (split on '\n'). Double-buffered
    // custom paint (not a moving child control). Position is time-based so uneven
    // WM_TIMER firing in Explorer doesn't stutter. Each row centers when short and
    // loops seamlessly when it overflows.
    sealed class MarqueeLabel : Control
    {
        const int Gap = 48;       // blank space between repeats, px
        const float Speed = 60f;  // scroll speed, px per second
        const TextFormatFlags TFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        readonly Timer _timer = new() { Interval = 16 };  // ~60 fps
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        Bitmap _buffer;
        string[] _lines = { "" };  // 1 or 2 rows
        int[] _lineW = { 0 };      // measured pixel width per row
        int _lineH;                // single-row text height
        bool _scroll, _overflow, _hover;

        public MarqueeLabel()
        {
            // We draw the frame ourselves (Opaque = system never erases the bg).
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            _timer.Tick += (s, e) => Frame();  // draw directly, not via Invalidate/WM_PAINT
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
            _lines = (Text ?? "").Split('\n');
            if (_lines.Length > 2) _lines = new[] { _lines[0], _lines[1] };  // cap at two rows
            _lineH = TextRenderer.MeasureText("Ag", Font, new System.Drawing.Size(int.MaxValue, 100), TFlags).Height;
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
                for (int i = 0; i < rows; i++)
                {
                    string line = _lines[i];
                    int lw = _lineW[i];
                    int y = i * rowH + (rowH - _lineH) / 2;  // center within the row band
                    if (_scroll && lw > w)
                    {
                        int period = lw + Gap;
                        int off = (int)((_clock.Elapsed.TotalSeconds * Speed) % period);
                        TextRenderer.DrawText(g, line, Font, new System.Drawing.Point(-off, y), ForeColor, BackColor, TFlags);
                        TextRenderer.DrawText(g, line, Font, new System.Drawing.Point(-off + period, y), ForeColor, BackColor, TFlags);  // seamless loop
                    }
                    else
                    {
                        int x = lw > w ? 0 : Math.Max(0, (w - lw) / 2);  // pin overflow left, center short
                        TextRenderer.DrawText(g, line, Font, new System.Drawing.Point(x, y), ForeColor, BackColor, TFlags);
                    }
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

    // Flat, owner-drawn icon button so the glyph is exactly centered (the stock
    // Button centered the glyph's text box, not its ink, and looked off).
    sealed class IconButton : Control
    {
        string _glyph;
        bool _over, _down;
        public event Action Clicked;

        public IconButton(string glyph)
        {
            _glyph = glyph;
            Font = new Font("Segoe MDL2 Assets", 11f);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public string Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = _down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            _down = false; Invalidate();
            base.OnMouseUp(e);
            if (click) Clicked?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg = _down ? Band.Lighten(BackColor, 40) : _over ? Band.Lighten(BackColor, 24) : BackColor;  // hover/press highlight
            g.Clear(bg);
            TextRenderer.DrawText(g, _glyph, Font, ClientRectangle, ForeColor, bg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // ---- Core Audio (master volume) COM interop ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int _EnumAudioEndpoints();  // slot placeholder
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr act, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int _RegisterControlChangeNotify();  // slot placeholders (order matters)
        [PreserveSig] int _UnregisterControlChangeNotify();
        [PreserveSig] int _GetChannelCount();
        [PreserveSig] int _SetMasterVolumeLevel();
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int _GetMasterVolumeLevel();
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }
}
