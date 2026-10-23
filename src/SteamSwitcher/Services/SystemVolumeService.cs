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
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static IAudioEndpointVolume? GetVolumeEndpoint()
        {
            try
            {
                // Each COM object is released explicitly: these RCWs would otherwise
                // pile up until the finalizer runs, which matters because this method
                // is hit on every volume tick.
                var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out var dev); // eRender = 0, eMultimedia = 1
                if (hr != 0 || dev == null)
                {
                    ReleaseCom(enumerator);
                    return null;
                }

                var iid = typeof(IAudioEndpointVolume).GUID;
                hr = dev.Activate(ref iid, 1, IntPtr.Zero, out var obj); // CLSCTX_INPROC_SERVER = 1
                if (hr != 0 || obj == null)
                {
                    ReleaseCom(dev);
                    ReleaseCom(enumerator);
                    return null;
                }

                var endpoint = (IAudioEndpointVolume)obj;
                // NOTE: `obj` and `endpoint` are the SAME RCW (per-COM-identity, not
                // per-interface). Releasing `obj` here would disconnect the RCW that the
                // caller still holds via `endpoint`, so the caller's ReleaseCom(endpoint)
                // in its finally block is the only place that releases it.
                ReleaseCom(dev);
                ReleaseCom(enumerator);
                return endpoint;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Failed to get audio endpoint: {ex.Message}");
                return null;
            }
        }

        private static void ReleaseCom(object? com)
        {
            if (com == null) return;
            try { Marshal.ReleaseComObject(com); }
            catch { }
        }

        public static float GetVolume()
        {
            var endpoint = GetVolumeEndpoint();
            if (endpoint == null) return 0.5f;
            try
            {
                int hr = endpoint.GetMasterVolumeLevelScalar(out float level);
                return hr == 0 ? level : 0.5f;
            }
            catch
            {
                return 0.5f;
            }
            finally
            {
                ReleaseCom(endpoint);
            }
        }

        public static bool IsMuted()
        {
            var endpoint = GetVolumeEndpoint();
            if (endpoint == null) return false;
            try
            {
                int hr = endpoint.GetMute(out bool isMuted);
                return hr == 0 && isMuted;
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseCom(endpoint);
            }
        }

        public static float AdjustVolume(float delta)
        {
            var endpoint = GetVolumeEndpoint();
            if (endpoint != null)
            {
                try
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
                finally
                {
                    ReleaseCom(endpoint);
                }
            }

            // Fallback to keyboard event. A key must be pressed and released, or the
            // volume key stays "held down" and scares the OS volume OSD into repeating.
            if (delta > 0)
            {
                keybd_event(VK_VOLUME_UP, 0, 0, UIntPtr.Zero);
                keybd_event(VK_VOLUME_UP, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            else if (delta < 0)
            {
                keybd_event(VK_VOLUME_DOWN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_VOLUME_DOWN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            return GetVolume();
        }
    }
}
