// Toggles "run at login" via the per-user Run key (HKCU, so no admin needed).
// Enabling writes this exe's path; disabling removes the value.

using System.Windows.Forms;
using Microsoft.Win32;

namespace MiniPlayerBand
{
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MiniPlayer";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }

        public static void SetEnabled(bool on)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (on) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
