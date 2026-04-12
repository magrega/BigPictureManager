using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace TinyScreen.Services
{
    public sealed class NightLight : IDisposable
    {
        private readonly INightLightLogger _logger;
        private readonly NightLightRegistryStore _registry;

        private NightLightSnapshot _startupSnapshot;
        private bool _disposed;

        public NightLight()
        {
            _logger = new BpmNightLightLogger();
            _registry = new NightLightRegistryStore();
        }

        public bool Enabled
        {
            get
            {
                if (!Supported)
                {
                    return false;
                }

                string statePath;
                var state = _registry.TryReadState(out statePath);
                var mode = NightLightCodec.DetectStateMode(state);
                return NightLightCodec.IsOnMode(mode);
            }
            set
            {
                if (!Supported)
                {
                    return;
                }

                if (value)
                {
                    RestoreNightLight();
                }
                else
                {
                    DisableNightLight();
                }
            }
        }

        public bool Supported
        {
            get
            {
                string statePath;
                string settingsPath;
                return _registry.TryReadState(out statePath) != null
                    && _registry.TryReadSettings(out settingsPath) != null;
            }
        }

        public void DisableNightLight()
        {
            if (!Supported)
            {
                return;
            }

            var snapshot = CaptureCurrentSnapshot();
            if (snapshot == null || snapshot.StateBytes == null || snapshot.SettingsBytes == null)
            {
                _logger.Error("[NightLight] Snapshot capture failed: state/settings are null.");
                return;
            }

            _startupSnapshot = snapshot;

            var mode = NightLightCodec.DetectStateMode(snapshot.StateBytes);
            var scheduleEnabledNow = NightLightCodec.IsScheduleEnabled(snapshot.SettingsBytes);
            _logger.Info("[NightLight] DisableNightLight() start.");
            _logger.Info("[NightLight] Current state: " + NightLightCodec.DescribeState(snapshot.StateBytes) +
                         "; settings: " + NightLightCodec.DescribeSettings(snapshot.SettingsBytes));
            _logger.Info("[NightLight] Detected mode: " + mode + ", scheduleEnabledNow=" + scheduleEnabledNow + ".");

            var nextState = snapshot.StateBytes;
            var nextSettings = snapshot.SettingsBytes;

            if (mode == StateMode.ScheduleOn || mode == StateMode.ScheduleOnUnknownShape)
            {
                nextState = NightLightCodec.BuildScheduleDisabledState(snapshot.StateBytes);
            }
            else if (mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape)
            {
                nextState = NightLightCodec.BuildGenericOffState(snapshot.StateBytes);
            }
            else if (mode != StateMode.ManualOff && mode != StateMode.ScheduleOff)
            {
                _logger.Info("[NightLight] Unknown state mode on disable, state kept as-is.");
            }

            // Business rule: always disable schedule when it is enabled.
            if (scheduleEnabledNow)
            {
                nextSettings = NightLightCodec.SetScheduleEnabled(snapshot.SettingsBytes, false);
            }

            if (NightLightCodec.ByteArrayEquals(snapshot.SettingsBytes, nextSettings) &&
                NightLightCodec.ByteArrayEquals(snapshot.StateBytes, nextState))
            {
                _logger.Info("[NightLight] No registry writes required.");
                return;
            }

            if (!NightLightCodec.ByteArrayEquals(snapshot.SettingsBytes, nextSettings))
            {
                _registry.WriteSettings(snapshot.SettingsPath, nextSettings);
                _logger.Info("[NightLight] Settings updated: " + NightLightCodec.DescribeSettings(nextSettings));
            }
            if (!NightLightCodec.ByteArrayEquals(snapshot.StateBytes, nextState))
            {
                _registry.WriteState(snapshot.StatePath, nextState);
                _logger.Info("[NightLight] State updated: " + NightLightCodec.DescribeState(nextState));
            }
        }

        public void RestoreNightLight()
        {
            if (!Supported)
            {
                return;
            }

            var restoreSnapshot = ResolveSnapshotForRestore();
            if (restoreSnapshot != null)
            {
                ExecuteSemanticRestore(restoreSnapshot);
                return;
            }

            // Fallback: best-effort manual ON if no snapshot exists.
            _logger.Info("[NightLight] No snapshot found. Fallback to manual ON logic.");
            string statePath;
            var state = _registry.TryReadState(out statePath);
            if (state == null)
            {
                _logger.Error("[NightLight] Could not read state for fallback enable.");
                return;
            }

            var mode = NightLightCodec.DetectStateMode(state);
            if (NightLightCodec.IsOnMode(mode))
            {
                _logger.Info("[NightLight] Fallback skipped: Night Light already ON.");
                return;
            }

            var manualOn = NightLightCodec.BuildManualEnabledState(state);
            _registry.WriteState(statePath, manualOn);
            _logger.Info("[NightLight] Fallback manual ON applied: " + NightLightCodec.DescribeState(manualOn));
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

        private NightLightSnapshot ResolveSnapshotForRestore()
        {
            if (_startupSnapshot != null)
            {
                _logger.Info("[NightLight] RestoreNightLight() using snapshot from memory.");
                return _startupSnapshot;
            }

            return null;
        }

        private void ExecuteSemanticRestore(NightLightSnapshot snapshot)
        {
            _logger.Info("[NightLight] Snapshot state: " + NightLightCodec.DescribeState(snapshot.StateBytes) +
                         "; settings: " + NightLightCodec.DescribeSettings(snapshot.SettingsBytes));

            string currentStatePath;
            string currentSettingsPath;
            var currentState = _registry.ReadState(out currentStatePath);
            var currentSettings = _registry.ReadSettings(out currentSettingsPath);

            var plan = BuildRestorePlan(snapshot);
            _logger.Info("[NightLight] Restore target: mode=" + plan.TargetMode + ", shouldEnableSchedule=" + plan.EnableSchedule + ".");

            var targetSettings = NightLightCodec.SetScheduleEnabled(currentSettings, plan.EnableSchedule);
            var targetState = BuildTargetState(currentState, plan.TargetMode);

            _registry.WriteSettings(currentSettingsPath, targetSettings);
            _registry.WriteState(currentStatePath, targetState);

            VerifyRestore(currentStatePath, currentSettingsPath, targetState, targetSettings);
        }

        private static byte[] BuildTargetState(byte[] currentState, StateMode targetMode)
        {
            switch (targetMode)
            {
                case StateMode.ScheduleOn:
                case StateMode.ScheduleOnUnknownShape:
                    return NightLightCodec.BuildScheduleEnabledState(currentState);
                case StateMode.ManualOn:
                case StateMode.ManualOnUnknownShape:
                    return NightLightCodec.BuildManualEnabledState(currentState);
                case StateMode.ScheduleOff:
                    return NightLightCodec.BuildScheduleDisabledState(currentState);
                default:
                    return NightLightCodec.BuildGenericOffState(currentState);
            }
        }

        private static RestorePlan BuildRestorePlan(NightLightSnapshot snapshot)
        {
            var snapshotMode = NightLightCodec.DetectStateMode(snapshot.StateBytes);
            bool enableSchedule;

            if (snapshotMode == StateMode.ScheduleOn || snapshotMode == StateMode.ScheduleOnUnknownShape)
            {
                enableSchedule = true;
            }
            else if (snapshotMode == StateMode.ManualOn || snapshotMode == StateMode.ManualOnUnknownShape)
            {
                enableSchedule = false;
            }
            else
            {
                enableSchedule = NightLightCodec.IsScheduleEnabled(snapshot.SettingsBytes);
            }

            return new RestorePlan
            {
                TargetMode = snapshotMode,
                EnableSchedule = enableSchedule
            };
        }

        private void VerifyRestore(string statePath, string settingsPath, byte[] expectedState, byte[] expectedSettings)
        {
            try
            {
                string actualStatePath;
                string actualSettingsPath;
                var currentState = _registry.TryReadState(out actualStatePath);
                var currentSettings = _registry.TryReadSettings(out actualSettingsPath);

                var stateOk = NightLightCodec.ByteArrayEquals(expectedState, currentState);
                var settingsOk = NightLightCodec.ByteArrayEquals(expectedSettings, currentSettings);

                _logger.Info("[NightLight] Restore verify state: " + (stateOk ? "OK" : "MISMATCH")
                    + " expected(" + NightLightCodec.DescribeState(expectedState) + ") current(" + NightLightCodec.DescribeState(currentState) + ")"
                    + " path=" + (actualStatePath ?? statePath ?? "n/a"));
                _logger.Info("[NightLight] Restore verify settings: " + (settingsOk ? "OK" : "MISMATCH")
                    + " expected(" + NightLightCodec.DescribeSettings(expectedSettings) + ") current(" + NightLightCodec.DescribeSettings(currentSettings) + ")"
                    + " path=" + (actualSettingsPath ?? settingsPath ?? "n/a"));
            }
            catch (Exception ex)
            {
                _logger.Error("[NightLight] Restore verify error: " + ex.Message);
            }
        }

        private NightLightSnapshot CaptureCurrentSnapshot()
        {
            string statePath;
            string settingsPath;

            var state = _registry.ReadState(out statePath);
            var settings = _registry.ReadSettings(out settingsPath);

            return new NightLightSnapshot
            {
                Version = NightLightSnapshot.SnapshotFormatVersion,
                SessionId = Guid.Empty,
                CreatedAtUtc = DateTime.UtcNow,
                StatePath = statePath,
                SettingsPath = settingsPath,
                StateBytes = state,
                SettingsBytes = settings
            };
        }

        private sealed class RestorePlan
        {
            public StateMode TargetMode;
            public bool EnableSchedule;
        }
    }

    internal sealed class NightLightRegistryStore
    {
        private const string DataValueName = "Data";

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

        public byte[] ReadState(out string resolvedPath)
        {
            return ReadDataBlob(StateKeyVariants, out resolvedPath);
        }

        public byte[] ReadSettings(out string resolvedPath)
        {
            return ReadDataBlob(SettingsKeyVariants, out resolvedPath);
        }

        public byte[] TryReadState(out string resolvedPath)
        {
            return TryReadDataBlob(StateKeyVariants, out resolvedPath);
        }

        public byte[] TryReadSettings(out string resolvedPath)
        {
            return TryReadDataBlob(SettingsKeyVariants, out resolvedPath);
        }

        public void WriteState(string preferredPath, byte[] data)
        {
            WriteDataBlob(StateKeyVariants, preferredPath, data);
        }

        public void WriteSettings(string preferredPath, byte[] data)
        {
            WriteDataBlob(SettingsKeyVariants, preferredPath, data);
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
                    return NightLightCodec.Clone(bytes);
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
                    return NightLightCodec.Clone(bytes);
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
    }

    internal static class NightLightCodec
    {
        internal const int StateMarkerIndex = 18;
        internal const int VersionStart = 10;
        internal const int VersionEnd = 15; // exclusive

        internal const int ManualOnLength = 43;
        internal const int ScheduleOnLength = 40;
        internal const int ManualOffLength = 41;
        internal const int ScheduleOffLength = 38;

        internal const byte MarkerManualOn = 0x15;
        internal const byte MarkerScheduleOn = 0x12;
        internal const byte MarkerManualOff = 0x13;
        internal const byte MarkerScheduleOff = 0x10;

        internal const byte SettingsMarkerOn = 0x1B;
        internal const byte SettingsMarkerOff = 0x19;

        internal static StateMode DetectStateMode(byte[] state)
        {
            if (state == null || state.Length <= StateMarkerIndex)
            {
                return StateMode.Unknown;
            }

            var marker = state[StateMarkerIndex];
            var len = state.Length;

            if (marker == MarkerManualOn && len == ManualOnLength) return StateMode.ManualOn;
            if (marker == MarkerScheduleOn && len == ScheduleOnLength) return StateMode.ScheduleOn;
            if (marker == MarkerManualOff && len == ManualOffLength) return StateMode.ManualOff;
            if (marker == MarkerScheduleOff && len == ScheduleOffLength) return StateMode.ScheduleOff;

            if (marker == MarkerManualOn) return StateMode.ManualOnUnknownShape;
            if (marker == MarkerScheduleOn) return StateMode.ScheduleOnUnknownShape;
            if (marker == MarkerManualOff) return StateMode.ManualOff;
            if (marker == MarkerScheduleOff) return StateMode.ScheduleOff;
            return StateMode.Unknown;
        }

        internal static bool IsOnMode(StateMode mode)
        {
            return mode == StateMode.ManualOn
                || mode == StateMode.ScheduleOn
                || mode == StateMode.ManualOnUnknownShape
                || mode == StateMode.ScheduleOnUnknownShape;
        }

        internal static byte[] BuildScheduleDisabledState(byte[] src)
        {
            var mode = DetectStateMode(src);
            if (mode == StateMode.ScheduleOff)
            {
                return Clone(src);
            }

            if ((mode == StateMode.ScheduleOn || mode == StateMode.ScheduleOnUnknownShape) && src.Length >= ScheduleOnLength)
            {
                var outBuf = new byte[ScheduleOffLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                Buffer.BlockCopy(src, 24, outBuf, 22, 16);
                outBuf[StateMarkerIndex] = MarkerScheduleOff;
                return IncrementVersionByte(outBuf);
            }

            if ((mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape) && src.Length >= ManualOnLength)
            {
                var outBuf = new byte[ScheduleOffLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 23);
                Buffer.BlockCopy(src, 28, outBuf, 23, 15);
                outBuf[StateMarkerIndex] = MarkerScheduleOff;
                return IncrementVersionByte(outBuf);
            }

            if (mode == StateMode.ManualOff)
            {
                return Clone(src);
            }

            return BuildGenericOffState(src);
        }

        internal static byte[] BuildGenericOffState(byte[] src)
        {
            var mode = DetectStateMode(src);
            if (mode == StateMode.ManualOff || mode == StateMode.ScheduleOff)
            {
                return Clone(src);
            }

            if ((mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape) && src.Length >= ManualOnLength)
            {
                var outBuf = new byte[ManualOffLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                Buffer.BlockCopy(src, 25, outBuf, 23, 18);
                outBuf[StateMarkerIndex] = MarkerManualOff;
                return IncrementVersionByte(outBuf);
            }

            if ((mode == StateMode.ScheduleOn || mode == StateMode.ScheduleOnUnknownShape) && src.Length >= ScheduleOnLength)
            {
                var outBuf = new byte[ScheduleOffLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                Buffer.BlockCopy(src, 24, outBuf, 22, 16);
                outBuf[StateMarkerIndex] = MarkerScheduleOff;
                return IncrementVersionByte(outBuf);
            }

            throw new InvalidOperationException("Unsupported state shape for OFF conversion.");
        }

        internal static byte[] BuildManualEnabledState(byte[] source)
        {
            if (source == null || source.Length < 23)
            {
                throw new InvalidOperationException("State blob is too short to build manual ON.");
            }

            var outBuf = new byte[ManualOnLength];
            Buffer.BlockCopy(source, 0, outBuf, 0, Math.Min(23, source.Length));
            outBuf[23] = 0x10;
            outBuf[24] = 0x00;
            if (source.Length > 23)
            {
                var count = Math.Min(source.Length - 23, 18);
                Buffer.BlockCopy(source, 23, outBuf, 25, count);
            }

            outBuf[StateMarkerIndex] = MarkerManualOn;
            return IncrementVersionByte(outBuf);
        }

        internal static byte[] BuildScheduleEnabledState(byte[] src)
        {
            var mode = DetectStateMode(src);
            if (mode == StateMode.ScheduleOn)
            {
                return IncrementVersionByte(src);
            }

            if (mode == StateMode.ScheduleOff && src.Length >= ScheduleOffLength)
            {
                var outBuf = new byte[ScheduleOnLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                outBuf[22] = 0x10;
                outBuf[23] = 0x00;
                Buffer.BlockCopy(src, 22, outBuf, 24, 16);
                outBuf[StateMarkerIndex] = MarkerScheduleOn;
                return IncrementVersionByte(outBuf);
            }

            if (mode == StateMode.ManualOff && src.Length >= ManualOffLength)
            {
                var outBuf = new byte[ScheduleOnLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                outBuf[22] = 0x10;
                outBuf[23] = 0x00;
                Buffer.BlockCopy(src, 23, outBuf, 24, 16);
                outBuf[StateMarkerIndex] = MarkerScheduleOn;
                return IncrementVersionByte(outBuf);
            }

            if ((mode == StateMode.ManualOn || mode == StateMode.ManualOnUnknownShape) && src.Length >= ManualOnLength)
            {
                var outBuf = new byte[ScheduleOnLength];
                Buffer.BlockCopy(src, 0, outBuf, 0, 22);
                outBuf[22] = 0x10;
                outBuf[23] = 0x00;
                Buffer.BlockCopy(src, 27, outBuf, 24, 16);
                outBuf[StateMarkerIndex] = MarkerScheduleOn;
                return IncrementVersionByte(outBuf);
            }

            if (src.Length > StateMarkerIndex && src[StateMarkerIndex] == MarkerScheduleOn)
            {
                return IncrementVersionByte(src);
            }

            throw new InvalidOperationException("Unsupported state shape for schedule ON conversion.");
        }

        internal static byte[] SetScheduleEnabled(byte[] settings, bool shouldEnable)
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
                if (outBuf.Length > StateMarkerIndex && outBuf[StateMarkerIndex] == SettingsMarkerOn)
                {
                    outBuf[StateMarkerIndex] = SettingsMarkerOff;
                }
            }
            else
            {
                outBuf = new byte[src.Length + 2];
                Buffer.BlockCopy(src, 0, outBuf, 0, idx);
                outBuf[idx] = 0x02;
                outBuf[idx + 1] = 0x01;
                Buffer.BlockCopy(src, idx, outBuf, idx + 2, src.Length - idx);
                if (outBuf.Length > StateMarkerIndex && outBuf[StateMarkerIndex] == SettingsMarkerOff)
                {
                    outBuf[StateMarkerIndex] = SettingsMarkerOn;
                }
            }

            return IncrementVersionByte(outBuf);
        }

        internal static bool IsScheduleEnabled(byte[] settings)
        {
            if (settings == null || settings.Length < 10)
            {
                return false;
            }

            var idx = FindScheduleMarkerIndex(settings);
            return idx >= 2 && settings[idx - 2] == 0x02 && settings[idx - 1] == 0x01;
        }

        internal static string DescribeState(byte[] state)
        {
            if (state == null)
            {
                return "null";
            }
            var marker = state.Length > StateMarkerIndex ? state[StateMarkerIndex] : (byte)0;
            return "len=" + state.Length + ", marker=0x" + marker.ToString("X2");
        }

        internal static string DescribeSettings(byte[] settings)
        {
            if (settings == null)
            {
                return "null";
            }
            return "len=" + settings.Length + ", scheduleEnabled=" + IsScheduleEnabled(settings);
        }

        internal static byte[] Clone(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            var clone = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, clone, 0, bytes.Length);
            return clone;
        }

        internal static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] IncrementVersionByte(byte[] bytes)
        {
            var outBuf = Clone(bytes);
            for (int i = VersionStart; i < VersionEnd && i < outBuf.Length; i++)
            {
                if (outBuf[i] != 0xFF)
                {
                    outBuf[i] = (byte)((outBuf[i] + 1) & 0xFF);
                    break;
                }
            }
            return outBuf;
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
    }

    internal sealed class NightLightSnapshot
    {
        internal const int SnapshotFormatVersion = 2;

        internal int Version;
        internal Guid SessionId;
        internal DateTime CreatedAtUtc;
        internal string StatePath;
        internal string SettingsPath;
        internal byte[] StateBytes;
        internal byte[] SettingsBytes;
    }

    internal interface INightLightLogger
    {
        void Info(string message);
        void Error(string message);
    }

    internal enum StateMode
    {
        Unknown = 0,
        ManualOn,
        ScheduleOn,
        ManualOff,
        ScheduleOff,
        ManualOnUnknownShape,
        ScheduleOnUnknownShape
    }
}