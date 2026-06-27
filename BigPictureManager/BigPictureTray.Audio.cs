using System;
using System.Collections.Generic;
using System.Linq;
using BigPictureManager.Properties;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        /// <summary>Latest snapshot of active playback devices, used to build the tray menu on demand.</summary>
        private List<AudioDevice> _audioDevices = new List<AudioDevice>();

        private void OnAudioDevicesChanged(List<AudioDevice> devices)
        {
            _audioDevices = devices ?? new List<AudioDevice>();
            ResolveSelectedDevice();
        }

        /// <summary>
        /// Picks the device the menu shows as selected: the saved one if still present, otherwise a TV
        /// device if any, otherwise the current system default.
        /// </summary>
        private void ResolveSelectedDevice()
        {
            if (_audioDevices.Count == 0)
            {
                _selectedDevice = null;
                return;
            }

            var lastDeviceId = Settings.Default.LastAudioDeviceId;
            if (!string.IsNullOrEmpty(lastDeviceId))
            {
                var saved = _audioDevices.FirstOrDefault(d => d.Id == lastDeviceId);
                if (saved != null)
                {
                    _selectedDevice = saved;
                    return;
                }
            }

            var tvDevice = _audioDevices.FirstOrDefault(
                d => d.Name.IndexOf("TV", StringComparison.OrdinalIgnoreCase) >= 0
            );
            _selectedDevice = tvDevice ?? _audio.GetCurrentDefault() ?? _audioDevices[0];
        }

        private void SelectAudioDevice(AudioDevice device)
        {
            // Only remember the preference; the system default is changed at Big Picture start (and
            // restored on exit), not when the user picks a device here.
            _selectedDevice = device;
            Settings.Default.LastAudioDeviceId = device.Id;
            SchedulePersist();
        }
    }
}
