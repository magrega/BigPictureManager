using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private volatile bool _steamMonitorDisableBusy;

        /// <summary>
        /// Closes Steam, clears Big Picture's Preferred Display in config.vdf, relaunches Steam, and toasts the result.
        /// </summary>
        private async void DisableSteamPreferredMonitor()
        {
            if (_steamMonitorDisableBusy)
            {
                return;
            }

            _steamMonitorDisableBusy = true;
            ProgressOverlay overlay = null;
            try
            {
                overlay = new ProgressOverlay(Resources.MsgSteamMonitorWorking);
                overlay.Show();
                overlay.Refresh();

                var message = await Task.Run(() => DisableSteamPreferredMonitorCore(status => overlay.SetStatus(status)))
                    .ConfigureAwait(true);
                AppNotificationSetup.ShowNotification(message);
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Steam] Disable preferred monitor failed: " + ex.Message);
                AppNotificationSetup.ShowNotification(
                    string.Format(Resources.MsgSteamMonitorFailed, SteamPreferredMonitor.DescribeFailure(ex))
                );
            }
            finally
            {
                if (overlay != null)
                {
                    overlay.Close();
                    overlay.Dispose();
                }

                _steamMonitorDisableBusy = false;
            }
        }

        private static string DisableSteamPreferredMonitorCore(Action<string> reportStatus)
        {
            reportStatus(Resources.MsgSteamMonitorFindingSteam);
            var steamPath = SteamPreferredMonitor.TryGetSteamInstallPath();
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
            {
                throw new FileNotFoundException("Steam install folder was not found.");
            }

            var configPath = SteamPreferredMonitor.GetConfigPath(steamPath);
            reportStatus(Resources.MsgSteamMonitorReadingSetting);
            var status = SteamPreferredMonitor.GetStatus(configPath, out var currentValue);
            if (status == SteamPreferredMonitorStatus.NotFound)
            {
                throw new InvalidOperationException("The BigPicture Monitor setting was not found in config.vdf.");
            }

            if (status == SteamPreferredMonitorStatus.AlreadyDisabled)
            {
                BpmLog.WriteLine("[Steam] Preferred Display is already disabled.");
                return Resources.MsgSteamMonitorAlreadyDisabled;
            }

            BpmLog.WriteLine("[Steam] Preferred Display is set to \"" + currentValue + "\"; clearing it.");
            var wasRunning = SteamPreferredMonitor.IsSteamClientRunning();
            if (wasRunning)
            {
                reportStatus(Resources.MsgSteamMonitorClosingSteam);
                SteamPreferredMonitor.ShutdownSteamClient(steamPath, CancellationToken.None);
            }

            reportStatus(Resources.MsgSteamMonitorEditingConfig);
            try
            {
                SteamPreferredMonitor.ClearPreferredMonitor(configPath);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new IOException(ex.Message, ex);
            }

            if (wasRunning)
            {
                reportStatus(Resources.MsgSteamMonitorLaunchingSteam);
                try
                {
                    SteamPreferredMonitor.LaunchSteam(steamPath);
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine("[Error] [Steam] Setting cleared but Steam failed to relaunch: " + ex.Message);
                    return Resources.MsgSteamMonitorClearedButRelaunchFailed;
                }
            }

            return Resources.MsgSteamMonitorDisabled;
        }
    }
}
