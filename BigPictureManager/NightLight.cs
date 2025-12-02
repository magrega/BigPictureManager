using System;
using Microsoft.Win32;

namespace TinyScreen.Services
{
    public sealed class NightLight : IDisposable
    {
        private const string DataValueName = "Data";
        private const int StateIndex = 18;
        private const byte EnabledMarker = 0x15;
        private const byte DisabledMarker = 0x13;

        private readonly string _key =
            "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\DefaultAccount\\Current\\default$windows.data.bluelightreduction.bluelightreductionstate\\windows.data.bluelightreduction.bluelightreductionstate";

        private RegistryKey _registryKey;
        private bool _disposed;

        public NightLight()
        {
            _registryKey = Registry.CurrentUser?.OpenSubKey(_key, writable: true);
        }

        public bool Enabled
        {
            get
            {
                var data = ReadData();
                return data != null && data.Length > StateIndex && data[StateIndex] == EnabledMarker;
            }
            set
            {
                if (!Supported || Enabled == value)
                {
                    return;
                }

                UpdateState(value);
            }
        }

        public bool Supported
        {
            get { return _registryKey != null; }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void UpdateState(bool enable)
        {
            var data = ReadData();
            if (data == null)
            {
                return;
            }

            var newData = enable ? BuildEnabledData(data) : BuildDisabledData(data);
            if (newData == null)
            {
                return;
            }

            _registryKey.SetValue(DataValueName, newData, RegistryValueKind.Binary);
            _registryKey.Flush();
        }

        private byte[] BuildEnabledData(byte[] source)
        {
            const int enabledLength = 43;
            if (source.Length < 25)
            {
                return null;
            }

            var newData = new byte[enabledLength];
            Array.Copy(source, 0, newData, 0, 22);
            Array.Copy(source, 23, newData, 25, Math.Min(source.Length, enabledLength) - 23);
            newData[StateIndex] = EnabledMarker;
            newData[23] = 0x10;
            newData[24] = 0x00;
            IncrementVersion(newData);
            return newData;
        }

        private byte[] BuildDisabledData(byte[] source)
        {
            const int disabledLength = 41;
            if (source.Length < 25)
            {
                return null;
            }

            var newData = new byte[disabledLength];
            Array.Copy(source, 0, newData, 0, 22);
            Array.Copy(source, 25, newData, 23, Math.Min(source.Length - 25, disabledLength - 23));
            newData[StateIndex] = DisabledMarker;
            IncrementVersion(newData);
            return newData;
        }

        private void IncrementVersion(byte[] data)
        {
            for (var i = 10; i < Math.Min(15, data.Length); i++)
            {
                if (data[i] == 0xff)
                {
                    continue;
                }

                data[i]++;
                break;
            }
        }

        private byte[] ReadData()
        {
            if (!Supported)
            {
                return null;
            }

            return _registryKey.GetValue(DataValueName) as byte[];
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _registryKey?.Dispose();
            }

            _registryKey = null;
            _disposed = true;
        }

        ~NightLight()
        {
            Dispose(false);
        }
    }
}