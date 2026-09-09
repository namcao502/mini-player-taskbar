// The right-click menu: transport, copy title / artist, language, About. Built once
// in the ctor; only the enabled/checked bits are refreshed when it opens.

using System.Windows.Forms;
using Windows.Media.Control;

namespace MiniPlayerBand
{
    partial class PlayerControl
    {
        void BuildMenu()
        {
            _miPrev = new ToolStripMenuItem(Loc.S.Previous, null, (s, e) => _ = RunCommand(x => x.TrySkipPreviousAsync()));
            _miPlay = new ToolStripMenuItem(Loc.S.PlayPause, null, (s, e) => _ = RunCommand(x => x.TryTogglePlayPauseAsync()));
            _miNext = new ToolStripMenuItem(Loc.S.Next, null, (s, e) => _ = RunCommand(x => x.TrySkipNextAsync()));
            _miStop = new ToolStripMenuItem(Loc.S.Stop, null, (s, e) => _ = RunCommand(x => x.TryStopAsync()));

            _miCopyTitleArtist = new ToolStripMenuItem(Loc.S.TitleAndArtist, null, (s, e) => Copy(JoinedTitle()));
            _miCopyTitle = new ToolStripMenuItem(Loc.S.TitleOnly, null, (s, e) => Copy(TitlePart()));
            _miCopyArtist = new ToolStripMenuItem(Loc.S.ArtistOnly, null, (s, e) => Copy(ArtistPart()));
            _miCopy = new ToolStripMenuItem(Loc.S.Copy);
            _miCopy.DropDownItems.AddRange(new ToolStripItem[] { _miCopyTitleArtist, _miCopyTitle, _miCopyArtist });

            _miLangEn = new ToolStripMenuItem(Loc.EnglishName, null, (s, e) => SetLanguage(Lang.En));
            _miLangVi = new ToolStripMenuItem(Loc.VietnameseName, null, (s, e) => SetLanguage(Lang.Vi));
            _miLang = new ToolStripMenuItem(Loc.S.Language);
            _miLang.DropDownItems.AddRange(new ToolStripItem[] { _miLangEn, _miLangVi });
            _miLangEn.Checked = Loc.Current == Lang.En;
            _miLangVi.Checked = Loc.Current == Lang.Vi;

            _miAbout = new ToolStripMenuItem(Loc.S.About, null, (s, e) => ShowAbout());

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miPrev, _miPlay, _miNext, _miStop,
                new ToolStripSeparator(),
                _miCopy,
                new ToolStripSeparator(),
                _miLang,
                new ToolStripSeparator(),
                _miAbout,
            });
            _menu.Opening += (s, e) => RefreshMenu();
            ContextMenuStrip = _menu;         // right-click the band body
            _title.ContextMenuStrip = _menu;  // and the title
        }

        // Switch language: persist the choice, relabel everything.
        void SetLanguage(Lang lang)
        {
            if (Loc.Current == lang) return;
            Loc.Current = lang;  // persists to %AppData%\MiniPlayer\lang.txt
            ApplyLanguage();
        }

        // Re-label every menu item for the current language and refresh the shown title.
        void ApplyLanguage()
        {
            _miPrev.Text = Loc.S.Previous;
            _miPlay.Text = Loc.S.PlayPause;
            _miNext.Text = Loc.S.Next;
            _miStop.Text = Loc.S.Stop;
            _miCopy.Text = Loc.S.Copy;
            _miCopyTitleArtist.Text = Loc.S.TitleAndArtist;
            _miCopyTitle.Text = Loc.S.TitleOnly;
            _miCopyArtist.Text = Loc.S.ArtistOnly;
            _miLang.Text = Loc.S.Language;
            _miLangEn.Checked = Loc.Current == Lang.En;
            _miLangVi.Checked = Loc.Current == Lang.Vi;
            _miAbout.Text = Loc.S.About;

            if (!_volTimer.Enabled) _title.Text = DisplayTitle();  // refresh a shown "no media"
        }

        // Refresh the dynamic bits just before the menu shows: enable transport only for
        // what the app reports supporting, and Copy only when there is a real title.
        void RefreshMenu()
        {
            _miPlay.Enabled = _session != null;
            _miCopy.Enabled = _session != null && _trackTitle.Length != 0;

            GlobalSystemMediaTransportControlsSessionPlaybackInfo info = null;
            try { info = _session?.GetPlaybackInfo(); } catch { }
            _miPrev.Enabled = info?.Controls.IsPreviousEnabled == true;
            _miNext.Enabled = info?.Controls.IsNextEnabled == true;
            _miStop.Enabled = info?.Controls.IsStopEnabled == true;
        }

        // Copy helpers. _trackTitle holds "title" or "title\nartist" ("" = no media).
        string TitlePart()
        {
            string t = _trackTitle;
            if (t.Length == 0) return null;
            int nl = t.IndexOf('\n');
            return nl < 0 ? t : t.Substring(0, nl);
        }

        string ArtistPart()
        {
            string t = _trackTitle;
            if (t.Length == 0) return null;
            int nl = t.IndexOf('\n');
            return nl < 0 ? null : t.Substring(nl + 1);
        }

        string JoinedTitle()
        {
            string t = _trackTitle;
            if (t.Length == 0) return null;
            return t.Replace("\n", " - ");
        }

        static void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        // A custom dialog, not a MessageBox: it draws a labeled mock of the band, which is
        // the only way to show where the unlabeled gestures actually live.
        void ShowAbout()
        {
            var v = typeof(PlayerControl).Assembly.GetName().Version;
            using (var dlg = new AboutForm(v))
                dlg.ShowDialog(this);
        }
    }
}
