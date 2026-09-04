// Core Audio COM for the render endpoint's volume. Every unused slot must still be
// declared, in vtable order -- reorder one and calls land on the wrong function, silently.

using System;
using System.Runtime.InteropServices;

namespace MiniPlayerBand
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int _EnumAudioEndpoints();  // slot placeholder
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr act, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int _RegisterControlChangeNotify();  // slot placeholders (order matters)
        [PreserveSig] int _UnregisterControlChangeNotify();
        [PreserveSig] int _GetChannelCount();
        [PreserveSig] int _SetMasterVolumeLevel();
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int _GetMasterVolumeLevel();
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int _SetChannelVolumeLevel();        // slot placeholders (order matters)
        [PreserveSig] int _SetChannelVolumeLevelScalar();
        [PreserveSig] int _GetChannelVolumeLevel();
        [PreserveSig] int _GetChannelVolumeLevelScalar();
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
