using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BigPictureManager
{
    /// <summary>
    /// A message-only window that raises <see cref="Changed"/> when any device interface arrives or is
    /// removed (WM_DEVICECHANGE). Lets us re-detect controllers that reconnect mid-session.
    /// </summary>
    internal sealed class DeviceArrivalWatcher : NativeWindow, IDisposable
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const int DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x00000004;
        private const int HwndMessage = -3;

        private IntPtr _notificationHandle;

        /// <summary>Raised on the UI thread when a device interface arrives or is removed.</summary>
        public event EventHandler Changed;

        public DeviceArrivalWatcher()
        {
            CreateHandle(new CreateParams { Caption = "BpmDeviceWatcher", Parent = new IntPtr(HwndMessage) });
            Register();
        }

        private void Register()
        {
            var filter = new DevBroadcastDeviceInterface
            {
                dbcc_size = Marshal.SizeOf(typeof(DevBroadcastDeviceInterface)),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            };

            var buffer = Marshal.AllocHGlobal(filter.dbcc_size);
            try
            {
                Marshal.StructureToPtr(filter, buffer, false);
                _notificationHandle = RegisterDeviceNotification(
                    Handle,
                    buffer,
                    DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES
                );
                if (_notificationHandle == IntPtr.Zero)
                {
                    BpmLog.WriteLine("[Xbox] Could not register for device-change notifications: Win32 " + Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                var evt = m.WParam.ToInt32();
                if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_notificationHandle != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_notificationHandle);
                _notificationHandle = IntPtr.Zero;
            }

            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevBroadcastDeviceInterface
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public short dbcc_name;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);
    }
}
