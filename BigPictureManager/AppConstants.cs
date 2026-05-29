namespace BigPictureManager
{
    internal static class AppConstants
    {
        /// <summary>Explicit AppUserModelID for Action Center toasts (must match Start Menu shortcut).</summary>
        internal const string AppUserModelId = "magrega.BigPictureManager";

        internal const string StartMenuShortcutFileName = "Big Picture Manager.lnk";

        internal const string NotificationLogoFileName = "bpman3.png";

        internal const string SingleInstanceMutexPrefix = @"Local\BigPictureManager-";

        internal const string BigPictureWindowName = "Steam Big Picture Mode";

        internal const string ProjectReadmeUrl =
            "https://github.com/magrega/BigPictureManager/blob/master/README.md";

        internal const int UacCancelledWin32Error = 1223;
    }
}
