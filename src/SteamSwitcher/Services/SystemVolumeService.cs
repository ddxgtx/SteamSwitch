using System;
using System.Runtime.InteropServices;
using SteamSwitcher.Core;

namespace SteamSwitcher.Services
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom
    {
    }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int NotImpl1();
        [PreserveSig] int NotImpl2();
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    public static class SystemVolumeService
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;

        private static IAudioEndpointVolume? GetVolumeEndpoint()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out var dev); // eRender = 0, eMultimedia = 1
                if (hr != 0 || dev == null) return null;

                var iid = typeof(IAudioEndpointVolume).GUID;
                hr = dev.Activate(ref iid, 1, IntPtr.Zero, out var obj); // CLSCTX_INPROC_SERVER = 1
                if (hr != 0 || obj == null) return null;

                return (IAudioEndpointVolume)obj;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Failed to get audio endpoint: {ex.Message}");
                return null;
            }
        }

        public static float GetVolume()
        {
            try
            {
                var endpoint = GetVolumeEndpoint();
                if (endpoint != null)
                {
                    int hr = endpoint.GetMasterVolumeLevelScalar(out float level);
                    if (hr == 0) return level;
                }
            }
            catch { }
            return 0.5f;
        }

        public static bool IsMuted()
        {
            try
            {
                var endpoint = GetVolumeEndpoint();
                if (endpoint != null)
                {
                    int hr = endpoint.GetMute(out bool isMuted);
                    if (hr == 0) return isMuted;
                }
            }
            catch { }
            return false;
        }

        public static float AdjustVolume(float delta)
        {
            try
            {
                var endpoint = GetVolumeEndpoint();
                if (endpoint != null)
                {
                    endpoint.GetMasterVolumeLevelScalar(out float currentLevel);
                    float newLevel = Math.Clamp(currentLevel + delta, 0.0f, 1.0f);
                    var guid = Guid.Empty;
                    endpoint.SetMasterVolumeLevelScalar(newLevel, ref guid);

                    endpoint.GetMute(out bool isMuted);
                    if (isMuted && newLevel > 0)
                    {
                        endpoint.SetMute(false, ref guid);
                    }
                    return newLevel;
                }
            }
            catch { }

            // Fallback to keyboard event
            if (delta > 0)
                keybd_event(VK_VOLUME_UP, 0, 0, UIntPtr.Zero);
            else if (delta < 0)
                keybd_event(VK_VOLUME_DOWN, 0, 0, UIntPtr.Zero);

            return GetVolume();
        }
    }
}
