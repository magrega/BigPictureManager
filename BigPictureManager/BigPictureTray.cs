using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using BigPictureManager.Properties;
using Windows.Devices.Radios;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray : ApplicationContext
    {
        private readonly SynchronizationContext _uiContext;
        private AutomationElement _targetWindow;
        private AudioDevice _prevDevice;
        private AudioDevice _selectedDevice;
        private readonly NotifyIcon _trayIcon;
        private ToolStripMenuItem _audioMenuItem;
        private ToolStripMenuItem _btMenuItem;
        private Radio _bluetoothDevice;
        private bool _isTurnOffBt = Settings.Default.isTurnOffBT;
        private bool _isAutoStart = Settings.Default.isAutoStart;
        private bool _isTurnOffNightLightOnBpStart = Settings.Default.isTurnOffNightLightOnBpStart;
        private bool _restoreNightLightAfterBigPicture;
        private ToolStripMenuItem _xboxGipPowerOffMenuItem;
        private ToolStripMenuItem _nightLightBpMenuItem;
        private ToolStripMenuItem _launchOnStartMenuItem;
        private readonly object _xboxGipIdsSync = new object();
        private List<ulong> _xboxGipDeviceIdsFromLastBpOpen;
        private readonly NightLight _nightLight = new NightLight();
        private readonly object _audioRefreshSync = new object();
        private CancellationTokenSource _audioRefreshCts = new CancellationTokenSource();

        public BigPictureTray()
        {
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _trayIcon = new NotifyIcon
            {
                Icon = Resources.TrayIcon,
                Visible = true,
                Text = Resources.AppTitle,
                ContextMenuStrip = CreateMainMenu(),
            };
            InitializeAsync();
            StartAudioDeviceMonitoring();
            ListenForBigPicture();
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

        private async void InitializeAsync()
        {
            _bluetoothDevice = await RequestBluetoothDeviceAsync();
            var isBtAvailable = _bluetoothDevice != null;
            _btMenuItem.Text = isBtAvailable
                ? Resources.MenuBluetoothTurnOffOnBpClose
                : Resources.MenuBluetoothNoDevice;
            _btMenuItem.Checked = isBtAvailable && _isTurnOffBt;
            _btMenuItem.Enabled = isBtAvailable;
            QueueAudioDeviceRefresh(0);
        }

        private ContextMenuStrip CreateMainMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) => ApplyXboxGipPowerOffMenuState();

            _audioMenuItem = new ToolStripMenuItem(Resources.MenuAudioLoading) { Enabled = false };
            var separatorMenuItem = new ToolStripSeparator();
            _btMenuItem = new ToolStripMenuItem
            {
                Text = Resources.MenuBluetoothNoDevice,
                Enabled = false,
                CheckOnClick = true,
                ToolTipText = Resources.TooltipBluetoothTurnOff,
            };
            _btMenuItem.Click += OnBluetoothMenuItemClick;

            _xboxGipPowerOffMenuItem = new ToolStripMenuItem(Resources.MenuWirelessXboxController)
            {
                CheckOnClick = true,
                ToolTipText = Resources.TooltipXboxGipPowerOff,
            };
            _xboxGipPowerOffMenuItem.Click += OnXboxGipPowerOffMenuItemClick;
            ApplyXboxGipPowerOffMenuState();

            _nightLightBpMenuItem = new ToolStripMenuItem(Resources.MenuNightLightTurnOffOnBpStart)
            {
                CheckOnClick = true,
                Checked = _isTurnOffNightLightOnBpStart,
                ToolTipText = Resources.TooltipNightLightBp,
            };
            _nightLightBpMenuItem.Click += OnNightLightBpMenuItemClick;

            var powerOffControllerMenuItem = new ToolStripMenuItem(Resources.MenuPowerOffController);
            powerOffControllerMenuItem.DropDownItems.Add(_btMenuItem);
            powerOffControllerMenuItem.DropDownItems.Add(_xboxGipPowerOffMenuItem);

            _launchOnStartMenuItem = new ToolStripMenuItem(Resources.MenuLaunchOnStart)
            {
                CheckOnClick = true,
                Checked = _isAutoStart,
                ToolTipText = Resources.TooltipLaunchOnStart,
            };
            _launchOnStartMenuItem.Click += OnLaunchOnStartMenuItemClick;

            var aboutMenuItem = new ToolStripMenuItem(Resources.MenuAbout);
            aboutMenuItem.Click += OnAboutMenuItemClick;

            var exitMenuItem = new ToolStripMenuItem(Resources.MenuExit);
            exitMenuItem.Click += OnExit;

            menu.Items.AddRange(
                new ToolStripItem[]
                {
                    _audioMenuItem,
                    separatorMenuItem,
                    _nightLightBpMenuItem,
                    powerOffControllerMenuItem,
                    _launchOnStartMenuItem,
                    aboutMenuItem,
                    exitMenuItem,
                }
            );
            return menu;
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
            System.Windows.Automation.Automation.RemoveAllEventHandlers();
            NativeAudioApi.StopDeviceWatcher();
            lock (_audioRefreshSync)
            {
                _audioRefreshCts.Cancel();
                _audioRefreshCts.Dispose();
                _audioRefreshCts = new CancellationTokenSource();
            }
            BpmLog.WriteLine("[Main] App exit requested.");

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
