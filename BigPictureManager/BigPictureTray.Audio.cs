using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BigPictureManager.Properties;
using static BigPictureManager.NativeAudioApi;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private void ApplyAudioDevicesToMenu(List<AudioDevice> deviceItems)
        {
            // Dispose the previously built drop-down so its items and Click handlers don't leak
            // on every device-change refresh.
            var previousDropDown = _audioMenuItem.HasDropDownItems ? _audioMenuItem.DropDown : null;

            if (deviceItems.Count == 0)
            {
                _audioMenuItem.Text = Resources.MenuAudioNoDevices;
                _audioMenuItem.Enabled = false;
                _audioMenuItem.DropDown = null;
            }
            else
            {
                _audioMenuItem.Text = Resources.MenuAudioChooseDevice;
                _audioMenuItem.Enabled = true;
                _audioMenuItem.DropDown = CreateAudioMenu(deviceItems);
            }

            previousDropDown?.Dispose();
        }

        private ContextMenuStrip CreateAudioMenu(List<AudioDevice> deviceItems)
        {
            var menu = new ContextMenuStrip();

            // Materialize once: a deferred Select() would re-create items (and re-attach handlers)
            // on every enumeration.
            var audioListItems = deviceItems
                .Select(device =>
                {
                    var item = new ToolStripMenuItem(device.Name) { Tag = device };

                    item.Click += (sender, e) =>
                    {
                        _selectedDevice = (AudioDevice)((ToolStripMenuItem)sender).Tag;
                        Settings.Default.LastAudioDeviceId = _selectedDevice.Id;
                        Settings.Default.Save();
                        UpdateDeviceCheckmarks(_selectedDevice, menu);
                        _audio.SetDefault(_selectedDevice, "tray menu selection");
                    };

                    return item;
                })
                .ToList();

            menu.Items.AddRange(audioListItems.Cast<ToolStripItem>().ToArray());

            var lastDeviceId = Settings.Default.LastAudioDeviceId;
            var lastDeviceItem = string.IsNullOrEmpty(lastDeviceId)
                ? null
                : audioListItems.FirstOrDefault(i => ((AudioDevice)i.Tag).Id == lastDeviceId);

            if (lastDeviceItem != null)
            {
                _selectedDevice = (AudioDevice)lastDeviceItem.Tag;
                UpdateDeviceCheckmarks(_selectedDevice, menu);
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
                _selectedDevice = _audio.GetCurrentDefault();
                UpdateDeviceCheckmarks(_selectedDevice, menu);
            }
        }
    }
}
