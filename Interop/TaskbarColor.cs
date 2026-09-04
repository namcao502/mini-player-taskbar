// Samples the taskbar's background color off the screen. Matching those pixels is the
// only way to blend in: Explorer paints no background inside the band's rectangle.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MiniPlayerBand
{
    static class TaskbarColor
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        struct RECT { public int Left, Top, Right, Bottom; }

        // Null = unreadable screen, never a color: a black fallback is indistinguishable
        // from a real black taskbar. One blit, because screen-DC GetPixel is ~ms each.
        internal static Color? Sample()
        {
            try
            {
                var tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero || !GetWindowRect(tray, out var r)) return null;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return null;

                using (var bmp = new Bitmap(w, 1))
                {
                    using (var g = Graphics.FromImage(bmp))  // final composited pixels (acrylic/blur included)
                        g.CopyFromScreen(r.Left, r.Top + h / 2, 0, 0, new System.Drawing.Size(w, 1));

                    var counts = new Dictionary<int, int>();
                    for (int x = 4; x < w - 4; x += 8)
                    {
                        int px = bmp.GetPixel(x, 0).ToArgb();
                        counts[px] = counts.TryGetValue(px, out var c) ? c + 1 : 1;
                    }
                    if (counts.Count == 0) return null;
                    int mode = 0, best = -1;
                    foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; mode = kv.Key; }
                    return Color.FromArgb(mode);
                }
            }
            catch { return null; }  // locked workstation, or the shell is mid-restart
        }
    }
}
