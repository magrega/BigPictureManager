using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BigPictureManager
{
    /// <summary>
    /// Audio API for enumerating/switching active playback devices.
    /// </summary>
    public static class NativeAudioApi
    {
        #region COM Interfaces for default device switching
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
            int SetDefaultEndpoint(string pszDeviceName, int role);
        }
        #endregion

        public class AudioDevice
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public override string ToString() => Name;
        }

        private static readonly MMDeviceEnumerator Enumerator;
        private static readonly IPolicyConfig PolicyConfig;
        private static readonly object WatcherSync = new object();
        private static AudioDeviceNotificationClient NotificationClient;
        private static Action OnDevicesChanged;

        static NativeAudioApi()
        {
            Enumerator = new MMDeviceEnumerator();
            var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
            PolicyConfig = (IPolicyConfig)Activator.CreateInstance(type);
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
        /// Set default playback device by ID
        /// </summary>
        public static bool SetDefaultDevice(string deviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    return false;
                }

                PolicyConfig.SetDefaultEndpoint(deviceId, 0); // eConsole
                PolicyConfig.SetDefaultEndpoint(deviceId, 1); // eMultimedia
                PolicyConfig.SetDefaultEndpoint(deviceId, 2); // eCommunications
                return true;
            }
            catch { return false; }
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