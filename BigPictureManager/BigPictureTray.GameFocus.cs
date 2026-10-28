using System;
using System.Windows.Forms;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private readonly GuideButtonWatcher _guideWatcher = new GuideButtonWatcher();
        private ToolStripMenuItem _focusGameOnGuideMenuItem;
        private volatile bool _isFocusGameOnGuideDoublePress = Settings.Default.isFocusGameOnGuideDoublePress;

        private void InitializeGameFocus()
        {
            _focusGameOnGuideMenuItem = new ToolStripMenuItem(Resources.MenuFocusGameOnGuideDoublePress)
            {
                Checked = _isFocusGameOnGuideDoublePress,
            };
            _guideWatcher.DoublePressed += OnGuideDoublePressed;
            _guideWatcher.Start();
        }

        private void OnFocusGameOnGuideMenuItemClick(object sender, EventArgs e)
        {
            _isFocusGameOnGuideDoublePress = _focusGameOnGuideMenuItem.Checked;
            Settings.Default.isFocusGameOnGuideDoublePress = _isFocusGameOnGuideDoublePress;
            Settings.Default.Save();
        }

        // Runs on the watcher's thread, which ignores presses made while this handler is busy.
        private void OnGuideDoublePressed(object sender, EventArgs e)
        {
            if (!_isFocusGameOnGuideDoublePress)
            {
                return;
            }

            try
            {
                var game = SteamGameFocuser.FocusGameAfterGuideDoublePress();
                BpmLog.WriteLine(
                    game != null
                        ? "[Guide] Double press: focused " + game
                        : "[Guide] Double press: no running Steam game to focus, or it already had focus."
                );
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Guide] Focusing the Steam game failed: " + ex.Message);
            }
        }
    }
}
