using BigPictureManager.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Windows.Devices.Radios;
using TinyScreen.Services;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (
                XboxGipPowerOff.TryParseServiceArgs(
                    args ?? new string[0],
                    out var xboxPowerOffTargetIndex,
                    out var xboxExplicitDeviceIds
                )
            )
            {
                Environment.Exit(XboxGipPowerOff.RunServiceMode(xboxPowerOffTargetIndex, xboxExplicitDeviceIds));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BigPictureTray());
        }
    }

    public class BigPictureTray : ApplicationContext
    {
        private static readonly string BPWindowName = "Steam Big Picture Mode";
        private static readonly string AppName = "Big Picture Audio Switcher";
        private static readonly string ProjectReadmeUrl =
            "https://github.com/magrega/BigPictureManager/blob/master/README.md";
        private readonly SynchronizationContext uiContext;
        private AutomationElement targetWindow;
        private AudioDevice prevDevice;
        private AudioDevice selectedDevice;
        private readonly NotifyIcon trayIcon;
        private ToolStripMenuItem AudioMenuItem;
        private ToolStripMenuItem BTMenuItem;
        private Radio BluetoothDevice = null;
        private bool isTurnOffBT = Settings.Default.isTurnOffBT || false;
        private bool isAutoStart = Settings.Default.isAutoStart || false;
        private bool isTurnOffNightLightOnBpStart = Settings.Default.isTurnOffNightLightOnBpStart;
        private bool restoreNightLightAfterBigPicture;
        private ToolStripMenuItem XboxGipPowerOffMenuItem;
        private ToolStripMenuItem NightLightBpMenuItem;
        private readonly object _xboxGipIdsSync = new object();
        private List<ulong> _xboxGipDeviceIdsFromLastBpOpen;
        private readonly NightLight NightLight = new NightLight();
        private readonly object audioRefreshSync = new object();
        private CancellationTokenSource audioRefreshCts = new CancellationTokenSource();

        public BigPictureTray()
        {
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            trayIcon = new NotifyIcon()
            {
                Icon = Resources.TrayIcon,
                Visible = true,
                Text = AppName,
                ContextMenuStrip = CreateMainMenu(),
            };
            InitializeAsync();
            StartAudioDeviceMonitoring();
            ListenForBP();
            LogApplicationStartup();
        }

        private void LogApplicationStartup()
        {
            BpmLog.WriteLine(
                IsAdministrator()
                    ? "[Main] Application started with administrator rights."
                    : "[Main] Application started without administrator rights."
            );
            try
            {
                var ids = XboxGipPowerOff.TryDiscoverGipControllers();
                BpmLog.WriteLine(
                    "[Xbox] At startup: found " + ids.Count + " wireless Xbox controller(s) (GIP enumeration)."
                );
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Xbox] Startup controller enumeration failed: " + ex.Message);
            }
        }

        private static void TrySetDefaultPlaybackDevice(AudioDevice device, string reason)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Id))
            {
                BpmLog.WriteLine("[Audio] Cannot change default playback device (" + reason + "): no device selected.");
                return;
            }

            if (!SetDefaultDevice(device.Id))
            {
                BpmLog.WriteLine(
                    "[Error] [Audio] Failed to set default playback device to \"" + device.Name + "\" (" + reason + ")."
                );
                return;
            }

            BpmLog.WriteLine("[Audio] Default playback device set to \"" + device.Name + "\" (" + reason + ").");
        }

        private async void InitializeAsync()
        {
            BluetoothDevice = await RequestBluetoothDevice();
            bool isBTAvailable = BluetoothDevice != null;
            BTMenuItem.Text = isBTAvailable
                ? "Turn off Bluetooth on BP close"
                : "No Bluetooth available";
            BTMenuItem.Checked = isBTAvailable && isTurnOffBT;
            BTMenuItem.Enabled = isBTAvailable;
            QueueAudioDeviceRefresh(0);
        }

        private void HandleNightLight()
        {
            if (!NightLight.Supported)
            {
                BpmLog.WriteLine("[NightLight] Night light isn’t supported on this machine.");
                return;
            }

            BpmLog.WriteLine("[NightLight] Night light is supported on this machine.");
            BpmLog.WriteLine("[NightLight] Current state: " + (NightLight.Enabled ? "On" : "Off"));
            NightLight.DisableNightLight();
            restoreNightLightAfterBigPicture = true;
        }
        private ContextMenuStrip CreateMainMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) => ApplyXboxGipPowerOffMenuState();

            AudioMenuItem = new ToolStripMenuItem("Loading audio devices...") { Enabled = false };
            var SeparatorMenuItem = new ToolStripSeparator();
            BTMenuItem = new ToolStripMenuItem()
            {
                Text = "No Bluetooth available",
                Enabled = false,
                CheckOnClick = true,
                ToolTipText = "Turn off Bluetooth on Big Picture exit",
            };
            BTMenuItem.Click += (s, e) =>
            {
                isTurnOffBT = BTMenuItem.Checked;
                Settings.Default.isTurnOffBT = isTurnOffBT;
                Settings.Default.Save();
            };

            XboxGipPowerOffMenuItem = new ToolStripMenuItem("Wireless Xbox Controller")
            {
                CheckOnClick = true,
                ToolTipText = "Power off Xbox wireless controllers (XboxGIP) when Steam Big Picture closes",
            };
            XboxGipPowerOffMenuItem.Click += (s, e) =>
            {
                if (!IsAdministrator())
                {
                    TryRestartElevatedForXboxGipFeature();
                    return;
                }

                Settings.Default.isPowerOffXboxGipOnBpClose = XboxGipPowerOffMenuItem.Checked;
                Settings.Default.Save();
            };
            ApplyXboxGipPowerOffMenuState();

            NightLightBpMenuItem = new ToolStripMenuItem("Turn off Night Light on BP start")
            {
                CheckOnClick = true,
                Checked = isTurnOffNightLightOnBpStart,
                ToolTipText = "Disable Windows Night Light when Steam Big Picture opens, then restore it when Big Picture closes",
            };
            NightLightBpMenuItem.Click += (s, e) =>
            {
                isTurnOffNightLightOnBpStart = NightLightBpMenuItem.Checked;
                Settings.Default.isTurnOffNightLightOnBpStart = isTurnOffNightLightOnBpStart;
                Settings.Default.Save();
            };

            var powerOffControllerMenuItem = new ToolStripMenuItem("Power Off Controller");
            powerOffControllerMenuItem.DropDownItems.Add(BTMenuItem);
            powerOffControllerMenuItem.DropDownItems.Add(XboxGipPowerOffMenuItem);

            var StartMenuItem = new ToolStripMenuItem("Launch on system start")
            {
                CheckOnClick = true,
                Checked = isAutoStart,
            };
            StartMenuItem.Click += (s, e) =>
            {
                isAutoStart = StartMenuItem.Checked;
                Settings.Default.isAutoStart = isAutoStart;
                Settings.Default.Save();
                SetStartup(StartMenuItem.Checked);
            };

            var aboutMenuItem = new ToolStripMenuItem("About");
            aboutMenuItem.Click += (s, e) =>
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = ProjectReadmeUrl,
                        UseShellExecute = true,
                    }
                );
            };

            var ExitMenuItem = new ToolStripMenuItem("Exit");
            ExitMenuItem.Click += new EventHandler(Exit);

            menu.Items.AddRange(
                new ToolStripItem[]
                {
                    AudioMenuItem,
                    SeparatorMenuItem,
                    NightLightBpMenuItem,
                    powerOffControllerMenuItem,
                    StartMenuItem,
                    aboutMenuItem,
                    ExitMenuItem,
                }
            );
            return menu;
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private void ApplyXboxGipPowerOffMenuState()
        {
            if (XboxGipPowerOffMenuItem == null)
            {
                return;
            }

            if (IsAdministrator())
            {
                XboxGipPowerOffMenuItem.Text = "Wireless Xbox Controller";
                XboxGipPowerOffMenuItem.Enabled = true;
                XboxGipPowerOffMenuItem.CheckOnClick = true;
                XboxGipPowerOffMenuItem.Checked = Settings.Default.isPowerOffXboxGipOnBpClose;
                XboxGipPowerOffMenuItem.ToolTipText =
                    "Power off Xbox wireless controllers (XboxGIP) when Steam Big Picture closes";
            }
            else
            {
                XboxGipPowerOffMenuItem.Text = "Wireless Xbox Controller (admin rights)";
                XboxGipPowerOffMenuItem.Enabled = true;
                XboxGipPowerOffMenuItem.CheckOnClick = false;
                XboxGipPowerOffMenuItem.Checked = false;
                XboxGipPowerOffMenuItem.ToolTipText =
                    "Restart this app as administrator to enable wireless Xbox controller power-off when Big Picture closes";
            }
        }

        private const int ErrorCancelled = 1223;

        private void TryRestartElevatedForXboxGipFeature()
        {
            try
            {
                var exePath = Application.ExecutablePath;
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show(
                        "Could not determine the application path to restart elevated.",
                        AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                Process.Start(psi);
                Settings.Default.isPowerOffXboxGipOnBpClose = true;
                Settings.Default.Save();
                BpmLog.WriteLine("[Main] Restarting elevated after UAC for Xbox GIP feature (Xbox power-off enabled in settings).");
                Exit(this, EventArgs.Empty);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                BpmLog.WriteLine("[Main] UAC prompt dismissed; staying non-elevated.");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Failed to restart elevated: " + ex.Message);
                MessageBox.Show(
                    "Could not restart as administrator: " + ex.Message,
                    AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        private void StartAudioDeviceMonitoring()
        {
            NativeAudioApi.StartDeviceWatcher(() => QueueAudioDeviceRefresh(250));
        }

        private void QueueAudioDeviceRefresh(int debounceMs)
        {
            CancellationToken token;
            lock (audioRefreshSync)
            {
                audioRefreshCts.Cancel();
                audioRefreshCts.Dispose();
                audioRefreshCts = new CancellationTokenSource();
                token = audioRefreshCts.Token;
            }

            _ = RefreshAudioMenuAsync(token, debounceMs);
        }

        private async Task RefreshAudioMenuAsync(CancellationToken token, int debounceMs)
        {
            try
            {
                if (debounceMs > 0)
                {
                    await Task.Delay(debounceMs, token);
                }

                var deviceItems = await Task.Run(() => GetPlaybackDevices(), token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                uiContext.Post(_ => ApplyAudioDevicesToMenu(deviceItems), null);
            }
            catch (OperationCanceledException)
            {
                // Expected during rapid device changes.
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Audio] Audio menu refresh failed: " + ex.Message);
            }
        }

        private void ApplyAudioDevicesToMenu(List<AudioDevice> deviceItems)
        {
            var audioMenu = CreateAudioMenu(deviceItems);
            if (audioMenu == null)
            {
                AudioMenuItem.Text = "No playback devices found";
                AudioMenuItem.Enabled = false;
                AudioMenuItem.DropDown = null;
                return;
            }

            AudioMenuItem.Text = "Choose Audio Device";
            AudioMenuItem.Enabled = true;
            AudioMenuItem.DropDown = audioMenu;
        }

        private ContextMenuStrip CreateAudioMenu(List<AudioDevice> deviceItems)
        {
            var menu = new ContextMenuStrip();

            if (deviceItems.Count == 0)
            {
                AudioMenuItem.Text = "No playback devices found";
                AudioMenuItem.Enabled = false;
                return null;
            }

            var audioListItems = deviceItems.Select(
                (device, index) =>
                {
                    ToolStripMenuItem item = new ToolStripMenuItem(device.Name)
                    {
                        Tag = device,
                    };

                    item.Click += (sender, e) =>
                    {
                        selectedDevice = (AudioDevice)((ToolStripMenuItem)sender).Tag;
                        Settings.Default.LastAudioDeviceId =
                            selectedDevice.Id;
                        Settings.Default.Save();
                        UpdateDeviceCheckmarks(selectedDevice, menu);
                        TrySetDefaultPlaybackDevice(selectedDevice, "tray menu selection");
                    };

                    return item;
                }
            );

            menu.Items.AddRange(audioListItems.Cast<ToolStripItem>().ToArray());

            if (!string.IsNullOrEmpty(Settings.Default.LastAudioDeviceId))
            {
                var lastDeviceId = Settings.Default.LastAudioDeviceId;
                var lastDeviceItem = audioListItems.FirstOrDefault(i =>
                    ((AudioDevice)i.Tag).Id == lastDeviceId
                );

                if (lastDeviceItem != null)
                {
                    selectedDevice = (AudioDevice)lastDeviceItem.Tag;
                    UpdateDeviceCheckmarks(selectedDevice, menu);
                }

                if (lastDeviceItem == null)
                {
                    SetDefaultAudio(menu, audioListItems);
                }
            }
            else
            {
                SetDefaultAudio(menu, audioListItems);
            }
            return menu;
        }

        private void SetStartup(bool enable)
        {
            try
            {
                string appPath = Application.ExecutablePath;
                string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string shortcutPath = Path.Combine(
                    startupPath,
                    Path.GetFileNameWithoutExtension(appPath) + ".lnk"
                );

                if (enable)
                {
                    var shell = new IWshRuntimeLibrary.WshShell();
                    IWshRuntimeLibrary.IWshShortcut shortcut = (IWshRuntimeLibrary.IWshShortcut)
                        shell.CreateShortcut(shortcutPath);

                    shortcut.TargetPath = appPath;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(appPath);
                    shortcut.Description = AppName;
                    shortcut.IconLocation = appPath + ",0";
                    shortcut.Save();
                }
                else
                {
                    if (File.Exists(shortcutPath))
                        File.Delete(shortcutPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting startup: {ex.Message}");
            }
        }

        private async Task<Radio> RequestBluetoothDevice()
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                BpmLog.WriteLine("[Error] [Bluetooth] Permission denied to control radios.");
                return null;
            }

            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            return bluetoothRadio;
        }

        public async Task ManageBluetoothAsync(RadioState radioState)
        {
            try
            {
                var bluetoothRadio = await RequestBluetoothDevice();

                if (bluetoothRadio == null)
                {
                    BpmLog.WriteLine("[Bluetooth] Bluetooth radio not found.");
                }
                else
                {
                    await bluetoothRadio.SetStateAsync(radioState);
                    BpmLog.WriteLine("[Bluetooth] Bluetooth state is set to " + radioState + ".");
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Bluetooth] " + ex.Message);
            }
        }

        private void ListenForBP()
        {
            Automation.AddAutomationEventHandler(
                eventId: WindowPattern.WindowOpenedEvent,
                element: AutomationElement.RootElement,
                scope: TreeScope.Children,
                eventHandler: (s, e) =>
                {
                    bool isBP = IsBigPictureWindow(s);
                    if (isBP)
                    {
                        BpmLog.WriteLine("[Main] Steam Big Picture Mode started!");
                        uiContext.Post(_ =>
                        {
                            if (isTurnOffNightLightOnBpStart)
                            {
                                HandleNightLight();
                            }
                            else
                            {
                                BpmLog.WriteLine("[NightLight] Turn off on Big Picture start is disabled in the menu; skipping.");
                            }

                            prevDevice = GetDefaultDevice();
                            if (selectedDevice != null && !string.IsNullOrWhiteSpace(selectedDevice.Id))
                            {
                                TrySetDefaultPlaybackDevice(selectedDevice, "Big Picture opened");
                            }
                        }, null);

                        if (IsAdministrator() && Settings.Default.isPowerOffXboxGipOnBpClose)
                        {
                            DiscoverXboxGipControllersForCurrentBpSession();
                        }

                        targetWindow = s as AutomationElement;
                        Automation.AddAutomationEventHandler(
                            eventId: WindowPattern.WindowClosedEvent,
                            element: targetWindow,
                            scope: TreeScope.Element,
                            eventHandler: OnWindowClosed
                        );
                    }
                }
            );
        }

        private static bool IsBigPictureWindow(object sender)
        {
            try
            {
                var element = sender as AutomationElement;
                if (element != null && element.Current.Name == BPWindowName)
                    return true;
                return false;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private async void OnWindowClosed(object sender, AutomationEventArgs e)
        {
            BpmLog.WriteLine("[Main] Steam Big Picture Mode closed!");
            uiContext.Post(_ =>
            {
                if (prevDevice != null && !string.IsNullOrWhiteSpace(prevDevice.Id))
                {
                    TrySetDefaultPlaybackDevice(prevDevice, "Big Picture closed (restore previous default)");
                }
                if (restoreNightLightAfterBigPicture)
                {
                    try
                    {
                        NightLight.RestoreNightLight();
                        BpmLog.WriteLine("[NightLight] Night Light restored on Big Picture exit.");
                    }
                    catch (Exception ex)
                    {
                        BpmLog.WriteLine("[Error] [NightLight] Restore on Big Picture exit failed: " + ex.Message);
                    }
                    finally
                    {
                        restoreNightLightAfterBigPicture = false;
                    }
                }
            }, null);

            if (isTurnOffBT)
                await ManageBluetoothAsync(RadioState.Off);

            List<ulong> xboxGipSnapshot = null;
            lock (_xboxGipIdsSync)
            {
                if (_xboxGipDeviceIdsFromLastBpOpen != null && _xboxGipDeviceIdsFromLastBpOpen.Count > 0)
                {
                    xboxGipSnapshot = new List<ulong>(_xboxGipDeviceIdsFromLastBpOpen);
                }

                _xboxGipDeviceIdsFromLastBpOpen = null;
            }

            if (Settings.Default.isPowerOffXboxGipOnBpClose && IsAdministrator())
            {
                _ = Task.Run(() =>
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
                });
            }
            else if (Settings.Default.isPowerOffXboxGipOnBpClose)
            {
                BpmLog.WriteLine(
                    "[Xbox] Power-off after Big Picture exit skipped (restart the app as administrator to enable this feature)."
                );
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
                        BpmLog.WriteLine("[Xbox] Cached " + ids.Count + " controller id(s) for this Big Picture session.");
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

        private void UpdateDeviceCheckmarks(AudioDevice selectedDevice, ContextMenuStrip menu)
        {
            foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
            {
                item.Checked = (item.Tag as AudioDevice)?.Id == selectedDevice?.Id;
            }
        }

        private void SetDefaultAudio(
            ContextMenuStrip menu,
            IEnumerable<ToolStripMenuItem> audioListItems
        )
        {
            var tvDevice = audioListItems.FirstOrDefault(d => d.Text.Contains("TV"));

            if (tvDevice != null)
            {
                selectedDevice = (AudioDevice)tvDevice.Tag;
                UpdateDeviceCheckmarks(selectedDevice, menu);
            }
            else
            {
                selectedDevice = GetDefaultDevice();
                UpdateDeviceCheckmarks(selectedDevice, menu);
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            Automation.RemoveAllEventHandlers();
            NativeAudioApi.StopDeviceWatcher();
            lock (audioRefreshSync)
            {
                audioRefreshCts.Cancel();
                audioRefreshCts.Dispose();
                audioRefreshCts = new CancellationTokenSource();
            }
            BpmLog.WriteLine("[Main] App exit requested.");
           
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Application.Exit();
        }

    }
}
