// Mini media player as a Windows taskbar deskband.
//
// A COM shell extension (CSDeskBand does the IDeskBand2 plumbing) that reads
// the active SMTC session and shows album art plus prev / play-pause / next
// (owner-drawn Segoe MDL2 icon buttons) in the taskbar. Event-driven, no polling.
// Long titles scroll smoothly. Mouse wheel over the band changes system volume.
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
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSDeskBand;
using CSDeskBand.Win;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

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
        const int Badge = 32;  // art bitmap size, then stretched to the band height

        // Segoe MDL2 Assets glyphs.
        const string GlyphPrev = "\uE892";
        const string GlyphNext = "\uE893";
        const string GlyphPlay = "\uE768";
        const string GlyphPause = "\uE769";

        static readonly Color Fg = Color.FromArgb(240, 240, 240);
        Color _bg = Color.FromArgb(32, 32, 32);  // taskbar color; sampled from the real taskbar at startup

        internal static Color Lighten(Color c, int d) =>
            Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d));

        readonly PictureBox _art = new();
        readonly MarqueeLabel _title = new();
        readonly IconButton _prev;
        readonly IconButton _play;
        readonly IconButton _next;
        Bitmap _placeholder;

        SessionManager _mgr;
        Session _session;
        Bitmap _baseArt;      // current track's album art (owned; disposed on replace)
        int _refreshSeq;      // newest RefreshAsync wins; older async reads are dropped
        bool _inited;         // guard so Init runs once
        string _trackTitle = "No media";                        // real title, restored after the volume readout
        readonly Timer _volTimer = new() { Interval = 1200 };   // how long the volume number stays

        public Band()
        {
            Options.Title = "Mini Player";
            Options.ShowTitle = false;
            Options.MinHorizontalSize = new CSDeskBand.Size(200, 20);
            Options.HorizontalSize = new CSDeskBand.Size(200, 40);  // fixed width (min == desired) so it does not auto-resize
            BackColor = _bg;

            _placeholder = new Bitmap(Badge, Badge);
            using (var g = Graphics.FromImage(_placeholder))
                g.Clear(_bg);

            _art.SizeMode = PictureBoxSizeMode.StretchImage;  // scales to the band height
            _art.BackColor = _bg;
            _art.Image = _placeholder;
            _art.Cursor = Cursors.Hand;
            _art.Click += async (s, e) => await RunCommand(x => x.TryTogglePlayPauseAsync());  // art also toggles
            Controls.Add(_art);

            _title.BackColor = _bg;
            _title.ForeColor = Fg;
            _title.Font = new Font("Segoe UI", 9f);
            _title.Text = "No media";
            Controls.Add(_title);

            _prev = MakeButton(GlyphPrev, x => x.TrySkipPreviousAsync());
            _play = MakeButton(GlyphPlay, x => x.TryTogglePlayPauseAsync());
            _next = MakeButton(GlyphNext, x => x.TrySkipNextAsync());
            Controls.Add(_prev);
            Controls.Add(_play);
            Controls.Add(_next);

            // Wheel over any part of the band adjusts volume.
            foreach (Control c in new Control[] { this, _art, _title, _prev, _play, _next })
                c.MouseWheel += OnWheel;
            _volTimer.Tick += (s, e) => { _volTimer.Stop(); _title.Text = _trackTitle; };  // restore title

            _bg = TaskbarColor();  // match the taskbar's own background color
            ApplyTheme();
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

        // Height-adaptive: square art fills the band height, three icon buttons
        // pin to the right (centered, never clipped), scrolling title in between.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_art == null || _title == null || _prev == null || _play == null || _next == null) return;
            int h = ClientSize.Height, w = ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            const int pad = 2;
            int side = Math.Max(1, h - pad * 2);          // art is square = band height
            int bw = Math.Min(34, Math.Max(22, w / 12));  // button width
            int bh = Math.Min(h - pad * 2, 32);           // button height (capped)
            int by = (h - bh) / 2;                        // vertically centered

            _art.SetBounds(pad, pad, side, side);
            int nextX = w - pad - bw;
            int playX = nextX - bw;
            int prevX = playX - bw;
            _next.SetBounds(nextX, by, bw, bh);
            _play.SetBounds(playX, by, bw, bh);
            _prev.SetBounds(prevX, by, bw, bh);

            int titleX = pad + side + 6;
            _title.SetBounds(titleX, 0, Math.Max(0, prevX - titleX - 4), h);
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
            {
                _session.MediaPropertiesChanged -= OnMediaProps;
                _session.PlaybackInfoChanged -= OnPlayback;
            }
            _session = _mgr.GetCurrentSession();
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaProps;
                _session.PlaybackInfoChanged += OnPlayback;
            }
            _ = RefreshAsync();
        }

        void OnMediaProps(Session s, MediaPropertiesChangedEventArgs e) => UiPost(() => { _ = RefreshAsync(); });
        void OnPlayback(Session s, PlaybackInfoChangedEventArgs e) => UiPost(UpdatePlayState);

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
            if (s == null)
            {
                UiPost(() => { if (seq == _refreshSeq) { SetTitle("No media"); ReplaceBase(null); ShowArt(); _play.Glyph = GlyphPlay; } });
                return;
            }
            try
            {
                MediaProps props = await s.TryGetMediaPropertiesAsync();
                string title = props.Title ?? "";
                string artist = props.Artist ?? "";
                string display = title.Length == 0 ? "No media"
                               : (artist.Length == 0 ? title : title + "  -  " + artist);
                bool playing = IsPlaying(s);
                Bitmap art = await LoadBaseArt(props);  // always reload so art can't lag behind the track

                UiPost(() =>
                {
                    if (seq != _refreshSeq) { art?.Dispose(); return; }  // superseded by a newer refresh
                    SetTitle(display);
                    ReplaceBase(art);
                    ShowArt();
                    _play.Glyph = playing ? GlyphPause : GlyphPlay;
                });
            }
            catch { }
        }

        void UpdatePlayState()
        {
            var s = _session;
            _play.Glyph = (s != null && IsPlaying(s)) ? GlyphPause : GlyphPlay;
        }

        static bool IsPlaying(Session s) =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        async Task RunCommand(Func<Session, IAsyncOperation<bool>> op)
        {
            var s = _session;
            if (s == null) return;
            try { await op(s); }
            catch { }  // keep the band alive
        }

        // ---- album art ----

        static async Task<Bitmap> LoadBaseArt(MediaProps props)
        {
            var reft = props.Thumbnail;
            if (reft == null) return null;
            try
            {
                using (var stream = await reft.OpenReadAsync())
                {
                    var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size);
                    var bytes = new byte[stream.Size];
                    reader.ReadBytes(bytes);
                    using (var ms = new MemoryStream(bytes))
                    using (var src = Image.FromStream(ms))
                        return new Bitmap(src, Badge, Badge);
                }
            }
            catch { return null; }  // missing/corrupt thumbnail -> placeholder
        }

        void ReplaceBase(Bitmap b)
        {
            _baseArt?.Dispose();
            _baseArt = b;
        }

        void ShowArt() => _art.Image = _baseArt ?? _placeholder;

        // ---- theming: match the taskbar's own background color ----

        void ApplyTheme()
        {
            BackColor = _bg;
            _art.BackColor = _bg;
            _title.BackColor = _bg;
            _prev.BackColor = _bg;
            _play.BackColor = _bg;
            _next.BackColor = _bg;

            var old = _placeholder;
            _placeholder = new Bitmap(Badge, Badge);
            using (var g = Graphics.FromImage(_placeholder)) g.Clear(_bg);
            if (_baseArt == null) ShowArt();
            old?.Dispose();
            Invalidate(true);
        }

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
            if (disposing) { _baseArt?.Dispose(); _placeholder?.Dispose(); _volTimer?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Smoothly scrolling single-line text. Double-buffered custom paint (not a
    // moving child control). Position is time-based so uneven WM_TIMER firing in
    // Explorer doesn't stutter. Centers short text; loops long text seamlessly.
    sealed class MarqueeLabel : Control
    {
        const int Gap = 48;       // blank space between repeats, px
        const float Speed = 60f;  // scroll speed, px per second
        const TextFormatFlags TFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        readonly Timer _timer = new() { Interval = 16 };  // ~60 fps
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        Bitmap _buffer;
        int _textWidth, _textHeight;
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
            var size = TextRenderer.MeasureText(Text, Font, new System.Drawing.Size(int.MaxValue, 100), TFlags);
            _textWidth = size.Width;
            _textHeight = size.Height;
            _overflow = _textWidth > Width;
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
                int y = (h - _textHeight) / 2;
                if (_scroll)
                {
                    int period = _textWidth + Gap;
                    int off = (int)((_clock.Elapsed.TotalSeconds * Speed) % period);
                    TextRenderer.DrawText(g, Text, Font, new System.Drawing.Point(-off, y), ForeColor, BackColor, TFlags);
                    TextRenderer.DrawText(g, Text, Font, new System.Drawing.Point(-off + period, y), ForeColor, BackColor, TFlags);  // seamless loop
                }
                else
                {
                    int x = _overflow ? 0 : Math.Max(0, (w - _textWidth) / 2);  // pin overflow left, center short
                    TextRenderer.DrawText(g, Text, Font, new System.Drawing.Point(x, y), ForeColor, BackColor, TFlags);
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
