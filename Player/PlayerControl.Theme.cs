// Taskbar color -> text color. A light taskbar needs dark text, or the band paints
// near-white on near-white.

using System.Drawing;
using Microsoft.Win32;

namespace MiniPlayerBand
{
    partial class PlayerControl
    {
        // Perceived brightness (ITU-R BT.601); >= 128 = a light background.
        internal static bool IsLight(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 >= 128;

        // Nudge a color away from itself: lighten a dark one, darken a light one, so
        // the shifted shade stays visible under either theme.
        internal static Color Shade(Color c, int d)
        {
            int k = IsLight(c) ? -d : d;
            return Color.FromArgb(Clamp(c.R + k), Clamp(c.G + k), Clamp(c.B + k));
        }

        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        void ApplyTheme()
        {
            Color? sampled = TaskbarColor.Sample();
            if (sampled == null) { RetryTheme(); return; }  // unreadable: keep the current colors
            Color bg = sampled.Value;
            if (_themed && bg == _bg) return;  // nothing changed
            _themed = true;
            _bg = bg;
            _fg = IsLight(bg) ? FgOnLight : FgOnDark;
            _fgDim = IsLight(bg) ? FgDimOnLight : FgDimOnDark;

            BackColor = bg;
            _title.BackColor = bg;
            _title.ForeColor = _titlePaused ? _fgDim : _fg;
            ThemeChanged?.Invoke();
            // Refresh, not Invalidate: synchronous, so the colors land even in Explorer's
            // pump where a posted WM_PAINT would be starved.
            if (IsHandleCreated) { Refresh(); _title.Refresh(); RepaintChrome(); }
        }

        // Raised off our UI thread. The taskbar repaints only after the notification, so
        // sampling once, immediately, would read the old color.
        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle) return;
            try { UiPost(RetryTheme); }
            catch { }  // handle torn down between the check and the post
        }

        // Unlocking is when a failing sample starts succeeding: the screen cannot be read
        // at all while the workstation is locked.
        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason != SessionSwitchReason.SessionUnlock &&
                e.Reason != SessionSwitchReason.ConsoleConnect) return;
            try { UiPost(RetryTheme); }
            catch { }  // handle torn down between the check and the post
        }

        // Four samples over ~2s, because the shell needs a moment to settle after either
        // trigger.
        void RetryTheme()
        {
            _themeTries = 4;
            _themeTimer.Stop();
            _themeTimer.Start();
        }

        // Safety net every 5 chrome ticks: without it a color the two events above missed
        // never heals, which is how the band ended up black on a 16,16,16 taskbar.
        void PollTheme()
        {
            if (++_themeTick < 5) return;
            _themeTick = 0;
            ApplyTheme();
        }
    }
}
