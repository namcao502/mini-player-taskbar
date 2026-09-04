// Mini media player UI; this file owns the state the PlayerControl.<Feature>.cs partials
// share. Explorer has no WinForms SynchronizationContext: every UI write goes via UiPost.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Media.Control;

namespace MiniPlayerBand
{
    using Session = GlobalSystemMediaTransportControlsSession;
    using SessionManager = GlobalSystemMediaTransportControlsSessionManager;

    public partial class PlayerControl : UserControl
    {
        // Picked in ApplyTheme: fixed light-on-dark text is invisible against the
        // near-white taskbar of the Windows light theme.
        static readonly Color FgOnDark = Color.FromArgb(240, 240, 240);
        static readonly Color FgDimOnDark = Color.FromArgb(120, 120, 120);  // title color while paused
        static readonly Color FgOnLight = Color.FromArgb(16, 16, 16);
        static readonly Color FgDimOnLight = Color.FromArgb(105, 105, 105);

        bool _titlePaused;  // dim look currently applied; ctor paints the bright one
        Color _bg = Color.FromArgb(32, 32, 32);  // taskbar color; sampled from the real taskbar
        Color _fg = FgOnDark, _fgDim = FgDimOnDark;
        bool _themed;  // ApplyTheme has run at least once

        Bitmap _chromeBuf;  // opaque back-buffer for the areas the label doesn't cover (see RepaintChrome)

        readonly MarqueeLabel _title = new();

        SessionManager _mgr;
        Session _session;
        readonly List<Session> _watched = new();  // sessions we've hooked PlaybackInfoChanged on
        int _refreshSeq;      // newest RefreshAsync wins; older async reads are dropped
        bool _inited;         // guard so Init runs once
        string _trackTitle = "";                                // real title, or "" = no media (shown as Loc.S.NoMedia)
        readonly Timer _volTimer = new() { Interval = 1200 };   // how long the volume number stays
        readonly Timer _clearTimer = new() { Interval = 800 };  // debounce before falling back to "No media"
        readonly Timer _themeTimer = new() { Interval = 500 };  // re-samples the taskbar after a light/dark switch
        int _themeTries;                                        // remaining re-samples
        int _themeTick;                                         // counts chrome ticks between safety-net re-samples
        readonly Timer _initTimer = new() { Interval = 3000 };  // retries SMTC when it isn't up yet
        int _initTries;
        readonly Timer _firstRunTimer = new() { Interval = 1500 };  // delays the first-run About until the band is up

        // Layout metrics, px. Scaled off the line height in MeasureMetrics, not fixed: as
        // constants they stay 2/8/2 physical px on a 150% taskbar and the seek target shrinks.
        int _lineH = 15;    // 9pt Segoe UI line height, ~15px at 96dpi
        int _barH = 2;      // progress bar height
        int _seekH = 8;     // bottom strip reserved as the click-to-seek target
        int _titlePad = 2;  // left/right margin the title label leaves uncovered

        // Progress bar: last timeline snapshot + interpolation while playing.
        readonly Timer _progressTimer = new() { Interval = 1000 }; // advances the bar between SMTC timeline events
        readonly Timer _settleTimer = new() { Interval = 300 };    // re-read after a session switch; the first snapshot is often stale
        int _settleTries;                                          // remaining settle re-reads
        TimeSpan _tlStart, _tlEnd, _tlPos;                         // last timeline snapshot (Position = last value the app pushed)
        DateTimeOffset _tlUpdated;                                 // the app's own LastUpdatedTime for that Position (interpolation anchor)
        long _tlStamp;                                             // Stopwatch fallback anchor, for apps that don't set LastUpdatedTime
        bool _playing;

        // Right-click menu (built once; dynamic bits refreshed on Opening).
        readonly ContextMenuStrip _menu = new();
        ToolStripMenuItem _miPrev, _miPlay, _miNext, _miStop, _miCopy, _miAbout, _miLang;
        ToolStripMenuItem _miCopyTitleArtist, _miCopyTitle, _miCopyArtist, _miLangEn, _miLangVi;

        // Raised after the taskbar color is (re-)sampled, so the host can repaint its
        // own background to match. Read the new color from BandColor.
        public event Action ThemeChanged;

        // The sampled taskbar color the player is currently painted with.
        public Color BandColor => _bg;

        public PlayerControl()
        {
            ApplyTheme();  // sample the taskbar color first, so children are built with it

            _title.Font = new Font("Segoe UI", 9f);
            MeasureMetrics();  // needs the title font; everything below lays out against it
            _title.Text = DisplayTitle();
            _title.Cursor = Cursors.Hand;
            _title.MouseClick += OnTitleClick;  // left 1/4 = prev, right 1/4 = next, middle = play/pause
            Controls.Add(_title);

            // Wheel over any part of the band adjusts volume.
            foreach (Control c in new Control[] { this, _title })
                c.MouseWheel += OnWheel;
            // Both only ever fire on the parts the title label leaves uncovered: the side
            // margins and the bottom strip. Each checks e.Y so exactly one of them acts.
            MouseClick += OnSeek;
            MouseClick += OnBandClick;
            _volTimer.Tick += (s, e) => { _volTimer.Stop(); _title.Text = DisplayTitle(); };  // restore title
            _clearTimer.Tick += (s, e) => { _clearTimer.Stop(); SetTitle(""); };
            _progressTimer.Tick += (s, e) => { PollTheme(); RepaintChrome(); };
            _settleTimer.Tick += (s, e) => SettleTimeline();
            _themeTimer.Tick += (s, e) => { ApplyTheme(); if (--_themeTries <= 0) _themeTimer.Stop(); };
            _initTimer.Tick += (s, e) => { _initTimer.Stop(); _ = Init(); };
            _firstRunTimer.Tick += (s, e) => { _firstRunTimer.Stop(); ShowAbout(); Loc.MarkSeen(); };
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;  // the screen is unreadable while locked
            BuildMenu();
        }

        // SMTC starts here, not in the ctor: a bare UserControl has no handle yet there.
        // The guard keeps it to one run for a host that realizes the handle early.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_inited) return;
            _inited = true;
            // Shown once ever, since nothing on the band spells the gestures out. Delayed
            // so the band paints first instead of opening a modal over a blank strip.
            if (Loc.IsFirstRun()) _firstRunTimer.Start();
            _ = Init();
        }

        // _trackTitle holds the real title ("" = no media); the placeholder is localized
        // only at display time, so a language switch cannot desync the sentinel.
        void SetTitle(string text)
        {
            _trackTitle = text ?? "";
            if (!_volTimer.Enabled) _title.Text = DisplayTitle();
        }

        // What the title area should read for the current state + language.
        string DisplayTitle() => _trackTitle.Length == 0 ? Loc.S.NoMedia : _trackTitle;

        // Marshal an action onto the control's UI thread.
        void UiPost(Action a)
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Static events: they leak the control otherwise.
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _volTimer?.Dispose(); _clearTimer?.Dispose(); _progressTimer?.Dispose(); _settleTimer?.Dispose();
                _themeTimer?.Dispose(); _initTimer?.Dispose(); _firstRunTimer?.Dispose();
                _menu?.Dispose(); _chromeBuf?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
