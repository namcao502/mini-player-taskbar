// SMTC wiring: which session to follow, and reading title / playback / timeline off
// it. Entirely event-driven -- Explorer gives the band no polling budget.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;

namespace MiniPlayerBand
{
    using Session = GlobalSystemMediaTransportControlsSession;
    using SessionManager = GlobalSystemMediaTransportControlsSessionManager;
    using MediaProps = GlobalSystemMediaTransportControlsSessionMediaProperties;

    partial class PlayerControl
    {
        async Task Init()
        {
            try
            {
                _mgr = await SessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += OnCurrentSessionChanged;
                _mgr.SessionsChanged += OnSessionsChanged;
                UiPost(Resync);
            }
            catch
            {
                // SMTC is not up yet: the band is constructed while Explorer is still
                // starting. Without the retry it would sit on "SMTC unavailable" forever.
                UiPost(() =>
                {
                    if (++_initTries <= 6) { _initTimer.Stop(); _initTimer.Start(); }
                    else _title.Text = Loc.S.SmtcUnavailable;
                });
            }
        }

        // UI thread. Follows the *playing* session, not GetCurrentSession() alone: an
        // ended browser video stays "current" with its title, and would show forever.
        void Resync()
        {
            IReadOnlyList<Session> sessions;
            try { sessions = _mgr.GetSessions(); }
            catch { sessions = null; }

            // Watch every session, not just the current one: with no polling budget, a
            // different session starting to play is the only signal to re-pick.
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

        // A Playing session (current preferred), else the current one so a paused track
        // survives a lock/unlock. RefreshAsync decides whether it is merely finished.
        Session PickBest(IReadOnlyList<Session> sessions)
        {
            Session cur = null;
            try { cur = _mgr.GetCurrentSession(); }
            catch { }
            return PickBestCore(cur, sessions, IsPlaying);
        }

        // The selection policy on its own, with no WinRT type in sight -- Session is a
        // sealed COM class and can't be mocked, so this is what PickBestTests exercises.
        internal static T PickBestCore<T>(T current, IReadOnlyList<T> items, Func<T, bool> isPlaying) where T : class
        {
            if (current != null && isPlaying(current)) return current;
            if (items != null)
                foreach (var item in items)
                    if (isPlaying(item)) return item;
            return current;  // nothing playing: keep the current session (paused track stays visible)
        }

        static bool IsPlaying(Session s)
        {
            if (s == null) return false;
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        // Run to its end, or stopped/closed -> a finished item, show No media. A track
        // paused mid-way is NOT ended and stays on screen.
        static bool HasEnded(Session s)
        {
            try
            {
                var t = s.GetTimelineProperties();
                return HasEndedCore(s.GetPlaybackInfo().PlaybackStatus, t.StartTime, t.EndTime, t.Position);
            }
            catch { return false; }
        }

        // The end-of-track heuristic on its own -- see HasEnded's comment for the rule.
        internal static bool HasEndedCore(GlobalSystemMediaTransportControlsSessionPlaybackStatus status,
            TimeSpan start, TimeSpan end, TimeSpan position)
        {
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
             || status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed) return true;
            if (end <= start) return false;  // no known duration -> can't tell, keep it
            return position >= end - TimeSpan.FromSeconds(1.5);
        }

        // Named (not inline lambdas) so Dispose can unhook them from _mgr.
        void OnCurrentSessionChanged(SessionManager s, CurrentSessionChangedEventArgs e) => UiPost(Resync);
        void OnSessionsChanged(SessionManager s, SessionsChangedEventArgs e) => UiPost(Resync);

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
                // Re-anchor only on a genuinely new snapshot: a re-read with no new push
                // would collapse the interpolated position back to the last pushed value.
                if (pos != _tlPos || start != _tlStart || end != _tlEnd)
                    _tlStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _tlStart = start;
                _tlEnd = end;
                _tlPos = pos;
                _tlUpdated = t?.LastUpdatedTime ?? default;
            }
            catch { _tlEnd = _tlStart; }  // treat as no-duration -> bar hidden
            // Never gate this on _playing: it is the only periodic RepaintChrome, and
            // gating it left the bottom strip window-class white while paused.
            _progressTimer.Enabled = IsHandleCreated;
            RepaintChrome();
        }

        // The first snapshot after a session switch is often stale at ~0, and a steady
        // track pushes no event for a while, so the bar would sit at 0 until interacted.
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

        // Read play/pause state, then re-anchor the timeline from source of truth. UI thread.
        void ReadPlayback()
        {
            var s = _session;
            try { _playing = s != null && s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { _playing = false; }
            if (_playing == _titlePaused)  // play state changed -> dim/brighten the title
            {
                _titlePaused = !_playing;
                _title.ForeColor = _playing ? _fg : _fgDim;
                _title.Refresh();  // force an immediate repaint (WM_PAINT is starved in Explorer)
            }
            ReadTimeline();
        }

        // Elapsed time comes from the app's own LastUpdatedTime, which stays correct however
        // often we re-read; the Stopwatch stamp is the fallback for apps that don't set it.
        TimeSpan CurrentPos() => ComputeCurrentPos(_tlPos, _tlStart, _tlEnd, _playing, _tlUpdated,
            DateTimeOffset.Now, _tlStamp, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency);

        // The interpolation math on its own -- nowTicks/frequency are Stopwatch.GetTimestamp()
        // / Stopwatch.Frequency taken as parameters so a test can fake elapsed time.
        internal static TimeSpan ComputeCurrentPos(TimeSpan lastPos, TimeSpan start, TimeSpan end,
            bool playing, DateTimeOffset lastUpdated, DateTimeOffset now, long tlStampTicks, long nowTicks, double frequency)
        {
            TimeSpan pos = lastPos;
            if (playing)
            {
                TimeSpan elapsed = lastUpdated > now - TimeSpan.FromHours(12) && lastUpdated <= now
                    ? now - lastUpdated  // app's timestamp: correct across Refresh / re-reads
                    : TimeSpan.FromSeconds((nowTicks - tlStampTicks) / frequency);
                if (elapsed > TimeSpan.Zero) pos += elapsed;
            }
            if (pos < start) pos = start;
            if (end > start && pos > end) pos = end;
            return pos;
        }

        // 0..1 played fraction; -1 = hide (no known duration).
        double ProgressFraction() => ComputeProgressFraction(_tlStart, _tlEnd, CurrentPos());

        internal static double ComputeProgressFraction(TimeSpan start, TimeSpan end, TimeSpan pos)
        {
            TimeSpan dur = end - start;
            if (dur <= TimeSpan.Zero) return -1;
            return (pos - start).TotalSeconds / dur.TotalSeconds;
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
                // An empty title on a playing session just means metadata is still loading
                // (a browser skip can gap for seconds) -- keep the old track, don't flash.
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
    }
}
