// Windows 11 (and 10) standalone host for the mini media player.
//
// Deskbands were removed in Win11, so this is a borderless window registered as an
// Application Desktop Toolbar (AppBar) via SHAppBarMessage. That reserves a strip
// along the bottom screen edge above the real taskbar, so the player is never
// covered by maximized windows. All the UI + SMTC logic is the shared PlayerControl.

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    sealed class AppBarForm : Form
    {
        // AppBar messages / edges (shellapi.h).
        const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
        const int ABN_POSCHANGED = 1;
        const int ABE_BOTTOM = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [DllImport("shell32.dll")] static extern uint SHAppBarMessage(int msg, ref APPBARDATA data);
        [DllImport("user32.dll")] static extern uint RegisterWindowMessage(string msg);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        readonly uint _callbackMsg = RegisterWindowMessage("MiniPlayerAppBarMessage");
        readonly int _barHeight = TaskbarHeight();
        bool _registered;

        public AppBarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = PlayerControl.TaskbarColor();
            // ponytail: full-width bottom AppBar, PlayerControl docked-fill. For a
            // compact bar, give the child a fixed Width and Dock = Left instead.
            Controls.Add(new PlayerControl { Dock = DockStyle.Fill });
        }

        // Bottom taskbar's own height, so this strip matches it. Falls back to 40px.
        static int TaskbarHeight()
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out var r))
            {
                int h = r.bottom - r.top;
                if (h > 0 && h < 200) return h;  // sanity: ignore odd values (e.g. a side taskbar)
            }
            return 40;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var abd = NewData();
            abd.uCallbackMessage = _callbackMsg;
            SHAppBarMessage(ABM_NEW, ref abd);
            _registered = true;
            DockBottom();
        }

        // Reserve the bottom strip and move the window into it. QUERYPOS lets the
        // shell shrink our request so we don't overlap the real taskbar; we then
        // re-pin our height against the adjusted bottom edge and SETPOS.
        void DockBottom()
        {
            var screen = Screen.PrimaryScreen.Bounds;  // ponytail: primary monitor only; multi-mon later
            var abd = NewData();
            abd.uEdge = ABE_BOTTOM;
            abd.rc = new RECT { left = screen.Left, right = screen.Right, top = screen.Bottom - _barHeight, bottom = screen.Bottom };
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc.top = abd.rc.bottom - _barHeight;  // keep our requested height above whatever's below
            SHAppBarMessage(ABM_SETPOS, ref abd);
            Bounds = new System.Drawing.Rectangle(abd.rc.left, abd.rc.top, abd.rc.right - abd.rc.left, abd.rc.bottom - abd.rc.top);
        }

        protected override void WndProc(ref Message m)
        {
            // The shell pings our callback when the taskbar/other appbars move.
            if (m.Msg == _callbackMsg && m.WParam.ToInt32() == ABN_POSCHANGED)
                DockBottom();
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_registered)
            {
                var abd = NewData();
                SHAppBarMessage(ABM_REMOVE, ref abd);  // release the reserved strip
                _registered = false;
            }
            base.OnFormClosing(e);
        }

        APPBARDATA NewData() => new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = Handle };
    }
}
