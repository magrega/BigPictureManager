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
        private static readonly string LogDirectory = ResolveLogDirectory();
        private static readonly string LogPath = Path.Combine(LogDirectory, LogFileName);
        private static readonly string RotatedPath = Path.Combine(LogDirectory, RotatedFileName);

        // %ProgramData%\BigPictureManager keeps the elevated UI process and the LocalSystem Xbox
        // service writing to one log, and stays writable when the app is installed under Program Files
        // (where the old next-to-exe location fails for non-elevated users).
        private static string ResolveLogDirectory()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "BigPictureManager"
                );
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

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
                    TryRotateIfTooLarge(LogPath);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
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

                if (File.Exists(RotatedPath))
                {
                    File.Delete(RotatedPath);
                }

                File.Move(path, RotatedPath);
            }
            catch
            {
                // If rotation fails, append may continue on the same file.
            }
        }
    }
}

