using System;
using System.Threading;
using System.Windows.Forms;

namespace MiniPlayerBand
{
    static class Program
    {
        // Held for the whole process lifetime to enforce a single running instance
        // (e.g. autostart + a manual launch must not stack two floating windows).
        static Mutex _instanceLock;

        [STAThread]
        static void Main()
        {
            _instanceLock = new Mutex(initiallyOwned: true, "MiniPlayer.SingleInstance", out bool createdNew);
            if (!createdNew) return;  // another instance already owns the lock: bail out

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FloatingForm());

            GC.KeepAlive(_instanceLock);  // keep the mutex alive until the app exits
        }
    }
}
