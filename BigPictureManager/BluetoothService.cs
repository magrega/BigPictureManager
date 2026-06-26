using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace BigPictureManager
{
    /// <summary>
    /// Wraps the system Bluetooth radio: a single access request, a cached radio, and state changes.
    /// </summary>
    internal sealed class BluetoothService
    {
        private Radio _radio;

        /// <summary>True once <see cref="InitializeAsync"/> has located a Bluetooth radio.</summary>
        public bool IsAvailable => _radio != null;

        /// <summary>
        /// Requests radio access once and caches the Bluetooth radio.
        /// </summary>
        /// <returns>Whether a controllable Bluetooth radio was found.</returns>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed)
                {
                    BpmLog.WriteLine("[Error] [Bluetooth] Permission denied to control radios.");
                    return false;
                }

                var radios = await Radio.GetRadiosAsync();
                _radio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
                if (_radio == null)
                {
                    BpmLog.WriteLine("[Bluetooth] Bluetooth radio not found.");
                }

                return _radio != null;
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Bluetooth] Radio initialization failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Sets the cached Bluetooth radio state. No-op when no radio is available.
        /// </summary>
        public async Task SetStateAsync(RadioState state)
        {
            var radio = _radio;
            if (radio == null)
            {
                BpmLog.WriteLine("[Bluetooth] Bluetooth radio not found.");
                return;
            }

            try
            {
                await radio.SetStateAsync(state);
                BpmLog.WriteLine("[Bluetooth] Bluetooth state is set to " + state + ".");
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Bluetooth] " + ex.Message);
            }
        }
    }
}
