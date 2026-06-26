using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BigPictureManager
{
    /// <summary>
    /// Audio API for enumerating/switching active playback devices.
    /// </summary>
    internal static class NativeAudioApi
    {
        #region COM Interfaces for default device switching
        // Windows 7+ layout of the undocumented IPolicyConfig (has ResetDeviceFormat).
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPolicyConfig
        {
            int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
            int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
            int ResetDeviceFormat(string pszDeviceName);
            int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);
            int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
            int GetShareMode(string pszDeviceName, IntPtr pMode);
            int SetShareMode(string pszDeviceName, IntPtr mode);
            int GetPropertyValue(string pszDeviceName, bool bFxStore, ref PropertyKey key, out PropVariant pv);
            int SetPropertyValue(string pszDeviceName, bool bFxStore, ref PropertyKey key, ref PropVariant pv);
            // PreserveSig so we read the real HRESULT instead of relying on a thrown COMException.
            [PreserveSig]
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
        }

        // Vista layout (no ResetDeviceFormat) — SetDefaultEndpoint sits in a different vtable slot.
        // Used as a fallback because Windows updates have shifted the IPolicyConfig vtable before.
        [Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPolicyConfigVista
        {
            int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
            int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
            int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);
            int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
            int GetShareMode(string pszDeviceName, IntPtr pMode);
            int SetShareMode(string pszDeviceName, IntPtr mode);
            int GetPropertyValue(string pszDeviceName, ref PropertyKey key, out PropVariant pv);
            int SetPropertyValue(string pszDeviceName, ref PropertyKey key, ref PropVariant pv);
            [PreserveSig]
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
            int SetEndpointVisibility(string pszDeviceName, bool bVisible);
        }
        #endregion

        internal class AudioDevice
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public override string ToString() => Name;
        }

        private static readonly MMDeviceEnumerator Enumerator;
        private static readonly Guid PolicyConfigClientClsid = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        private static readonly object WatcherSync = new object();
        private static AudioDeviceNotificationClient NotificationClient;
        private static Action OnDevicesChanged;

        static NativeAudioApi()
        {
            Enumerator = new MMDeviceEnumerator();
        }

        /// <summary>
        /// Returns only active playback devices.
        /// </summary>
        public static List<AudioDevice> GetPlaybackDevices()
        {
            try
            {
                return Enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Where(d => d != null && d.State == DeviceState.Active)
                    .Select(d => new AudioDevice { Id = d.ID, Name = d.FriendlyName })
                    .Where(d => !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.Name))
                    .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }
            catch
            {
                return new List<AudioDevice>();
            }
        }

        /// <summary>
        /// Set default playback device by ID for all three roles (Console, Multimedia, Communications).
        /// </summary>
        public static bool SetDefaultDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return false;
            }

            // CPolicyConfigClient is apartment-threaded. If it is created from an MTA thread COM hands
            // back a marshaling proxy, and QueryInterface for the (proxy-less) IPolicyConfig interface
            // fails with E_NOINTERFACE. Always create and use it on a dedicated STA thread so behaviour
            // is deterministic regardless of which thread the caller runs on.
            var result = false;
            var staThread = new Thread(() => result = SetDefaultDeviceSta(deviceId))
            {
                IsBackground = true,
                Name = "BpmSetDefaultAudio",
            };
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            return result;
        }

        private static bool SetDefaultDeviceSta(string deviceId)
        {
            object comObject = null;
            try
            {
                var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid);
                comObject = Activator.CreateInstance(type);

                // The IPolicyConfig vtable has shifted across Windows builds, so try the modern
                // layout first and fall back to the Vista layout before giving up.
                return TrySetDefaultEndpoint(comObject, deviceId, useVistaLayout: false)
                    || TrySetDefaultEndpoint(comObject, deviceId, useVistaLayout: true);
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine(
                    "[Error] [Audio] Could not create PolicyConfig client (0x" + ex.HResult.ToString("X8")
                        + "): " + ex.Message
                );
                return false;
            }
            finally
            {
                if (comObject != null)
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
        }

        private static bool TrySetDefaultEndpoint(object comObject, string deviceId, bool useVistaLayout)
        {
            var layoutName = useVistaLayout ? "IPolicyConfigVista" : "IPolicyConfig";
            try
            {
                foreach (var role in new[] { 0, 1, 2 }) // eConsole, eMultimedia, eCommunications
                {
                    var hr = useVistaLayout
                        ? ((IPolicyConfigVista)comObject).SetDefaultEndpoint(deviceId, role)
                        : ((IPolicyConfig)comObject).SetDefaultEndpoint(deviceId, role);

                    if (hr != 0)
                    {
                        BpmLog.WriteLine(
                            "[Error] [Audio] " + layoutName + ".SetDefaultEndpoint(role=" + role
                                + ") returned HRESULT 0x" + hr.ToString("X8") + "."
                        );
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine(
                    "[Error] [Audio] " + layoutName + " path threw (0x" + ex.HResult.ToString("X8")
                        + "): " + ex.Message
                );
                return false;
            }
        }

        /// <summary>
        /// Get current default playback device
        /// </summary>
        public static AudioDevice GetDefaultDevice()
        {
            try
            {
                var device = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device != null)
                {
                    return new AudioDevice { Id = device.ID, Name = device.FriendlyName };
                }
            }
            catch { }
            return null;
        }

        public static void StartDeviceWatcher(Action onDevicesChanged)
        {
            lock (WatcherSync)
            {
                OnDevicesChanged = onDevicesChanged;
                if (NotificationClient == null)
                {
                    NotificationClient = new AudioDeviceNotificationClient(RaiseDevicesChanged);
                    Enumerator.RegisterEndpointNotificationCallback(NotificationClient);
                }
            }
        }

        public static void StopDeviceWatcher()
        {
            lock (WatcherSync)
            {
                if (NotificationClient != null)
                {
                    Enumerator.UnregisterEndpointNotificationCallback(NotificationClient);
                    NotificationClient = null;
                }
                OnDevicesChanged = null;
            }
        }

        private static void RaiseDevicesChanged()
        {
            Action callback;
            lock (WatcherSync)
            {
                callback = OnDevicesChanged;
            }
            callback?.Invoke();
        }

        private sealed class AudioDeviceNotificationClient : IMMNotificationClient
        {
            private readonly Action _changed;

            public AudioDeviceNotificationClient(Action changed)
            {
                _changed = changed;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            {
                _changed?.Invoke();
            }

            public void OnDeviceAdded(string pwstrDeviceId)
            {
                _changed?.Invoke();
            }

            public void OnDeviceRemoved(string deviceId)
            {
                _changed?.Invoke();
            }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render)
                {
                    _changed?.Invoke();
                }
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
            {
                // FriendlyName or availability can change dynamically.
                _changed?.Invoke();
            }
        }
    }
}