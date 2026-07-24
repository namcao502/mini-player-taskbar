// Mini media player UI — host-agnostic WinForms control.
//
// Reads the active SMTC session and shows the track title + artist (two scrolling
// rows) with invisible click-zones (left 1/4 prev, right 1/4 next, middle
// play/pause), a bottom progress+seek strip, wheel-to-volume, and a right-click
// menu. Event-driven, no polling. Contains no host coupling: the Win10 deskband
// (Band.cs) and the Win11 standalone app (app/AppBarForm.cs) both just add one of
// these docked-fill.
//
// Threading: hosts may run with no WinForms SynchronizationContext (Explorer does),
// so awaits resume off the UI thread; every UI write goes through UiPost. SMTC is
// started from OnHandleCreated. Layout is height-adaptive.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;

namespace MiniPlayerBand
{
    using Session = GlobalSystemMediaTransportControlsSession;
    using SessionManager = GlobalSystemMediaTransportControlsSessionManager;
    using MediaProps = GlobalSystemMediaTransportControlsSessionMediaProperties;

    public class PlayerControl : UserControl
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
        readonly Timer _settleTimer = new() { Interval = 300 };    // re-read after a session switch; the first snapshot is often stale
        int _settleTries;                                          // remaining settle re-reads
        readonly Timer _menuTimer = new() { Interval = 500 };      // live-updates the menu readouts while it is open
        TimeSpan _tlStart, _tlEnd, _tlPos;                         // last timeline snapshot (Position = last value the app pushed)
        DateTimeOffset _tlUpdated;                                 // the app's own LastUpdatedTime for that Position (interpolation anchor)
        long _tlStamp;                                             // Stopwatch fallback anchor, for apps that don't set LastUpdatedTime
        bool _playing;

        // Right-click menu (built once; dynamic bits refreshed on Opening).
        readonly ContextMenuStrip _menu = new();
        ToolStripMenuItem _miPrev, _miPlay, _miNext, _miStop, _miShuffle, _miRepeat, _miMute, _miCopy, _miSource;
        ToolStripMenuItem _miRewind, _miFastForward, _miRestart, _miSkipBack, _miSkipFwd;
        ToolStripMenuItem _miSpeed, _miRecord, _miSetVolume, _miOpenSource, _miFollow, _miRefresh, _miAbout;
        ToolStripLabel _lblVolume, _lblPosition;  // read-only readouts, rendered disabled (grayed)
        Session _pinned;  // source picked via right-click > Source; overrides auto-pick until it ends

