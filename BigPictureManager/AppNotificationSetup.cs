using System;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using BigPictureManager.Properties;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BigPictureManager
{
    /// <summary>
    /// Registers the application for Action Center toasts (AUMID + Start Menu shortcut) and shows notifications.
    /// </summary>
    internal static class AppNotificationSetup
    {
        private const string AppUserModelRegistryRelativePath = @"Software\Classes\AppUserModelId\";

        /// <summary>
        /// Ensures the process AUMID, registry entry, and Start Menu shortcut exist so toasts can be displayed.
        /// </summary>
        internal static void EnsureRegistered(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                BpmLog.WriteLine("[Error] [Toast] Cannot register notifications: executable path is empty.");
                return;
            }

            exePath = Path.GetFullPath(exePath);
            ApplyProcessAppUserModelId();

            if (!IsAppUserModelIdRegisteredInRegistry())
            {
                RegisterAppUserModelIdInRegistry(exePath);
            }

            EnsureStartMenuShortcut(exePath);
        }

        /// <summary>
        /// Shows a toast when another instance is launched while the app is already running.
        /// </summary>
        internal static void ShowAlreadyRunningToast()
        {
            try
            {
                var logoPath = GetNotificationLogoPath();
                var logoUri = File.Exists(logoPath) ? new Uri(logoPath).AbsoluteUri : null;
                var xml = BuildToastXml(Resources.MsgAlreadyRunning, logoUri);

                var document = new XmlDocument();
                document.LoadXml(xml);

                var toast = new ToastNotification(document)
                {
                    Tag = AppConstants.AlreadyRunningToastTag,
                    Group = AppConstants.AlreadyRunningToastGroup,
                };

                var notifier = ToastNotificationManager.CreateToastNotifier(AppConstants.AppUserModelId);
                notifier.Show(toast);
                BpmLog.WriteLine("[Toast] Displayed \"already running\" notification.");

                Thread.Sleep(AppConstants.AlreadyRunningToastVisibleMs);
                RemoveAlreadyRunningToastFromHistory();
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Toast] Failed to show notification: " + ex.Message);
            }
        }

        private static void RemoveAlreadyRunningToastFromHistory()
        {
            try
            {
                ToastNotificationManager.History.Remove(
                    AppConstants.AlreadyRunningToastTag,
                    AppConstants.AlreadyRunningToastGroup,
                    AppConstants.AppUserModelId
                );
                BpmLog.WriteLine("[Toast] Removed \"already running\" notification from Action Center history.");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Toast] Failed to remove notification from history: " + ex.Message);
            }
        }

        internal static string GetStartMenuShortcutPath()
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            return Path.Combine(startMenu, "Programs", AppConstants.StartMenuShortcutFileName);
        }

        private static void ApplyProcessAppUserModelId()
        {
            var hr = NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppConstants.AppUserModelId);
            if (hr != 0)
            {
                BpmLog.WriteLine(
                    "[Error] [Toast] SetCurrentProcessExplicitAppUserModelID failed with HRESULT 0x"
                        + hr.ToString("X8")
                );
            }
        }

        private static bool IsAppUserModelIdRegisteredInRegistry()
        {
            var keyPath = AppUserModelRegistryRelativePath + AppConstants.AppUserModelId;
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
            {
                return key != null;
            }
        }

        private static void RegisterAppUserModelIdInRegistry(string exePath)
        {
            var keyPath = AppUserModelRegistryRelativePath + AppConstants.AppUserModelId;
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (key == null)
                {
                    BpmLog.WriteLine("[Error] [Toast] Failed to create AppUserModelID registry key.");
                    return;
                }

                key.SetValue(null, Resources.NotificationTitle, RegistryValueKind.String);

                var iconPath = Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, "bpman3.ico");
                if (File.Exists(iconPath))
                {
                    key.SetValue("IconUri", iconPath, RegistryValueKind.String);
                }

                BpmLog.WriteLine("[Toast] Registered AppUserModelID in the registry.");
            }
        }

        private static void EnsureStartMenuShortcut(string exePath)
        {
            var shortcutPath = GetStartMenuShortcutPath();
            if (IsStartMenuShortcutValid(shortcutPath, exePath))
            {
                return;
            }

            var workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            var iconPath = Path.Combine(workingDirectory, "bpman3.ico");
            var iconLocation = File.Exists(iconPath) ? iconPath + ",0" : exePath + ",0";

            if (
                ShellShortcutHelper.TryCreateOrUpdate(
                    shortcutPath,
                    exePath,
                    workingDirectory,
                    Resources.NotificationTitle,
                    iconLocation,
                    AppConstants.AppUserModelId
                )
            )
            {
                BpmLog.WriteLine("[Toast] Created or updated Start Menu shortcut: " + shortcutPath);
            }
        }

        private static bool IsStartMenuShortcutValid(string shortcutPath, string exePath)
        {
            if (!File.Exists(shortcutPath))
            {
                return false;
            }

            if (
                !ShellShortcutHelper.TryReadAppUserModelId(shortcutPath, out var appId)
                || !string.Equals(appId, AppConstants.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            if (!ShellShortcutHelper.TryReadTargetPath(shortcutPath, out var targetPath))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(targetPath),
                Path.GetFullPath(exePath),
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static string GetNotificationLogoPath()
        {
            var baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            return Path.Combine(baseDirectory, AppConstants.NotificationLogoFileName);
        }

        private static string BuildToastXml(string message, string logoFileUri)
        {
            var builder = new StringBuilder();
            builder.Append("<toast><visual><binding template=\"ToastGeneric\">");
            builder.Append("<text>").Append(EscapeXml(message)).Append("</text>");
            if (!string.IsNullOrEmpty(logoFileUri))
            {
                builder
                    .Append("<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"")
                    .Append(EscapeXml(logoFileUri))
                    .Append("\"/>");
            }

            builder.Append("</binding></visual></toast>");
            return builder.ToString();
        }

        private static string EscapeXml(string value)
        {
            return SecurityElement.Escape(value) ?? string.Empty;
        }
    }
}
