using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BigPictureManager
{
    /// <summary>
    /// Power off Xbox controllers via the XboxGIP driver (same protocol as xbpoweroff).
    /// Requires a short-lived Windows service running as SYSTEM to open \\.\XboxGIP_Admin.
    /// </summary>
    internal static class XboxGipPowerOff
    {
        internal const string ServiceArgFlag = "--xbpoweroff-svc";
        internal const string ServiceArgIndexPrefix = "--xbpoweroff-index";
        internal const string ServiceArgIdsPrefix = "--xbpoweroff-ids";

        private const string ServiceName = "BigPictureMgr_XboxGipOff";
        private const string GipDevicePath = @"\\.\XboxGIP";
        private const string GipAdminDevicePath = @"\\.\XboxGIP_Admin";

        private const uint GipReenumerate = 0x40001CD0;
        private const uint GipControlDevice = 0x40001C4C;
        private const byte SubcmdTurnOff = 2;

        private const int MaxControllers = 8;
        private const int MaxReadAttempts = 16;
        private const uint GenericReadWrite = 0xC0000000;
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileFlagOverlapped = 0x40000000;

        private const uint ErrorIoPending = 997;
        private const uint WaitTimeout = 0x00000102;
        private const uint WaitObject0 = 0;

        private const int ServiceTypeOwnProcess = 0x00000010;
        private const int ServiceDemandStart = 0x00000003;
        private const int ServiceErrorIgnore = 0x00000000;
        private const int ServiceControlStop = 0x00000001;
        private const int ServiceRunning = 0x00000004;
        private const int ServiceStopped = 0x00000001;
        private const int ServiceAcceptStop = 0x00000001;

        private const int ErrorServiceExists = 1073;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GipHeader
        {
            public ulong DeviceId;
            public byte CommandId;
            public byte ClientFlags;
            public byte Sequence;
            public byte Unknown1;
            public uint Length;
            public uint Unknown2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOverlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public IntPtr UnionPointer;
            public IntPtr hEvent;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ServiceTableEntry
        {
            public string lpServiceName;
            public IntPtr lpServiceProc;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ServiceMainFn(uint dwArgc, IntPtr lpszArgv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ServiceControlHandlerFn(uint dwControl);

        private static readonly ServiceMainFn ServiceMainDelegate = ServiceMain;
        private static readonly ServiceControlHandlerFn ServiceControlHandlerDelegate = ServiceControlHandler;

        private static readonly IntPtr ServiceMainPtr = Marshal.GetFunctionPointerForDelegate(ServiceMainDelegate);
        private static readonly IntPtr ServiceControlHandlerPtr = Marshal.GetFunctionPointerForDelegate(ServiceControlHandlerDelegate);

        private static IntPtr _serviceStatusHandle;
        private static ServiceStatus _serviceStatus;
        private static int _serviceTargetIndex = -1;
        private static List<ulong> _serviceExplicitDeviceIds;

        /// <summary>
        /// Enumerates Xbox GIP devices (opens \\.\XboxGIP only; does not require admin).
        /// </summary>
        internal static List<ulong> TryDiscoverGipControllers()
        {
            var ids = new List<ulong>(MaxControllers);
            DiscoverDevices(ids, MaxControllers);
            return ids;
        }

        internal static bool TryParseServiceArgs(string[] args, out int targetIndex, out List<ulong> explicitDeviceIds)
        {
            targetIndex = -1;
            explicitDeviceIds = null;
            if (args == null || args.Length == 0)
            {
                return false;
            }

            var svc = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ServiceArgFlag, StringComparison.OrdinalIgnoreCase))
                {
                    svc = true;
                }
                else if (
                    string.Equals(args[i], ServiceArgIndexPrefix, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length
                )
                {
                    int.TryParse(args[i + 1], out targetIndex);
                    i++;
                }
                else if (
                    string.Equals(args[i], ServiceArgIdsPrefix, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length
                )
                {
                    var parts = args[i + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var ids = new List<ulong>();
                    foreach (var p in parts)
                    {
                        if (
                            ulong.TryParse(
                                p.Trim(),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out var id
                            )
                        )
                        {
                            ids.Add(id);
                        }
                    }

                    if (ids.Count > 0)
                    {
                        explicitDeviceIds = ids.Distinct().ToList();
                    }

                    i++;
                }
            }

            return svc;
        }

        /// <summary>
        /// Entry point when launched as SCM worker (--xbpoweroff-svc). Blocks until the service stops.
        /// </summary>
        internal static int RunServiceMode(int targetIndex, List<ulong> explicitDeviceIds)
        {
            _serviceTargetIndex = targetIndex;
            _serviceExplicitDeviceIds = explicitDeviceIds;

            var entry0 = new ServiceTableEntry { lpServiceName = ServiceName, lpServiceProc = ServiceMainPtr };
            var entry1 = new ServiceTableEntry { lpServiceName = null, lpServiceProc = IntPtr.Zero };

            var entrySize = Marshal.SizeOf(typeof(ServiceTableEntry));
            var table = Marshal.AllocHGlobal(checked(entrySize * 2));
            try
            {
                Marshal.StructureToPtr(entry0, table, false);
                Marshal.StructureToPtr(entry1, IntPtr.Add(table, entrySize), false);

                if (!StartServiceCtrlDispatcher(table))
                {
                    return Marshal.GetLastWin32Error();
                }

                return 0;
            }
            finally
            {
                try
                {
                    Marshal.DestroyStructure(table, typeof(ServiceTableEntry));
                }
                catch
                {
                    // ignore
                }

                try
                {
                    Marshal.DestroyStructure(IntPtr.Add(table, entrySize), typeof(ServiceTableEntry));
                }
                catch
                {
                    // ignore
                }

                Marshal.FreeHGlobal(table);
            }
        }

        private static void ServiceMain(uint dwArgc, IntPtr lpszArgv)
        {
            _serviceStatusHandle = RegisterServiceCtrlHandler(ServiceName, ServiceControlHandlerDelegate);
            if (_serviceStatusHandle == IntPtr.Zero)
            {
                return;
            }

            _serviceStatus = new ServiceStatus
            {
                dwServiceType = ServiceTypeOwnProcess,
                dwCurrentState = ServiceRunning,
                dwControlsAccepted = ServiceAcceptStop,
                dwWin32ExitCode = 0,
                dwServiceSpecificExitCode = 0,
                dwCheckPoint = 0,
                dwWaitHint = 0,
            };
            SetServiceStatus(_serviceStatusHandle, ref _serviceStatus);

            try
            {
                DoPowerOff(_serviceTargetIndex);
            }
            finally
            {
                _serviceStatus.dwCurrentState = ServiceStopped;
                SetServiceStatus(_serviceStatusHandle, ref _serviceStatus);
            }
        }

        private static void ServiceControlHandler(uint dwControl)
        {
            if (dwControl != ServiceControlStop)
            {
                return;
            }

            _serviceStatus.dwCurrentState = ServiceStopped;
            SetServiceStatus(_serviceStatusHandle, ref _serviceStatus);
        }

        /// <summary>
        /// Creates a temporary SYSTEM service, runs power-off, then deletes the service. Caller must be elevated admin.
        /// </summary>
        /// <param name="targetIndex">If &gt;= 0, power off controller by index after in-service discovery (ignored when <paramref name="knownDeviceIds"/> is set).</param>
        /// <param name="knownDeviceIds">If non-empty, power off these device IDs only (no GIP discovery in the service).</param>
        internal static void PowerOffViaEphemeralService(int targetIndex = -1, IReadOnlyList<ulong> knownDeviceIds = null)
        {
            var exePath = System.Windows.Forms.Application.ExecutablePath;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new InvalidOperationException("Executable path is empty.");
            }

            string binPath;
            if (knownDeviceIds != null && knownDeviceIds.Count > 0)
            {
                var hex = string.Join(
                    ",",
                    knownDeviceIds.Distinct().Select(id => id.ToString("X16", CultureInfo.InvariantCulture))
                );
                binPath = $"\"{exePath}\" {ServiceArgFlag} {ServiceArgIdsPrefix} {hex}";
            }
            else if (targetIndex >= 0)
            {
                binPath = $"\"{exePath}\" {ServiceArgFlag} {ServiceArgIndexPrefix} {targetIndex}";
            }
            else
            {
                binPath = $"\"{exePath}\" {ServiceArgFlag}";
            }

            var scm = OpenSCManager(null, null, ScManagerCreateService);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var svc = CreateService(
                    scm,
                    ServiceName,
                    "Big Picture Manager — Xbox GIP power off",
                    ServiceAllAccess,
                    ServiceTypeOwnProcess,
                    ServiceDemandStart,
                    ServiceErrorIgnore,
                    binPath,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null
                );

                if (svc == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != ErrorServiceExists)
                    {
                        throw new Win32Exception(err);
                    }

                    svc = OpenService(scm, ServiceName, ServiceAllAccess);
                    if (svc == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (
                        !ChangeServiceConfig(
                            svc,
                            ServiceTypeOwnProcess,
                            ServiceDemandStart,
                            ServiceErrorIgnore,
                            binPath,
                            null,
                            IntPtr.Zero,
                            null,
                            null,
                            null,
                            null
                        )
                    )
                    {
                        CloseServiceHandle(svc);
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }

                try
                {
                    if (!StartService(svc, 0, IntPtr.Zero))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err != 1056)
                        {
                            // ERROR_SERVICE_ALREADY_RUNNING — still wait for work to finish
                            throw new Win32Exception(err);
                        }
                    }

                    Thread.Sleep(3000);

                    DeleteService(svc);
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        private static void DoPowerOff(int targetIdx)
        {
            var explicitIds = _serviceExplicitDeviceIds;
            if (explicitIds != null && explicitIds.Count > 0)
            {
                foreach (var id in explicitIds)
                {
                    PowerOffDevice(id);
                }

                return;
            }

            var ids = new List<ulong>(MaxControllers);
            DiscoverDevices(ids, MaxControllers);

            if (ids.Count == 0)
            {
                return;
            }

            if (targetIdx < 0)
            {
                foreach (var id in ids)
                {
                    PowerOffDevice(id);
                }
            }
            else if (targetIdx < ids.Count)
            {
                PowerOffDevice(ids[targetIdx]);
            }
        }

        private static void DiscoverDevices(List<ulong> ids, int max)
        {
            var h = CreateFile(
                GipDevicePath,
                GenericReadWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal | FileFlagOverlapped,
                IntPtr.Zero
            );

            if (h == new IntPtr(-1) || h == IntPtr.Zero)
            {
                return;
            }

            try
            {
                uint bytes = 0;
                DeviceIoControl(h, GipReenumerate, IntPtr.Zero, 0, IntPtr.Zero, 0, out bytes, IntPtr.Zero);

                var ev = CreateEvent(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    var buf = new byte[4096];
                    var gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    try
                    {
                        var bufPtr = gch.AddrOfPinnedObject();

                        for (var attempt = 0; attempt < MaxReadAttempts && ids.Count < max; attempt++)
                        {
                            var ov = new NativeOverlapped { hEvent = ev };
                            ResetEvent(ev);

                            uint rd = 0;
                            var readOk = ReadFile(h, bufPtr, (uint)buf.Length, out rd, ref ov);
                            if (!readOk)
                            {
                                var readErr = Marshal.GetLastWin32Error();
                                if (readErr != unchecked((int)ErrorIoPending))
                                {
                                    continue;
                                }

                                if (WaitForSingleObject(ev, 300) == WaitTimeout)
                                {
                                    CancelIo(h);
                                    WaitForSingleObject(ev, 100);
                                    continue;
                                }

                                if (!GetOverlappedResult(h, ref ov, out rd, false))
                                {
                                    continue;
                                }
                            }

                            if (rd < (uint)Marshal.SizeOf(typeof(GipHeader)))
                            {
                                continue;
                            }

                            var hdr = Marshal.PtrToStructure<GipHeader>(bufPtr);
                            if (hdr.CommandId == 0x01 || hdr.CommandId == 0x02)
                            {
                                if (!ids.Contains(hdr.DeviceId))
                                {
                                    ids.Add(hdr.DeviceId);
                                }
                            }
                        }
                    }
                    finally
                    {
                        gch.Free();
                    }
                }
                finally
                {
                    CloseHandle(ev);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static bool PowerOffDevice(ulong deviceId)
        {
            var ha = CreateFile(
                GipAdminDevicePath,
                GenericReadWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero
            );

            if (ha == new IntPtr(-1) || ha == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var input = new byte[9];
                BitConverter.GetBytes(deviceId).CopyTo(input, 0);
                input[8] = SubcmdTurnOff;

                var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
                try
                {
                    uint bytesRet = 0;
                    return DeviceIoControl(
                        ha,
                        GipControlDevice,
                        pin.AddrOfPinnedObject(),
                        (uint)input.Length,
                        IntPtr.Zero,
                        0,
                        out bytesRet,
                        IntPtr.Zero
                    );
                }
                finally
                {
                    pin.Free();
                }
            }
            finally
            {
                CloseHandle(ha);
            }
        }

        private const uint ScManagerCreateService = 0x00002;
        private const uint ServiceAllAccess = 0xF01FF;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lpServiceStartName,
            string lpPassword
        );

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfig(
            IntPtr hService,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lpServiceStartName,
            string lpPassword,
            string lpDisplayName
        );

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool StartService(IntPtr hService, int dwNumServiceArgs, IntPtr lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterServiceCtrlHandler(string lpServiceName, ServiceControlHandlerFn lpHandlerProc);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref ServiceStatus lpServiceStatus);

        [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool StartServiceCtrlDispatcher(IntPtr lpServiceStartTable);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            ref NativeOverlapped lpOverlapped
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(
            IntPtr hFile,
            ref NativeOverlapped lpOverlapped,
            out uint lpNumberOfBytesTransferred,
            bool bWait
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
