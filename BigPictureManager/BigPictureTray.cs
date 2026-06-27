using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BigPictureManager.Properties;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray : ApplicationContext
    {
        private readonly SynchronizationContext _uiContext;
        private readonly BigPictureWatcher _bpWatcher = new BigPictureWatcher();
        private AudioDevice _prevDevice;
        private AudioDevice _selectedDevice;
        private readonly NotifyIcon _trayIcon;
        private readonly TrayMenu _trayMenu = new TrayMenu();
        private ToolStripMenuItem _btMenuItem;
        private readonly BluetoothService _bluetooth = new BluetoothService();
        private bool _isTurnOffBt = Settings.Default.isTurnOffBT;
        private bool _isTurnOffNightLightOnBpStart = Settings.Default.isTurnOffNightLightOnBpStart;
        private bool _isPauseMediaOnBpStart = Settings.Default.isPauseMediaOnBpStart;
        private bool _restoreNightLightAfterBigPicture;
        private ToolStripMenuItem _xboxGipPowerOffMenuItem;
        private ToolStripMenuItem _nightLightBpMenuItem;
        private ToolStripMenuItem _pauseMediaBpMenuItem;
        private ToolStripMenuItem _launchOnStartMenuItem;
        private readonly object _xboxGipIdsSync = new object();
        private List<ulong> _xboxGipDeviceIdsFromLastBpOpen;
        private volatile bool _bigPictureActive;
        private readonly DeviceArrivalWatcher _deviceWatcher = new DeviceArrivalWatcher();
        private readonly System.Windows.Forms.Timer _xboxRedimTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        private readonly NightLight _nightLight = new NightLight();
        private readonly AudioDeviceService _audio;
        private bool _xboxServiceInstalled;
        private TrayMenuPage _menuPage = TrayMenuPage.Main;
        private readonly System.Drawing.Bitmap _shieldIcon = System.Drawing.SystemIcons.Shield.ToBitmap();

        private enum TrayMenuPage
        {
            Main,
            Settings,
        }

        public BigPictureTray()
        {
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _audio = new AudioDeviceService(_uiContext);
            _xboxRedimTimer.Tick += OnXboxRedimTick;
            _deviceWatcher.Changed += OnDeviceChangedDuringSession;
            InitializeMenuModel();
            _trayIcon = new NotifyIcon
            {
                Icon = Resources.TrayIcon,
                Visible = true,
                Text = Resources.AppTitle,
            };
            _trayIcon.MouseUp += OnTrayIconMouseUp;
            _trayMenu.ContentBuilder = BuildMenuContent;
            // Populate the device list immediately and independently of Bluetooth: radio enumeration
            // (RequestAccessAsync/GetRadiosAsync) can take a while and must not delay the audio list.
            _audio.DevicesChanged += OnAudioDevicesChanged;
            _audio.Start();
            InitializeBluetoothAsync();
            _bpWatcher.Opened += OnBigPictureOpened;
            _bpWatcher.Closed += OnBigPictureClosed;
            _bpWatcher.Start();
            LogApplicationStartup();
        }

        private void LogApplicationStartup()
        {
            BpmLog.WriteLine(
                WindowsIdentityHelper.IsAdministrator()
                    ? "[Main] Application started with administrator rights."
                    : "[Main] Application started without administrator rights."
            );
        }

        private async void InitializeBluetoothAsync()
        {
            var isBtAvailable = await _bluetooth.InitializeAsync();
            _btMenuItem.Text = isBtAvailable
                ? Resources.MenuBluetoothTurnOffOnBpClose
                : Resources.MenuBluetoothNoDevice;
            _btMenuItem.Checked = isBtAvailable && _isTurnOffBt;
            _btMenuItem.Enabled = isBtAvailable;
        }

        /// <summary>
        /// Creates the menu's state model. These <see cref="ToolStripMenuItem"/>s are never shown; they
        /// hold checked/enabled/text state that the existing handlers read and write, while the custom
        /// <see cref="TrayMenu"/> presents them. This keeps the elevation/startup/Xbox logic untouched.
        /// </summary>
        private void InitializeMenuModel()
        {
            _btMenuItem = new ToolStripMenuItem
            {
                Text = Resources.MenuBluetoothNoDevice,
                Enabled = false,
            };

            _xboxGipPowerOffMenuItem = new ToolStripMenuItem(Resources.MenuWirelessXboxController);
            ApplyXboxGipPowerOffMenuState();

            _nightLightBpMenuItem = new ToolStripMenuItem(Resources.MenuNightLightTurnOffOnBpStart)
            {
                Checked = _isTurnOffNightLightOnBpStart,
            };

            _pauseMediaBpMenuItem = new ToolStripMenuItem(Resources.MenuPauseMedia)
            {
                Checked = _isPauseMediaOnBpStart,
            };

            _launchOnStartMenuItem = new ToolStripMenuItem(Resources.MenuLaunchOnStart)
            {
                Checked = StartupRegistration.IsEnabled(),
            };
        }

        private void OnTrayIconMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        }

        private void ShowTrayMenu()
        {
            _menuPage = TrayMenuPage.Main;
            _xboxServiceInstalled = XboxGipPowerOff.IsServiceInstalled();
            ApplyXboxGipPowerOffMenuState();
            SyncLaunchOnStartMenuState();
            _trayMenu.ShowAtCursor();
        }

        private void BuildMenuContent()
        {
            _trayMenu.ClearRows();
            if (_menuPage == TrayMenuPage.Settings)
            {
                BuildSettingsPage();
            }
            else
            {
                BuildMainPage();
            }
        }

        private void BuildMainPage()
        {
            _trayMenu.AddTitle(Resources.AppTitle);
            _trayMenu.AddSeparator();

            _trayMenu.AddHeader(Resources.MenuHeaderAudio);
            if (_audioDevices.Count == 0)
            {
                _trayMenu.AddInfo(Resources.MenuAudioNoDevices);
            }
            else
            {
                foreach (var device in _audioDevices)
                {
                    var captured = device;
                    _trayMenu.AddRadio(
                        device.Name,
                        device.Id == _selectedDevice?.Id,
                        () => SelectAudioDevice(captured)
                    );
                }
            }

            _trayMenu.AddSeparator();
            _trayMenu.AddHeader(Resources.MenuHeaderOnBpStart);
            _trayMenu.AddToggle(
                TrayMenu.GlyphNightLight,
                Resources.MenuNightLightTurnOffOnBpStart,
                _nightLightBpMenuItem.Checked,
                true,
                () => Toggle(_nightLightBpMenuItem, OnNightLightBpMenuItemClick)
            );
            _trayMenu.AddToggle(
                TrayMenu.GlyphPause,
                Resources.MenuPauseMedia,
                _pauseMediaBpMenuItem.Checked,
                true,
                () => Toggle(_pauseMediaBpMenuItem, OnPauseMediaBpMenuItemClick)
            );

            _trayMenu.AddSeparator();
            _trayMenu.AddHeader(Resources.MenuHeaderOnBpExit);
            _trayMenu.AddToggle(
                TrayMenu.GlyphController,
                Resources.MenuWirelessXboxController,
                _xboxGipPowerOffMenuItem.Checked,
                _xboxGipPowerOffMenuItem.Enabled,
                () => Toggle(_xboxGipPowerOffMenuItem, OnXboxGipPowerOffMenuItemClick)
            );
            _trayMenu.AddToggle(
                TrayMenu.GlyphBluetooth,
                _btMenuItem.Text,
                _btMenuItem.Checked,
                _btMenuItem.Enabled,
                () => Toggle(_btMenuItem, OnBluetoothMenuItemClick)
            );

            _trayMenu.AddSeparator();
            _trayMenu.AddHeader(Resources.MenuHeaderApp);
            _trayMenu.AddToggle(
                TrayMenu.GlyphStartup,
                Resources.MenuLaunchOnStart,
                _launchOnStartMenuItem.Checked,
                true,
                () => Toggle(_launchOnStartMenuItem, OnLaunchOnStartMenuItemClick)
            );
            _trayMenu.AddNavigation(TrayMenu.GlyphSettings, Resources.MenuSettings, () => _menuPage = TrayMenuPage.Settings);
            _trayMenu.AddAction(TrayMenu.GlyphExit, Resources.MenuExit, () => OnExit(this, EventArgs.Empty));
        }

        private void BuildSettingsPage()
        {
            _trayMenu.AddTitle(Resources.MenuSettings);
            _trayMenu.AddNavigation(TrayMenu.GlyphBack, Resources.MenuBack, () => _menuPage = TrayMenuPage.Main);
            _trayMenu.AddSeparator();

            _trayMenu.AddHeader(Resources.MenuHeaderControllerService);
            _trayMenu.AddActionImage(
                _shieldIcon,
                _xboxServiceInstalled ? Resources.MenuRemoveControllerService : Resources.MenuInstallControllerService,
                InstallOrRemoveXboxService
            );

            _trayMenu.AddSeparator();
            _trayMenu.AddAction(TrayMenu.GlyphAbout, Resources.MenuAbout, () => OnAboutMenuItemClick(this, EventArgs.Empty));
        }

        /// <summary>Launches the elevated one-shot helper to install or remove the global power-off service.</summary>
        private void InstallOrRemoveXboxService()
        {
            var install = !XboxGipPowerOff.IsServiceInstalled();
            var arg = install ? XboxGipPowerOff.InstallServiceArg : XboxGipPowerOff.UninstallServiceArg;
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = arg,
                        UseShellExecute = true,
                        Verb = "runas",
                    }
                );
                BpmLog.WriteLine("[Xbox] Requested " + (install ? "install" : "removal") + " of power-off service (elevated).");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == AppConstants.UacCancelledWin32Error)
            {
                BpmLog.WriteLine("[Xbox] Service management UAC prompt dismissed.");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Xbox] Could not launch service management: " + ex.Message);
            }
        }

        /// <summary>Flips the backing item's checked state and runs its existing handler.</summary>
        private static void Toggle(ToolStripMenuItem item, EventHandler handler)
        {
            item.Checked = !item.Checked;
            handler(item, EventArgs.Empty);
        }

        private void OnBluetoothMenuItemClick(object sender, EventArgs e)
        {
            _isTurnOffBt = _btMenuItem.Checked;
            Settings.Default.isTurnOffBT = _isTurnOffBt;
            Settings.Default.Save();
        }

        private void OnNightLightBpMenuItemClick(object sender, EventArgs e)
        {
            _isTurnOffNightLightOnBpStart = _nightLightBpMenuItem.Checked;
            Settings.Default.isTurnOffNightLightOnBpStart = _isTurnOffNightLightOnBpStart;
            Settings.Default.Save();
        }

        private void OnPauseMediaBpMenuItemClick(object sender, EventArgs e)
        {
            _isPauseMediaOnBpStart = _pauseMediaBpMenuItem.Checked;
            Settings.Default.isPauseMediaOnBpStart = _isPauseMediaOnBpStart;
            Settings.Default.Save();
        }

        private void OnAboutMenuItemClick(object sender, EventArgs e)
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = AppConstants.ProjectReadmeUrl,
                    UseShellExecute = true,
                }
            );
        }

        private void OnExit(object sender, EventArgs e)
        {
            _xboxRedimTimer.Stop();
            _xboxRedimTimer.Dispose();
            _deviceWatcher.Dispose();
            _bpWatcher.Dispose();
            _audio.Dispose();
            BpmLog.WriteLine("[Main] App exit requested.");

            _trayMenu.Dispose();
            _shieldIcon.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }
    }
}
