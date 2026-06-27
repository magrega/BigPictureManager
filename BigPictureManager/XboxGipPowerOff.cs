using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace BigPictureManager
{
    /// <summary>
    /// Power off Xbox controllers via the XboxGIP driver (same protocol as xbpoweroff).
    /// Requires a short-lived Windows service running as SYSTEM to open \\.\XboxGIP_Admin.
    /// </summary>
    internal static class XboxGipPowerOff
    {
        internal const string ServiceArgFlag = "--xbpoweroff-svc";
        internal const string ServiceArgLogDirPrefix = "--xbpoweroff-logdir";

        /// <summary>Elevated one-shot helper args: install/remove the persistent power-off service.</summary>
        internal const string InstallServiceArg = "--install-xbox-service";
        internal const string UninstallServiceArg = "--uninstall-xbox-service";

        private const string ServiceName = "BigPictureMgr_XboxGipOff";
        private const string GipDevicePath = @"\\.\XboxGIP";
        private const string GipAdminDevicePath = @"\\.\XboxGIP_Admin";

        private const uint GipReenumerate = 0x40001CD0;
        private const uint GipControlDevice = 0x40001C4C;
        private const byte SubcmdTurnOff = 2;

        /// <summary>Guide-button LED intensity for ~10% (5 of 47).</summary>
        internal const byte LedIntensityPercent10 = 0x05;

        /// <summary>Guide-button LED intensity for 100% (47 of 47).</summary>
        internal const byte LedIntensityPercent100 = 0x2F;

        private const int LedCommandLength = 23;

        // Bytes 8..21 of the GIP LED command (command 0x0A); byte 22 is intensity.
        private static readonly byte[] LedCommandBody =
        {
            0x0A, 0x20, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
        };

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

        /// <summary>
        /// Enumerates Xbox GIP devices (opens \\.\XboxGIP only; does not require admin).
        /// </summary>
        internal static List<ulong> TryDiscoverGipControllers()
        {
            var ids = new List<ulong>(MaxControllers);
            DiscoverDevices(ids, MaxControllers);
            return ids;
        }

        /// <summary>
        /// Sets guide-button LED brightness for one controller via \\.\XboxGIP (no admin required).
        /// </summary>
        internal static bool TrySetLedBrightness(ulong deviceId, byte intensity)
        {
            var h = CreateFile(
                GipDevicePath,
                GenericReadWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero
            );

            if (h == new IntPtr(-1) || h == IntPtr.Zero)
            {
                BpmLog.WriteLine(
                    "[Xbox] Could not open "
                        + GipDevicePath
                        + " for LED control (device "
                        + deviceId.ToString("X16", CultureInfo.InvariantCulture)
                        + "): Win32 "
                        + Marshal.GetLastWin32Error()
                );
                return false;
            }

            try
            {
                var command = BuildLedCommand(deviceId, intensity);
                var pin = GCHandle.Alloc(command, GCHandleType.Pinned);
                try
                {
                    uint bytesWritten;
                    var ok = WriteFile(
                        h,
                        pin.AddrOfPinnedObject(),
                        (uint)command.Length,
                        out bytesWritten,
                        IntPtr.Zero
                    );
                    if (ok && bytesWritten == command.Length)
                    {
                        BpmLog.WriteLine(
                            "[Xbox] LED brightness set to "
                                + intensity
                                + " for device "
                                + deviceId.ToString("X16", CultureInfo.InvariantCulture)
                                + "."
                        );
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[Error] [Xbox] LED brightness write failed for device "
                                + deviceId.ToString("X16", CultureInfo.InvariantCulture)
                                + ": Win32 "
                                + Marshal.GetLastWin32Error()
                        );
                    }

                    return ok && bytesWritten == command.Length;
                }
                finally
                {
                    pin.Free();
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        internal static void TrySetLedBrightnessForAll(IReadOnlyList<ulong> deviceIds, byte intensity)
        {
            if (deviceIds == null || deviceIds.Count == 0)
            {
                return;
            }

            foreach (var id in deviceIds)
            {
                TrySetLedBrightness(id, intensity);
            }
        }

        private static byte[] BuildLedCommand(ulong deviceId, byte intensity)
        {
            var command = new byte[LedCommandLength];
            BitConverter.GetBytes(deviceId).CopyTo(command, 0);
            LedCommandBody.CopyTo(command, 8);
            command[LedCommandLength - 1] = intensity;
            return command;
        }

        internal static bool TryParseServiceArgs(string[] args, out string logDirectory)
        {
            logDirectory = null;
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
                    string.Equals(args[i], ServiceArgLogDirPrefix, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length
                )
                {
                    logDirectory = args[i + 1];
                    i++;
                }
            }

            return svc;
        }

        /// <summary>
        /// Entry point when launched as SCM worker (--xbpoweroff-svc). Blocks until the service stops.
        /// </summary>
        internal static int RunServiceMode()
        {
            BpmLog.WriteLine("[Xbox] Power-off service worker started.");

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
                BpmLog.WriteLine(
                    "[Error] [Xbox] Ephemeral service failed to register control handler: Win32 "
                        + Marshal.GetLastWin32Error()
                );
                return;
            }

            BpmLog.WriteLine("[Xbox] Ephemeral service \"" + ServiceName + "\" entered RUNNING state.");

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
                DoPowerOff();
            }
            finally
            {
                _serviceStatus.dwCurrentState = ServiceStopped;
                SetServiceStatus(_serviceStatusHandle, ref _serviceStatus);
                BpmLog.WriteLine("[Xbox] Ephemeral service \"" + ServiceName + "\" stopped.");
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

        private const string ServiceDisplayName = "Big Picture Manager — Xbox controller power off";

        // DACL: SYSTEM and Builtin Admins keep full control; Interactive Users may start/query only.
        // (RP = SERVICE_START, CC/LC query, LO interrogate, RC read.)
        private const string ServiceSddl =
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPLORC;;;IU)";

        /// <summary>True when the persistent power-off service is installed (query works without admin).</summary>
        internal static bool IsServiceInstalled()
        {
            var scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var svc = OpenService(scm, ServiceName, ServiceQueryStatus);
                if (svc == IntPtr.Zero)
                {
                    return false;
                }

                CloseServiceHandle(svc);
                return true;
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// Installs the persistent, demand-start SYSTEM service and grants interactive users the right to
        /// start it (so any user can trigger a power-off without elevation). Requires admin; throws on failure.
        /// </summary>
        internal static void InstallService()
        {
            var exePath = System.Windows.Forms.Application.ExecutablePath;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new InvalidOperationException("Executable path is empty.");
            }

            // The global service logs to a machine-wide location (it runs as SYSTEM for every user).
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BigPictureManager"
            );
            var binPath = $"\"{exePath}\" {ServiceArgFlag} {ServiceArgLogDirPrefix} \"{logDir}\"";

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
                    ServiceDisplayName,
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

                    if (!ChangeServiceConfig(svc, ServiceTypeOwnProcess, ServiceDemandStart, ServiceErrorIgnore,
                            binPath, null, IntPtr.Zero, null, null, null, ServiceDisplayName))
                    {
                        var configErr = Marshal.GetLastWin32Error();
                        CloseServiceHandle(svc);
                        throw new Win32Exception(configErr);
                    }
                }

                try
                {
                    GrantInteractiveUsersStart(svc);
                    BpmLog.WriteLine("[Xbox] Power-off service installed.");
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

        /// <summary>Removes the persistent power-off service. Requires admin; throws on failure.</summary>
        internal static void UninstallService()
        {
            var scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var svc = OpenService(scm, ServiceName, ServiceDelete);
                if (svc == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ErrorServiceDoesNotExist)
                    {
                        return;
                    }

                    throw new Win32Exception(err);
                }

                try
                {
                    if (!DeleteService(svc))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    BpmLog.WriteLine("[Xbox] Power-off service removed.");
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

        /// <summary>
        /// Starts the pre-installed service so it powers off connected controllers. Works without admin
        /// (interactive users were granted SERVICE_START at install time). No-op if not installed.
        /// </summary>
        internal static void TriggerPowerOff()
        {
            var scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
            {
                BpmLog.WriteLine("[Error] [Xbox] OpenSCManager failed: Win32 " + Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                var svc = OpenService(scm, ServiceName, ServiceStart | ServiceQueryStatus);
                if (svc == IntPtr.Zero)
                {
                    BpmLog.WriteLine(
                        "[Xbox] Power-off service is not available (Win32 " + Marshal.GetLastWin32Error() + ")."
                    );
                    return;
                }

                try
                {
                    if (StartService(svc, 0, IntPtr.Zero))
                    {
                        BpmLog.WriteLine("[Xbox] Power-off service triggered.");
                    }
                    else
                    {
                        var err = Marshal.GetLastWin32Error();
                        BpmLog.WriteLine(
                            err == ErrorServiceAlreadyRunning
                                ? "[Xbox] Power-off service was already running."
                                : "[Error] [Xbox] Could not start power-off service: Win32 " + err
                        );
                    }
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

        private static void GrantInteractiveUsersStart(IntPtr service)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(ServiceSddl, 1, out var securityDescriptor, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!SetServiceObjectSecurity(service, DaclSecurityInformation, securityDescriptor))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                LocalFree(securityDescriptor);
            }
        }

        private static void DoPowerOff()
        {
            var ids = new List<ulong>(MaxControllers);
            DiscoverDevices(ids, MaxControllers);

            if (ids.Count == 0)
            {
                BpmLog.WriteLine("[Xbox] No Xbox wireless controllers found to power off.");
                return;
            }

            var succeeded = 0;
            var failed = 0;
            foreach (var id in ids)
            {
                if (PowerOffDevice(id))
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }

            LogPowerOffSummary(succeeded, failed, ids.Count);
        }

        private static void LogPowerOffSummary(int succeeded, int failed, int attempted)
        {
            if (attempted == 0)
            {
                BpmLog.WriteLine("[Xbox] No Xbox wireless controllers found to power off.");
                return;
            }

            if (failed == 0 && succeeded > 0)
            {
                BpmLog.WriteLine("[Xbox] Controllers have been turned off (" + succeeded + " device(s)).");
                return;
            }

            BpmLog.WriteLine(
                "[Xbox] Power-off finished: " + succeeded + " succeeded, " + failed + " failed (of " + attempted + " device(s))."
            );
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
                BpmLog.WriteLine(
                    "[Error] [Xbox] Could not open "
                        + GipAdminDevicePath
                        + " for device "
                        + deviceId.ToString("X16", CultureInfo.InvariantCulture)
                        + ": Win32 "
                        + Marshal.GetLastWin32Error()
                );
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
                    var ok = DeviceIoControl(
                        ha,
                        GipControlDevice,
                        pin.AddrOfPinnedObject(),
                        (uint)input.Length,
                        IntPtr.Zero,
                        0,
                        out bytesRet,
                        IntPtr.Zero
                    );
                    if (ok)
                    {
                        BpmLog.WriteLine(
                            "[Xbox] Power-off IOCTL succeeded for device "
                                + deviceId.ToString("X16", CultureInfo.InvariantCulture)
                                + "."
                        );
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[Error] [Xbox] Power-off IOCTL failed for device "
                                + deviceId.ToString("X16", CultureInfo.InvariantCulture)
                                + ": Win32 "
                                + Marshal.GetLastWin32Error()
                        );
                    }

                    return ok;
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

        private const uint ScManagerConnect = 0x00001;
        private const uint ScManagerCreateService = 0x00002;
        private const uint ServiceAllAccess = 0xF01FF;
        private const uint ServiceQueryStatus = 0x00004;
        private const uint ServiceStart = 0x00010;
        private const uint ServiceDelete = 0x10000;
        private const uint DaclSecurityInformation = 0x00000004;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceAlreadyRunning = 1056;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor,
            uint stringSdRevision,
            out IntPtr securityDescriptor,
            out uint securityDescriptorSize
        );

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceObjectSecurity(
            IntPtr hService,
            uint dwSecurityInformation,
            IntPtr lpSecurityDescriptor
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

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
        private static extern bool WriteFile(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
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
