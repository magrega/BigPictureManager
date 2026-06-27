using System;
using System.Windows.Forms;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private void SyncLaunchOnStartMenuState()
        {
            if (_launchOnStartMenuItem != null)
            {
                _launchOnStartMenuItem.Checked = StartupRegistration.IsEnabled();
            }
        }

        private void OnLaunchOnStartMenuItemClick(object sender, EventArgs e)
        {
            if (_launchOnStartMenuItem.Checked)
            {
                StartupRegistration.Enable(Application.ExecutablePath);
            }
            else
            {
                StartupRegistration.Disable();
            }
        }

        /// <summary>
        /// One-time migration from the old elevated logon scheduled task to the per-user Run entry, so
        /// autostart no longer needs the app to run elevated. The Run entry is set immediately to preserve
        /// the user's preference; the legacy task is removed when we have the rights to (it launches us
        /// elevated at logon, so it self-cleans on the next boot if not now).
        /// </summary>
        private static void MigrateLegacyStartupTask()
        {
            try
            {
                if (!ElevatedLogonStartupTask.IsRegistered())
                {
                    return;
                }

                StartupRegistration.Enable(Application.ExecutablePath);

                if (WindowsIdentityHelper.IsAdministrator())
                {
                    ElevatedLogonStartupTask.TryUnregister(out _);
                    BpmLog.WriteLine("[Startup] Migrated autostart from scheduled task to per-user Run entry.");
                }
                else
                {
                    BpmLog.WriteLine("[Startup] Legacy startup task present; it will be removed on the next elevated start.");
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Startup] Legacy startup migration failed: " + ex.Message);
            }
        }
    }
}
