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

            DiscoverXboxGipControllersForCurrentBpSession();
        }

        private async void OnBigPictureClosed(object sender, EventArgs e)
        {
            // async void UIA callback: a leaked exception would crash the process, so guard the whole body.
            try
            {
                BpmLog.WriteLine("[Main] Steam Big Picture Mode closed!");
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

                if (Settings.Default.isPowerOffXboxGipOnBpClose && WindowsIdentityHelper.IsAdministrator())
                {
                    _ = Task.Run(() => PowerOffXboxControllersAfterBigPictureExit(xboxGipSnapshot));
                }
                else
                {
                    if (Settings.Default.isPowerOffXboxGipOnBpClose)
                    {
                        BpmLog.WriteLine(
                            "[Xbox] Power-off after Big Picture exit skipped (application is not running with administrator rights)."
                        );
                    }

                    if (xboxGipSnapshot != null && xboxGipSnapshot.Count > 0)
                    {
                        _ = Task.Run(() => RestoreXboxControllerLedAfterBigPictureExit(xboxGipSnapshot));
                    }
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Big Picture close handling failed: " + ex.Message);
            }
        }

        private static void PowerOffXboxControllersAfterBigPictureExit(List<ulong> xboxGipSnapshot)
        {
            try
            {
                if (xboxGipSnapshot != null && xboxGipSnapshot.Count > 0)
                {
                    BpmLog.WriteLine(
                        "[Xbox] Invoking Xbox GIP power-off service after Big Picture exit (cached "
                            + xboxGipSnapshot.Count
                            + " controller id(s))."
                    );
                    XboxGipPowerOff.PowerOffViaEphemeralService(-1, xboxGipSnapshot);
                }
                else
                {
                    BpmLog.WriteLine(
                        "[Xbox] Invoking Xbox GIP power-off service after Big Picture exit (discovery in service)."
                    );
                    XboxGipPowerOff.PowerOffViaEphemeralService();
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Xbox] Xbox GIP power off failed: " + ex.Message);
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
        /// Discovers Xbox GIP controller IDs when Big Picture opens, dims the guide LED, and caches IDs for exit handling.
        /// </summary>
        private void DiscoverXboxGipControllersForCurrentBpSession()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var ids = XboxGipPowerOff.TryDiscoverGipControllers();
                    lock (_xboxGipIdsSync)
                    {
                        _xboxGipDeviceIdsFromLastBpOpen = ids.Count > 0 ? ids : null;
                    }

                    if (ids.Count > 0)
                    {
                        BpmLog.WriteLine(
                            "[Xbox] Cached " + ids.Count + " controller id(s) for this Big Picture session."
                        );
                        BpmLog.WriteLine("[Xbox] Dimming guide-button LED to ~10% for Big Picture session.");
                        XboxGipPowerOff.TrySetLedBrightnessForAll(ids, XboxGipPowerOff.LedIntensityPercent10);
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[Xbox] No controllers at Big Picture start (LED dim and exit actions skipped for this session)."
                        );
                    }
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine("[Error] [Xbox] Discovery at Big Picture start failed: " + ex.Message);
                }
            });
        }
    }
}
