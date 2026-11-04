using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace BigPictureManager
{
    internal enum SteamPreferredMonitorStatus
    {
        NotFound,
        AlreadyDisabled,
        Enabled,
    }

    /// <summary>
    /// Reads and clears Steam Big Picture's Preferred Display setting in config.vdf.
    /// The file is only safe to edit while Steam is not running.
    /// </summary>
    internal static class SteamPreferredMonitor
    {
        private const string ConfigRelativePath = @"config\config.vdf";
        private const int ShutdownTimeoutMs = 45000;
        private const int ShutdownPollMs = 400;

        // Matches the Monitor value under the top-level BigPicture block Steam writes near the end of config.vdf.
        private static readonly Regex BigPictureMonitorRegex = new Regex(
            @"(?is)(""BigPicture""\s*\{\s*""Monitor""\s*"")([^""]*)("")",
            RegexOptions.CultureInvariant | RegexOptions.Compiled
        );

        private static readonly string[] ClientProcessNames = { "steam", "steamwebhelper" };

        public static string TryGetSteamInstallPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var path = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return Path.GetFullPath(path.Replace('/', '\\'));
                    }
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Steam] Could not read SteamPath from the registry: " + ex.Message);
            }

            return null;
        }

        public static string GetConfigPath(string steamInstallPath)
        {
            if (string.IsNullOrWhiteSpace(steamInstallPath))
            {
                throw new ArgumentException("Steam install path is required.", nameof(steamInstallPath));
            }

            return Path.Combine(steamInstallPath, ConfigRelativePath);
        }

        public static SteamPreferredMonitorStatus GetStatus(string configPath, out string currentValue)
        {
            currentValue = null;
            if (!File.Exists(configPath))
            {
                return SteamPreferredMonitorStatus.NotFound;
            }

            var text = File.ReadAllText(configPath, Encoding.UTF8);
            var match = BigPictureMonitorRegex.Match(text);
            if (!match.Success)
            {
                return SteamPreferredMonitorStatus.NotFound;
            }

            currentValue = match.Groups[2].Value;
            return string.IsNullOrEmpty(currentValue)
                ? SteamPreferredMonitorStatus.AlreadyDisabled
                : SteamPreferredMonitorStatus.Enabled;
        }

        /// <summary>
        /// Clears Steam's Preferred Display. Steam must already be closed; otherwise it can overwrite the file from memory.
        /// </summary>
        public static void ClearPreferredMonitor(string configPath)
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException("Steam config.vdf was not found.", configPath);
            }

            var text = File.ReadAllText(configPath, Encoding.UTF8);
            var match = BigPictureMonitorRegex.Match(text);
            if (!match.Success)
            {
                throw new InvalidOperationException("The BigPicture Monitor setting was not found in config.vdf.");
            }

            if (string.IsNullOrEmpty(match.Groups[2].Value))
            {
                return;
            }

            var updated = BigPictureMonitorRegex.Replace(text, m => m.Groups[1].Value + m.Groups[3].Value, 1);
            var backupPath = configPath + ".bpm-backup";
            File.Copy(configPath, backupPath, overwrite: true);
            File.WriteAllText(configPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var verify = GetStatus(configPath, out _);
            if (verify != SteamPreferredMonitorStatus.AlreadyDisabled)
            {
                try
                {
                    File.Copy(backupPath, configPath, overwrite: true);
                }
                catch (Exception restoreEx)
                {
                    BpmLog.WriteLine("[Error] [Steam] Failed to restore config.vdf backup: " + restoreEx.Message);
                }

                throw new InvalidOperationException("config.vdf was written but the Monitor setting is still set.");
            }

            BpmLog.WriteLine("[Steam] Cleared BigPicture Monitor in config.vdf (backup: " + backupPath + ").");
        }

        public static bool IsSteamClientRunning()
        {
            return EnumerateClientProcesses().Any();
        }

        /// <summary>Asks Steam to quit cleanly, then force-kills leftover client processes if needed.</summary>
        public static void ShutdownSteamClient(string steamInstallPath, CancellationToken cancellationToken)
        {
            var steamExe = Path.Combine(steamInstallPath, "steam.exe");
            if (File.Exists(steamExe) && IsSteamClientRunning())
            {
                BpmLog.WriteLine("[Steam] Requesting steam.exe -shutdown.");
                try
                {
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName = steamExe,
                            Arguments = "-shutdown",
                            UseShellExecute = true,
                        }
                    );
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine("[Steam] steam.exe -shutdown failed to start: " + ex.Message);
                }
            }

            if (WaitForClientExit(ShutdownTimeoutMs / 2, cancellationToken))
            {
                return;
            }

            BpmLog.WriteLine("[Steam] Steam did not exit after -shutdown; force-closing client processes.");
            foreach (var process in EnumerateClientProcesses())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine("[Error] [Steam] Could not kill " + process.ProcessName + " PID " + process.Id + ": " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!WaitForClientExit(ShutdownTimeoutMs / 2, cancellationToken))
            {
                throw new InvalidOperationException("Steam did not exit.");
            }
        }

        public static void LaunchSteam(string steamInstallPath)
        {
            var steamExe = Path.Combine(steamInstallPath, "steam.exe");
            if (!File.Exists(steamExe))
            {
                throw new FileNotFoundException("steam.exe was not found.", steamExe);
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = true,
                }
            );
            BpmLog.WriteLine("[Steam] Launched steam.exe.");
        }

        private static bool WaitForClientExit(int timeoutMs, CancellationToken cancellationToken)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (unchecked(Environment.TickCount - deadline) < 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSteamClientRunning())
                {
                    return true;
                }

                Thread.Sleep(ShutdownPollMs);
            }

            return !IsSteamClientRunning();
        }

        private static IEnumerable<Process> EnumerateClientProcesses()
        {
            foreach (var name in ClientProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    yield return process;
                }
            }
        }

        /// <summary>Maps a caught exception to a short user-facing reason for the tray toast.</summary>
        public static string DescribeFailure(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return "cancelled.";
            }

            if (ex is FileNotFoundException fileNotFound)
            {
                if (!string.IsNullOrEmpty(fileNotFound.FileName)
                    && fileNotFound.FileName.IndexOf("steam.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "couldn't find steam.exe.";
                }

                if (!string.IsNullOrEmpty(fileNotFound.Message)
                    && fileNotFound.Message.IndexOf("install", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "couldn't find the Steam install folder.";
                }

                return "couldn't find the Steam settings file.";
            }

            if (ex is UnauthorizedAccessException
                || (ex is IOException && IsAccessDenied(ex))
                || (ex is Win32Exception win32 && (win32.NativeErrorCode == 5 || win32.NativeErrorCode == 740)))
            {
                return "need elevated rights to edit Steam's settings.";
            }

            if (ex is InvalidOperationException && ex.Message.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "couldn't close Steam.";
            }

            if (ex is InvalidOperationException && ex.Message.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "couldn't find the monitor setting in Steam's config.";
            }

            return ex.Message;
        }

        private static bool IsAccessDenied(Exception ex)
        {
            var hresult = ex.HResult;
            // ERROR_ACCESS_DENIED / ERROR_SHARING_VIOLATION wrapped as HRESULT.
            return hresult == unchecked((int)0x80070005) || hresult == unchecked((int)0x80070020);
        }
    }
}
