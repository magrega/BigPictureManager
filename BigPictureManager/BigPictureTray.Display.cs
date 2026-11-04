using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BigPictureManager.Properties;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        // Steam may still be laying out its window right after the display change and can move it back.
        private const int BigPictureWindowSettleMs = 1500;

        // How long after Big Picture exit Steam may take to show its desktop windows again.
        private const int SteamWindowWatchMs = 6000;
        private const int SteamWindowPollMs = 500;

        private readonly object _displaySync = new object();
        private readonly System.Windows.Forms.Timer _displayRecheckTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private List<DisplayMonitorInfo> _displays = new List<DisplayMonitorInfo>();

        /// <summary>Layout from before this session's switch; guarded by <see cref="_displaySync"/>.</summary>
        private DisplaySnapshot _displaysBeforeBigPicture;

        /// <summary>The selected monitor was on the desktop at the last check this session; used to notify only on loss.</summary>
        private volatile bool _bpDisplayAvailable;

        private static string SelectedDisplayPath => Settings.Default.BpDisplayDevicePath;

        private static string SelectedDisplayName =>
            string.IsNullOrWhiteSpace(Settings.Default.BpDisplayName) ? Resources.MenuDisplayUnknownName : Settings.Default.BpDisplayName;

        private void InitializeDisplaySwitching()
        {
            _displayRecheckTimer.Tick += OnDisplayRecheckTick;
        }

        private void RefreshDisplays()
        {
            try
            {
                _displays = DisplayTopology.GetActiveMonitors();
            }
            catch (Win32Exception ex)
            {
                BpmLog.WriteLine("[Error] [Display] Could not list monitors: " + ex.Message);
                _displays = new List<DisplayMonitorInfo>();
            }
        }

        private void BuildDisplaySection()
        {
            var selectedPath = SelectedDisplayPath;
            _trayMenu.AddRadio(Resources.MenuDisplayDontChange, string.IsNullOrEmpty(selectedPath), () => SelectDisplay(null));

            var selectedAvailable = false;
            foreach (var display in _displays)
            {
                var captured = display;
                var selected = IsSameDisplay(display.DevicePath, selectedPath);
                selectedAvailable |= selected;
                _trayMenu.AddRadio(display.Name ?? Resources.MenuDisplayUnknownName, selected, () => SelectDisplay(captured));
            }

            // Keep a saved but unavailable monitor visible (greyed) so turning the TV off doesn't drop the choice.
            if (!string.IsNullOrEmpty(selectedPath) && !selectedAvailable)
            {
                _trayMenu.AddRadio(string.Format(Resources.MenuDisplayNotAvailable, SelectedDisplayName), true, null, enabled: false);
            }
        }

        private void SelectDisplay(DisplayMonitorInfo display)
        {
            Settings.Default.BpDisplayDevicePath = display?.DevicePath ?? string.Empty;
            Settings.Default.BpDisplayName = display?.Name ?? string.Empty;
            Settings.Default.Save();
            BpmLog.WriteLine("[Display] Big Picture display set to: " + (display == null ? "don't change" : SelectedDisplayName));

            if (display != null && _bigPictureActive)
            {
                _ = Task.Run(() => ApplyBigPictureDisplay());
            }
        }

        /// <summary>
        /// Makes the selected monitor primary and moves the Big Picture window onto it. If the monitor isn't on the
        /// desktop, nothing changes (Big Picture stays on the current main monitor) and the user is notified.
        /// Blocks for a few seconds (display mode change plus a settle re-check), so call it off the UI thread.
        /// </summary>
        private void ApplyBigPictureDisplay()
        {
            var devicePath = SelectedDisplayPath;
            if (string.IsNullOrEmpty(devicePath))
            {
                return;
            }

            var bounds = Rectangle.Empty;
            WindowLayout windowsToRestore = null;
            try
            {
                lock (_displaySync)
                {
                    if (!_bigPictureActive)
                    {
                        return;
                    }

                    var windows = WindowLayout.Capture(DisplayTopology.GetActiveMonitors(), _bpWatcher.WindowHandle);
                    var before = DisplayTopology.Capture();
                    var result = DisplayTopology.MakePrimary(devicePath, out bounds);
                    _bpDisplayAvailable = result != PrimaryDisplayResult.NotAvailable;
                    if (result == PrimaryDisplayResult.Changed)
                    {
                        windowsToRestore = windows;
                        if (_displaysBeforeBigPicture == null)
                        {
                            _displaysBeforeBigPicture = before;
                        }
                    }

                    BpmLog.WriteLine("[Display] " + SelectedDisplayName + ": " + result);
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                BpmLog.WriteLine("[Error] [Display] Switching to " + SelectedDisplayName + " failed: " + ex.Message);
                return;
            }

            if (!_bpDisplayAvailable)
            {
                AppNotificationSetup.ShowNotification(string.Format(Resources.MsgDisplayNotAvailable, SelectedDisplayName));
                return;
            }

            MoveBigPictureWindow(bounds);
            Thread.Sleep(BigPictureWindowSettleMs);
            if (!_bigPictureActive)
            {
                return;
            }

            MoveBigPictureWindow(bounds);
            RestoreWindowLayout(windowsToRestore);
        }

        /// <summary>Runs after apps have finished reacting to the display change, so their own moves are undone.</summary>
        private static void RestoreWindowLayout(WindowLayout windows)
        {
            if (windows == null)
            {
                return;
            }

            try
            {
                windows.Restore(DisplayTopology.GetActiveMonitors());
            }
            catch (Win32Exception ex)
            {
                BpmLog.WriteLine("[Error] [Display] Putting windows back on their monitors failed: " + ex.Message);
            }
        }

        private void MoveBigPictureWindow(Rectangle bounds)
        {
            var hwnd = _bpWatcher.WindowHandle;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            using (new PerMonitorDpiScope())
            {
                if (NativeMethods.GetWindowRect(hwnd, out var rect)
                    && bounds.Contains((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2))
                {
                    return;
                }

                var maximized = NativeMethods.IsZoomed(hwnd);
                if (maximized)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                }

                if (!NativeMethods.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        bounds.X,
                        bounds.Y,
                        bounds.Width,
                        bounds.Height,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
                {
                    BpmLog.WriteLine("[Error] [Display] Moving the Big Picture window failed: Win32 " + Marshal.GetLastWin32Error());
                    return;
                }

                if (maximized)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
                }

                BpmLog.WriteLine("[Display] Moved the Big Picture window to " + SelectedDisplayName + ".");
            }
        }

        private void RestartDisplayRecheck()
        {
            if (!string.IsNullOrEmpty(SelectedDisplayPath))
            {
                _displayRecheckTimer.Stop();
                _displayRecheckTimer.Start();
            }
        }

        private void OnDisplayRecheckTick(object sender, EventArgs e)
        {
            _displayRecheckTimer.Stop();
            if (_bigPictureActive)
            {
                _ = Task.Run(() => RecheckBigPictureDisplay());
            }
        }

        /// <summary>
        /// Warns when the selected monitor drops out mid-session. It deliberately doesn't switch back when the
        /// monitor returns; the switch happens only when Big Picture starts.
        /// </summary>
        private void RecheckBigPictureDisplay()
        {
            var devicePath = SelectedDisplayPath;
            if (string.IsNullOrEmpty(devicePath))
            {
                return;
            }

            bool available;
            try
            {
                available = DisplayTopology.GetActiveMonitors().Any(m => IsSameDisplay(m.DevicePath, devicePath));
            }
            catch (Win32Exception ex)
            {
                BpmLog.WriteLine("[Error] [Display] Could not check monitors: " + ex.Message);
                return;
            }

            var wasAvailable = _bpDisplayAvailable;
            _bpDisplayAvailable = available;
            if (wasAvailable && !available && _bigPictureActive)
            {
                BpmLog.WriteLine("[Display] " + SelectedDisplayName + " disconnected during Big Picture.");
                AppNotificationSetup.ShowNotification(string.Format(Resources.MsgDisplayDisconnected, SelectedDisplayName));
            }
        }

        private void RestoreDisplaysAfterBigPicture()
        {
            _bpDisplayAvailable = false;
            WindowLayout windows;
            lock (_displaySync)
            {
                var snapshot = _displaysBeforeBigPicture;
                _displaysBeforeBigPicture = null;
                if (snapshot == null)
                {
                    return;
                }

                try
                {
                    windows = WindowLayout.Capture(DisplayTopology.GetActiveMonitors(), IntPtr.Zero);
                    DisplayTopology.Restore(snapshot);
                    BpmLog.WriteLine("[Display] Display layout restored after Big Picture.");
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                {
                    BpmLog.WriteLine("[Error] [Display] Restoring the display layout failed: " + ex.Message);
                    return;
                }
            }

            Thread.Sleep(BigPictureWindowSettleMs);
            RestoreWindowLayout(windows);
            MoveSteamWindowsToMainDisplay();
        }

        /// <summary>
        /// Steam hides its desktop windows (client, friends) during Big Picture and shows them again on the Big Picture
        /// monitor after it closes, so they can't be captured beforehand. Watch briefly and move them to the main monitor.
        /// </summary>
        private void MoveSteamWindowsToMainDisplay()
        {
            var devicePath = SelectedDisplayPath;
            var moved = 0;
            for (var elapsed = 0; elapsed < SteamWindowWatchMs && !_bigPictureActive; elapsed += SteamWindowPollMs)
            {
                try
                {
                    var monitors = DisplayTopology.GetActiveMonitors();
                    var bigPictureMonitor = monitors.FirstOrDefault(m => IsSameDisplay(m.DevicePath, devicePath));
                    var mainMonitor = monitors.FirstOrDefault(m => m.Bounds.Location == Point.Empty);
                    if (bigPictureMonitor == null || mainMonitor == null || bigPictureMonitor == mainMonitor)
                    {
                        // The Big Picture monitor left the desktop or is the main one; Windows already placed everything.
                        break;
                    }

                    moved += WindowLayout.MoveWindowsBetweenMonitors(bigPictureMonitor.Bounds, mainMonitor.Bounds, IsSteamWindow);
                }
                catch (Win32Exception ex)
                {
                    BpmLog.WriteLine("[Error] [Display] Moving Steam windows to the main monitor failed: " + ex.Message);
                    break;
                }

                Thread.Sleep(SteamWindowPollMs);
            }

            if (moved > 0)
            {
                BpmLog.WriteLine("[Display] Moved " + moved + " Steam window(s) from " + SelectedDisplayName + " to the main monitor.");
            }
        }

        private static bool IsSteamWindow(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    return process.ProcessName.StartsWith(AppConstants.SteamProcessNamePrefix, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException)
            {
                // The process exited between finding the window and checking it.
                return false;
            }
        }

        private static bool IsSameDisplay(string devicePath, string otherDevicePath)
        {
            return string.Equals(devicePath, otherDevicePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
