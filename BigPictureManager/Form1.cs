using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using System;
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

        public ManagementEventWatcher startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName='notepad.exe'"));

        public ManagementEventWatcher stopWatcher = new ManagementEventWatcher(
                  new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName='notepad.exe'"));
        private void Form1_Load(object sender, EventArgs ev)
        {
            var controller = new CoreAudioController();
            var devices = controller.GetPlaybackDevices()
                .Where(d => d.State == DeviceState.Active)
                .ToList();

            audioDeviceList.DataSource = devices;
            audioDeviceList.DisplayMember = "FullName";
            CoreAudioDevice prevDevice = controller.DefaultPlaybackDevice;


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

            stopWatcher.EventArrived += async (s, e) =>
            {
                prevDevice.SetAsDefault();
                if (turnOffBT.Checked) { await TurnOffBluetoothAsync(); }
                Console.WriteLine($"Stopped: {e.NewEvent["ProcessName"]}");
            };

            startWatcher.Start();
            stopWatcher.Start();
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            startWatcher?.Stop();
            stopWatcher?.Stop();
            startWatcher?.Dispose();
            stopWatcher?.Dispose();
        }
    }
}