        public PlayerControl()
        {
            _bg = TaskbarColor();  // sample the taskbar color first, so children are built with it
            BackColor = _bg;

            _title.BackColor = _bg;
            _title.ForeColor = Fg;
            _title.Font = new Font("Segoe UI", 9f);
            if (string.IsNullOrEmpty(_title.Text)) _title.Text = "No media";
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
            _settleTimer.Tick += (s, e) => SettleTimeline();
            _menuTimer.Tick += (s, e) => UpdateMenuReadouts();
            BuildMenu();
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

        // ---- right-click menu ----

        void BuildMenu()
        {
            // Transport.
            _miPrev = new ToolStripMenuItem("Previous", null, (s, e) => _ = RunCommand(x => x.TrySkipPreviousAsync()));
            _miPlay = new ToolStripMenuItem("Play / Pause", null, (s, e) => _ = RunCommand(x => x.TryTogglePlayPauseAsync()));
            _miNext = new ToolStripMenuItem("Next", null, (s, e) => _ = RunCommand(x => x.TrySkipNextAsync()));
            _miStop = new ToolStripMenuItem("Stop", null, (s, e) => _ = RunCommand(x => x.TryStopAsync()));

            // Seek: SMTC rewind/ff (app-defined step) plus explicit position jumps.
            _miRewind = new ToolStripMenuItem("Rewind", null, (s, e) => _ = RunCommand(x => x.TryRewindAsync()));
            _miFastForward = new ToolStripMenuItem("Fast forward", null, (s, e) => _ = RunCommand(x => x.TryFastForwardAsync()));
            _miRestart = new ToolStripMenuItem("Restart track", null, (s, e) => SeekToStart());
            _miSkipBack = new ToolStripMenuItem("Skip back 10s", null, (s, e) => SeekBy(TimeSpan.FromSeconds(-10)));
            _miSkipFwd = new ToolStripMenuItem("Skip forward 10s", null, (s, e) => SeekBy(TimeSpan.FromSeconds(10)));
            _lblPosition = new ToolStripLabel("Position: --") { Enabled = false };  // readout, grayed

            // Modes.
            _miShuffle = new ToolStripMenuItem("Shuffle", null, (s, e) => ToggleShuffle());
            _miRepeat = new ToolStripMenuItem("Repeat", null, (s, e) => CycleRepeat());
            _miSpeed = new ToolStripMenuItem("Speed");
            foreach (double r in new[] { 0.5, 1.0, 1.25, 1.5, 2.0 })
            {
                double rate = r;  // don't close over the loop variable
                _miSpeed.DropDownItems.Add(new ToolStripMenuItem(
                    SpeedLabel(r), null, (s, e) => _ = RunCommand(x => x.TryChangePlaybackRateAsync(rate))) { Tag = rate });
            }
            _miRecord = new ToolStripMenuItem("Record", null, (s, e) => _ = RunCommand(x => x.TryRecordAsync()));

            // Volume (Core Audio; wheel-over does the same live).
            _lblVolume = new ToolStripLabel("Volume: --") { Enabled = false };  // readout, grayed
            _miSetVolume = new ToolStripMenuItem("Set volume");
            foreach (int p in new[] { 0, 25, 50, 75, 100 })
            {
                int pct = p;  // don't close over the loop variable
                _miSetVolume.DropDownItems.Add(new ToolStripMenuItem(
                    p + "%", null, (s, e) => { int n = SetVolumeScalar(pct / 100f); if (n >= 0) ShowVolume(n); }));
            }
            _miMute = new ToolStripMenuItem("Mute", null, (s, e) => ToggleMute());

            // Utilities.
            _miCopy = new ToolStripMenuItem("Copy");
            _miCopy.DropDownItems.Add(new ToolStripMenuItem("Title and artist", null, (s, e) => Copy(JoinedTitle())));
            _miCopy.DropDownItems.Add(new ToolStripMenuItem("Title only", null, (s, e) => Copy(TitlePart())));
            _miCopy.DropDownItems.Add(new ToolStripMenuItem("Artist only", null, (s, e) => Copy(ArtistPart())));
            _miOpenSource = new ToolStripMenuItem("Open source app", null, (s, e) => OpenSource());
            _miFollow = new ToolStripMenuItem("Follow active session", null, (s, e) => { _pinned = null; Resync(); });
            _miRefresh = new ToolStripMenuItem("Refresh", null, (s, e) => Resync());
            _miSource = new ToolStripMenuItem("Source");
            _miAbout = new ToolStripMenuItem("About", null, (s, e) => ShowAbout());

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miPrev, _miPlay, _miNext, _miStop,
                new ToolStripSeparator(),
                _miRewind, _miFastForward, _miRestart, _miSkipBack, _miSkipFwd, _lblPosition,
                new ToolStripSeparator(),
                _miShuffle, _miRepeat, _miSpeed, _miRecord,
                new ToolStripSeparator(),
                _lblVolume, _miSetVolume, _miMute,
                new ToolStripSeparator(),
                _miCopy, _miOpenSource, _miFollow, _miRefresh, _miSource,
                new ToolStripSeparator(),
                _miAbout,
            });
            _menu.Opening += (s, e) => RefreshMenu();
            _menu.Opened += (s, e) => _menuTimer.Start();   // keep position/volume readouts live while open
            _menu.Closed += (s, e) => _menuTimer.Stop();
            ContextMenuStrip = _menu;         // right-click the band body
            _title.ContextMenuStrip = _menu;  // and the title
        }

        static string SpeedLabel(double r) =>
            (r == Math.Floor(r) ? ((int)r).ToString() : r.ToString("0.##")) + "x";

        // Refresh the dynamic bits just before the menu shows.
        void RefreshMenu()
        {
            _miPlay.Enabled = _session != null;
            _miMute.Checked = IsMuted();
            _miCopy.Enabled = _session != null && !string.IsNullOrEmpty(_trackTitle) && _trackTitle != "No media";
            int v = VolumePercent();
            _lblVolume.Text = v < 0 ? "Volume: --" : "Volume: " + v + "%";

            // Transport/mode items: enable only what the app reports supporting (Controls flags).
            GlobalSystemMediaTransportControlsSessionPlaybackInfo info = null;
            try { info = _session?.GetPlaybackInfo(); } catch { }
            _miPrev.Enabled = info?.Controls.IsPreviousEnabled == true;
            _miNext.Enabled = info?.Controls.IsNextEnabled == true;
            _miStop.Enabled = info?.Controls.IsStopEnabled == true;
            _miRewind.Enabled = info?.Controls.IsRewindEnabled == true;
            _miFastForward.Enabled = info?.Controls.IsFastForwardEnabled == true;
            _miRecord.Enabled = info?.Controls.IsRecordEnabled == true;
            _miShuffle.Enabled = info?.Controls.IsShuffleEnabled == true;
            _miShuffle.Checked = info?.IsShuffleActive == true;
            _miRepeat.Enabled = info?.Controls.IsRepeatEnabled == true;
            _miRepeat.Text = "Repeat: " + RepeatLabel(info?.AutoRepeatMode);

            // Position jumps + readout: only when the app allows seeking.
            bool canSeek = info?.Controls.IsPlaybackPositionEnabled == true;
            _miRestart.Enabled = canSeek;
            _miSkipBack.Enabled = canSeek;
            _miSkipFwd.Enabled = canSeek;
            UpdatePosition();

            // Speed: enable when supported; check the active rate.
            _miSpeed.Enabled = info?.Controls.IsPlaybackRateEnabled == true;
            double? rate = null;
            try { rate = info?.PlaybackRate; } catch { }
            foreach (ToolStripItem it in _miSpeed.DropDownItems)
                if (it is ToolStripMenuItem mi && mi.Tag is double d)
                    mi.Checked = rate.HasValue && Math.Abs(rate.Value - d) < 0.001;

            // Follow is on when nothing is pinned; Open needs a known source app.
            _miFollow.Checked = _pinned == null;
            string aumid = null;
            try { aumid = _session?.SourceAppUserModelId; } catch { }
            _miOpenSource.Enabled = !string.IsNullOrEmpty(aumid);

            BuildSourceSubmenu();
        }

        // Refresh the readouts that change over time while the menu stays open.
        void UpdateMenuReadouts()
        {
            UpdatePosition();
            int v = VolumePercent();
            _lblVolume.Text = v < 0 ? "Volume: --" : "Volume: " + v + "%";
        }

        // Show current position / duration in the disabled readout label, using the
        // interpolated position (same as the bar) so it advances between SMTC pushes.
        void UpdatePosition()
        {
            if (_tlEnd > _tlStart)
                _lblPosition.Text = "Position: " + FmtTime(CurrentPos() - _tlStart) + " / " + FmtTime(_tlEnd - _tlStart);
            else
                _lblPosition.Text = "Position: --";
        }

        static string FmtTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? (int)t.TotalHours + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : t.Minutes + ":" + t.Seconds.ToString("00");
        }

        static string RepeatLabel(MediaPlaybackAutoRepeatMode? m) =>
            m == MediaPlaybackAutoRepeatMode.Track ? "Track"
          : m == MediaPlaybackAutoRepeatMode.List ? "List"
          : "Off";

        void ToggleShuffle()
        {
            bool cur = false;
            try { cur = _session?.GetPlaybackInfo().IsShuffleActive == true; } catch { }
            _ = RunCommand(x => x.TryChangeShuffleActiveAsync(!cur));
        }

        // Cycle Off -> Track -> List -> Off.
        void CycleRepeat()
        {
            MediaPlaybackAutoRepeatMode next = MediaPlaybackAutoRepeatMode.None;
            try
            {
                var cur = _session?.GetPlaybackInfo().AutoRepeatMode;
                next = cur == MediaPlaybackAutoRepeatMode.None ? MediaPlaybackAutoRepeatMode.Track
                     : cur == MediaPlaybackAutoRepeatMode.Track ? MediaPlaybackAutoRepeatMode.List
                     : MediaPlaybackAutoRepeatMode.None;
            }
            catch { }
            _ = RunCommand(x => x.TryChangeAutoRepeatModeAsync(next));
        }

        // Rebuild the Source submenu from the live session list; check on the shown one.
        void BuildSourceSubmenu()
        {
            _miSource.DropDownItems.Clear();
            IReadOnlyList<Session> sessions = null;
            try { sessions = _mgr?.GetSessions(); } catch { }
            if (sessions == null || sessions.Count == 0) { _miSource.Enabled = false; return; }
            _miSource.Enabled = true;
            foreach (var s in sessions)
            {
                var captured = s;  // don't close over the loop variable
                _miSource.DropDownItems.Add(new ToolStripMenuItem(SourceName(s), null, (a, b) => PinSource(captured))
                {
                    Checked = ReferenceEquals(s, _session),
                });
            }
        }

        static string SourceName(Session s)
        {
            try { return string.IsNullOrEmpty(s.SourceAppUserModelId) ? "(unknown)" : s.SourceAppUserModelId; }
            catch { return "(unknown)"; }
        }

        // Pin a source so the band follows it until it ends (then auto-follow resumes).
        void PinSource(Session s) { _pinned = s; Resync(); }

        // Copy helpers. _trackTitle holds "title" or "title\nartist" (empty/"No media" = nothing).
        string TitlePart()
        {
            string t = _trackTitle;
            if (string.IsNullOrEmpty(t) || t == "No media") return null;
            int nl = t.IndexOf('\n');
            return nl < 0 ? t : t.Substring(0, nl);
        }

        string ArtistPart()
        {
            string t = _trackTitle;
            if (string.IsNullOrEmpty(t) || t == "No media") return null;
            int nl = t.IndexOf('\n');
            return nl < 0 ? null : t.Substring(nl + 1);
        }

        string JoinedTitle()
        {
            string t = _trackTitle;
            if (string.IsNullOrEmpty(t) || t == "No media") return null;
            return t.Replace("\n", " - ");
        }

        static void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        // Launch/focus the app backing the current session via its AppUserModelId.
        void OpenSource()
        {
            string aumid = null;
            try { aumid = _session?.SourceAppUserModelId; } catch { }
            if (string.IsNullOrEmpty(aumid)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + aumid); } catch { }
        }

        void ShowAbout()
        {
            var v = typeof(PlayerControl).Assembly.GetName().Version;
            MessageBox.Show("Mini Player " + v + "\nSMTC media band for Windows.",
                "About Mini Player", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // A bare UserControl doesn't create its handle in the ctor, so SMTC is started
        // here once the handle exists. (The old deskband base created it earlier; the
        // guard is kept so hosts that realize the handle early still init exactly once.)
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
                    StartSettle();  // the first timeline snapshot after a switch is often stale (bar sits at 0)
                }
                else _settleTimer.Stop();
            }
            ReadPlayback();       // updates _playing + timeline for the bar
            _ = RefreshAsync();   // title, or debounced "No media" when nothing plays
        }

        // The session to show: one that is Playing (current preferred), else the
        // current session so a paused-mid-track stays visible across lock/unlock.
        // Whether a non-playing session is a finished item is decided in RefreshAsync.
        Session PickBest(IReadOnlyList<Session> sessions)
        {
            // Honor a user pin (right-click > Source) until that source ends or vanishes.
            if (_pinned != null)
            {
                if (sessions != null && Contains(sessions, _pinned) && !HasEnded(_pinned)) return _pinned;
                _pinned = null;  // ponytail: pin auto-clears when its source ends; no manual "unpin" item
            }
            Session cur = null;
            try { cur = _mgr.GetCurrentSession(); }
            catch { }
            if (IsPlaying(cur)) return cur;
            if (sessions != null)
                foreach (var s in sessions)
                    if (IsPlaying(s)) return s;
            return cur;  // nothing playing: keep the current session (paused track stays visible)
        }

        static bool Contains(IReadOnlyList<Session> list, Session s)
        {
            foreach (var x in list) if (ReferenceEquals(x, s)) return true;
            return false;
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
                TimeSpan start = t?.StartTime ?? TimeSpan.Zero;
                TimeSpan end = t?.EndTime ?? TimeSpan.Zero;
                TimeSpan pos = t?.Position ?? TimeSpan.Zero;
                // Only re-anchor the Stopwatch fallback when the raw snapshot actually
                // changes. Re-reads that bring no new push (Refresh, settle) must not
                // reset the clock, or the interpolated position collapses back to the
                // last-pushed value (often ~0 for apps that push Position once).
                if (pos != _tlPos || start != _tlStart || end != _tlEnd)
                    _tlStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _tlStart = start;
                _tlEnd = end;
                _tlPos = pos;
                _tlUpdated = t?.LastUpdatedTime ?? default;
            }
            catch { _tlEnd = _tlStart; }  // treat as no-duration -> bar hidden
            _progressTimer.Enabled = _playing && _tlEnd > _tlStart && IsHandleCreated;
            InvalidateBar();
        }

        // After a session switch the first GetTimelineProperties() snapshot is often
        // stale (position ~0) until SMTC populates it, and a steadily-playing track
        // won't push a TimelinePropertiesChanged for a while -- so the bar would sit
        // at 0 until the user interacts. Re-read a few times to catch the real position.
        void StartSettle()
        {
            _settleTries = 8;  // ~2.4s at 300ms
            _settleTimer.Stop();
            _settleTimer.Start();
        }

        void SettleTimeline()
        {
            ReadPlayback();  // refresh _playing + timeline snapshot
            // Stop early once a real mid-track position with a known duration shows up.
            if (--_settleTries <= 0 || (_tlEnd > _tlStart && _tlPos > _tlStart))
                _settleTimer.Stop();
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

        // Seek relative to the live position, clamped to the app's seekable range.
        void SeekBy(TimeSpan delta)
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var t = s.GetTimelineProperties();
                TimeSpan lo = t.MinSeekTime, hi = t.MaxSeekTime;
                if (hi <= lo) { lo = t.StartTime; hi = t.EndTime; }  // no explicit seek range -> use track bounds
                TimeSpan target = t.Position + delta;
                if (target < lo) target = lo;
                if (hi > lo && target > hi) target = hi;
                _ = RunCommand(x => x.TryChangePlaybackPositionAsync(target.Ticks));
            }
            catch { }
        }

        // Seek to the beginning of the current track.
        void SeekToStart()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var t = s.GetTimelineProperties();
                TimeSpan lo = t.MinSeekTime > TimeSpan.Zero ? t.MinSeekTime : t.StartTime;
                _ = RunCommand(x => x.TryChangePlaybackPositionAsync(lo.Ticks));
            }
            catch { }
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

        // Current position while playing, clamped to the track. The elapsed time since
        // the last push is measured from the app's own LastUpdatedTime (stable no matter
        // when we read it), falling back to our Stopwatch stamp if the app didn't set it.
        TimeSpan CurrentPos()
        {
            TimeSpan pos = _tlPos;
            if (_playing)
            {
                DateTimeOffset now = DateTimeOffset.Now;
                TimeSpan elapsed = _tlUpdated > now - TimeSpan.FromHours(12) && _tlUpdated <= now
                    ? now - _tlUpdated  // app's timestamp: correct across Refresh / re-reads
                    : TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp() - _tlStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
                if (elapsed > TimeSpan.Zero) pos += elapsed;
            }
            if (pos < _tlStart) pos = _tlStart;
            if (_tlEnd > _tlStart && pos > _tlEnd) pos = _tlEnd;
            return pos;
        }

        // 0..1 played fraction; -1 = hide (no known duration).
        double ProgressFraction()
        {
            TimeSpan dur = _tlEnd - _tlStart;
            if (dur <= TimeSpan.Zero) return -1;
            return (CurrentPos() - _tlStart).TotalSeconds / dur.TotalSeconds;
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
        // middle row. Falls back to a dark gray if anything looks off. Internal so
        // the deskband host can paint its own BackColor to match (no gray sliver).
        internal static Color TaskbarColor()
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

        // Default render endpoint's volume control; caller releases via Marshal.ReleaseComObject.
        static IAudioEndpointVolume GetVolume()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var dev) != 0 || dev == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var o) != 0 || o == null) return null;
            return (IAudioEndpointVolume)o;
        }

        static int AdjustVolume(float delta)
        {
            var vol = GetVolume();
            if (vol == null) return -1;
            try
            {
                if (vol.GetMasterVolumeLevelScalar(out float cur) != 0) return -1;
                float next = Math.Max(0f, Math.Min(1f, cur + delta));
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                return (int)Math.Round(next * 100);
            }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(vol); }
        }

        // Set master volume to an absolute scalar (0..1); returns the new 0..100, or -1.
        static int SetVolumeScalar(float scalar)
        {
            var vol = GetVolume();
            if (vol == null) return -1;
            try
            {
                float next = Math.Max(0f, Math.Min(1f, scalar));
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                return (int)Math.Round(next * 100);
            }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(vol); }
        }

        // Flip the master mute; returns the new state, or null on failure.
        static bool? ToggleMute()
        {
            var vol = GetVolume();
            if (vol == null) return null;
            try
            {
                if (vol.GetMute(out bool cur) != 0) return null;
                var ctx = Guid.Empty;
                vol.SetMute(!cur, ref ctx);
                return !cur;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(vol); }
        }

        static bool IsMuted()
        {
            var vol = GetVolume();
            if (vol == null) return false;
            try { return vol.GetMute(out bool cur) == 0 && cur; }
            catch { return false; }
            finally { Marshal.ReleaseComObject(vol); }
        }

        // Current master volume 0..100, or -1 on failure.
        static int VolumePercent()
        {
            var vol = GetVolume();
            if (vol == null) return -1;
            try { return vol.GetMasterVolumeLevelScalar(out float cur) == 0 ? (int)Math.Round(cur * 100) : -1; }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(vol); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _volTimer?.Dispose(); _clearTimer?.Dispose(); _progressTimer?.Dispose(); _settleTimer?.Dispose(); _menuTimer?.Dispose(); _menu?.Dispose(); }
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
        [PreserveSig] int _SetChannelVolumeLevel();        // slot placeholders (order matters)
        [PreserveSig] int _SetChannelVolumeLevelScalar();
        [PreserveSig] int _GetChannelVolumeLevel();
        [PreserveSig] int _GetChannelVolumeLevelScalar();
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
