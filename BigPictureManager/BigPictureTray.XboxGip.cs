using System;
using System.Drawing;
using System.Windows.Forms;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private void ApplyXboxGipPowerOffMenuState()
        {
            if (_xboxGipPowerOffMenuItem == null)
            {
                return;
            }

            _xboxGipPowerOffMenuItem.Text = Resources.MenuWirelessXboxController;
            _xboxGipPowerOffMenuItem.Enabled = true;

            if (WindowsIdentityHelper.IsAdministrator())
            {
                _xboxGipPowerOffMenuItem.ForeColor = SystemColors.ControlText;
                _xboxGipPowerOffMenuItem.CheckOnClick = true;
                _xboxGipPowerOffMenuItem.Checked = Settings.Default.isPowerOffXboxGipOnBpClose;
                _xboxGipPowerOffMenuItem.ToolTipText = Resources.TooltipXboxGipPowerOff;
            }
            else
            {
                _xboxGipPowerOffMenuItem.ForeColor = SystemColors.GrayText;
                _xboxGipPowerOffMenuItem.CheckOnClick = false;
                _xboxGipPowerOffMenuItem.Checked = false;
                _xboxGipPowerOffMenuItem.ToolTipText = Resources.TooltipXboxGipRequestElevation;
            }
        }

        private void OnXboxGipPowerOffMenuItemClick(object sender, EventArgs e)
        {
            if (WindowsIdentityHelper.IsAdministrator())
            {
                Settings.Default.isPowerOffXboxGipOnBpClose = _xboxGipPowerOffMenuItem.Checked;
                SchedulePersist();
                BpmLog.WriteLine(
                    "[Xbox] Wireless controller power-off "
                        + (_xboxGipPowerOffMenuItem.Checked ? "enabled" : "disabled")
                        + " in settings."
                );
                return;
            }

            Settings.Default.pendingEnableXboxGipPowerOff = true;
            Settings.Default.Save();
            BpmLog.WriteLine("[Xbox] Requesting elevated restart to enable wireless controller power-off.");
            RequestElevatedRestart(
                "[Xbox] Restarting elevated to enable wireless controller power-off.",
                onUacCancelled: ClearPendingXboxGipPowerOffEnable
            );
        }

        private void TryCompletePendingXboxGipPowerOffEnable()
        {
            if (!Settings.Default.pendingEnableXboxGipPowerOff)
            {
                return;
            }

            if (!WindowsIdentityHelper.IsAdministrator())
            {
                BpmLog.WriteLine(
                    "[Xbox] Pending wireless controller power-off enable was not completed (application is not elevated)."
                );
                ClearPendingXboxGipPowerOffEnable();
                ApplyXboxGipPowerOffMenuState();
                return;
            }

            var wasEnabledBefore = Settings.Default.isPowerOffXboxGipOnBpClose;
            Settings.Default.isPowerOffXboxGipOnBpClose = true;
            ClearPendingXboxGipPowerOffEnable();
            ApplyXboxGipPowerOffMenuState();
            BpmLog.WriteLine(
                wasEnabledBefore
                    ? "[Xbox] Wireless controller power-off re-enabled after elevated restart (was already on in settings)."
                    : "[Xbox] Wireless controller power-off enabled after elevated restart."
            );
        }

        private static void ClearPendingXboxGipPowerOffEnable()
        {
            if (!Settings.Default.pendingEnableXboxGipPowerOff)
            {
                return;
            }

            Settings.Default.pendingEnableXboxGipPowerOff = false;
            Settings.Default.Save();
        }
    }
}
