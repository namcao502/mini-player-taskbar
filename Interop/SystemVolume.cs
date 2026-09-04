// Master volume + mute through Core Audio directly, not the volume media key: that key
// pops the Windows OSD banner over the taskbar, which is where the band lives.

using System;
using System.Runtime.InteropServices;

namespace MiniPlayerBand
{
    static class SystemVolume
    {
        // Default render endpoint's volume control; caller releases via Marshal.ReleaseComObject.
        static IAudioEndpointVolume GetVolume()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var dev) != 0 || dev == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var o) != 0 || o == null) return null;
            return (IAudioEndpointVolume)o;
        }

        // Shift the master level and return the new percentage, or -1 on failure.
        internal static int Adjust(float delta)
        {
            var vol = GetVolume();
            if (vol == null) return -1;
            try
            {
                if (vol.GetMasterVolumeLevelScalar(out float cur) != 0) return -1;
                float next = Math.Max(0f, Math.Min(1f, cur + delta));
                var ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(next, ref ctx);
                return (int)Math.Round(next * 100);
            }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(vol); }
        }

        // Flip the master mute; returns the new state, or null on failure.
        internal static bool? ToggleMute()
        {
            var vol = GetVolume();
            if (vol == null) return null;
            try
            {
                if (vol.GetMute(out bool cur) != 0) return null;
                var ctx = Guid.Empty;
                vol.SetMute(!cur, ref ctx);
                return !cur;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(vol); }
        }
    }
}
