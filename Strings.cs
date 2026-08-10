// UI string table + language selection, shared by every host (deskband + app).
//
// No .resx/satellite assemblies on purpose: the deskband ships as a single DLL
// registered via RegAsm /codebase, and satellite probing would complicate that
// deploy for two languages. This hand-rolled table is enough -- one Strings
// object per language, picked by Loc.Current, which defaults to the Windows
// display language and remembers the user's choice in %AppData%\MiniPlayer.
//
// The literal values below are user-facing content, so the Vietnamese entries
// carry real diacritics (this file must stay UTF-8).

using System;
using System.Globalization;
using System.IO;

namespace MiniPlayerBand
{
    enum Lang { En, Vi }

    // One immutable bag of strings per language (built once, never mutated).
    sealed class Strings
    {
        // Right-click menu.
        public string Previous, PlayPause, Next, Stop;
        public string Copy, TitleAndArtist, TitleOnly, ArtistOnly;
        public string About, Language;

        // Status text shown in the band title area.
        public string NoMedia, SmtcUnavailable, VolumePrefix;

        // Host (standalone app) menu items + the About host note.
        public string StartWithWindows, Exit, HostNoteAltDrag;

        // About / how-to-use dialog.
        public string AboutDesc;
        public string ZonePrev, ZonePlay, ZoneNext;
        public string FracLeft, FracMiddle, FracRight;
        public string CheatVolume, CheatMute, CheatSeek, CheatMenu;
        public string Ok;
    }

    static class Loc
    {
        // Language names are shown as endonyms in both languages (standard practice).
        public const string EnglishName = "English";
        public const string VietnameseName = "Tiếng Việt";
        public const string AppName = "Mini Player";  // product name, not translated

        static readonly Strings En = new Strings
        {
            Previous = "Previous",
            PlayPause = "Play / Pause",
            Next = "Next",
            Stop = "Stop",
            Copy = "Copy",
            TitleAndArtist = "Title and artist",
            TitleOnly = "Title only",
            ArtistOnly = "Artist only",
            About = "About / How to use",
            Language = "Language",

            NoMedia = "No media",
            SmtcUnavailable = "SMTC unavailable",
            VolumePrefix = "Volume  ",

            StartWithWindows = "Start with Windows",
            Exit = "Exit",
            HostNoteAltDrag = "Alt+drag anywhere:   move the window",

            AboutDesc = "Follows whatever app is currently playing (browser, Spotify, etc.) via Windows SMTC.",
            ZonePrev = "Prev",
            ZonePlay = "Play / Pause",
            ZoneNext = "Next",
            FracLeft = "left 1/4",
            FracMiddle = "middle half",
            FracRight = "right 1/4",
            CheatVolume = "Scroll the wheel over the player  =  volume",
            CheatMute = "Middle-click  =  mute / unmute",
            CheatSeek = "Click the bottom edge  =  seek within the track",
            CheatMenu = "Right-click  =  menu (transport, copy title / artist)",
            Ok = "OK",
        };

        static readonly Strings Vi = new Strings
        {
            Previous = "Bài trước",
            PlayPause = "Phát / Tạm dừng",
            Next = "Bài sau",
            Stop = "Dừng",
            Copy = "Sao chép",
            TitleAndArtist = "Tên bài và nghệ sĩ",
            TitleOnly = "Chỉ tên bài",
            ArtistOnly = "Chỉ nghệ sĩ",
            About = "Giới thiệu / Cách dùng",
            Language = "Ngôn ngữ",

            NoMedia = "Không có nội dung",
            SmtcUnavailable = "SMTC không khả dụng",
            VolumePrefix = "Âm lượng  ",

            StartWithWindows = "Khởi động cùng Windows",
            Exit = "Thoát",
            HostNoteAltDrag = "Alt+kéo bất kỳ đâu:   di chuyển cửa sổ",

            AboutDesc = "Hiển thị ứng dụng đang phát (trình duyệt, Spotify, v.v.) qua Windows SMTC.",
            ZonePrev = "Bài trước",
            ZonePlay = "Phát / Dừng",
            ZoneNext = "Bài sau",
            FracLeft = "trái 1/4",
            FracMiddle = "giữa 1/2",
            FracRight = "phải 1/4",
            CheatVolume = "Lăn chuột trên trình phát  =  âm lượng",
            CheatMute = "Nhấn chuột giữa  =  tắt / bật tiếng",
            CheatSeek = "Nhấn mép dưới  =  tua trong bài",
            CheatMenu = "Nhấn chuột phải  =  menu (điều khiển, sao chép tên / nghệ sĩ)",
            Ok = "OK",
        };

        static Lang _current = Load();

        // Current language. Setting it persists the choice immediately.
        public static Lang Current
        {
            get => _current;
            set { _current = value; Save(value); }
        }

        // The active string table.
        public static Strings S => _current == Lang.Vi ? Vi : En;

        // ---- persistence: %AppData%\MiniPlayer\lang.txt holding "en" / "vi" ----

        static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniPlayer");
        static string FilePath => Path.Combine(Dir, "lang.txt");

        // Saved choice if present, else the Windows display language (vi -> Vietnamese).
        static Lang Load()
        {
            try
            {
                if (File.Exists(FilePath) && File.ReadAllText(FilePath).Trim().Equals("vi", StringComparison.OrdinalIgnoreCase))
                    return Lang.Vi;
                if (File.Exists(FilePath))
                    return Lang.En;
            }
            catch { }
            try
            {
                if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase))
                    return Lang.Vi;
            }
            catch { }
            return Lang.En;
        }

        static void Save(Lang lang)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, lang == Lang.Vi ? "vi" : "en");
            }
            catch { }  // a failed write just means the choice isn't remembered
        }
    }
}
