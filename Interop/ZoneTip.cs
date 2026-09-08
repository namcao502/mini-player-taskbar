// A tracking tooltip driven straight through comctl32. WinForms' ToolTip cannot do this
// job in a deskband: its automatic placement drops the tip under the cursor (inside the
// band, clipped by the screen edge), and ToolTip.Show -- the only way to place it by hand
// -- silently refuses whenever the control's root window is not the active one, which for
// a taskbar band is always. Measured: the tip window got created and positioned correctly
// and was simply never made visible.

using System;
using System.Runtime.InteropServices;

namespace MiniPlayerBand
{
    sealed class ZoneTip : IDisposable
    {
        const int WmUser = 0x0400;
        const int TtmAddTool = WmUser + 50;        // the W variants; the struct is CharSet.Unicode
        const int TtmUpdateTipText = WmUser + 57;
        const int TtmTrackActivate = WmUser + 17;
        const int TtmTrackPosition = WmUser + 18;
        const int TtsAlwaysTip = 0x01, TtsNoPrefix = 0x02;
        const int TtfTrack = 0x0020, TtfAbsolute = 0x0080;
        const int WsPopup = unchecked((int)0x80000000);
        const int WsExTopmost = 0x0008;
        const int FallbackHeight = 20;  // one line of tip at 96dpi; only used before the first measurement

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
                                            int x, int y, int w, int h,
                                            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref ToolInfo ti);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        struct RECT { public int Left, Top, Right, Bottom; }

        // cbSize is deliberately the pre-Vista size (no lpReserved): comctl32 accepts it on
        // every version, while the v6 size fails on anything older.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ToolInfo
        {
            public int cbSize;
            public int uFlags;
            public IntPtr hwnd;
            public IntPtr uId;
            public RECT rect;
            public IntPtr hinst;
            public string lpszText;
            public IntPtr lParam;
        }

        IntPtr _hwnd;
        ToolInfo _ti;
        int _height = FallbackHeight;

        bool Ensure(IntPtr owner)
        {
            if (_hwnd != IntPtr.Zero) return true;
            if (owner == IntPtr.Zero) return false;
            _hwnd = CreateWindowEx(WsExTopmost, "tooltips_class32", null,
                                   WsPopup | TtsNoPrefix | TtsAlwaysTip,
                                   0, 0, 0, 0, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            _ti = new ToolInfo
            {
                cbSize = Marshal.SizeOf(typeof(ToolInfo)),
                uFlags = TtfTrack | TtfAbsolute,  // we place it ourselves, in screen coords
                hwnd = owner,
                uId = (IntPtr)1,
                lpszText = "",
            };
            if (SendMessage(_hwnd, TtmAddTool, IntPtr.Zero, ref _ti) != IntPtr.Zero) return true;
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            return false;
        }

        // bottomY is where the tip's lower edge should land, so the caller can keep it clear
        // of the taskbar without knowing how tall a tip is. TTF_ABSOLUTE anchors the
        // top-left, so the height has to be known up front -- TTM_GETBUBBLESIZE reports it
        // far too small here (6px against a real 20), hence measuring the shown window and
        // reusing that: only a first tip can land low, and it self-corrects in the same call.
        public void Show(string text, IntPtr owner, int screenX, int bottomY)
        {
            if (!Ensure(owner)) return;
            _ti.lpszText = text;
            SendMessage(_hwnd, TtmUpdateTipText, IntPtr.Zero, ref _ti);
            Move(screenX, bottomY - _height);
            SendMessage(_hwnd, TtmTrackActivate, (IntPtr)1, ref _ti);
            if (!GetWindowRect(_hwnd, out var r) || r.Bottom - r.Top == _height) return;
            _height = r.Bottom - r.Top;
            Move(screenX, bottomY - _height);
        }

        // Screen coords packed as MAKELPARAM(x, y).
        void Move(int x, int y) =>
            SendMessage(_hwnd, TtmTrackPosition, IntPtr.Zero, (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)));

        public void Hide()
        {
            if (_hwnd == IntPtr.Zero) return;
            SendMessage(_hwnd, TtmTrackActivate, IntPtr.Zero, ref _ti);
        }

        public void Dispose()
        {
            if (_hwnd == IntPtr.Zero) return;
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
