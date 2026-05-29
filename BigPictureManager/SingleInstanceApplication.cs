using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading;

namespace BigPictureManager
{
    /// <summary>
    /// Ensures one UI instance per user and activates an existing instance when possible.
    /// </summary>
    internal static class SingleInstanceApplication
    {
        internal static bool TryAcquireMutex(out Mutex mutex, out bool isFirstInstance)
        {
            mutex = null;
            isFirstInstance = false;

            try
            {
                var mutexName = AppConstants.SingleInstanceMutexPrefix + GetCurrentUserSid();
                mutex = new Mutex(true, mutexName, out isFirstInstance);
                return true;
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Could not create single-instance mutex: " + ex.Message);
                isFirstInstance = true;
                return false;
            }
        }

        internal static void TryActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var processName = current.ProcessName;
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        NativeMethods.SetForegroundWindow(handle);
                        BpmLog.WriteLine("[Main] Brought existing instance to the foreground.");
                    }

                    break;
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Failed to activate existing instance: " + ex.Message);
            }
        }

        internal static string GetExecutablePath()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(location) ? null : location;
        }

        private static string GetCurrentUserSid()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return identity.User?.Value ?? Environment.UserName;
                }
            }
            catch
            {
                return Environment.UserName;
            }
        }
    }
}
