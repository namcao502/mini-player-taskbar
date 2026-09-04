namespace MiniPlayerBand
{
    // One immutable bag of strings per language (built once, never mutated).
    // The literal values live in Loc.cs.
    sealed class Strings
    {
        // Right-click menu.
        public string Previous, PlayPause, Next, Stop;
        public string Copy, TitleAndArtist, TitleOnly, ArtistOnly;
        public string About, Language;

        // Status text shown in the band title area.
        public string NoMedia, SmtcUnavailable, VolumePrefix, Muted, Unmuted;

        // About / how-to-use dialog.
        public string AboutDesc;
        public string ZonePrev, ZonePlay, ZoneNext;
        public string FracLeft, FracMiddle, FracRight;
        public string CheatVolume, CheatMute, CheatSeek, CheatMenu;
        public string Ok;
    }
}
