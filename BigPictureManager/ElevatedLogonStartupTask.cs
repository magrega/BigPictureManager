using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BigPictureManager
{
    /// <summary>
    /// Registers a per-user logon task that starts Big Picture Manager with highest available privileges.
    /// </summary>
    internal static class ElevatedLogonStartupTask
    {
        public const string TaskName = "BigPictureManager";

        public static bool IsRegistered()
        {
            var exitCode = RunSchTasks("/Query /TN \"" + TaskName + "\"", out var output, out var error);
            var exists = exitCode == 0;
            BpmLog.WriteLine(
                exists
                    ? "[Startup] Scheduled task \"" + TaskName + "\" is registered."
                    : "[Startup] Scheduled task \"" + TaskName + "\" is not registered."
            );
            if (!exists && !string.IsNullOrWhiteSpace(error))
            {
                BpmLog.WriteLine("[Startup] Task query: " + error.Trim());
            }

            return exists;
        }

        public static bool TryRegister(string exePath, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                errorMessage = "Executable path is empty.";
                BpmLog.WriteLine("[Error] [Startup] Cannot create scheduled task: " + errorMessage);
                return false;
            }

            var tr = "\"" + exePath + "\"";
            var args = "/Create /TN \"" + TaskName + "\" /TR " + tr + " /SC ONLOGON /RL HIGHEST /F";

            BpmLog.WriteLine(
                "[Startup] Creating scheduled task \"" + TaskName + "\" (ONLOGON, highest privileges) for: " + exePath
            );

            var exitCode = RunSchTasks(args, out var output, out var error);
            if (exitCode == 0)
            {
                BpmLog.WriteLine("[Startup] Scheduled task \"" + TaskName + "\" created successfully.");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    BpmLog.WriteLine("[Startup] schtasks: " + output.Trim());
                }

                return true;
            }

            errorMessage = FormatSchTasksFailure(exitCode, output, error);
            BpmLog.WriteLine("[Error] [Startup] Failed to create scheduled task: " + errorMessage);
            return false;
        }

        public static bool TryUnregister(out string errorMessage)
        {
            errorMessage = null;
            BpmLog.WriteLine("[Startup] Deleting scheduled task \"" + TaskName + "\".");

            var exitCode = RunSchTasks("/Delete /TN \"" + TaskName + "\" /F", out var output, out var error);
            if (exitCode == 0)
            {
                BpmLog.WriteLine("[Startup] Scheduled task \"" + TaskName + "\" deleted successfully.");
                return true;
            }

            errorMessage = FormatSchTasksFailure(exitCode, output, error);
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

        private static string FormatSchTasksFailure(int exitCode, string output, string error)
        {
            var message = string.IsNullOrWhiteSpace(error) ? output : error;
            message = message?.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                return "schtasks exited with code " + exitCode;
            }

            return message;
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
                var encoding = GetConsoleOutputEncoding();
                standardOutput = ReadAllText(process.StandardOutput.BaseStream, encoding);
                standardError = ReadAllText(process.StandardError.BaseStream, encoding);
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string ReadAllText(Stream stream, Encoding encoding)
        {
            using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false))
            {
                return reader.ReadToEnd();
            }
        }

        private static Encoding GetConsoleOutputEncoding()
        {
            try
            {
                var codePage = GetConsoleOutputCP();
                if (codePage != 0)
                {
                    return Encoding.GetEncoding((int)codePage);
                }
            }
            catch
            {
                // fall through
            }

            return Encoding.Default;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();
    }
}
