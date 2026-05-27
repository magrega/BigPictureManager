using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BigPictureManager.Properties;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private static void TrySetDefaultPlaybackDevice(AudioDevice device, string reason)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Id))
            {
                BpmLog.WriteLine("[Audio] Cannot change default playback device (" + reason + "): no device selected.");
                return;
            }

            if (!SetDefaultDevice(device.Id))
            {
                BpmLog.WriteLine(
                    "[Error] [Audio] Failed to set default playback device to \"" + device.Name + "\" (" + reason + ")."
                );
                return;
            }

            BpmLog.WriteLine("[Audio] Default playback device set to \"" + device.Name + "\" (" + reason + ").");
        }

        private void StartAudioDeviceMonitoring()
        {
            NativeAudioApi.StartDeviceWatcher(() => QueueAudioDeviceRefresh(250));
        }

        private void QueueAudioDeviceRefresh(int debounceMs)
        {
            CancellationToken token;
            lock (_audioRefreshSync)
            {
                _audioRefreshCts.Cancel();
                _audioRefreshCts.Dispose();
                _audioRefreshCts = new CancellationTokenSource();
                token = _audioRefreshCts.Token;
            }

            _ = RefreshAudioMenuAsync(token, debounceMs);
        }

        private async Task RefreshAudioMenuAsync(CancellationToken token, int debounceMs)
        {
            try
            {
                if (debounceMs > 0)
                {
                    await Task.Delay(debounceMs, token);
                }

                var deviceItems = await Task.Run(() => GetPlaybackDevices(), token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _uiContext.Post(_ => ApplyAudioDevicesToMenu(deviceItems), null);
            }
            catch (OperationCanceledException)
            {
                // Expected during rapid device changes.
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Audio] Audio menu refresh failed: " + ex.Message);
            }
        }

        private void ApplyAudioDevicesToMenu(List<AudioDevice> deviceItems)
        {
            var audioMenu = CreateAudioMenu(deviceItems);
            if (audioMenu == null)
            {
                _audioMenuItem.Text = Resources.MenuAudioNoDevices;
                _audioMenuItem.Enabled = false;
                _audioMenuItem.DropDown = null;
                return;
            }

            _audioMenuItem.Text = Resources.MenuAudioChooseDevice;
            _audioMenuItem.Enabled = true;
            _audioMenuItem.DropDown = audioMenu;
        }

        private ContextMenuStrip CreateAudioMenu(List<AudioDevice> deviceItems)
        {
            var menu = new ContextMenuStrip();

            if (deviceItems.Count == 0)
            {
                _audioMenuItem.Text = Resources.MenuAudioNoDevices;
                _audioMenuItem.Enabled = false;
                return null;
            }

            var audioListItems = deviceItems.Select(
                device =>
                {
                    var item = new ToolStripMenuItem(device.Name) { Tag = device };

                    item.Click += (sender, e) =>
                    {
                        _selectedDevice = (AudioDevice)((ToolStripMenuItem)sender).Tag;
                        Settings.Default.LastAudioDeviceId = _selectedDevice.Id;
                        Settings.Default.Save();
                        UpdateDeviceCheckmarks(_selectedDevice, menu);
                        TrySetDefaultPlaybackDevice(_selectedDevice, "tray menu selection");
                    };

                    return item;
                }
            );

            menu.Items.AddRange(audioListItems.Cast<ToolStripItem>().ToArray());

            if (!string.IsNullOrEmpty(Settings.Default.LastAudioDeviceId))
            {
                var lastDeviceId = Settings.Default.LastAudioDeviceId;
                var lastDeviceItem = audioListItems.FirstOrDefault(i =>
                    ((AudioDevice)i.Tag).Id == lastDeviceId
                );

                if (lastDeviceItem != null)
                {
                    _selectedDevice = (AudioDevice)lastDeviceItem.Tag;
                    UpdateDeviceCheckmarks(_selectedDevice, menu);
                }
                else
                {
                    SetDefaultAudio(menu, audioListItems);
                }
            }
            else
            {
                SetDefaultAudio(menu, audioListItems);
            }

            return menu;
        }

        private void UpdateDeviceCheckmarks(AudioDevice selectedDevice, ContextMenuStrip menu)
        {
            foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
            {
                item.Checked = (item.Tag as AudioDevice)?.Id == selectedDevice?.Id;
            }
        }

        private void SetDefaultAudio(ContextMenuStrip menu, IEnumerable<ToolStripMenuItem> audioListItems)
        {
            var tvDevice = audioListItems.FirstOrDefault(d => d.Text.Contains("TV"));

            if (tvDevice != null)
            {
                _selectedDevice = (AudioDevice)tvDevice.Tag;
                UpdateDeviceCheckmarks(_selectedDevice, menu);
            }
            else
            {
                _selectedDevice = GetDefaultDevice();
                UpdateDeviceCheckmarks(_selectedDevice, menu);
            }
        }
    }
}
