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
            if (
                XboxGipPowerOff.TryParseServiceArgs(
                    args ?? Array.Empty<string>(),
                    out var xboxPowerOffTargetIndex,
                    out var xboxExplicitDeviceIds
                )
            )
            {
                Environment.Exit(
                    XboxGipPowerOff.RunServiceMode(xboxPowerOffTargetIndex, xboxExplicitDeviceIds)
                );
                return;
            }

            var exePath = SingleInstanceApplication.GetExecutablePath();
            Mutex singleInstanceMutex = null;
            var ownsMutex = SingleInstanceApplication.TryAcquireMutex(out singleInstanceMutex, out var isFirstInstance);

            if (ownsMutex && !isFirstInstance)
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
                if (ownsMutex && isFirstInstance && singleInstanceMutex != null)
                {
                    singleInstanceMutex.ReleaseMutex();
                    singleInstanceMutex.Dispose();
                }
            }
        }
    }
}
