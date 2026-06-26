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
                isFirstInstance = false;
                return false;
            }
        }

        /// <summary>
        /// Returns true when another UI process with the same name is already running for the current user session.
        /// Used when the named mutex is unavailable across elevation levels (elevated autostart vs normal manual launch).
        /// </summary>
        internal static bool IsAnotherInstanceRunningForCurrentUser()
        {
            return IsAnotherInstanceRunningForCurrentUser(log: true);
        }

        /// <summary>
        /// Waits until no other same-session instance is running, or the timeout elapses. Used by an
        /// elevated restart to let the previous (non-elevated) instance exit before it takes over.
        /// </summary>
        internal static void WaitForOtherInstancesToExit(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAnotherInstanceRunningForCurrentUser(log: false))
                {
                    return;
                }

                Thread.Sleep(100);
            }

            BpmLog.WriteLine("[Main] Previous instance still present after wait; elevated restart proceeding anyway.");
        }

        private static bool IsAnotherInstanceRunningForCurrentUser(bool log)
        {
            var current = Process.GetCurrentProcess();
            var currentSessionId = current.SessionId;
            var currentProcessId = current.Id;
            var processName = current.ProcessName;

            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.Id == currentProcessId || process.SessionId != currentSessionId)
                    {
                        continue;
                    }

                    if (log)
                    {
                        BpmLog.WriteLine(
                            "[Main] Another instance is already running (PID "
                                + process.Id
                                + ", session "
                                + process.SessionId
                                + ")."
                        );
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine(
                        "[Error] [Main] Could not inspect process PID "
                            + process.Id
                            + ": "
                            + ex.Message
                    );
                }
                finally
                {
                    process.Dispose();
                }
            }

            return false;
        }

        internal static void TryActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var currentSessionId = current.SessionId;
                var processName = current.ProcessName;
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (process.Id == current.Id || process.SessionId != currentSessionId)
                        {
                            continue;
                        }

                        var handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            NativeMethods.SetForegroundWindow(handle);
                            BpmLog.WriteLine("[Main] Brought existing instance to the foreground.");
                        }

                        return;
                    }
                    finally
                    {
                        process.Dispose();
                    }
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
