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
        private static string _logDirectory = ResolveDefaultLogDirectory();

        /// <summary>
        /// Current log directory. The ephemeral SYSTEM service is launched with this path so its lines
        /// land in the launching user's log file (SYSTEM can write to the user's profile).
        /// </summary>
        internal static string Directory
        {
            get
            {
                lock (FileLock)
                {
                    return _logDirectory;
                }
            }
        }

        /// <summary>
        /// Overrides the log directory. Used by the ephemeral SYSTEM service, whose own
        /// %LOCALAPPDATA% points at the system profile rather than the launching user's. Call before
        /// the first <see cref="WriteLine"/>.
        /// </summary>
        internal static void UseLogDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(directory);
                lock (FileLock)
                {
                    _logDirectory = directory;
                }
            }
            catch
            {
                // Keep the default directory if the supplied one is unusable.
            }
        }

        // Per-user %LOCALAPPDATA%\BigPictureManager, alongside the application settings. Writable without
        // elevation and not lost when the app is installed under Program Files.
        private static string ResolveDefaultLogDirectory()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BigPictureManager"
                );
                System.IO.Directory.CreateDirectory(dir);
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
                    var logPath = Path.Combine(_logDirectory, LogFileName);
                    TryRotateIfTooLarge(logPath);
                    File.AppendAllText(logPath, line + Environment.NewLine);
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

                var rotatedPath = Path.Combine(_logDirectory, RotatedFileName);
                if (File.Exists(rotatedPath))
                {
                    File.Delete(rotatedPath);
                }

                File.Move(path, rotatedPath);
            }
            catch
            {
                // If rotation fails, append may continue on the same file.
            }
        }
    }
}

