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
        static readonly Color Fg = Color.FromArgb(240, 240, 240);
        static readonly Color FgDim = Color.FromArgb(120, 120, 120);  // title color while paused
        bool _titlePaused = true;  // last applied look, so we only repaint on change
        Color _bg = Color.FromArgb(32, 32, 32);  // taskbar color; sampled from the real taskbar at startup

        internal static Color Lighten(Color c, int d) =>
            Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d));

        readonly MarqueeLabel _title = new();

        SessionManager _mgr;
        Session _session;
        readonly List<Session> _watched = new();  // sessions we've hooked PlaybackInfoChanged on
        int _refreshSeq;      // newest RefreshAsync wins; older async reads are dropped
        bool _inited;         // guard so Init runs once
        string _trackTitle = "No media";                        // real title, restored after the volume readout
        readonly Timer _volTimer = new() { Interval = 1200 };   // how long the volume number stays
        readonly Timer _clearTimer = new() { Interval = 800 };  // debounce before falling back to "No media"

        // Progress bar: last timeline snapshot + interpolation while playing.
        const int BarH = 2;                                        // progress bar height, px
        const int SeekH = 8;                                       // bottom strip reserved as the click-to-seek target
        readonly Timer _progressTimer = new() { Interval = 1000 }; // advances the bar between SMTC timeline events
        TimeSpan _tlStart, _tlEnd, _tlPos;                         // last timeline snapshot
        long _tlStamp;                                             // Stopwatch ticks when the snapshot was taken
        bool _playing;

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
            _title.MouseClick += OnTitleClick;  // left 1/4 = prev, right 1/4 = next, middle = play/pause
            Controls.Add(_title);

            // Wheel over any part of the band adjusts volume.
            foreach (Control c in new Control[] { this, _title })
                c.MouseWheel += OnWheel;
            MouseDown += OnSeek;  // clicks land on the band only in the uncovered bottom strip
            _volTimer.Tick += (s, e) => { _volTimer.Stop(); _title.Text = _trackTitle; };  // restore title
            _clearTimer.Tick += (s, e) => { _clearTimer.Stop(); SetTitle("No media"); };
            _progressTimer.Tick += (s, e) => InvalidateBar();
        }

        // Central title setter: don't overwrite the volume readout while it is showing.
        void SetTitle(string text)
        {
            _trackTitle = text;
            if (!_volTimer.Enabled) _title.Text = text;
        }

        // Invisible click zones across the title: left quarter skips back, right
        // quarter skips forward, the middle half toggles play/pause.
        void OnTitleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int w = _title.Width;
            if (w <= 0) return;
            if (e.X < w / 4) _ = RunCommand(x => x.TrySkipPreviousAsync());
            else if (e.X > w * 3 / 4) _ = RunCommand(x => x.TrySkipNextAsync());
            else _ = RunCommand(x => x.TryTogglePlayPauseAsync());
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
            DrawProgress(e.Graphics);
        }

        // Thin bar along the bottom edge: dim track + brighter filled portion.
        void DrawProgress(Graphics g)
        {
            double f = ProgressFraction();
            if (f < 0) return;  // no/unknown duration -> no bar
            int w = ClientSize.Width, y = ClientSize.Height - BarH;
            using (var track = new SolidBrush(Lighten(_bg, 24)))
                g.FillRectangle(track, 0, y, w, BarH);
            using (var fill = new SolidBrush(Fg))
                g.FillRectangle(fill, 0, y, (int)(w * f), BarH);
        }

        void InvalidateBar()
        {
            if (IsHandleCreated) Invalidate(new Rectangle(0, ClientSize.Height - BarH, ClientSize.Width, BarH));
        }

        // Height-adaptive: the scrolling title fills the full width; the bottom
        // strip is left for the progress bar + its click-to-seek target.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_title == null) return;
            int h = ClientSize.Height, w = ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            const int pad = 2;
            _title.SetBounds(pad, 0, Math.Max(0, w - pad * 2), h - SeekH);
        }

        // ---- SMTC wiring (events, no polling) ----

        async Task Init()
        {
            try
            {
                _mgr = await SessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (s, e) => UiPost(Resync);
                _mgr.SessionsChanged += (s, e) => UiPost(Resync);
                UiPost(Resync);
            }
            catch
            {
                UiPost(() => _title.Text = "SMTC unavailable");
            }
        }

        // Re-pick the session that is actually playing and re-hook. UI thread.
        // We follow the *playing* session, not GetCurrentSession() alone: an ended
        // browser video (e.g. one attached to a social post) stays "current" with
        // its title intact, so hard-following it would show that title forever.
        void Resync()
        {
            IReadOnlyList<Session> sessions;
            try { sessions = _mgr.GetSessions(); }
            catch { sessions = null; }

            // Watch playback on every session so any of them starting to play
            // re-triggers a pick. Explorer gives us no polling, and the "current"
            // session does not switch away on its own when a video just ends.
            foreach (var w in _watched) w.PlaybackInfoChanged -= OnAnyPlayback;
            _watched.Clear();
            if (sessions != null)
                foreach (var s in sessions)
                {
                    s.PlaybackInfoChanged += OnAnyPlayback;
                    _watched.Add(s);
                }

            Session target = PickBest(sessions);
            if (!ReferenceEquals(target, _session))
            {
                if (_session != null)
                {
                    _session.MediaPropertiesChanged -= OnMediaProps;
                    _session.TimelinePropertiesChanged -= OnTimeline;
                }
                _session = target;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged += OnMediaProps;
                    _session.TimelinePropertiesChanged += OnTimeline;
                }
            }
            ReadPlayback();       // updates _playing + timeline for the bar
            _ = RefreshAsync();   // title, or debounced "No media" when nothing plays
        }

        // The session to show: one that is Playing (current preferred), else the
        // current session so a paused-mid-track stays visible across lock/unlock.
        // Whether a non-playing session is a finished item is decided in RefreshAsync.
        Session PickBest(IReadOnlyList<Session> sessions)
        {
            Session cur = null;
            try { cur = _mgr.GetCurrentSession(); }
            catch { }
            if (IsPlaying(cur)) return cur;
            if (sessions != null)
                foreach (var s in sessions)
                    if (IsPlaying(s)) return s;
            return cur;  // nothing playing: keep the current session (paused track stays visible)
        }

        static bool IsPlaying(Session s)
        {
            if (s == null) return false;
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        // A non-playing session that has run to its end (or is stopped/closed) is a
        // finished item, e.g. a browser video attached to a post -> show No media.
        // A track paused mid-way (position below the end) is NOT ended, so it stays.
        static bool HasEnded(Session s)
        {
            try
            {
                var st = s.GetPlaybackInfo().PlaybackStatus;
                if (st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                 || st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed) return true;
                var t = s.GetTimelineProperties();
                if (t.EndTime <= t.StartTime) return false;  // no known duration -> can't tell, keep it
                return t.Position >= t.EndTime - TimeSpan.FromSeconds(1.5);
            }
            catch { return false; }
        }

        void OnAnyPlayback(Session s, PlaybackInfoChangedEventArgs e) => UiPost(Resync);
        void OnMediaProps(Session s, MediaPropertiesChangedEventArgs e) => UiPost(() => { _ = RefreshAsync(); });
        void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => UiPost(ReadTimeline);

        // Snapshot the current position/duration and repaint the bar. UI thread.
        void ReadTimeline()
        {
            var s = _session;
            try
            {
                var t = s?.GetTimelineProperties();
                _tlStart = t?.StartTime ?? TimeSpan.Zero;
                _tlEnd = t?.EndTime ?? TimeSpan.Zero;
                _tlPos = t?.Position ?? TimeSpan.Zero;
                _tlStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            catch { _tlEnd = _tlStart; }  // treat as no-duration -> bar hidden
            _progressTimer.Enabled = _playing && _tlEnd > _tlStart && IsHandleCreated;
            InvalidateBar();
        }

        // Left-click in the bottom strip seeks to that fraction of the duration.
        void OnSeek(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Y < ClientSize.Height - SeekH) return;
            TimeSpan dur = _tlEnd - _tlStart;
            if (dur <= TimeSpan.Zero || ClientSize.Width <= 0) return;  // nothing seekable
            double f = e.X / (double)ClientSize.Width;
            f = f < 0 ? 0 : f > 1 ? 1 : f;
            long ticks = _tlStart.Ticks + (long)(dur.Ticks * f);
            _ = RunCommand(s => s.TryChangePlaybackPositionAsync(ticks));  // app repaints via TimelinePropertiesChanged
        }

        // Read play/pause state, then re-anchor the timeline from source of truth. UI thread.
        void ReadPlayback()
        {
            var s = _session;
            try { _playing = s != null && s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { _playing = false; }
            if (_playing == _titlePaused)  // play state changed -> dim/brighten the title
            {
                _titlePaused = !_playing;
                _title.ForeColor = _playing ? Fg : FgDim;
                _title.Refresh();  // force an immediate repaint (WM_PAINT is starved in Explorer)
            }
            ReadTimeline();
        }

        // 0..1 played fraction, interpolated by wall-clock while playing; -1 = hide.
        double ProgressFraction()
        {
            TimeSpan dur = _tlEnd - _tlStart;
            if (dur <= TimeSpan.Zero) return -1;
            TimeSpan pos = _tlPos - _tlStart;
            if (_playing)
                pos += TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp() - _tlStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
            double f = pos.TotalSeconds / dur.TotalSeconds;
            return f < 0 ? 0 : f > 1 ? 1 : f;
        }

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
            // A paused session that has run to its end (an ended video) falls back to
            // No media; a track merely paused mid-way stays shown (survives lock/unlock).
            if (!IsPlaying(s) && HasEnded(s)) { UiPost(() => ScheduleNoMedia(seq)); return; }
            try
            {
                MediaProps props = await s.TryGetMediaPropertiesAsync();
                string title = props.Title ?? "";
                // The selected session is playing; an empty title just means its
                // metadata is still loading (a browser skip can gap for seconds).
                // Keep the old track shown until the real title arrives, no flash.
                if (title.Length == 0) return;
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
            if (disposing) { _volTimer?.Dispose(); _clearTimer?.Dispose(); _progressTimer?.Dispose(); }
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

        readonly Timer _timer = new() { Interval = 8 };  // up to ~120 fps; WM_TIMER floors near 15ms unless the system timer resolution is raised (browsers playing media usually do)
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
                int maxW = w;
                foreach (int lwi in _lineW) if (lwi > maxW) maxW = lwi;
                int period = maxW + Gap;  // shared: both rows reset together, short one waits for the long one
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
                        TextRenderer.DrawText(g, line, Font, new System.Drawing.Point(0, y), ForeColor, BackColor, TFlags);  // left-align
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
