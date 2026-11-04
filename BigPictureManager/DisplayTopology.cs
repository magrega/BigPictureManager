using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using static BigPictureManager.NativeDisplayConfigApi;

namespace BigPictureManager
{
    /// <summary>A monitor that is part of the Windows desktop.</summary>
    internal sealed class DisplayMonitorInfo
    {
        public DisplayMonitorInfo(string devicePath, string name, Rectangle bounds)
        {
            DevicePath = devicePath;
            Name = name;
            Bounds = bounds;
        }

        /// <summary>PnP device interface path; unlike adapter LUIDs and \\.\DISPLAYn names it survives reboots.</summary>
        public string DevicePath { get; }

        /// <summary>EDID friendly name (e.g. "LG TV"), or null if the monitor doesn't report one.</summary>
        public string Name { get; }

        /// <summary>Desktop rectangle in physical pixels.</summary>
        public Rectangle Bounds { get; }
    }

    /// <summary>The active display layout as it was before Big Picture Manager changed it.</summary>
    internal sealed class DisplaySnapshot
    {
        internal DisplaySnapshot(DisplayConfigPathInfo[] paths, DisplayConfigModeInfo[] modes, string primaryDevicePath)
        {
            Paths = paths;
            Modes = modes;
            PrimaryDevicePath = primaryDevicePath;
        }

        internal DisplayConfigPathInfo[] Paths { get; }

        internal DisplayConfigModeInfo[] Modes { get; }

        /// <summary>Fallback for when the full layout can no longer be applied (e.g. a monitor was unplugged).</summary>
        internal string PrimaryDevicePath { get; }
    }

    internal enum PrimaryDisplayResult
    {
        /// <summary>The monitor is unplugged, powered off, or turned off in Windows display settings.</summary>
        NotAvailable,
        AlreadyPrimary,
        Changed,
    }

    /// <summary>Reads and changes the Windows display layout through the CCD API.</summary>
    internal static class DisplayTopology
    {
        private const uint ApplyFlags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE;

        /// <summary>Lists monitors that are currently part of the desktop.</summary>
        public static List<DisplayMonitorInfo> GetActiveMonitors()
        {
            var active = Query(QDC_ONLY_ACTIVE_PATHS);
            var visitedTargets = new HashSet<(long, uint)>();
            var monitors = new List<DisplayMonitorInfo>();
            foreach (var path in active.Paths)
            {
                var target = path.TargetInfo;
                var sourceModeIndex = path.SourceInfo.ModeInfoIdx;
                if (sourceModeIndex >= active.Modes.Length
                    || !visitedTargets.Add((ToInt64(target.AdapterId), target.Id))
                    || !TryGetTargetName(target, out var name)
                    || string.IsNullOrEmpty(name.MonitorDevicePath))
                {
                    continue;
                }

                var source = active.Modes[sourceModeIndex].SourceMode;
                monitors.Add(new DisplayMonitorInfo(
                    name.MonitorDevicePath,
                    string.IsNullOrWhiteSpace(name.MonitorFriendlyDeviceName) ? null : name.MonitorFriendlyDeviceName,
                    new Rectangle(source.Position.X, source.Position.Y, (int)source.Width, (int)source.Height)
                ));
            }

            return monitors;
        }

        /// <summary>Captures the active layout so it can be put back with <see cref="Restore"/>.</summary>
        public static DisplaySnapshot Capture()
        {
            var active = Query(QDC_ONLY_ACTIVE_PATHS);
            var primaryIndex = FindPrimaryPathIndex(active.Paths, active.Modes);
            string primaryDevicePath = null;
            if (primaryIndex >= 0 && TryGetTargetName(active.Paths[primaryIndex].TargetInfo, out var name))
            {
                primaryDevicePath = name.MonitorDevicePath;
            }

            return new DisplaySnapshot(active.Paths, active.Modes, primaryDevicePath);
        }

        /// <summary>
        /// Makes an active monitor the primary display; monitors that aren't on the desktop are never turned on.
        /// <paramref name="bounds"/> receives its desktop rectangle in physical pixels.
        /// </summary>
        public static PrimaryDisplayResult MakePrimary(string devicePath, out Rectangle bounds)
        {
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                throw new ArgumentException("A monitor device path is required.", nameof(devicePath));
            }

