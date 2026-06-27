using System;
using Microsoft.Win32;

namespace BigPictureManager
{
    /// <summary>
    /// Per-user "launch at logon" via HKCU\...\Run. Runs at the user's normal level — no admin, no task.
    /// </summary>
    internal static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BigPictureManager";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key?.GetValue(ValueName) is string;
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Startup] Could not read the Run key: " + ex.Message);
                return false;
            }
        }

        public static void Enable(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    key.SetValue(ValueName, "\"" + exePath + "\"");
                }

                BpmLog.WriteLine("[Startup] Launch on system start enabled (per-user Run entry).");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Startup] Could not enable autostart: " + ex.Message);
            }
        }

        public static void Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    key?.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                BpmLog.WriteLine("[Startup] Launch on system start disabled.");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Startup] Could not disable autostart: " + ex.Message);
            }
        }
    }
}
