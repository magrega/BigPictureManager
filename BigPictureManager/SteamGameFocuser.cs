using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace BigPictureManager
{
    /// <summary>A game process launched by Steam and its main window.</summary>
    internal sealed class SteamGameInfo
    {
        public int ProcessId { get; set; }
        public string ExePath { get; set; }
        public int AppId { get; set; }
        public IntPtr WindowHandle { get; set; }
        public string WindowTitle { get; set; }

        public override string ToString()
        {
            return "AppId=" + AppId + " PID=" + ProcessId + " Exe=" + ExePath + " Title=\"" + WindowTitle + "\"";
        }
    }

    /// <summary>Finds the game currently run by Steam and brings its window to the foreground.</summary>
    internal static class SteamGameFocuser
    {
        // Steam's own helpers that can be children of steam.exe but are never the game.
        private static readonly HashSet<string> SteamHelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steamwebhelper.exe", "gameoverlayui.exe", "gameoverlayui64.exe", "steamerrorreporter.exe",
            "steamerrorreporter64.exe", "steamservice.exe", "streaming_client.exe", "steamsysinfo.exe",
            "x64launcher.exe", "x86launcher.exe", "vrmonitor.exe", "conhost.exe",
        };

        private static readonly Regex OverlayPidRegex = new Regex(@"-pid\s+(\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex OverlayGameIdRegex = new Regex(@"-gameid\s+(\d+)", RegexOptions.IgnoreCase);

        private const int SteamMenuShowDelayMs = 250;
        private const int SteamMenuHideTimeoutMs = 1000;
        private const int SteamSettleDelayMs = 150;

        /// <summary>
        /// Gives focus to the running Steam game after a Guide double press. Steam reacts to both presses
        /// (menu open, menu close), so its window is given a moment to go away first; if it stays, the game
        /// is brought over it anyway. Returns the focused game, or null if no game runs or it already had focus.
        /// </summary>
        public static SteamGameInfo FocusGameAfterGuideDoublePress()
        {
            if (GetRunningAppId() == 0)
            {
                return null;
            }

            // Steam may still be about to show its menu for the first press.
            Thread.Sleep(SteamMenuShowDelayMs);
            var deadline = Environment.TickCount + SteamMenuHideTimeoutMs;
            while (IsSteamUiForeground() && unchecked(Environment.TickCount - deadline) < 0)
            {
                Thread.Sleep(50);
            }

            Thread.Sleep(SteamSettleDelayMs);

            var game = FindRunningGame();
            if (game == null)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var foregroundPid);
            if (foregroundPid == game.ProcessId)
            {
                return null;
            }

            return FocusWindow(game.WindowHandle) ? game : null;
        }

        /// <summary>
        /// Finds the game process Steam is running. Prefers the overlay's "-pid" argument, which pins the
        /// exact game even when it was started through a launcher; falls back to descendants of steam.exe.
        /// </summary>
        public static SteamGameInfo FindRunningGame()
        {
            var processes = SnapshotProcesses();
            var runningAppId = GetRunningAppId();

            // Steam starts the overlay as "gameoverlayui64.exe -pid <game pid> -steampid <pid> -gameid <appid>".
            foreach (var overlay in processes.Values.Where(p => p.Name.StartsWith("gameoverlayui", StringComparison.OrdinalIgnoreCase)))
            {
                var pidMatch = OverlayPidRegex.Match(overlay.CommandLine ?? string.Empty);
                if (!pidMatch.Success || !processes.TryGetValue(int.Parse(pidMatch.Groups[1].Value), out var game))
                {
                    continue;
                }

                var gameIdMatch = OverlayGameIdRegex.Match(overlay.CommandLine);
                var appId = gameIdMatch.Success && int.TryParse(gameIdMatch.Groups[1].Value, out var parsed) ? parsed : runningAppId;
                var info = BuildInfo(game, appId);
                if (info.WindowHandle != IntPtr.Zero)
                {
                    return info;
                }
            }

            var steamPids = new HashSet<int>(processes.Values
                .Where(p => p.Name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.ProcessId));
            if (steamPids.Count == 0)
            {
                return null;
            }

            // Prefer the deepest descendant: in launcher -> game chains the game is the leaf.
            return processes.Values
                .Where(p => !SteamHelperNames.Contains(p.Name))
                .Select(p => new { Process = p, Depth = DepthBelow(p, steamPids, processes) })
                .Where(x => x.Depth > 0)
                .OrderByDescending(x => x.Depth)
                .Select(x => BuildInfo(x.Process, runningAppId))
                .FirstOrDefault(i => i.WindowHandle != IntPtr.Zero);
        }

        /// <summary>Restores the window if minimized and makes it the foreground window.</summary>
        public static bool FocusWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            if (NativeMethods.GetForegroundWindow() == hwnd)
            {
                return true;
            }

            // Windows refuses SetForegroundWindow from a background process; a synthetic Alt press marks
            // this process as having received the last input, which lifts the foreground lock.
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

            var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
            var currentThread = NativeMethods.GetCurrentThreadId();
            var attached = foregroundThread != 0
                && foregroundThread != currentThread
                && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.SetFocus(hwnd);
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            for (var i = 0; i < 10 && NativeMethods.GetForegroundWindow() != hwnd; i++)
            {
                Thread.Sleep(20);
            }

            return NativeMethods.GetForegroundWindow() == hwnd;
        }

        private static int GetRunningAppId()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                return key?.GetValue("RunningAppID") is int id ? id : 0;
            }
        }

        private static bool IsSteamUiForeground()
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    return process.ProcessName.StartsWith(AppConstants.SteamProcessNamePrefix, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException)
            {
                // The foreground process exited between the two calls.
                return false;
            }
        }

        private sealed class ProcessEntry
        {
            public int ProcessId;
            public int ParentProcessId;
            public string Name;
            public string ExePath;
            public string CommandLine;
            public DateTime? CreatedAt;
        }

        private static Dictionary<int, ProcessEntry> SnapshotProcesses()
        {
            var result = new Dictionary<int, ProcessEntry>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process"))
            using (var collection = searcher.Get())
            {
                foreach (ManagementObject mo in collection)
                {
                    using (mo)
                    {
                        var created = mo["CreationDate"] as string;
                        var entry = new ProcessEntry
                        {
                            ProcessId = Convert.ToInt32(mo["ProcessId"]),
                            ParentProcessId = Convert.ToInt32(mo["ParentProcessId"]),
                            Name = mo["Name"] as string ?? string.Empty,
                            ExePath = mo["ExecutablePath"] as string,
                            CommandLine = mo["CommandLine"] as string,
                            CreatedAt = created != null ? ManagementDateTimeConverter.ToDateTime(created) : (DateTime?)null,
                        };
                        result[entry.ProcessId] = entry;
                    }
                }
            }

            return result;
        }

        // A parent PID can be reused by an unrelated newer process, so the parent must be older than the child.
        private static ProcessEntry GetParent(ProcessEntry entry, Dictionary<int, ProcessEntry> all)
        {
            if (entry.ParentProcessId == 0
                || entry.ParentProcessId == entry.ProcessId
                || !all.TryGetValue(entry.ParentProcessId, out var parent))
            {
                return null;
            }

            return parent.CreatedAt > entry.CreatedAt ? null : parent;
        }

        /// <summary>Number of generations between the process and the nearest ancestor in <paramref name="ancestors"/>; 0 if none.</summary>
        private static int DepthBelow(ProcessEntry entry, HashSet<int> ancestors, Dictionary<int, ProcessEntry> all)
        {
            var depth = 0;
            for (var current = GetParent(entry, all); current != null && depth < 32; current = GetParent(current, all))
            {
                depth++;
                if (ancestors.Contains(current.ProcessId))
                {
                    return depth;
                }
            }

            return 0;
        }

        private static SteamGameInfo BuildInfo(ProcessEntry entry, int appId)
        {
            var hwnd = FindMainWindow(entry.ProcessId);
            return new SteamGameInfo
            {
                ProcessId = entry.ProcessId,
                ExePath = entry.ExePath ?? entry.Name,
                AppId = appId,
                WindowHandle = hwnd,
                WindowTitle = hwnd != IntPtr.Zero ? GetWindowTitle(hwnd) : null,
            };
        }

        // Process.MainWindowHandle is not used: it can be 0 or a splash/hidden window for minimized games.
        private static IntPtr FindMainWindow(int processId)
        {
            var best = IntPtr.Zero;
            long bestArea = -1;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != processId
                    || !NativeMethods.IsWindowVisible(hwnd)
                    || NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                {
                    return true;
                }

                long area = 0;
                if (NativeMethods.IsIconic(hwnd))
                {
                    area = long.MaxValue / 2;
                }
                else if (NativeMethods.GetWindowRect(hwnd, out var rect))
                {
                    area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                }

                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }

                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var text = new StringBuilder(NativeMethods.GetWindowTextLength(hwnd) + 1);
            NativeMethods.GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }
    }
}
