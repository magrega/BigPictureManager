using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BigPictureManager.Properties;
using Windows.Devices.Radios;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private void HandleNightLightOnBigPictureOpen()
        {
            if (!_nightLight.Supported)
            {
                BpmLog.WriteLine("[NightLight] Night light isn’t supported on this machine.");
                return;
            }

            BpmLog.WriteLine("[NightLight] Night light is supported on this machine.");
            BpmLog.WriteLine("[NightLight] Current state: " + (_nightLight.Enabled ? "On" : "Off"));
            _nightLight.DisableNightLight();
            _restoreNightLightAfterBigPicture = true;
        }

        private void OnBigPictureOpened(object sender, EventArgs e)
        {
            BpmLog.WriteLine("[Main] Steam Big Picture Mode started!");
            _bigPictureActive = true;
            _uiContext.Post(
                _ =>
                {
                    if (_isTurnOffNightLightOnBpStart)
                    {
                        HandleNightLightOnBigPictureOpen();
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[NightLight] Turn off on Big Picture start is disabled in the menu; skipping."
                        );
                    }

                    if (_isPauseMediaOnBpStart)
                    {
                        _ = Task.Run(async () => await SystemMediaPause.PauseAllPlayingAsync());
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[Media] Pause on Big Picture start is disabled in the menu; skipping."
                        );
                    }

                    _prevDevice = _audio.GetCurrentDefault();
                    if (_selectedDevice != null && !string.IsNullOrWhiteSpace(_selectedDevice.Id))
                    {
                        _audio.SetDefault(_selectedDevice, "Big Picture opened");
                    }
                },
                null
            );

            DiscoverAndDimXboxControllers();
        }

        private async void OnBigPictureClosed(object sender, EventArgs e)
        {
            // async void UIA callback: a leaked exception would crash the process, so guard the whole body.
            try
            {
                BpmLog.WriteLine("[Main] Steam Big Picture Mode closed!");
                _bigPictureActive = false;
                _uiContext.Post(_ => _xboxRedimTimer.Stop(), null);
                _uiContext.Post(
                    _ =>
                    {
                        if (_prevDevice != null && !string.IsNullOrWhiteSpace(_prevDevice.Id))
                        {
                            _audio.SetDefault(
                                _prevDevice,
                                "Big Picture closed (restore previous default)"
                            );
                        }

                        if (_restoreNightLightAfterBigPicture)
                        {
                            try
                            {
                                _nightLight.RestoreNightLight();
                                BpmLog.WriteLine("[NightLight] Night Light restored on Big Picture exit.");
                            }
                            catch (Exception ex)
                            {
                                BpmLog.WriteLine(
                                    "[Error] [NightLight] Restore on Big Picture exit failed: " + ex.Message
                                );
                            }
                            finally
                            {
                                _restoreNightLightAfterBigPicture = false;
                            }
                        }
                    },
                    null
                );

                if (_isTurnOffBt)
                {
                    await _bluetooth.SetStateAsync(RadioState.Off);
                }

                List<ulong> xboxGipSnapshot = null;
                lock (_xboxGipIdsSync)
                {
                    if (_xboxGipDeviceIdsFromLastBpOpen != null && _xboxGipDeviceIdsFromLastBpOpen.Count > 0)
                    {
                        xboxGipSnapshot = new List<ulong>(_xboxGipDeviceIdsFromLastBpOpen);
                    }

                    _xboxGipDeviceIdsFromLastBpOpen = null;
                }

                // Power-off goes through the global service (any user, no elevation). If it's off or the
                // service isn't installed, just restore the dimmed guide LED instead.
                if (Settings.Default.isPowerOffXboxGipOnBpClose && XboxGipPowerOff.IsServiceInstalled())
                {
                    _ = Task.Run(() => XboxGipPowerOff.TriggerPowerOff());
                }
                else if (xboxGipSnapshot != null && xboxGipSnapshot.Count > 0)
                {
                    _ = Task.Run(() => RestoreXboxControllerLedAfterBigPictureExit(xboxGipSnapshot));
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Big Picture close handling failed: " + ex.Message);
            }
        }

        private static void RestoreXboxControllerLedAfterBigPictureExit(List<ulong> xboxGipSnapshot)
        {
            try
            {
                BpmLog.WriteLine(
                    "[Xbox] Restoring guide-button LED to 100% for "
                        + xboxGipSnapshot.Count
                        + " cached controller id(s) after Big Picture exit."
                );
                XboxGipPowerOff.TrySetLedBrightnessForAll(xboxGipSnapshot, XboxGipPowerOff.LedIntensityPercent100);
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Xbox] LED restore after Big Picture exit failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-discovers Xbox GIP controllers, dims their guide LED and caches the IDs. Called when Big
        /// Picture opens and again whenever a device connects/disconnects during the session, so a
        /// controller that is powered off and on again gets re-dimmed. The active-session checks avoid
        /// dimming after Big Picture has already closed (discovery takes a few seconds).
        /// </summary>
        private void DiscoverAndDimXboxControllers()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!_bigPictureActive)
                    {
                        return;
                    }

                    var ids = XboxGipPowerOff.TryDiscoverGipControllers();
                    if (!_bigPictureActive)
                    {
                        return;
                    }

                    lock (_xboxGipIdsSync)
                    {
                        _xboxGipDeviceIdsFromLastBpOpen = ids.Count > 0 ? ids : null;
                    }

                    if (ids.Count > 0)
                    {
                        BpmLog.WriteLine("[Xbox] Cached " + ids.Count + " controller id(s); dimming guide LED to ~10%.");
                        XboxGipPowerOff.TrySetLedBrightnessForAll(ids, XboxGipPowerOff.LedIntensityPercent10);
                    }
                    else
                    {
                        BpmLog.WriteLine("[Xbox] No controllers found for this Big Picture session.");
                    }
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine("[Error] [Xbox] Controller discovery/dim failed: " + ex.Message);
                }
            });
        }

        private void OnDeviceChangedDuringSession(object sender, EventArgs e)
        {
            if (!_bigPictureActive)
            {
                return;
            }

            // Debounce: a single connect can raise several interface-arrival messages.
            _xboxRedimTimer.Stop();
            _xboxRedimTimer.Start();
        }

        private void OnXboxRedimTick(object sender, EventArgs e)
        {
            _xboxRedimTimer.Stop();
            if (_bigPictureActive)
            {
                BpmLog.WriteLine("[Xbox] Device change during Big Picture; re-detecting controllers.");
                DiscoverAndDimXboxControllers();
            }
        }
    }
}
