using System;
using System.Windows.Forms;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private void SyncLaunchOnStartMenuState()
        {
            _isAutoStart = ElevatedLogonStartupTask.IsRegistered();
            if (_launchOnStartMenuItem != null)
            {
                _launchOnStartMenuItem.Checked = _isAutoStart;
            }

            if (Settings.Default.isAutoStart != _isAutoStart)
            {
                Settings.Default.isAutoStart = _isAutoStart;
                Settings.Default.Save();
            }
        }

        private void OnLaunchOnStartMenuItemClick(object sender, EventArgs e)
        {
            if (_launchOnStartMenuItem.Checked)
            {
                var exePath = Application.ExecutablePath;
                if (string.IsNullOrEmpty(exePath))
                {
                    _launchOnStartMenuItem.Checked = false;
                    MessageBox.Show(
                        Resources.MsgCouldNotDetermineAppPath,
                        Resources.AppTitle,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }

                if (WindowsIdentityHelper.IsAdministrator())
                {
                    if (!ElevatedLogonStartupTask.TryRegister(exePath, out var registerError))
                    {
                        _launchOnStartMenuItem.Checked = false;
                        MessageBox.Show(
                            string.Format(Resources.MsgStartupTaskCreateFailed, registerError),
                            Resources.AppTitle,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
                        return;
                    }

                    CompleteStartupTaskInstallSuccess();
                    return;
                }

                Settings.Default.pendingInstallElevatedStartupTask = true;
                Settings.Default.Save();
                BpmLog.WriteLine(
                    "[Startup] Launch on system start enabled; requesting elevated restart to create the scheduled task."
                );
                RequestElevatedRestart(
                    "[Startup] Restarting elevated to create the scheduled startup task.",
                    onUacCancelled: () =>
                    {
                        ClearPendingStartupTaskInstall();
                        UncheckLaunchOnStartMenuItem();
                    },
                    onRestartFailed: () =>
                    {
                        ClearPendingStartupTaskInstall();
                        UncheckLaunchOnStartMenuItem();
                    }
                );
                return;
            }

            ClearPendingStartupTaskInstall();

            if (!WindowsIdentityHelper.IsAdministrator() && ElevatedLogonStartupTask.IsRegistered())
            {
                Settings.Default.pendingUnregisterElevatedStartupTask = true;
                Settings.Default.Save();
                BpmLog.WriteLine(
                    "[Startup] Launch on system start disabled; requesting elevated restart to remove the scheduled task."
                );
                RequestElevatedRestart(
                    "[Startup] Restarting elevated to remove the scheduled startup task.",
                    onUacCancelled: () =>
                    {
                        ClearPendingStartupTaskUnregister();
                        CheckLaunchOnStartMenuItem();
                    },
                    onRestartFailed: () =>
                    {
                        ClearPendingStartupTaskUnregister();
                        CheckLaunchOnStartMenuItem();
                    }
                );
                return;
            }

            if (!CompleteStartupTaskUnregister())
            {
                CheckLaunchOnStartMenuItem();
            }
        }

        private void TryCompletePendingElevatedStartupTaskInstall()
        {
            if (!Settings.Default.pendingInstallElevatedStartupTask)
            {
                return;
            }

            if (!WindowsIdentityHelper.IsAdministrator())
            {
                BpmLog.WriteLine(
                    "[Startup] Pending scheduled-task install was not completed (application is not elevated)."
                );
                ClearPendingStartupTaskInstall();
                UncheckLaunchOnStartMenuItem();
                return;
            }

            var exePath = Application.ExecutablePath;
            if (string.IsNullOrEmpty(exePath))
            {
                BpmLog.WriteLine("[Error] [Startup] Pending scheduled-task install failed: application path is empty.");
                ClearPendingStartupTaskInstall();
                UncheckLaunchOnStartMenuItem();
                return;
            }

            BpmLog.WriteLine("[Startup] Completing pending scheduled-task install (elevated).");
            if (!ElevatedLogonStartupTask.TryRegister(exePath, out var registerError))
            {
                ClearPendingStartupTaskInstall();
                UncheckLaunchOnStartMenuItem();
                MessageBox.Show(
                    string.Format(Resources.MsgStartupTaskCreateFailed, registerError),
                    Resources.AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            CompleteStartupTaskInstallSuccess();
        }

        private void TryCompletePendingElevatedStartupTaskUnregister()
        {
            if (!Settings.Default.pendingUnregisterElevatedStartupTask)
            {
                return;
            }

            if (!WindowsIdentityHelper.IsAdministrator())
            {
                BpmLog.WriteLine(
                    "[Startup] Pending scheduled-task removal was not completed (application is not elevated)."
                );
                ClearPendingStartupTaskUnregister();
                CheckLaunchOnStartMenuItem();
                return;
            }

            BpmLog.WriteLine("[Startup] Completing pending scheduled-task removal (elevated).");
            if (!CompleteStartupTaskUnregister())
            {
                CheckLaunchOnStartMenuItem();
            }
        }

        /// <summary>
        /// Deletes the logon scheduled task and clears autostart settings. Returns false on failure.
        /// </summary>
        private bool CompleteStartupTaskUnregister()
        {
            if (!ElevatedLogonStartupTask.TryUnregister(out var unregisterError))
            {
                BpmLog.WriteLine("[Error] [Startup] Failed to remove scheduled task: " + unregisterError);
                MessageBox.Show(
                    string.Format(Resources.MsgStartupTaskRemoveFailed, unregisterError),
                    Resources.AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return false;
            }

            ClearPendingStartupTaskUnregister();
            _isAutoStart = false;
            Settings.Default.isAutoStart = false;
            Settings.Default.Save();
            UncheckLaunchOnStartMenuItem();
            BpmLog.WriteLine("[Startup] Launch on system start is disabled.");
            return true;
        }

        private void CompleteStartupTaskInstallSuccess()
        {
            ClearPendingStartupTaskInstall();
            _isAutoStart = true;
            Settings.Default.isAutoStart = true;
            Settings.Default.Save();
            CheckLaunchOnStartMenuItem();
            BpmLog.WriteLine("[Startup] Launch on system start is enabled.");
        }

        private static void ClearPendingStartupTaskInstall()
        {
            if (!Settings.Default.pendingInstallElevatedStartupTask)
            {
                return;
            }

            Settings.Default.pendingInstallElevatedStartupTask = false;
            Settings.Default.Save();
        }

        private static void ClearPendingStartupTaskUnregister()
        {
            if (!Settings.Default.pendingUnregisterElevatedStartupTask)
            {
                return;
            }

            Settings.Default.pendingUnregisterElevatedStartupTask = false;
            Settings.Default.Save();
        }

        private void UncheckLaunchOnStartMenuItem()
        {
            if (_launchOnStartMenuItem != null)
            {
                _launchOnStartMenuItem.Checked = false;
            }
        }

        private void CheckLaunchOnStartMenuItem()
        {
            if (_launchOnStartMenuItem != null)
            {
                _launchOnStartMenuItem.Checked = true;
            }
        }
    }
}
