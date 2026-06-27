using System;
using System.Threading;
using System.Windows.Forms;

namespace BigPictureManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            args = args ?? Array.Empty<string>();

            if (XboxGipPowerOff.TryParseServiceArgs(args, out var xboxServiceLogDirectory))
            {
                // Point the SYSTEM service at the launching user's log directory before it logs anything.
                BpmLog.UseLogDirectory(xboxServiceLogDirectory);
                Environment.Exit(XboxGipPowerOff.RunServiceMode());
                return;
            }

            // Elevated one-shot helpers: install/remove the persistent power-off service, then exit.
            if (HasArg(args, XboxGipPowerOff.InstallServiceArg))
            {
                Environment.Exit(RunServiceManagement(install: true));
                return;
            }

            if (HasArg(args, XboxGipPowerOff.UninstallServiceArg))
            {
                Environment.Exit(RunServiceManagement(install: false));
                return;
            }

            var exePath = SingleInstanceApplication.GetExecutablePath();
            Mutex singleInstanceMutex = null;
            var ownsMutex = SingleInstanceApplication.TryAcquireMutex(out singleInstanceMutex, out var isMutexFirst);
            var isAnotherInstanceRunning = SingleInstanceApplication.IsAnotherInstanceRunningForCurrentUser();
            var isDuplicateInstance = (ownsMutex && !isMutexFirst) || isAnotherInstanceRunning;

            if (isDuplicateInstance)
            {
                try
                {
                    AppNotificationSetup.EnsureRegistered(exePath);
                    AppNotificationSetup.ShowAlreadyRunningToast();
                    SingleInstanceApplication.TryActivateExistingInstance();
                }
                finally
                {
                    singleInstanceMutex?.Dispose();
                }

                return;
            }

            try
            {
                AppNotificationSetup.EnsureRegistered(exePath);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new BigPictureTray());
            }
            finally
            {
                if (ownsMutex && isMutexFirst && singleInstanceMutex != null)
                {
                    singleInstanceMutex.ReleaseMutex();
                    singleInstanceMutex.Dispose();
                }
            }
        }

        private static bool HasArg(string[] args, string name) =>
            Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static int RunServiceManagement(bool install)
        {
            try
            {
                if (install)
                {
                    XboxGipPowerOff.InstallService();
                }
                else
                {
                    XboxGipPowerOff.UninstallService();
                }

                return 0;
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Xbox] Service " + (install ? "install" : "uninstall") + " failed: " + ex.Message);
                return 1;
            }
        }
    }
}
