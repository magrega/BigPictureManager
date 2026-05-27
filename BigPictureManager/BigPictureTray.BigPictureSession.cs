using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Automation;
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

        private void ListenForBigPicture()
        {
            Automation.AddAutomationEventHandler(
                eventId: WindowPattern.WindowOpenedEvent,
                element: AutomationElement.RootElement,
                scope: TreeScope.Children,
                eventHandler: OnBigPictureWindowOpened
            );
        }

        private void OnBigPictureWindowOpened(object sender, AutomationEventArgs e)
        {
            if (!IsBigPictureWindow(sender))
            {
                return;
            }

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

                    _prevDevice = GetDefaultDevice();
                    if (_selectedDevice != null && !string.IsNullOrWhiteSpace(_selectedDevice.Id))
                    {
                        TrySetDefaultPlaybackDevice(_selectedDevice, "Big Picture opened");
                    }
                },
                null
            );

            if (WindowsIdentityHelper.IsAdministrator() && Settings.Default.isPowerOffXboxGipOnBpClose)
            {
                DiscoverXboxGipControllersForCurrentBpSession();
            }

            _targetWindow = sender as AutomationElement;
            Automation.AddAutomationEventHandler(
                eventId: WindowPattern.WindowClosedEvent,
                element: _targetWindow,
                scope: TreeScope.Element,
                eventHandler: OnBigPictureWindowClosed
            );
        }

        private static bool IsBigPictureWindow(object sender)
        {
            try
            {
                var element = sender as AutomationElement;
                return element != null && element.Current.Name == AppConstants.BigPictureWindowName;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private async void OnBigPictureWindowClosed(object sender, AutomationEventArgs e)
        {
            BpmLog.WriteLine("[Main] Steam Big Picture Mode closed!");
            _uiContext.Post(
                _ =>
                {
                    if (_prevDevice != null && !string.IsNullOrWhiteSpace(_prevDevice.Id))
                    {
                        TrySetDefaultPlaybackDevice(
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
                await ManageBluetoothAsync(RadioState.Off);
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
            else if (Settings.Default.isPowerOffXboxGipOnBpClose)
            {
                BpmLog.WriteLine(
                    "[Xbox] Power-off after Big Picture exit skipped (application is not running with administrator rights)."
                );
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

        /// <summary>
        /// Discovers Xbox GIP controller IDs when Big Picture opens so shutdown can skip discovery in the elevated service.
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
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[Xbox] No controllers at Big Picture start (exit will use in-service discovery if power-off is enabled)."
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
