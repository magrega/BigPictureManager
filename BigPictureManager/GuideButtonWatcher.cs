using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BigPictureManager
{
    /// <summary>
    /// Raises <see cref="DoublePressed"/> when the Guide (Xbox) button is pressed twice in quick succession.
    /// Guide is read event-driven from Raw Input HID reports of XInput-compatible controllers; XInput polling
    /// is used only while a controller is connected that exposes no such HID collection.
    /// Events fire on the watcher's own threads.
    /// </summary>
    internal sealed class GuideButtonWatcher : IDisposable
    {
        private const int DoublePressWindowMs = 400;

        // XInput-compatible HID collections ("&IG_" in the device path) report Guide as button 11.
        private const string XInputHidPathMarker = "&IG_";
        private const ushort HidUsagePageGenericDesktop = 0x01;
        private const ushort HidUsageJoystick = 0x04;
        private const ushort HidUsageGamepad = 0x05;
        private const ushort HidUsagePageButton = 0x09;
        private const ushort HidGuideButtonUsage = 0x0B;

        private const ushort XInputGamepadGuide = 0x0400;
        private const int XInputSlotCount = 4;
        private const int XInputPollIntervalMs = 16;
        // XInputGetState on an empty slot is expensive, so empty slots are re-checked rarely.
        private const int XInputDisconnectedRecheckMs = 2000;

        private readonly Thread _rawInputThread;
        private readonly Dictionary<IntPtr, GuideDevice> _guideDevices = new Dictionary<IntPtr, GuideDevice>();
        private RawInputWindow _window;
        private volatile bool _stopRequested;
        private int _ignorePressesUntil;

        private Thread _pollThread;
        private volatile bool _pollStopRequested;
        private bool _xInputUnavailable;

        /// <summary>Raised when Guide is pressed twice within <see cref="DoublePressWindowMs"/> on the same controller.</summary>
        public event EventHandler DoublePressed;

        public GuideButtonWatcher()
        {
            _rawInputThread = new Thread(RunRawInputThread) { IsBackground = true, Name = "GuideButtonWatcher" };
            _rawInputThread.SetApartmentState(ApartmentState.STA);
        }

        /// <summary>Starts watching controllers.</summary>
        public void Start()
        {
            _rawInputThread.Start();
        }

        public void Dispose()
        {
            _stopRequested = true;
            _pollStopRequested = true;
            var window = _window;
            if (window != null && window.Handle != IntPtr.Zero)
            {
                PostMessage(window.Handle, RawInputWindow.WmStop, IntPtr.Zero, IntPtr.Zero);
            }

            _rawInputThread.Join(500);
            _pollThread?.Join(500);
        }

        private void RunRawInputThread()
        {
            _window = new RawInputWindow(this);
            if (!_window.Register())
            {
                BpmLog.WriteLine("[Error] [Guide] Raw Input registration failed (Win32 " + Marshal.GetLastWin32Error() + "); using XInput polling.");
                _window.DestroyHandle();
                // No device-change notifications without Raw Input, so polling has to keep running.
                StartPolling(stopWhenNoController: false);
                return;
            }

            foreach (var device in EnumerateHidDevices())
            {
                TryAddGuideDevice(device);
            }

            UpdatePollingFallback();

            if (!_stopRequested)
            {
                Application.Run();
            }

            _window.DestroyHandle();
        }

        private void OnDeviceArrived(IntPtr device)
        {
            TryAddGuideDevice(device);
            UpdatePollingFallback();
        }

        private void OnDeviceRemoved(IntPtr device)
        {
            if (_guideDevices.Remove(device))
            {
                BpmLog.WriteLine("[Guide] Controller removed from Raw Input watch.");
            }

            UpdatePollingFallback();
        }

        private void TryAddGuideDevice(IntPtr device)
        {
            if (_guideDevices.ContainsKey(device))
            {
                return;
            }

            var path = GetDevicePath(device);
            if (path == null || path.IndexOf(XInputHidPathMarker, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            var preparsedData = GetPreparsedData(device);
            if (preparsedData == null || HidP_MaxUsageListLength(HidPInput, HidUsagePageButton, preparsedData) < HidGuideButtonUsage)
            {
                return;
            }

            _guideDevices[device] = new GuideDevice(preparsedData);
            BpmLog.WriteLine("[Guide] Watching Guide via Raw Input: " + path);
        }

        private void OnRawInput(IntPtr rawInput, int messageTime)
        {
            var headerSize = (uint)(8 + 2 * IntPtr.Size);
            uint size = 0;
            GetRawInputData(rawInput, RidInput, null, ref size, headerSize);
            var data = new byte[size];
            if (GetRawInputData(rawInput, RidInput, data, ref size, headerSize) != size
                || BitConverter.ToUInt32(data, 0) != RimTypeHid)
            {
                return;
            }

            var device = IntPtr.Size == 8 ? new IntPtr(BitConverter.ToInt64(data, 8)) : new IntPtr(BitConverter.ToInt32(data, 8));
            if (!_guideDevices.TryGetValue(device, out var guideDevice))
            {
                return;
            }

            var offset = (int)headerSize;
            var reportSize = (int)BitConverter.ToUInt32(data, offset);
            var reportCount = (int)BitConverter.ToUInt32(data, offset + 4);
            var report = new byte[reportSize];
            var usages = new ushort[HidGuideButtonUsage + 8];
            for (var i = 0; i < reportCount; i++)
            {
                Array.Copy(data, offset + 8 + i * reportSize, report, 0, reportSize);
                var usageCount = (uint)usages.Length;
                if (HidP_GetUsages(HidPInput, HidUsagePageButton, 0, usages, ref usageCount, guideDevice.PreparsedData, report, (uint)reportSize)
                    != HidPStatusSuccess)
                {
                    continue;
                }

                var guideDown = Array.IndexOf(usages, HidGuideButtonUsage, 0, (int)usageCount) >= 0;

                // Messages queued while a previous double press was being handled are stale.
                if (unchecked(messageTime - _ignorePressesUntil) < 0)
                {
                    guideDevice.Presses.Reset(guideDown);
                    continue;
                }

                if (guideDevice.Presses.Update(guideDown, messageTime))
                {
                    DoublePressed?.Invoke(this, EventArgs.Empty);
                    _ignorePressesUntil = Environment.TickCount;
                }
            }
        }

        // XInput polling only covers controllers that expose no Guide-capable HID collection.
        private void UpdatePollingFallback()
        {
            if (_guideDevices.Count > 0)
            {
                _pollStopRequested = true;
                return;
            }

            if (AnyXInputControllerConnected())
            {
                StartPolling(stopWhenNoController: true);
            }
        }

        private void StartPolling(bool stopWhenNoController)
        {
            _pollStopRequested = false;
            if (_pollThread != null && _pollThread.IsAlive)
            {
                return;
            }

            BpmLog.WriteLine("[Guide] No Guide-capable HID controller found; polling XInput.");
            _pollThread = new Thread(() => PollXInput(stopWhenNoController)) { IsBackground = true, Name = "GuideButtonWatcher.XInput" };
            _pollThread.Start();
        }

        private void PollXInput(bool stopWhenNoController)
        {
            var presses = new PressTracker[XInputSlotCount];
            var connected = new bool[XInputSlotCount];
            var nextRecheckAt = new int[XInputSlotCount];
            for (var slot = 0; slot < XInputSlotCount; slot++)
            {
                presses[slot] = new PressTracker();
                nextRecheckAt[slot] = Environment.TickCount;
            }

            while (!_pollStopRequested && !_stopRequested)
            {
                var now = Environment.TickCount;
                for (uint slot = 0; slot < XInputSlotCount; slot++)
                {
                    if (!connected[slot] && unchecked(now - nextRecheckAt[slot]) < 0)
                    {
                        continue;
                    }

                    if (!TryGetXInputState(slot, out var state))
                    {
                        connected[slot] = false;
                        presses[slot].Reset(false);
                        nextRecheckAt[slot] = now + XInputDisconnectedRecheckMs;
                        continue;
                    }

                    connected[slot] = true;
                    if (presses[slot].Update((state.Gamepad.Buttons & XInputGamepadGuide) != 0, now))
                    {
                        DoublePressed?.Invoke(this, EventArgs.Empty);
                    }
                }

                // With nothing connected, wait for a device-change notification instead of polling.
                if (stopWhenNoController && Array.IndexOf(connected, true) < 0)
                {
                    BpmLog.WriteLine("[Guide] No XInput controller connected; XInput polling stopped.");
                    return;
                }

                Thread.Sleep(XInputPollIntervalMs);
            }
        }

        private bool AnyXInputControllerConnected()
        {
            for (uint slot = 0; slot < XInputSlotCount; slot++)
            {
                if (TryGetXInputState(slot, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetXInputState(uint slot, out XInputState state)
        {
            state = default(XInputState);
            if (_xInputUnavailable)
            {
                return false;
            }

            try
            {
                return XInputGetStateEx(slot, out state) == ErrorSuccess;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _xInputUnavailable = true;
                BpmLog.WriteLine("[Error] [Guide] XInput is unavailable: " + ex.Message);
                return false;
            }
        }

        private static List<IntPtr> EnumerateHidDevices()
        {
            var result = new List<IntPtr>();
            var entrySize = (uint)Marshal.SizeOf(typeof(RawInputDeviceListEntry));
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, entrySize) != 0 || count == 0)
            {
                return result;
            }

            var entries = new RawInputDeviceListEntry[count];
            var written = GetRawInputDeviceList(entries, ref count, entrySize);
            for (var i = 0; i < written && i < entries.Length; i++)
            {
                if (entries[i].Type == RimTypeHid)
                {
                    result.Add(entries[i].Device);
                }
            }

            return result;
        }

        private static string GetDevicePath(IntPtr device)
        {
            uint length = 0;
            GetRawInputDeviceInfo(device, RidiDeviceName, (StringBuilder)null, ref length);
            if (length == 0)
            {
                return null;
            }

            var path = new StringBuilder((int)length + 1);
            return GetRawInputDeviceInfo(device, RidiDeviceName, path, ref length) == uint.MaxValue ? null : path.ToString();
        }

        private static byte[] GetPreparsedData(IntPtr device)
        {
            uint size = 0;
            GetRawInputDeviceInfo(device, RidiPreparsedData, (byte[])null, ref size);
            if (size == 0)
            {
                return null;
            }

            var data = new byte[size];
            return GetRawInputDeviceInfo(device, RidiPreparsedData, data, ref size) == uint.MaxValue ? null : data;
        }

        /// <summary>Edge detection plus double-press timing for one controller.</summary>
        private sealed class PressTracker
        {
            private bool _down;
            private int? _lastPressAt;

            /// <summary>Feeds the current Guide state; returns true when it completes a double press.</summary>
            public bool Update(bool down, int time)
            {
                var pressed = down && !_down;
                _down = down;
                if (!pressed)
                {
                    return false;
                }

                if (_lastPressAt.HasValue && unchecked(time - _lastPressAt.Value) <= DoublePressWindowMs)
                {
                    // A third quick press must start a new pair, not form another double press.
                    _lastPressAt = null;
                    return true;
                }

                _lastPressAt = time;
                return false;
            }

            public void Reset(bool down)
            {
                _down = down;
                _lastPressAt = null;
            }
        }

        private sealed class GuideDevice
        {
            public GuideDevice(byte[] preparsedData)
            {
                PreparsedData = preparsedData;
            }

            public byte[] PreparsedData { get; }

            public PressTracker Presses { get; } = new PressTracker();
        }

        /// <summary>Hidden top-level window that receives Raw Input and device-change messages in the background.</summary>
        private sealed class RawInputWindow : NativeWindow
        {
            internal const int WmStop = 0x8000 + 0x47; // WM_APP + n
            private const int WmInput = 0x00FF;
            private const int WmInputDeviceChange = 0x00FE;
            private const int WmDeviceChange = 0x0219;
            private const int GidcArrival = 1;
            private const int GidcRemoval = 2;
            private const uint RidevInputSink = 0x00000100;
            private const uint RidevDevNotify = 0x00002000;

            private readonly GuideButtonWatcher _owner;

            public RawInputWindow(GuideButtonWatcher owner)
            {
                _owner = owner;
                CreateHandle(new CreateParams { Caption = "BpmGuideButtonWatcher" });
            }

            public bool Register()
            {
                var devices = new[]
                {
                    new RawInputDeviceRegistration { UsagePage = HidUsagePageGenericDesktop, Usage = HidUsageGamepad, Flags = RidevInputSink | RidevDevNotify, Target = Handle },
                    new RawInputDeviceRegistration { UsagePage = HidUsagePageGenericDesktop, Usage = HidUsageJoystick, Flags = RidevInputSink | RidevDevNotify, Target = Handle },
                };
                return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RawInputDeviceRegistration)));
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WmInput:
                        _owner.OnRawInput(m.LParam, GetMessageTime());
                        break;
                    case WmInputDeviceChange:
                        if (m.WParam.ToInt32() == GidcArrival)
                        {
                            _owner.OnDeviceArrived(m.LParam);
                        }
                        else if (m.WParam.ToInt32() == GidcRemoval)
                        {
                            _owner.OnDeviceRemoved(m.LParam);
                        }

                        break;
                    case WmDeviceChange:
                        // Covers XInput controllers that never show up as HID devices.
                        _owner.UpdatePollingFallback();
                        break;
                    case WmStop:
                        Application.ExitThread();
                        return;
                }

                base.WndProc(ref m);
            }
        }

        private const uint ErrorSuccess = 0;
        private const uint RimTypeHid = 2;
        private const uint RidInput = 0x10000003;
        private const uint RidiPreparsedData = 0x20000005;
        private const uint RidiDeviceName = 0x20000007;
        private const int HidPInput = 0;
        private const int HidPStatusSuccess = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDeviceRegistration
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDeviceListEntry
        {
            public IntPtr Device;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(RawInputDeviceRegistration[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[] data, ref uint size, uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList([Out] RawInputDeviceListEntry[] devices, ref uint count, uint size);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[] data, ref uint size);

        [DllImport("user32.dll")]
        private static extern int GetMessageTime();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("hid.dll")]
        private static extern int HidP_MaxUsageListLength(int reportType, ushort usagePage, byte[] preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsages(
            int reportType,
            ushort usagePage,
            ushort linkCollection,
            [Out] ushort[] usageList,
            ref uint usageLength,
            byte[] preparsedData,
            byte[] report,
            uint reportLength
        );

        // Undocumented XInputGetStateEx (ordinal 100): the only XInput call that reports the Guide button.
        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx(uint userIndex, out XInputState state);
    }
}
