using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BigPictureManager
{
    /// <summary>
    /// Legacy elevated logon scheduled task from older versions. Kept only to detect and remove it during
    /// migration to the per-user <see cref="StartupRegistration"/> Run entry.
    /// </summary>
    internal static class ElevatedLogonStartupTask
    {
        public const string TaskName = "BigPictureManager";

        public static bool IsRegistered()
        {
            var exitCode = RunSchTasks("/Query /TN \"" + TaskName + "\"", out _, out _);
            var exists = exitCode == 0;
            BpmLog.WriteLine(
                exists
                    ? "[Startup] Legacy scheduled task \"" + TaskName + "\" is present."
                    : "[Startup] Legacy scheduled task \"" + TaskName + "\" is not present."
            );

            return exists;
        }

        public static bool TryUnregister(out string errorMessage)
        {
            errorMessage = null;
            BpmLog.WriteLine("[Startup] Deleting scheduled task \"" + TaskName + "\".");

            var exitCode = RunSchTasks("/Delete /TN \"" + TaskName + "\" /F", out _, out _);
            if (exitCode == 0)
            {
                BpmLog.WriteLine("[Startup] Scheduled task \"" + TaskName + "\" deleted successfully.");
                return true;
            }

            errorMessage = DescribeSchTasksFailure("delete", exitCode);
            BpmLog.WriteLine("[Error] [Startup] Failed to delete scheduled task: " + errorMessage);
            return false;
        }

        /// <summary>
        /// Removes legacy Startup-folder shortcut from older versions.
        /// </summary>
        public static void RemoveLegacyStartupShortcut(string exePath)
        {
            try
            {
                var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var shortcutPath = Path.Combine(
                    startupPath,
                    Path.GetFileNameWithoutExtension(exePath) + ".lnk"
                );
                if (!File.Exists(shortcutPath))
                {
                    return;
                }

                File.Delete(shortcutPath);
                BpmLog.WriteLine("[Startup] Removed legacy Startup folder shortcut: " + shortcutPath);
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Startup] Failed to remove legacy Startup shortcut: " + ex.Message);
            }
        }

        private static string DescribeSchTasksFailure(string operation, int exitCode)
        {
            if (operation == "create")
            {
                if (exitCode == 1)
                {
                    return "Could not create the scheduled task. Administrator rights are required.";
                }

                return "Could not create the scheduled task \""
                    + TaskName
                    + "\" (schtasks exit code "
                    + exitCode
                    + ").";
            }

            if (exitCode == 1)
            {
                return "Could not delete the scheduled task. Administrator rights may be required, or the task may already be removed.";
            }

            return "Could not delete the scheduled task \""
                + TaskName
                + "\" (schtasks exit code "
                + exitCode
                + ").";
        }

        private static int RunSchTasks(string arguments, out string standardOutput, out string standardError)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var process = Process.Start(psi))
            {
                standardOutput = ReadAllText(process.StandardOutput.BaseStream);
                standardError = ReadAllText(process.StandardError.BaseStream);
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string ReadAllText(Stream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
