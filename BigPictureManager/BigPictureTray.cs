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
        private bool _isAutoStart = Settings.Default.isAutoStart;
        private bool _isTurnOffNightLightOnBpStart = Settings.Default.isTurnOffNightLightOnBpStart;
        private bool _isPauseMediaOnBpStart = Settings.Default.isPauseMediaOnBpStart;
        private bool _restoreNightLightAfterBigPicture;
        private ToolStripMenuItem _xboxGipPowerOffMenuItem;
        private ToolStripMenuItem _nightLightBpMenuItem;
        private ToolStripMenuItem _pauseMediaBpMenuItem;
        private ToolStripMenuItem _launchOnStartMenuItem;
        private readonly object _xboxGipIdsSync = new object();
        private List<ulong> _xboxGipDeviceIdsFromLastBpOpen;
        private readonly NightLight _nightLight = new NightLight();
        private readonly AudioDeviceService _audio;
        private readonly System.Windows.Forms.Timer _persistTimer = new System.Windows.Forms.Timer { Interval = 300 };
        private AudioDevice _pendingAudioDevice;

        public BigPictureTray()
        {
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _audio = new AudioDeviceService(_uiContext);
            _persistTimer.Tick += OnPersistTick;
            InitializeMenuModel();
            _trayIcon = new NotifyIcon
            {
                Icon = Resources.TrayIcon,
                Visible = true,
                Text = Resources.AppTitle,
            };
            _trayIcon.MouseUp += OnTrayIconMouseUp;
            // Populate the device list immediately and independently of Bluetooth: radio enumeration
            // (RequestAccessAsync/GetRadiosAsync) can take a while and must not delay the audio list.
            _audio.DevicesChanged += OnAudioDevicesChanged;
            _audio.Start();
            InitializeBluetoothAsync();
            _bpWatcher.Opened += OnBigPictureOpened;
            _bpWatcher.Closed += OnBigPictureClosed;
            _bpWatcher.Start();
            var exePath = Application.ExecutablePath;
            if (!string.IsNullOrEmpty(exePath))
            {
                ElevatedLogonStartupTask.RemoveLegacyStartupShortcut(exePath);
            }

            SyncLaunchOnStartMenuState();
            TryCompletePendingElevatedStartupTaskInstall();
            TryCompletePendingElevatedStartupTaskUnregister();
            TryCompletePendingXboxGipPowerOffEnable();
            LogApplicationStartup();
        }

        private void LogApplicationStartup()
        {
            BpmLog.WriteLine(
                WindowsIdentityHelper.IsAdministrator()
                    ? "[Main] Application started with administrator rights."
                    : "[Main] Application started without administrator rights."
            );

            // GIP discovery blocks ~5s when no controller is connected (16 read attempts x 300ms).
            // Run it off the UI thread so the tray menu responds immediately — this is diagnostics only.
            _ = Task.Run(() =>
            {
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
            });
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
                Checked = _isAutoStart,
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
            ApplyXboxGipPowerOffMenuState();
            BuildTrayMenu();
            _trayMenu.ShowAtCursor();
        }

        /// <summary>
        /// Restarts the debounce window. Settings are kept in memory immediately (so behaviour is correct
        /// at once); the disk write and any pending audio switch are coalesced and applied after a short
        /// idle, so rapid toggling stays snappy and doesn't thrash the disk or audio device.
        /// </summary>
        private void SchedulePersist()
        {
            _persistTimer.Stop();
            _persistTimer.Start();
        }

        private void OnPersistTick(object sender, EventArgs e)
        {
            _persistTimer.Stop();
            Settings.Default.Save();

            var device = _pendingAudioDevice;
            _pendingAudioDevice = null;
            if (device != null)
            {
                _ = Task.Run(() => _audio.SetDefault(device, "tray menu selection"));
            }
        }

        private void BuildTrayMenu()
        {
            _trayMenu.ClearRows();
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
            _trayMenu.AddAction(TrayMenu.GlyphAbout, Resources.MenuAbout, () => OnAboutMenuItemClick(this, EventArgs.Empty));
            _trayMenu.AddAction(TrayMenu.GlyphExit, Resources.MenuExit, () => OnExit(this, EventArgs.Empty));
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
            SchedulePersist();
        }

        private void OnNightLightBpMenuItemClick(object sender, EventArgs e)
        {
            _isTurnOffNightLightOnBpStart = _nightLightBpMenuItem.Checked;
            Settings.Default.isTurnOffNightLightOnBpStart = _isTurnOffNightLightOnBpStart;
            SchedulePersist();
        }

        private void OnPauseMediaBpMenuItemClick(object sender, EventArgs e)
        {
            _isPauseMediaOnBpStart = _pauseMediaBpMenuItem.Checked;
            Settings.Default.isPauseMediaOnBpStart = _isPauseMediaOnBpStart;
            SchedulePersist();
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
            // Flush any debounced settings change before tearing down.
            _persistTimer.Stop();
            _persistTimer.Dispose();
            Settings.Default.Save();

            _bpWatcher.Dispose();
            _audio.Dispose();
            BpmLog.WriteLine("[Main] App exit requested.");

            _trayMenu.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }

        private void RequestElevatedRestart(
            string logMessage,
            Action onUacCancelled = null,
            Action onRestartFailed = null
        )
        {
            ElevatedProcessLauncher.RestartElevated(
                logMessage,
                () => OnExit(this, EventArgs.Empty),
                onUacCancelled,
                onRestartFailed
            );
        }
    }
}
