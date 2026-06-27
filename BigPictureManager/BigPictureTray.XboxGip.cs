using System;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        // The "power off on exit" toggle is only meaningful when the global service is installed; otherwise
        // there is nothing privileged to trigger, so the toggle is disabled.
        private void ApplyXboxGipPowerOffMenuState()
        {
            if (_xboxGipPowerOffMenuItem == null)
            {
                return;
            }

            _xboxGipPowerOffMenuItem.Text = Resources.MenuWirelessXboxController;
            _xboxGipPowerOffMenuItem.Enabled = _xboxServiceInstalled;
            _xboxGipPowerOffMenuItem.Checked = _xboxServiceInstalled && Settings.Default.isPowerOffXboxGipOnBpClose;
        }

        private void OnXboxGipPowerOffMenuItemClick(object sender, EventArgs e)
        {
            Settings.Default.isPowerOffXboxGipOnBpClose = _xboxGipPowerOffMenuItem.Checked;
            SchedulePersist();
            BpmLog.WriteLine(
                "[Xbox] Wireless controller power-off "
                    + (_xboxGipPowerOffMenuItem.Checked ? "enabled" : "disabled")
                    + " in settings."
            );
        }
    }
}
