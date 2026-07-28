// Persists the floating window's last location to a small "x,y" file under
// %AppData%\MiniPlayer. All access is best-effort: a missing or unreadable file
// just means "no saved position", and write failures are ignored.

using System;
using System.Drawing;
using System.IO;

namespace MiniPlayerBand
{
    static class WindowPosition
    {
        static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniPlayer");
        static string FilePath => Path.Combine(DirPath, "window.txt");

        public static Point? Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var parts = File.ReadAllText(FilePath).Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                    return new Point(x, y);
            }
            catch { /* unreadable settings -> treat as no saved position */ }
            return null;
        }

        public static void Save(Point p)
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(FilePath, p.X + "," + p.Y);
            }
            catch { /* best-effort persistence */ }
        }
    }
}
