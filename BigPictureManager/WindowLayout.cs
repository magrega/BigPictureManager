using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;

namespace BigPictureManager
{
    /// <summary>
    /// Where the user's top-level windows sit relative to their monitors. Changing the primary monitor shifts every
    /// monitor on the desktop, and some apps (Chrome, Electron apps) react by jumping to another monitor; restoring
    /// this layout puts each window back on its own monitor at the same spot.
    /// </summary>
    internal sealed class WindowLayout
    {
        private static readonly HashSet<string> ShellWindowClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        };

        private readonly List<SavedWindow> _windows;

        private WindowLayout(List<SavedWindow> windows)
        {
            _windows = windows;
        }

        /// <summary>Records visible, non-minimized app windows and the monitor each one is on.</summary>
        public static WindowLayout Capture(IReadOnlyList<DisplayMonitorInfo> monitors, IntPtr excludedWindow)
        {
            if (monitors == null)
            {
                throw new ArgumentNullException(nameof(monitors));
            }

            var ownProcessId = GetOwnProcessId();
            var windows = new List<SavedWindow>();
            using (new PerMonitorDpiScope())
            {
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    if (hwnd != excludedWindow
                        && IsUserWindow(hwnd, ownProcessId)
                        && TryGetBounds(hwnd, out var bounds))
                    {
                        var monitor = FindMonitor(monitors, bounds);
                        if (monitor != null)
                        {
                            windows.Add(new SavedWindow(hwnd, bounds, monitor));
                        }
                    }

                    return true;
                }, IntPtr.Zero);
            }

            return new WindowLayout(windows);
        }

        /// <summary>
        /// Moves every recorded window back to the same spot on its original monitor, wherever that monitor now sits
        /// on the desktop. Windows whose monitor is no longer active are left where Windows put them.
        /// </summary>
        public void Restore(IReadOnlyList<DisplayMonitorInfo> monitors)
        {
            if (monitors == null)
            {
                throw new ArgumentNullException(nameof(monitors));
            }

            var moved = 0;
            var failed = 0;
            using (new PerMonitorDpiScope())
            {
                foreach (var saved in _windows)
                {
                    var monitor = monitors.FirstOrDefault(
                        m => string.Equals(m.DevicePath, saved.Monitor.DevicePath, StringComparison.OrdinalIgnoreCase)
                    );
                    if (monitor == null
                        || !NativeMethods.IsWindow(saved.Handle)
                        || NativeMethods.IsIconic(saved.Handle)
                        || !TryGetBounds(saved.Handle, out var current))
                    {
                        continue;
                    }

                    var target = saved.Bounds;
                    target.Offset(monitor.Bounds.X - saved.Monitor.Bounds.X, monitor.Bounds.Y - saved.Monitor.Bounds.Y);
                    if (current == target)
                    {
                        continue;
                    }

                    // No activation: Big Picture must keep the foreground while desktop windows are rearranged.
                    if (NativeMethods.SetWindowPos(
                            saved.Handle,
                            IntPtr.Zero,
                            target.X,
                            target.Y,
                            target.Width,
                            target.Height,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
                    {
                        moved++;
                    }
                    else
                    {
                        // Typically ERROR_ACCESS_DENIED: windows of elevated apps can't be moved from a normal process.
                        failed++;
                    }
                }
            }

            BpmLog.WriteLine(
                "[Display] Put " + moved + " window(s) back on their monitors"
                    + (failed > 0 ? "; " + failed + " could not be moved (elevated apps)." : ".")
            );
        }

        /// <summary>
        /// Moves app windows centred on <paramref name="from"/> and accepted by <paramref name="filter"/> to the same
        /// relative spot on <paramref name="to"/>, shrunk if needed to fit. Returns how many were moved.
        /// </summary>
        public static int MoveWindowsBetweenMonitors(Rectangle from, Rectangle to, Func<IntPtr, bool> filter)
        {
            if (filter == null)
            {
                throw new ArgumentNullException(nameof(filter));
            }

            var ownProcessId = GetOwnProcessId();
            var moved = 0;
            using (new PerMonitorDpiScope())
            {
                var candidates = new List<KeyValuePair<IntPtr, Rectangle>>();
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    if (IsUserWindow(hwnd, ownProcessId)
                        && TryGetBounds(hwnd, out var bounds)
                        && from.Contains(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2))
                    {
                        candidates.Add(new KeyValuePair<IntPtr, Rectangle>(hwnd, bounds));
                    }

                    return true;
                }, IntPtr.Zero);

                foreach (var candidate in candidates.Where(c => filter(c.Key)))
                {
                    if (MoveToMonitor(candidate.Key, candidate.Value, from, to))
                    {
                        moved++;
                    }
                }
            }

            return moved;
        }

        private static bool MoveToMonitor(IntPtr hwnd, Rectangle bounds, Rectangle from, Rectangle to)
        {
            var maximized = NativeMethods.IsZoomed(hwnd);
            if (maximized)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                TryGetBounds(hwnd, out bounds);
            }

            var width = Math.Min(bounds.Width, to.Width);
            var height = Math.Min(bounds.Height, to.Height);
            var x = to.X + (int)((long)(bounds.X - from.X) * to.Width / from.Width);
            var y = to.Y + (int)((long)(bounds.Y - from.Y) * to.Height / from.Height);
            x = Math.Max(to.X, Math.Min(x, to.Right - width));
            y = Math.Max(to.Y, Math.Min(y, to.Bottom - height));

            if (!NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
            {
                return false;
            }

            if (maximized)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
            }

            return true;
        }

        private static uint GetOwnProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return (uint)process.Id;
            }
        }

        private static bool IsUserWindow(IntPtr hwnd, uint ownProcessId)
        {
            if (!NativeMethods.IsWindowVisible(hwnd)
                || NativeMethods.IsIconic(hwnd)
                || NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero
                || NativeMethods.GetWindowTextLength(hwnd) == 0)
            {
                return false;
            }

            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcessId)
            {
                return false;
            }

            // Suspended UWP apps and windows on other virtual desktops are "cloaked": visible to the API, not on screen.
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                && cloaked != 0)
            {
                return false;
            }

            var className = new StringBuilder(64);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            return !ShellWindowClasses.Contains(className.ToString());
        }

        private static bool TryGetBounds(IntPtr hwnd, out Rectangle bounds)
        {
            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                return !bounds.IsEmpty;
            }

            bounds = Rectangle.Empty;
            return false;
        }

        /// <summary>The monitor showing the largest part of the window, or null if it is off every monitor.</summary>
        private static DisplayMonitorInfo FindMonitor(IReadOnlyList<DisplayMonitorInfo> monitors, Rectangle bounds)
        {
            DisplayMonitorInfo best = null;
            long bestArea = 0;
            foreach (var monitor in monitors)
            {
                var overlap = Rectangle.Intersect(monitor.Bounds, bounds);
                long area = (long)overlap.Width * overlap.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = monitor;
                }
            }

            return best;
        }

        private sealed class SavedWindow
        {
            public SavedWindow(IntPtr handle, Rectangle bounds, DisplayMonitorInfo monitor)
            {
                Handle = handle;
                Bounds = bounds;
                Monitor = monitor;
            }

            public IntPtr Handle { get; }

            public Rectangle Bounds { get; }

            public DisplayMonitorInfo Monitor { get; }
        }
    }

    /// <summary>
    /// Switches the calling thread to per-monitor DPI awareness so window rectangles are in physical pixels, the same
    /// space as display-config monitor bounds, even when monitors use different scaling.
    /// </summary>
    internal sealed class PerMonitorDpiScope : IDisposable
    {
        private readonly IntPtr _previousContext =
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        public void Dispose()
        {
            if (_previousContext != IntPtr.Zero)
            {
                NativeMethods.SetThreadDpiAwarenessContext(_previousContext);
            }
        }
    }
}
