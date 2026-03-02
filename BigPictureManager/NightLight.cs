using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace TinyScreen.Services
{
    public sealed class NightLight : IDisposable
    {
        private const string DataValueName = "Data";
        private const int StateIndex = 18;
        private const int SnapshotFormatVersion = 1;
        private static readonly string DebugLogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NightLightDebug.log");

        private static readonly string[] StateKeyVariants = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate",
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Cloud\default$windows.data.bluelightreduction.bluelightreductionstate"
        };

        private static readonly string[] SettingsKeyVariants = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings",
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Cloud\default$windows.data.bluelightreduction.settings"
        };

        private Snapshot _startupSnapshot;
        private bool _disposed;

        public bool Enabled
        {
            get
            {
                string statePath;
                var state = TryReadDataBlob(StateKeyVariants, out statePath);
                var mode = DetectStateMode(state);
                return mode == StateMode.ManualOn
                    || mode == StateMode.ScheduleOn
                    || mode == StateMode.ManualOnUnknownShape
                    || mode == StateMode.ScheduleOnUnknownShape;
            }
            set
            {
                if (!Supported)
                {
                    return;
                }

                if (value)
                {
                    RestoreOrEnable();
                }
                else
                {
                    DisableWithSnapshot();
                }
            }
        }

        public bool Supported
        {
            get
            {
                string statePath;
                string settingsPath;
                return TryReadDataBlob(StateKeyVariants, out statePath) != null
                    && TryReadDataBlob(SettingsKeyVariants, out settingsPath) != null;
            }
        }

        public void DisableNightLight()
        {
            if (!Supported)
            {
                return;
            }

            DisableWithSnapshot();
        }

        public void RestoreNightLight()
        {
            if (!Supported)
            {
                return;
            }

            RestoreOrEnable();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void DisableWithSnapshot()
        {
            var snapshot = CaptureCurrentSnapshot();
            if (snapshot == null || snapshot.StateBytes == null || snapshot.SettingsBytes == null)
            {
                Log("[NightLight] Snapshot capture failed: state/settings are null.");
                return;
            }

            var mode = DetectStateMode(snapshot.StateBytes);
            var nextState = Clone(snapshot.StateBytes);
            var nextSettings = Clone(snapshot.SettingsBytes);
            var scheduleEnabledNow = IsScheduleEnabled(snapshot.SettingsBytes);
            Log("[NightLight] DisableNightLight() start.");
            Log("[NightLight] Current state: " + DescribeState(snapshot.StateBytes) + "; settings: " + DescribeSettings(snapshot.SettingsBytes));
            Log("[NightLight] Detected mode: " + mode + ", scheduleEnabledNow=" + scheduleEnabledNow + ".");

            // Save exactly what we saw before mutating anything.
            _startupSnapshot = snapshot;
            TrySaveSnapshotToProjectFolder(snapshot);

            if (mode == StateMode.ScheduleOn || mode == StateMode.ScheduleOnUnknownShape)
            {
                nextState = BuildScheduleDisabledState(snapshot.StateBytes);
            }
            else if (mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape)
            {
                nextState = BuildGenericOffState(snapshot.StateBytes);
            }
            else if (mode != StateMode.ManualOff && mode != StateMode.ScheduleOff)
            {
                // Unknown shape: keep state as-is but still allow schedule disable below.
                Log("[NightLight] Unknown state mode on disable, state will be kept as-is.");
            }

            // Business rule: if schedule is enabled, disable it in any case (active or inactive).
            if (scheduleEnabledNow)
            {
                nextSettings = SetScheduleEnabled(snapshot.SettingsBytes, false);
            }

            if (ByteArrayEquals(snapshot.SettingsBytes, nextSettings) && ByteArrayEquals(snapshot.StateBytes, nextState))
            {
                Log("[NightLight] No registry writes required.");
                return;
            }

            if (!ByteArrayEquals(snapshot.SettingsBytes, nextSettings))
            {
                WriteDataBlob(SettingsKeyVariants, snapshot.SettingsPath, nextSettings);
                Log("[NightLight] Settings updated: " + DescribeSettings(nextSettings));
            }
            if (!ByteArrayEquals(snapshot.StateBytes, nextState))
            {
                WriteDataBlob(StateKeyVariants, snapshot.StatePath, nextState);
                Log("[NightLight] State updated: " + DescribeState(nextState));
            }
        }

        private void RestoreOrEnable()
        {
            var fromMemory = _startupSnapshot != null;
            var restoreSnapshot = _startupSnapshot ?? TryLoadLatestSnapshotFromProjectFolder();
            if (restoreSnapshot != null)
            {
                Log("[NightLight] RestoreNightLight() using snapshot from " + (fromMemory ? "memory" : "file") + ".");
                Log("[NightLight] Snapshot state: " + DescribeState(restoreSnapshot.StateBytes) + "; settings: " + DescribeSettings(restoreSnapshot.SettingsBytes));
                // Semantic restore: restore target mode from snapshot, but build fresh blobs from current registry state/settings.
                string currentStatePath;
                string currentSettingsPath;
                var currentState = ReadDataBlob(StateKeyVariants, out currentStatePath);
                var currentSettings = ReadDataBlob(SettingsKeyVariants, out currentSettingsPath);

                var snapshotMode = DetectStateMode(restoreSnapshot.StateBytes);
                bool shouldEnableSchedule;
                if (snapshotMode == StateMode.ScheduleOn || snapshotMode == StateMode.ScheduleOnUnknownShape)
                {
                    // If light was on by schedule, restore schedule ON.
                    shouldEnableSchedule = true;
                }
                else if (snapshotMode == StateMode.ManualOn || snapshotMode == StateMode.ManualOnUnknownShape)
                {
                    // If light was manual, restore manual ON without enabling schedule.
                    shouldEnableSchedule = false;
                }
                else
                {
                    // For off states keep schedule as it was in snapshot settings.
                    shouldEnableSchedule = IsScheduleEnabled(restoreSnapshot.SettingsBytes);
                }
                Log("[NightLight] Restore target: mode=" + snapshotMode + ", shouldEnableSchedule=" + shouldEnableSchedule + ".");

                var targetSettings = SetScheduleEnabled(currentSettings, shouldEnableSchedule);
                byte[] targetState;

                if (snapshotMode == StateMode.ScheduleOn || snapshotMode == StateMode.ScheduleOnUnknownShape)
                {
                    targetState = BuildScheduleEnabledState(currentState);
                }
                else if (snapshotMode == StateMode.ManualOn || snapshotMode == StateMode.ManualOnUnknownShape)
                {
                    targetState = BuildManualEnabledState(currentState);
                }
                else if (snapshotMode == StateMode.ScheduleOff)
                {
                    targetState = BuildScheduleDisabledState(currentState);
                }
                else
                {
                    targetState = BuildGenericOffState(currentState);
                }

                WriteDataBlob(SettingsKeyVariants, currentSettingsPath, targetSettings);
                WriteDataBlob(StateKeyVariants, currentStatePath, targetState);
                VerifyRestore(new Snapshot
                {
                    StatePath = currentStatePath,
                    SettingsPath = currentSettingsPath,
                    StateBytes = targetState,
                    SettingsBytes = targetSettings
                });
                return;
            }

            // Fallback: best-effort manual ON if no startup snapshot exists.
            Log("[NightLight] No snapshot found. Fallback to manual ON logic.");
            string statePath;
            var state = TryReadDataBlob(StateKeyVariants, out statePath);
            if (state == null)
            {
                Log("[NightLight] Could not read state for fallback enable.");
                return;
            }

            var mode = DetectStateMode(state);
            if (mode == StateMode.ManualOn || mode == StateMode.ScheduleOn || mode == StateMode.ManualOnUnknownShape || mode == StateMode.ScheduleOnUnknownShape)
            {
                Log("[NightLight] Fallback skipped: Night Light already ON.");
                return;
            }

            var manualOn = BuildManualEnabledState(state);
            WriteDataBlob(StateKeyVariants, statePath, manualOn);
            Log("[NightLight] Fallback manual ON applied: " + DescribeState(manualOn));
        }

        private Snapshot CaptureCurrentSnapshot()
        {
            string statePath;
            string settingsPath;

            var state = ReadDataBlob(StateKeyVariants, out statePath);
            var settings = ReadDataBlob(SettingsKeyVariants, out settingsPath);

            return new Snapshot
            {
                CreatedAtUtc = DateTime.UtcNow,
                StatePath = statePath,
                SettingsPath = settingsPath,
                StateBytes = state,
                SettingsBytes = settings
            };
        }

        private static string GetBackupsDirectory()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NightLightBackups");
        }

        private static void TrySaveSnapshotToProjectFolder(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.StateBytes == null || snapshot.SettingsBytes == null)
            {
                return;
            }

            try
            {
                var dir = GetBackupsDirectory();
                Directory.CreateDirectory(dir);

                var created = snapshot.CreatedAtUtc == default(DateTime) ? DateTime.UtcNow : snapshot.CreatedAtUtc;
                var filePath = Path.Combine(
                    dir,
                    string.Format("NightLightSnapshot-{0:yyyyMMdd-HHmmss-fff}.bin", created)
                );

                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(SnapshotFormatVersion);
                    bw.Write(created.ToBinary());
                    bw.Write(snapshot.StatePath ?? string.Empty);
                    bw.Write(snapshot.SettingsPath ?? string.Empty);
                    bw.Write(snapshot.StateBytes.Length);
                    bw.Write(snapshot.StateBytes);
                    bw.Write(snapshot.SettingsBytes.Length);
                    bw.Write(snapshot.SettingsBytes);
                }
                Log("[NightLight] Snapshot saved: " + filePath);
            }
            catch (Exception ex)
            {
                // Backup write errors should not block Night Light control.
                Log("[NightLight] Snapshot save error: " + ex.Message);
            }
        }

        private static Snapshot TryLoadLatestSnapshotFromProjectFolder()
        {
            try
            {
                var dir = GetBackupsDirectory();
                if (!Directory.Exists(dir))
                {
                    return null;
                }

                var files = Directory.GetFiles(dir, "NightLightSnapshot-*.bin");
                if (files == null || files.Length == 0)
                {
                    return null;
                }

                Array.Sort(files, StringComparer.Ordinal);
                for (int i = files.Length - 1; i >= 0; i--)
                {
                    Snapshot snapshot;
                    if (TryReadSnapshotFile(files[i], out snapshot))
                    {
                        Log("[NightLight] Loaded snapshot file: " + files[i]);
                        return snapshot;
                    }
                }
            }
            catch (Exception ex)
            {
                // If backups are unavailable/corrupted, fallback path will be used.
                Log("[NightLight] Snapshot load error: " + ex.Message);
            }

            return null;
        }

        private static bool TryReadSnapshotFile(string path, out Snapshot snapshot)
        {
            snapshot = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    var version = br.ReadInt32();
                    if (version != SnapshotFormatVersion)
                    {
                        return false;
                    }

                    var created = DateTime.FromBinary(br.ReadInt64());
                    var statePath = br.ReadString();
                    var settingsPath = br.ReadString();
                    var stateLen = br.ReadInt32();
                    var state = br.ReadBytes(stateLen);
                    var settingsLen = br.ReadInt32();
                    var settings = br.ReadBytes(settingsLen);

                    if (state.Length != stateLen || settings.Length != settingsLen)
                    {
                        return false;
                    }

                    snapshot = new Snapshot
                    {
                        CreatedAtUtc = created,
                        StatePath = statePath,
                        SettingsPath = settingsPath,
                        StateBytes = state,
                        SettingsBytes = settings
                    };
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static byte[] ReadDataBlob(IEnumerable<string> keyVariants, out string resolvedPath)
        {
            foreach (var keyPath in keyVariants)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (key == null)
                    {
                        continue;
                    }

                    var value = key.GetValue(DataValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var bytes = value as byte[];
                    if (bytes == null || bytes.Length == 0)
                    {
                        continue;
                    }

                    resolvedPath = keyPath;
                    return Clone(bytes);
                }
            }

            throw new InvalidOperationException("Night Light registry key was not found or has empty Data.");
        }

        private static byte[] TryReadDataBlob(IEnumerable<string> keyVariants, out string resolvedPath)
        {
            resolvedPath = null;
            foreach (var keyPath in keyVariants)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (key == null)
                    {
                        continue;
                    }

                    var bytes = key.GetValue(DataValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                    if (bytes == null || bytes.Length == 0)
                    {
                        continue;
                    }

                    resolvedPath = keyPath;
                    return Clone(bytes);
                }
            }
            return null;
        }

        private static void WriteDataBlob(IEnumerable<string> keyVariants, string preferredPath, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            var order = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                order.Add(preferredPath);
            }
            foreach (var v in keyVariants)
            {
                if (!order.Contains(v))
                {
                    order.Add(v);
                }
            }

            Exception last = null;
            foreach (var keyPath in order)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        key.SetValue(DataValueName, data, RegistryValueKind.Binary);
                        key.Flush();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException("Failed to write Night Light registry blob.", last);
        }

        private static StateMode DetectStateMode(byte[] state)
        {
            if (state == null || state.Length <= StateIndex)
            {
                return StateMode.Unknown;
            }

            var marker = state[StateIndex];
            var len = state.Length;

            if (marker == 0x15 && len == 43) return StateMode.ManualOn;
            if (marker == 0x12 && len == 40) return StateMode.ScheduleOn;
            if (marker == 0x13 && len == 41) return StateMode.ManualOff;
            if (marker == 0x10 && len == 38) return StateMode.ScheduleOff;

            if (marker == 0x15) return StateMode.ManualOnUnknownShape;
            if (marker == 0x12) return StateMode.ScheduleOnUnknownShape;
            if (marker == 0x13) return StateMode.ManualOff;
            if (marker == 0x10) return StateMode.ScheduleOff;
            return StateMode.Unknown;
        }

        private static byte[] BuildScheduleDisabledState(byte[] src)
        {
            var mode = DetectStateMode(src);
            if (mode == StateMode.ScheduleOff)
            {
                return Clone(src);
            }

            // schedule_on (40) -> schedule_off (38): remove bytes [22..23] (10 00)
            if ((mode == StateMode.ScheduleOn || mode == StateMode.ScheduleOnUnknownShape) && src.Length >= 40)
            {
                var outBuf = new byte[38];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                Buffer.BlockCopy(src, 24, outBuf, 22, 16);
                outBuf[StateIndex] = 0x10;
                return IncrementVersionByte(outBuf);
            }

            // manual_on (43) + schedule enabled -> schedule_off (38): remove 10 00 D0 0A 02 block.
            if ((mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape) && src.Length >= 43)
            {
                var outBuf = new byte[38];
                Buffer.BlockCopy(src, 0, outBuf, 0, 23);
                Buffer.BlockCopy(src, 28, outBuf, 23, 15);
                outBuf[StateIndex] = 0x10;
                return IncrementVersionByte(outBuf);
            }

            if (mode == StateMode.ManualOff)
            {
                return Clone(src);
            }

            return BuildGenericOffState(src);
        }

        private static byte[] BuildGenericOffState(byte[] src)
        {
            var mode = DetectStateMode(src);
            if (mode == StateMode.ManualOff || mode == StateMode.ScheduleOff)
            {
                return Clone(src);
            }

            // manual_on (43) -> manual_off (41)
            if ((mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape) && src.Length >= 43)
            {
                var outBuf = new byte[41];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                Buffer.BlockCopy(src, 25, outBuf, 23, 18);
                outBuf[StateIndex] = 0x13;
                return IncrementVersionByte(outBuf);
            }

            // schedule_on (40) -> schedule_off (38)
            if ((mode == StateMode.ScheduleOn || mode == StateMode.ScheduleOnUnknownShape) && src.Length >= 40)
            {
                var outBuf = new byte[38];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                Buffer.BlockCopy(src, 24, outBuf, 22, 16);
                outBuf[StateIndex] = 0x10;
                return IncrementVersionByte(outBuf);
            }

            throw new InvalidOperationException("Unsupported state shape for OFF conversion.");
        }

        private static byte[] BuildManualEnabledState(byte[] source)
        {
            if (source == null || source.Length < 23)
            {
                throw new InvalidOperationException("State blob is too short to build manual ON.");
            }

            var outBuf = new byte[43];
            // manual_off (41) -> manual_on (43):
            // keep source[0..22], insert 10 00 at [23..24], move source[23..40] to [25..42].
            Buffer.BlockCopy(source, 0, outBuf, 0, Math.Min(23, source.Length));
            outBuf[23] = 0x10;
            outBuf[24] = 0x00;
            if (source.Length > 23)
            {
                var count = Math.Min(source.Length - 23, 18);
                Buffer.BlockCopy(source, 23, outBuf, 25, count);
            }

            outBuf[StateIndex] = 0x15;
            return IncrementVersionByte(outBuf);
        }

        private static byte[] BuildScheduleEnabledState(byte[] src)
        {
            var mode = DetectStateMode(src);
            if (mode == StateMode.ScheduleOn)
            {
                return IncrementVersionByte(src);
            }

            // schedule_off (38) -> schedule_on (40): insert 10 00 at [22..23]
            if (mode == StateMode.ScheduleOff && src.Length >= 38)
            {
                var outBuf = new byte[40];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                outBuf[22] = 0x10;
                outBuf[23] = 0x00;
                Buffer.BlockCopy(src, 22, outBuf, 24, 16);
                outBuf[StateIndex] = 0x12;
                return IncrementVersionByte(outBuf);
            }

            // manual_off (41) -> schedule_on (40): keep common head, insert 10 00, move tail.
            if (mode == StateMode.ManualOff && src.Length >= 41)
            {
                var outBuf = new byte[40];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                outBuf[22] = 0x10;
                outBuf[23] = 0x00;
                Buffer.BlockCopy(src, 23, outBuf, 24, 16);
                outBuf[StateIndex] = 0x12;
                return IncrementVersionByte(outBuf);
            }

            // manual_on (43) -> schedule_on (40): remove D0 0A 02 block.
            if ((mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape) && src.Length >= 43)
            {
                var outBuf = new byte[40];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                outBuf[22] = 0x10;
                outBuf[23] = 0x00;
                Buffer.BlockCopy(src, 27, outBuf, 24, 16);
                outBuf[StateIndex] = 0x12;
                return IncrementVersionByte(outBuf);
            }

            // Unknown "on by schedule" shape but marker=0x12: best effort clone+version.
            if (src.Length > StateIndex && src[StateIndex] == 0x12)
            {
                return IncrementVersionByte(src);
            }

            throw new InvalidOperationException("Unsupported state shape for schedule ON conversion.");
        }

        private static byte[] SetScheduleEnabled(byte[] settings, bool shouldEnable)
        {
            var src = Clone(settings);
            int idx = FindScheduleMarkerIndex(src);
            if (idx < 0)
            {
                throw new InvalidOperationException("Schedule marker (CA 14 0E ... CA 1E 0E) not found.");
            }

            bool enabledNow = idx >= 2 && src[idx - 2] == 0x02 && src[idx - 1] == 0x01;
            if (enabledNow == shouldEnable)
            {
                return src;
            }

            byte[] outBuf;
            if (!shouldEnable)
            {
                outBuf = new byte[src.Length - 2];
                Buffer.BlockCopy(src, 0, outBuf, 0, idx - 2);
                Buffer.BlockCopy(src, idx, outBuf, idx - 2, src.Length - idx);
                if (outBuf.Length > 18 && outBuf[18] == 0x1B)
                {
                    outBuf[18] = 0x19;
                }
            }
            else
            {
                outBuf = new byte[src.Length + 2];
                Buffer.BlockCopy(src, 0, outBuf, 0, idx);
                outBuf[idx] = 0x02;
                outBuf[idx + 1] = 0x01;
                Buffer.BlockCopy(src, idx, outBuf, idx + 2, src.Length - idx);
                if (outBuf.Length > 18 && outBuf[18] == 0x19)
                {
                    outBuf[18] = 0x1B;
                }
            }

            return IncrementVersionByte(outBuf);
        }

        private static int FindScheduleMarkerIndex(byte[] settings)
        {
            for (int i = 0; i < settings.Length - 9; i++)
            {
                if (settings[i] == 0xCA &&
                    settings[i + 1] == 0x14 &&
                    settings[i + 2] == 0x0E &&
                    settings[i + 5] == 0xCA &&
                    settings[i + 6] == 0x1E &&
                    settings[i + 7] == 0x0E)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsScheduleEnabled(byte[] settings)
        {
            if (settings == null || settings.Length < 10)
            {
                return false;
            }

            var idx = FindScheduleMarkerIndex(settings);
            return idx >= 2 && settings[idx - 2] == 0x02 && settings[idx - 1] == 0x01;
        }

        private static byte[] IncrementVersionByte(byte[] bytes)
        {
            var outBuf = Clone(bytes);
            for (int i = 10; i < 15 && i < outBuf.Length; i++)
            {
                if (outBuf[i] != 0xFF)
                {
                    outBuf[i] = (byte)((outBuf[i] + 1) & 0xFF);
                    break;
                }
            }
            return outBuf;
        }

        private static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static byte[] Clone(byte[] bytes)
        {
            if (bytes == null) return null;
            var clone = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, clone, 0, bytes.Length);
            return clone;
        }

        private static string DescribeState(byte[] state)
        {
            if (state == null)
            {
                return "null";
            }
            var marker = state.Length > StateIndex ? state[StateIndex] : 0;
            return "len=" + state.Length + ", marker=0x" + marker.ToString("X2");
        }

        private static string DescribeSettings(byte[] settings)
        {
            if (settings == null)
            {
                return "null";
            }
            var idx = FindScheduleMarkerIndex(settings);
            var scheduleEnabled = idx >= 2 && settings[idx - 2] == 0x02 && settings[idx - 1] == 0x01;
            return "len=" + settings.Length + ", scheduleEnabled=" + scheduleEnabled;
        }

        private static void VerifyRestore(Snapshot expected)
        {
            try
            {
                string currentStatePath;
                string currentSettingsPath;
                var currentState = TryReadDataBlob(StateKeyVariants, out currentStatePath);
                var currentSettings = TryReadDataBlob(SettingsKeyVariants, out currentSettingsPath);

                var stateOk = ByteArrayEquals(expected.StateBytes, currentState);
                var settingsOk = ByteArrayEquals(expected.SettingsBytes, currentSettings);

                Log("[NightLight] Restore verify state: " + (stateOk ? "OK" : "MISMATCH")
                    + " expected(" + DescribeState(expected.StateBytes) + ") current(" + DescribeState(currentState) + ")"
                    + " path=" + (currentStatePath ?? "n/a"));
                Log("[NightLight] Restore verify settings: " + (settingsOk ? "OK" : "MISMATCH")
                    + " expected(" + DescribeSettings(expected.SettingsBytes) + ") current(" + DescribeSettings(currentSettings) + ")"
                    + " path=" + (currentSettingsPath ?? "n/a"));
            }
            catch (Exception ex)
            {
                Log("[NightLight] Restore verify error: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(DebugLogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break app flow.
            }
        }

        private enum StateMode
        {
            Unknown = 0,
            ManualOn,
            ScheduleOn,
            ManualOff,
            ScheduleOff,
            ManualOnUnknownShape,
            ScheduleOnUnknownShape
        }

        private sealed class Snapshot
        {
            public DateTime CreatedAtUtc;
            public string StatePath;
            public string SettingsPath;
            public byte[] StateBytes;
            public byte[] SettingsBytes;
        }
    }
}