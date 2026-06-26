using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    /// <summary>
    /// Owns playback-device enumeration, change monitoring (debounced), and default-device switching.
    /// UI-agnostic: it raises <see cref="DevicesChanged"/> on the supplied synchronization context.
    /// </summary>
    internal sealed class AudioDeviceService : IDisposable
    {
        private readonly SynchronizationContext _syncContext;
        private readonly object _refreshSync = new object();
        private CancellationTokenSource _refreshCts = new CancellationTokenSource();
        private bool _watching;

        /// <summary>
        /// Raised on the supplied synchronization context with the current active playback devices
        /// whenever the device list (or a device name) changes.
        /// </summary>
        public event Action<List<AudioDevice>> DevicesChanged;

        public AudioDeviceService(SynchronizationContext syncContext)
        {
            _syncContext = syncContext ?? throw new ArgumentNullException(nameof(syncContext));
        }

        /// <summary>Starts watching for device changes and performs the initial enumeration.</summary>
        public void Start()
        {
            NativeAudioApi.StartDeviceWatcher(() => QueueRefresh(250));
            _watching = true;
            QueueRefresh(0);
        }

        /// <summary>Current default playback device, or null if it can't be read.</summary>
        public AudioDevice GetCurrentDefault() => GetDefaultDevice();

        /// <summary>
        /// Sets the default playback device for all roles and logs the outcome.
        /// </summary>
        /// <returns>True when the device became the default.</returns>
        public bool SetDefault(AudioDevice device, string reason)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Id))
            {
                BpmLog.WriteLine("[Audio] Cannot change default playback device (" + reason + "): no device selected.");
                return false;
            }

            if (!SetDefaultDevice(device.Id))
            {
                BpmLog.WriteLine(
                    "[Error] [Audio] Failed to set default playback device to \"" + device.Name + "\" (" + reason + ")."
                );
                return false;
            }

            BpmLog.WriteLine("[Audio] Default playback device set to \"" + device.Name + "\" (" + reason + ").");
            return true;
        }

        private void QueueRefresh(int debounceMs)
        {
            CancellationToken token;
            lock (_refreshSync)
            {
                _refreshCts.Cancel();
                _refreshCts.Dispose();
                _refreshCts = new CancellationTokenSource();
                token = _refreshCts.Token;
            }

            _ = RefreshAsync(token, debounceMs);
        }

        private async Task RefreshAsync(CancellationToken token, int debounceMs)
        {
            try
            {
                if (debounceMs > 0)
                {
                    await Task.Delay(debounceMs, token);
                }

                var devices = await Task.Run(() => GetPlaybackDevices(), token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _syncContext.Post(_ => DevicesChanged?.Invoke(devices), null);
            }
            catch (OperationCanceledException)
            {
                // Expected during rapid device changes.
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Audio] Audio device refresh failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_watching)
            {
                NativeAudioApi.StopDeviceWatcher();
                _watching = false;
            }

            lock (_refreshSync)
            {
                _refreshCts.Cancel();
                _refreshCts.Dispose();
                _refreshCts = new CancellationTokenSource();
            }
        }
    }
}
