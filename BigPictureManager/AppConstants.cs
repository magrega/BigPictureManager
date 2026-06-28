namespace BigPictureManager
{
    internal static class AppConstants
    {
        /// <summary>Explicit AppUserModelID for Action Center toasts (must match Start Menu shortcut).</summary>
        internal const string AppUserModelId = "magrega.BigPictureManager";

        internal const string StartMenuShortcutFileName = "Big Picture Manager.lnk";

        internal const string NotificationLogoFileName = "bpman3.png";

        internal const string AlreadyRunningToastTag = "AlreadyRunning";

        internal const string AlreadyRunningToastGroup = "SingleInstance";

        internal const int AlreadyRunningToastVisibleMs = 3000;

        internal const string SingleInstanceMutexPrefix = @"Local\BigPictureManager-";

        /// <summary>Substring matched (case-insensitively) against the opened window's UIA name.</summary>
        internal const string BigPictureWindowNameFragment = "Big Picture";

        /// <summary>The Big Picture window must belong to a Steam process; its name starts with this (steam.exe / steamwebhelper.exe).</summary>
        internal const string SteamProcessNamePrefix = "steam";

        internal const string ProjectReadmeUrl =
            "https://github.com/magrega/BigPictureManager/blob/master/README.md";

        internal const int UacCancelledWin32Error = 1223;
    }
}
