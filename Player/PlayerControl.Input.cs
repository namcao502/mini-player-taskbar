// Mouse gestures. Nothing on the band looks clickable, so every control here is an
// invisible zone.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    partial class PlayerControl
    {
        // Left quarter = previous, right quarter = next, middle half = play/pause, middle
        // button = mute. MarqueeLabel.ZoneAt is the single definition of that split.
        void OnTitleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle) { ToggleMuteAndShow(); return; }
            if (e.Button != MouseButtons.Left) return;
            RunZone(_title.ZoneAt(e.X));
        }

        // The band itself, i.e. only the parts the title does not cover. Above the seek
        // strip that is the _titlePad margin, which would otherwise be a dead 2px column.
        void OnBandClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle) { ToggleMuteAndShow(); return; }
            if (e.Button != MouseButtons.Left || e.Y >= ClientSize.Height - _seekH) return;
            int x = e.X - _titlePad;
            int max = _title.Width - 1;
            RunZone(_title.ZoneAt(x < 0 ? 0 : x > max ? max : x));
        }

        // Nothing is drawn on the band to mark the split, so the zone name is the only
        // in-place hint. Re-armed per zone crossed, not once per control.
        void OnTitleHover(object sender, MouseEventArgs e)
        {
            int zone = _title.ZoneAt(e.X);
            if (zone == _hoverZone) return;
            _hoverZone = zone;
            _tip.Hide();
            _tipTimer.Stop();
            _tipTimer.Start();
        }

        void OnTitleLeave(object sender, EventArgs e)
        {
            _hoverZone = -1;  // else a language switch leaves stale text
            _tipTimer.Stop();
            _tip.Hide();
        }

        // Stays up for as long as the pointer rests in the zone; OnTitleLeave takes it down.
        void ShowZoneTip()
        {
            _tipTimer.Stop();
            string name;
            switch (_hoverZone)
            {
                case 0: name = Loc.S.ZonePrev; break;
                case 1: name = Loc.S.ZonePlay; break;
                case 2: name = Loc.S.ZoneNext; break;
                default: return;
            }
            // Anchored at the zone's left edge, so the tip's own left edge marks the
            // boundary -- the job the removed divider lines used to do.
            int x = _hoverZone == 0 ? 0 : _hoverZone == 1 ? _title.Width / 4 : _title.Width * 3 / 4;
            // The label's client y=0 is the taskbar's top edge, so anchoring the tip's
            // bottom just above it keeps the tip clear of the band it is naming.
            Point anchor = _title.PointToScreen(new Point(x, 0));
            _tip.Show(name, Handle, anchor.X, anchor.Y - Scale(4));
        }

        void RunZone(int zone)
        {
            switch (zone)
            {
                case 0: _ = RunCommand(x => x.TrySkipPreviousAsync()); break;
                case 1: _ = RunCommand(x => x.TryTogglePlayPauseAsync()); break;
                case 2: _ = RunCommand(x => x.TrySkipNextAsync()); break;
            }
        }

        // Seeks on a completed click, not on press: the strip owns the screen's bottom
        // pixel row, so a mouse slammed at the edge would otherwise seek by accident.
        void OnSeek(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Y < ClientSize.Height - _seekH) return;
            TimeSpan dur = _tlEnd - _tlStart;
            if (dur <= TimeSpan.Zero || ClientSize.Width <= 0) return;  // nothing seekable
            double f = e.X / (double)ClientSize.Width;
            f = f < 0 ? 0 : f > 1 ? 1 : f;
            long ticks = _tlStart.Ticks + (long)(dur.Ticks * f);
            _ = RunCommand(s => s.TryChangePlaybackPositionAsync(ticks));  // app repaints via TimelinePropertiesChanged
        }

        void OnWheel(object sender, MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;  // stop it bubbling to the parent (double-counts the step)
            int notches = e.Delta / 120;
            if (notches == 0) return;
            int pct = SystemVolume.Adjust(0.02f * notches);  // +/- 2 units (0-100 scale) per notch
            if (pct >= 0) ShowReadout(Loc.S.VolumePrefix + pct + "%");
        }

        // Flip the system mute and say so. Middle-click otherwise silences the machine
        // with no sign that this control was what did it.
        void ToggleMuteAndShow()
        {
            bool? muted = SystemVolume.ToggleMute();
            if (muted == null) return;
            ShowReadout(muted.Value ? Loc.S.Muted : Loc.S.Unmuted);
        }

        // Status on row 2, track title kept on row 1: replacing both rows with a bare
        // number drops the more useful half of the display on every wheel notch.
        void ShowReadout(string text)
        {
            _volTimer.Stop();
            _title.Text = (TitlePart() ?? Loc.S.NoMedia) + "\n" + text;
            _volTimer.Start();  // restart the restore countdown on each notch
        }
    }
}
