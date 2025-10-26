using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BigPictureManager
{
    /// <summary>
    /// Minimal native API - Only for switching default playback devices
    /// </summary>
    public static class NativeAudioApi
    {
        #region COM Interfaces
        
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
            int GetDevice(string pwstrId, out IMMDevice ppDevice);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            int GetCount(out uint pcDevices);
            int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
        }

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

        [StructLayout(LayoutKind.Sequential)]
        internal struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        internal struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pwszVal; }

        #endregion

        public class AudioDevice
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public override string ToString() => Name;
        }

        private static IMMDeviceEnumerator _enumerator;
        private static IPolicyConfig _policyConfig;

        static NativeAudioApi()
        {
            _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
            _policyConfig = (IPolicyConfig)Activator.CreateInstance(type);
        }

        /// <summary>
        /// Get all active playback devices
        /// </summary>
        public static List<AudioDevice> GetPlaybackDevices()
        {
            var devices = new List<AudioDevice>();
            IMMDeviceCollection collection = null;
            
            try
            {
                _enumerator.EnumAudioEndpoints(0, 0x1, out collection); // 0=Render, 0x1=Active
                uint count;
                collection.GetCount(out count);
                
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IPropertyStore props = null;
                    try
                    {
                        collection.Item(i, out device);
                        device.GetId(out string id);
                        device.OpenPropertyStore(0, out props);
                        
                        if (props != null)
                        {
                            var key = new PropertyKey { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 14 };
                            props.GetValue(ref key, out PropVariant nameVar);
                            string name = Marshal.PtrToStringUni(nameVar.pwszVal);
                            Marshal.FreeCoTaskMem(nameVar.pwszVal);
                            
                            if (!string.IsNullOrEmpty(name))
                                devices.Add(new AudioDevice { Id = id, Name = name });
                        }
                    }
                    finally
                    {
                        if (props != null) Marshal.ReleaseComObject(props);
                        if (device != null) Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                if (collection != null) Marshal.ReleaseComObject(collection);
            }
            return devices;
        }

        /// <summary>
        /// Set default playback device by ID
        /// </summary>
        public static bool SetDefaultDevice(string deviceId)
        {
            try
            {
                _policyConfig.SetDefaultEndpoint(deviceId, 0); // eConsole
                _policyConfig.SetDefaultEndpoint(deviceId, 1); // eMultimedia  
                _policyConfig.SetDefaultEndpoint(deviceId, 2); // eCommunications
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Get current default playback device
        /// </summary>
        public static AudioDevice GetDefaultDevice()
        {
            IMMDevice device = null;
            IPropertyStore props = null;
            try
            {
                _enumerator.GetDefaultAudioEndpoint(0, 1, out device); // 0=Render, 1=Multimedia
                if (device != null)
                {
                    device.GetId(out string id);
                    device.OpenPropertyStore(0, out props);
                    if (props != null)
                    {
                        var key = new PropertyKey { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 14 };
                        props.GetValue(ref key, out PropVariant nameVar);
                        string name = Marshal.PtrToStringUni(nameVar.pwszVal);
                        Marshal.FreeCoTaskMem(nameVar.pwszVal);
                        return new AudioDevice { Id = id, Name = name };
                    }
                }
            }
            finally
            {
                if (props != null) Marshal.ReleaseComObject(props);
                if (device != null) Marshal.ReleaseComObject(device);
            }
            return null;
        }
    }
}