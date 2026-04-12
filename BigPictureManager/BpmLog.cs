using System;
using System.IO;

namespace BigPictureManager
{
    /// <summary>
    /// Single shared log file (BPMLog.txt) with size-based rotation.
    /// </summary>
    internal static class BpmLog
    {
        private const string LogFileName = "BPMLog.txt";
        private const string RotatedFileName = "BPMLog_old.txt";
        private const long MaxSizeBytes = 10L * 1024 * 1024;
        private static readonly object FileLock = new object();

        /// <summary>
        /// Writes one line. <paramref name="message"/> should already include a category prefix, e.g. <c>[Main] ...</c>.
        /// </summary>
        public static void WriteLine(string message)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message;
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // ignore
            }

            try
            {
                lock (FileLock)
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
                    TryRotateIfTooLarge(path);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break app flow.
            }
        }

        private static void TryRotateIfTooLarge(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var len = new FileInfo(path).Length;
                if (len < MaxSizeBytes)
                {
                    return;
                }

                var rotated = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RotatedFileName);
                if (File.Exists(rotated))
                {
                    File.Delete(rotated);
                }

                File.Move(path, rotated);
            }
            catch
            {
                // If rotation fails, append may continue on the same file.
            }
        }
    }
}

namespace TinyScreen.Services
{
    internal sealed class BpmNightLightLogger : INightLightLogger
    {
        public void Info(string message)
        {
            BigPictureManager.BpmLog.WriteLine(message);
        }

        public void Error(string message)
        {
            BigPictureManager.BpmLog.WriteLine(message);
        }
    }
}
