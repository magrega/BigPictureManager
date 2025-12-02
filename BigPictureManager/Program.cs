using BigPictureManager.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BigPictureTray());
        }
    }

    public class BigPictureTray : ApplicationContext
    {
        private static readonly string BPWindowName = "Steam Big Picture Mode";
        private static readonly string AppName = "Big Picture Audio Switcher";
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
        private readonly NightLight NightLight = new NightLight();
        private bool isNightLightOnStart;

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
            ListenForBP();
        }

        private async void InitializeAsync()
        {
            var audioMenu = CreateAudioMenu();
            BluetoothDevice = await RequestBluetoothDevice();
            bool isBTAvailable = BluetoothDevice != null;
            BTMenuItem.Text = isBTAvailable
                ? "Turn off Bluetooth on BP close"
                : "No Bluetooth available";
            BTMenuItem.Checked = isBTAvailable && isTurnOffBT;
            BTMenuItem.Enabled = isBTAvailable;

            AudioMenuItem.Text = "Choose Audio Device";
            AudioMenuItem.Enabled = true;
            AudioMenuItem.DropDown = audioMenu;
        }

        private void HandleNightLight()
        {
            if (!NightLight.Supported)
            {
                Console.WriteLine("Night light isn’t supported on this machine.");
                return;
            }
            isNightLightOnStart = NightLight.Enabled;
            Console.WriteLine("Night light is supported on this machine.");
            Console.WriteLine($"Current state: {(NightLight.Enabled ? "On" : "Off")}");

            if (NightLight.Enabled) NightLight.Enabled = false; 

        }
        private ContextMenuStrip CreateMainMenu()
        {
            var menu = new ContextMenuStrip();

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

            var ExitMenuItem = new ToolStripMenuItem("Exit");
            var NightLightItem = new ToolStripMenuItem("The app will turn off Night Light on BP start.") { Enabled = false};
            ExitMenuItem.Click += new EventHandler(Exit);

            menu.Items.AddRange(
                new ToolStripItem[]
                {
                    AudioMenuItem,
                    SeparatorMenuItem,
                    BTMenuItem,
                    StartMenuItem,
                    NightLightItem,
                    ExitMenuItem,
                }
            );
            return menu;
        }

        private ContextMenuStrip CreateAudioMenu()
        {
            var menu = new ContextMenuStrip();

            var deviceItems = GetPlaybackDevices();

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
                Console.WriteLine("Permission denied to control radios");
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
                    Console.WriteLine("Bluetooth radio not found");
                } else
                {
                    await bluetoothRadio.SetStateAsync(radioState);
                    Console.WriteLine($"Bluetooth state is set to ${radioState}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
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
                        Console.WriteLine("Steam Big Picture Mode started!");
                        uiContext.Post(_ =>
                        {
                            HandleNightLight();
                            prevDevice = GetDefaultDevice();
                            SetDefaultDevice(selectedDevice.Id);
                        }, null);

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
            Console.WriteLine("Steam Big Picture Mode closed!");
            uiContext.Post(_ =>
            {
                SetDefaultDevice(prevDevice.Id);
                NightLight.Enabled = isNightLightOnStart;
            }, null);

            if (isTurnOffBT)
                await ManageBluetoothAsync(RadioState.Off);
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
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Application.Exit();
        }
    }
}
