using System;
using System.Runtime.InteropServices;

namespace BigPictureManager
{
    /// <summary>P/Invoke surface for the Windows CCD (Connecting and Configuring Displays) API.</summary>
    internal static class NativeDisplayConfigApi
    {
        internal const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

        internal const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        internal const uint SDC_APPLY = 0x80;
        internal const uint SDC_SAVE_TO_DATABASE = 0x200;
        internal const uint SDC_ALLOW_CHANGES = 0x400;

        internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigRational
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfig2DRegion
        {
            public uint Cx;
            public uint Cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PointL
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigPathSourceInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIdx;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigPathTargetInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIdx;
            public uint OutputTechnology;
            public uint Rotation;
            public uint Scaling;
            public DisplayConfigRational RefreshRate;
            public uint ScanLineOrdering;
            public int TargetAvailable;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigPathInfo
        {
            public DisplayConfigPathSourceInfo SourceInfo;
            public DisplayConfigPathTargetInfo TargetInfo;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigVideoSignalInfo
        {
            public ulong PixelRate;
            public DisplayConfigRational HSyncFreq;
            public DisplayConfigRational VSyncFreq;
            public DisplayConfig2DRegion ActiveSize;
            public DisplayConfig2DRegion TotalSize;
            public uint VideoStandard;
            public uint ScanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigSourceMode
        {
            public uint Width;
            public uint Height;
            public uint PixelFormat;
            public PointL Position;
        }

        // Native DISPLAYCONFIG_MODE_INFO is a 64-byte struct whose tail is a union; the target mode is its largest member.
        [StructLayout(LayoutKind.Explicit)]
        internal struct DisplayConfigModeInfo
        {
            [FieldOffset(0)]
            public uint InfoType;

            [FieldOffset(4)]
            public uint Id;

            [FieldOffset(8)]
            public Luid AdapterId;

            [FieldOffset(16)]
            public DisplayConfigVideoSignalInfo TargetMode;

            [FieldOffset(16)]
            public DisplayConfigSourceMode SourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigDeviceInfoHeader
        {
            public uint Type;
            public uint Size;
            public Luid AdapterId;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DisplayConfigTargetDeviceName
        {
            public DisplayConfigDeviceInfoHeader Header;
            public uint Flags;
            public uint OutputTechnology;
            public ushort EdidManufactureId;
            public ushort EdidProductCodeId;
            public uint ConnectorInstance;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string MonitorFriendlyDeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string MonitorDevicePath;
        }

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DisplayConfigPathInfo[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DisplayConfigModeInfo[] modeInfoArray,
            IntPtr currentTopologyId
        );

        [DllImport("user32.dll")]
        internal static extern int SetDisplayConfig(
            uint numPathArrayElements,
            [In] DisplayConfigPathInfo[] pathArray,
            uint numModeInfoArrayElements,
            [In] DisplayConfigModeInfo[] modeInfoArray,
            uint flags
        );

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);
    }
}
