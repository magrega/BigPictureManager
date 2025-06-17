using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Radios;

namespace BigPictureManager
{
    public partial class Form1 : Form
    {
        public async Task<bool> TurnOffBluetoothAsync()
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed)
                {
                    Console.WriteLine("Permission denied to control radios");
                    return false;
                }

                var radios = await Radio.GetRadiosAsync();
                var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);

                if (bluetoothRadio == null)
                {
                    Console.WriteLine("Bluetooth radio not found");
                    return false;
                }

                if (bluetoothRadio.State != RadioState.Off)
                {
                    await bluetoothRadio.SetStateAsync(RadioState.Off);
                    Console.WriteLine("Bluetooth turned off successfully");
                    return true;
                }

                Console.WriteLine("Bluetooth is already off");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }

        public Form1() { InitializeComponent(); }

        private void Form1_Load(object sender, EventArgs ev)
        {
            var controller = new CoreAudioController();
            IEnumerable<CoreAudioDevice> devices = controller.GetPlaybackDevices().Where(d => d.State == DeviceState.Active);

            audioDeviceList.SelectedItem = devices.ElementAt(0);
            audioDeviceList.SelectedText = devices.ElementAt(0).FullName;
            audioDeviceList.ValueMember = "FullName";

            foreach (CoreAudioDevice d in devices) { audioDeviceList.Items.Add(d); }
            CoreAudioDevice prevDevice = devices.ElementAt(0);

            string getProcessQuery = "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName='notepad.exe'";
            var startWatcher = new ManagementEventWatcher(new WqlEventQuery(getProcessQuery));

            startWatcher.EventArrived += (s, e) =>
            {
                Console.WriteLine($"Notepad started! PID: {e.NewEvent["ProcessID"]}");
                if (audioDeviceList.InvokeRequired)
                {
                    audioDeviceList.Invoke((MethodInvoker)delegate
                    {
                        if (audioDeviceList.SelectedItem is CoreAudioDevice selectedDevice)
                        {
                            prevDevice = controller.DefaultPlaybackDevice;
                            selectedDevice.SetAsDefault();
                        }
                    });
                }
            };

            string stopProcessQuery = "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName='notepad.exe'";
            var stopWatcher = new ManagementEventWatcher(new WqlEventQuery(stopProcessQuery));
            stopWatcher.EventArrived += async (s, e) =>
            {
                devices.ElementAt(1).SetAsDefault();
                prevDevice.SetAsDefault();

                if (turnOffBT.Checked) { await TurnOffBluetoothAsync(); }
                Console.WriteLine($"Stopped: {e.NewEvent["ProcessName"]}");
            };

            startWatcher.Start();
            stopWatcher.Start();
        }
    }
}