using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using BigPictureManager.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Windows.Devices.Radios;

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
        private AutomationElement targetWindow;
        private CoreAudioDevice prevDevice;
        private CoreAudioDevice selectedDevice;
        private CoreAudioController controller;
        private readonly NotifyIcon trayIcon;
        private ToolStripMenuItem AudioMenuItem;
        private ToolStripMenuItem BTMenuItem;
        private Radio BluetoothDevice = null;
        private readonly bool isTurnOffBT = Properties.Settings.Default.isTurnOffBT;
        private readonly bool isAutoStart = Properties.Settings.Default.isAutoStart;

        public BigPictureTray()
        {
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
            var audioMenu = await Task.Run(() => CreateAudioMenu());
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

        private ContextMenuStrip CreateMainMenu()
        {
            var menu = new ContextMenuStrip();

            AudioMenuItem = new ToolStripMenuItem("Loading audio devices...") { Enabled = false };
            var SeparatorMenuItem = new ToolStripSeparator();
            bool isBTAvailable = BluetoothDevice != null;
            BTMenuItem = new ToolStripMenuItem()
            {
                Text = "No Bluetooth available",
                Enabled = false,
                CheckOnClick = true,
                ToolTipText = "Turn off Bluetooth on Big Picture exit",
            };
            BTMenuItem.Click += (s, e) =>
            {
                if (BTMenuItem.Checked)
                {
                    Properties.Settings.Default.isTurnOffBT = true;
                }
                else
                {
                    Properties.Settings.Default.isTurnOffBT = false;
                }
                Properties.Settings.Default.Save();
            };

            var StartMenuItem = new ToolStripMenuItem("Launch on system start")
            {
                CheckOnClick = true,
                Checked = isAutoStart,
            };
            StartMenuItem.Click += (s, e) =>
            {
                if (StartMenuItem.Checked)
                {
                    Properties.Settings.Default.isAutoStart = true;
                }
                else
                {
                    Properties.Settings.Default.isAutoStart = false;
                }
                Properties.Settings.Default.Save();
                SetStartup(StartMenuItem.Checked);
            };

            var ExitMenuItem = new ToolStripMenuItem("Exit");
            ExitMenuItem.Click += new EventHandler(Exit);

            menu.Items.AddRange(
                new ToolStripItem[]
                {
                    AudioMenuItem,
                    SeparatorMenuItem,
                    BTMenuItem,
                    StartMenuItem,
                    ExitMenuItem,
                }
            );
            return menu;
        }

        private ContextMenuStrip CreateAudioMenu()
        {
            var menu = new ContextMenuStrip();

            controller = new CoreAudioController();
            prevDevice = controller.DefaultPlaybackDevice;

            var deviceItems = controller
                .GetPlaybackDevices()
                .Where(d => d.State == DeviceState.Active);

            if (deviceItems.Count() == 0)
            {
                AudioMenuItem.Text = "No playback devices found";
                AudioMenuItem.Enabled = false;
                return null;
            }

            var audioListItems = deviceItems.Select(
                (device, index) =>
                {
                    ToolStripMenuItem item = new ToolStripMenuItem(device.FullName)
                    {
                        Tag = device,
                    };

                    item.Click += (sender, e) =>
                    {
                        selectedDevice = (CoreAudioDevice)((ToolStripMenuItem)sender).Tag;
                        Properties.Settings.Default.LastAudioDeviceId =
                            selectedDevice.Id.ToString();
                        Properties.Settings.Default.Save();

                        UpdateDeviceCheckmarks(selectedDevice, menu);
                    };

                    return item;
                }
            );

            menu.Items.AddRange(audioListItems.Cast<ToolStripItem>().ToArray());

            if (!string.IsNullOrEmpty(Properties.Settings.Default.LastAudioDeviceId))
            {
                var lastDeviceId = Guid.Parse(Properties.Settings.Default.LastAudioDeviceId);
                var lastDeviceItem = audioListItems.FirstOrDefault(i =>
                    ((CoreAudioDevice)i.Tag).Id == lastDeviceId
                );

                if (lastDeviceItem != null)
                {
                    selectedDevice = (CoreAudioDevice)lastDeviceItem.Tag;
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

        public async Task<bool> TurnOffBluetoothAsync()
        {
            try
            {
                var bluetoothRadio = await RequestBluetoothDevice();

                if (bluetoothRadio == null)
                {
                    Console.WriteLine("Bluetooth radio not found");
                    return false;
                }

                if (bluetoothRadio.State != RadioState.Off)
                {
                    await bluetoothRadio.SetStateAsync(RadioState.Off);
                    Console.WriteLine("Bluetooth turned off successfully");
                    return true;
                }

                Console.WriteLine("Bluetooth is already off");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return false;
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
                        prevDevice = controller.DefaultPlaybackDevice;
                        selectedDevice.SetAsDefault();

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

        async void OnWindowClosed(object sender, AutomationEventArgs e)
        {
            Console.WriteLine("Target window closed!");
            prevDevice.SetAsDefault();

            if (isTurnOffBT)
                await TurnOffBluetoothAsync();

            //Automation.RemoveAutomationEventHandler(
            //    WindowPattern.WindowClosedEvent,
            //    sender as AutomationElement,
            //    OnWindowClosed);
        }

        private void UpdateDeviceCheckmarks(CoreAudioDevice selectedDevice, ContextMenuStrip menu)
        {
            foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
            {
                item.Checked = (item.Tag as CoreAudioDevice)?.Id == selectedDevice?.Id;
            }
        }

        private void UpdateUI(Action action)
        {
            if (trayIcon.ContextMenuStrip.InvokeRequired)
            {
                trayIcon.ContextMenuStrip.Invoke(new MethodInvoker(action));
            }
            else
            {
                action();
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
                selectedDevice = (CoreAudioDevice)tvDevice.Tag;
                UpdateDeviceCheckmarks(selectedDevice, menu);
            }
            else
            {
                var defaultAudio = audioListItems.FirstOrDefault(d =>
                    (d.Tag as CoreAudioDevice).IsDefaultDevice
                );
                selectedDevice = (CoreAudioDevice)defaultAudio.Tag;
                UpdateDeviceCheckmarks(selectedDevice, menu);
            }
            ;
        }

        void Exit(object sender, EventArgs e)
        {
            Automation.RemoveAllEventHandlers();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Application.Exit();
        }
    }
}
