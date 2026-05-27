using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace BigPictureManager
{
    /// <summary>
    /// Pauses active system media sessions via Global System Media Transport Controls (GSMTC).
    /// </summary>
    internal static class SystemMediaPause
    {
        /// <summary>
        /// Requests pause on every session that reports <see cref="GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing"/>.
        /// </summary>
        public static async Task PauseAllPlayingAsync()
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
                BpmLog.WriteLine("[Media] No media sessions found; nothing to pause.");
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
                BpmLog.WriteLine("[Media] No playing media sessions to pause.");
            }
            else
            {
                BpmLog.WriteLine("[Media] Paused " + pausedCount + " playing session(s).");
            }
        }

        private static string DescribeSession(GlobalSystemMediaTransportControlsSession session)
        {
            var appId = session.SourceAppUserModelId;
            return string.IsNullOrWhiteSpace(appId) ? "(unknown app)" : appId;
        }
    }
}
