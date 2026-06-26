using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal static class ElevatedProcessLauncher
    {
        internal static void RestartElevated(
            string logMessage,
            Action shutdown,
            Action onUacCancelled = null,
            Action onRestartFailed = null
        )
        {
            try
            {
                var exePath = Application.ExecutablePath;
                if (string.IsNullOrEmpty(exePath))
                {
                    BpmLog.WriteLine("[Error] [Main] Cannot restart elevated: application path is empty.");
                    onRestartFailed?.Invoke();
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = AppConstants.ElevatedRestartArg,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                Process.Start(psi);
                BpmLog.WriteLine(logMessage);
                if (shutdown == null)
                {
                    throw new ArgumentNullException(nameof(shutdown));
                }

                shutdown();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == AppConstants.UacCancelledWin32Error)
            {
                BpmLog.WriteLine("[Main] UAC prompt dismissed; elevated restart was not performed.");
                onUacCancelled?.Invoke();
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Failed to restart elevated: " + ex.Message);
                onRestartFailed?.Invoke();
                MessageBox.Show(
                    string.Format(Resources.MsgRestartAsAdminFailed, ex.Message),
                    Resources.AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
    }
}