            bounds = Rectangle.Empty;
            var active = Query(QDC_ONLY_ACTIVE_PATHS);
            var index = FindPathIndex(active.Paths, devicePath);
            if (index < 0)
            {
                return PrimaryDisplayResult.NotAvailable;
            }

            var sourceModeIndex = active.Paths[index].SourceInfo.ModeInfoIdx;
            if (sourceModeIndex >= active.Modes.Length)
            {
                throw new InvalidOperationException("The monitor has no source mode in the active display configuration.");
            }

            var source = active.Modes[sourceModeIndex].SourceMode;
            bounds = new Rectangle(0, 0, (int)source.Width, (int)source.Height);
            var offset = source.Position;
            if (offset.X == 0 && offset.Y == 0)
            {
                return PrimaryDisplayResult.AlreadyPrimary;
            }

            // Windows treats the source at the desktop origin as primary, so shift every source by the same offset.
            for (var i = 0; i < active.Modes.Length; i++)
            {
                if (active.Modes[i].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    active.Modes[i].SourceMode.Position.X -= offset.X;
                    active.Modes[i].SourceMode.Position.Y -= offset.Y;
                }
            }

            ThrowIfFailed(
                SetDisplayConfig((uint)active.Paths.Length, active.Paths, (uint)active.Modes.Length, active.Modes, ApplyFlags),
                nameof(SetDisplayConfig)
            );
            return PrimaryDisplayResult.Changed;
        }

        /// <summary>
        /// Puts back a captured layout. If it can't be applied any more (a monitor from it is gone), only its
        /// primary monitor is restored.
        /// </summary>
        public static void Restore(DisplaySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var error = SetDisplayConfig(
                (uint)snapshot.Paths.Length,
                snapshot.Paths,
                (uint)snapshot.Modes.Length,
                snapshot.Modes,
                ApplyFlags
            );
            if (error == ERROR_SUCCESS)
            {
                return;
            }

            BpmLog.WriteLine("[Display] Saved layout could not be reapplied (Win32 " + error + "); restoring the primary monitor only.");
            if (!string.IsNullOrEmpty(snapshot.PrimaryDevicePath))
            {
                MakePrimary(snapshot.PrimaryDevicePath, out _);
            }
        }

        private static (DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes) Query(uint flags)
        {
            // The layout can change between sizing the buffers and querying, which surfaces as ERROR_INSUFFICIENT_BUFFER.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ThrowIfFailed(GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount), nameof(GetDisplayConfigBufferSizes));
                var paths = new DisplayConfigPathInfo[pathCount];
                var modes = new DisplayConfigModeInfo[modeCount];
                var error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == ERROR_INSUFFICIENT_BUFFER)
                {
                    continue;
                }

                ThrowIfFailed(error, nameof(QueryDisplayConfig));
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return (paths, modes);
            }

            throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER, "QueryDisplayConfig kept reporting a changing display layout.");
        }

        private static int FindPathIndex(DisplayConfigPathInfo[] paths, string devicePath)
        {
            for (var i = 0; i < paths.Length; i++)
            {
                if (TryGetTargetName(paths[i].TargetInfo, out var name)
                    && string.Equals(name.MonitorDevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindPrimaryPathIndex(DisplayConfigPathInfo[] paths, DisplayConfigModeInfo[] modes)
        {
            for (var i = 0; i < paths.Length; i++)
            {
                var modeIndex = paths[i].SourceInfo.ModeInfoIdx;
                if (modeIndex < modes.Length
                    && modes[modeIndex].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                    && modes[modeIndex].SourceMode.Position.X == 0
                    && modes[modeIndex].SourceMode.Position.Y == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryGetTargetName(DisplayConfigPathTargetInfo target, out DisplayConfigTargetDeviceName name)
        {
            name = new DisplayConfigTargetDeviceName
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    Size = (uint)Marshal.SizeOf(typeof(DisplayConfigTargetDeviceName)),
                    AdapterId = target.AdapterId,
                    Id = target.Id,
                },
            };
            return DisplayConfigGetDeviceInfo(ref name) == ERROR_SUCCESS;
        }

        private static long ToInt64(Luid luid)
        {
            return ((long)luid.HighPart << 32) | luid.LowPart;
        }

        private static void ThrowIfFailed(int error, string api)
        {
            if (error != ERROR_SUCCESS)
            {
                throw new Win32Exception(error, api + " failed (Win32 " + error + ").");
            }
        }
    }
}
