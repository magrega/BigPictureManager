using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace BigPictureManager
{
    /// <summary>
    /// Pauses playing media. SMTC-aware apps (Chrome, Spotify, …) are paused via Global System Media
    /// Transport Controls; iTunes is paused separately because it never registers an SMTC session.
    /// </summary>
    internal static class SystemMediaPause
    {
        /// <summary>
        /// Pauses every SMTC session that reports
        /// <see cref="GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing"/>, then pauses iTunes.
        /// </summary>
        public static async Task PauseAllPlayingAsync()
        {
            await PauseSmtcSessionsAsync();
            PauseITunes();
        }

        private static async Task PauseSmtcSessionsAsync()
        {
            GlobalSystemMediaTransportControlsSessionManager manager;
            try
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Media] Could not access system media session manager: " + ex.Message);
                return;
            }

            var sessions = manager.GetSessions();
            if (sessions == null || sessions.Count == 0)
            {
                BpmLog.WriteLine("[Media] No SMTC media sessions found.");
                return;
            }

            var pausedCount = 0;
            foreach (var session in sessions)
            {
                if (session == null)
                {
                    continue;
                }

                GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
                try
                {
                    status = session.GetPlaybackInfo().PlaybackStatus;
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine(
                        "[Error] [Media] Could not read playback status for \""
                            + DescribeSession(session)
                            + "\": "
                            + ex.Message
                    );
                    continue;
                }

                // Log every session so we can see exactly what the system reports (e.g. whether iTunes appears).
                BpmLog.WriteLine("[Media] SMTC session \"" + DescribeSession(session) + "\" status: " + status + ".");

                if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    continue;
                }

                try
                {
                    var paused = await session.TryPauseAsync();
                    if (paused)
                    {
                        pausedCount++;
                        BpmLog.WriteLine("[Media] Paused \"" + DescribeSession(session) + "\".");
                    }
                    else
                    {
                        BpmLog.WriteLine(
                            "[Media] Pause was not accepted for \"" + DescribeSession(session) + "\"."
                        );
                    }
                }
                catch (Exception ex)
                {
                    BpmLog.WriteLine(
                        "[Error] [Media] Pause failed for \"" + DescribeSession(session) + "\": " + ex.Message
                    );
                }
            }

            if (pausedCount == 0)
            {
                BpmLog.WriteLine("[Media] No playing SMTC sessions to pause.");
            }
            else
            {
                BpmLog.WriteLine("[Media] Paused " + pausedCount + " playing SMTC session(s).");
            }
        }

        /// <summary>
        /// Pauses iTunes by posting WM_APPCOMMAND to its main window. iTunes ignores SMTC but handles
        /// media app-commands at its window (the same path the keyboard media key reaches it through).
        /// </summary>
        private static void PauseITunes()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("iTunes");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Media] Could not enumerate iTunes processes: " + ex.Message);
                return;
            }

            if (processes.Length == 0)
            {
                return;
            }

            var sent = false;
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var hWnd = process.MainWindowHandle;
                        if (hWnd == IntPtr.Zero)
                        {
                            continue;
                        }

                        // Dedicated PAUSE (not the PLAY_PAUSE toggle): keeps the track position and never
                        // starts playback if iTunes is already paused.
                        var lParam = (IntPtr)(NativeMethods.APPCOMMAND_MEDIA_PAUSE << 16);
                        NativeMethods.SendMessage(hWnd, NativeMethods.WM_APPCOMMAND, hWnd, lParam);
                        sent = true;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Media] iTunes pause failed: " + ex.Message);
                return;
            }

            BpmLog.WriteLine(
                sent
                    ? "[Media] Sent play/pause media command to iTunes."
                    : "[Media] iTunes is running but exposed no window to receive the media command."
            );
        }

        private static string DescribeSession(GlobalSystemMediaTransportControlsSession session)
        {
            var appId = session.SourceAppUserModelId;
            return string.IsNullOrWhiteSpace(appId) ? "(unknown app)" : appId;
        }
    }
}
