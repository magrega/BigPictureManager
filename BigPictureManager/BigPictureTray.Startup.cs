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
    }
}
