// Windows 11 (and 10) standalone host for the mini media player.
//
// Deskbands were removed in Win11, so this is a plain floating window instead: a
// borderless, always-on-top popup sized to match the Win10 deskband (see Band.cs:
// HorizontalSize = 150x40), painted the taskbar color. It docks nowhere and
// reserves no space; it just floats on top. All UI + SMTC logic is the shared
// PlayerControl.
//
// The player fills the window and every click is already used (play/pause,
// prev/next, seek, wheel), so there's no blank area to grab. To move it, hold ALT
// and drag anywhere.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    sealed class FloatingForm : Form, IMessageFilter
    {
        // Match the Win10 deskband's declared size (Band.cs). ponytail: tweak to taste.
        const int BandWidth = 150, BandHeight = 40, EdgeGap = 8;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        const int WM_LBUTTONDOWN = 0x0201, WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;

        readonly PlayerControl _player;

        // Don't steal focus from the user's active window when we show.
        protected override bool ShowWithoutActivation => true;

        public FloatingForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(BandWidth, BandHeight);
            StartPosition = FormStartPosition.Manual;
            Location = StartLocation();
            BackColor = PlayerControl.TaskbarColor();
            _player = new PlayerControl { Dock = DockStyle.Fill };
            Controls.Add(_player);
            AddMenuItems();
            _player.HostNote = "Alt+drag anywhere:   move the window";
        }

        // Restore the last saved position if it's still on a visible screen; else
        // start bottom-right of the working area (just above the taskbar). The user
        // can Alt+drag it anywhere from there.
        static Point StartLocation()
        {
            var saved = WindowPosition.Load();
            if (saved is Point p && OnScreen(new Rectangle(p, new Size(BandWidth, BandHeight))))
                return p;
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - BandWidth - EdgeGap, wa.Bottom - BandHeight - EdgeGap);
        }

        static bool OnScreen(Rectangle r) =>
            Array.Exists(Screen.AllScreens, s => s.WorkingArea.IntersectsWith(r));

        // Extend the shared player's right-click menu with host-only actions: toggle
        // run-at-login, and quit (the window is borderless with no taskbar entry, so
        // this is the only way out).
        void AddMenuItems()
        {
            var menu = _player.ContextMenuStrip;
            if (menu == null) return;

            var startup = new ToolStripMenuItem("Start with Windows",
                null, (s, e) => Startup.SetEnabled(!Startup.IsEnabled()));
            menu.Opening += (s, e) => startup.Checked = Startup.IsEnabled();

            var exit = new ToolStripMenuItem("Exit", null, (s, e) => Close());

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(exit);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Application.AddMessageFilter(this);  // catch Alt+click before the player does
        }

        // Fires at the end of an Alt+drag move loop; persist where the user left it.
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            WindowPosition.Save(Location);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            WindowPosition.Save(Location);
            base.OnFormClosed(e);
        }

        // Alt + left-drag anywhere moves the borderless window: swallow the click and
        // hand it to the shell's window-move loop via a synthetic caption drag. Only
        // this app's windows reach our thread's filter, so no owner check is needed.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDOWN && (ModifierKeys & Keys.Alt) == Keys.Alt)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                return true;
            }
            return false;
        }
    }
}
