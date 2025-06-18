using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Automation;
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
        private static bool OnWindowOpened(object sender, AutomationEventArgs automationEventArgs)
        {
            try
            {
                var element = sender as AutomationElement;
                if (element != null && element.Current.Name == "Steam Big Picture Mode")
                {
                    Console.WriteLine("{0} started!", element.Current.Name);
                    return true;
                }
                return false;
            }
            catch (ElementNotAvailableException)
            {
                return false;

            }
        }


        public Form1() { InitializeComponent(); }


        public ManagementEventWatcher stopWatcher = new ManagementEventWatcher(
                  new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName='steamwebhelper.exe'"));

        private void Form1_Load(object sender, EventArgs ev)
        {
            var controller = new CoreAudioController();
            var devices = controller.GetPlaybackDevices()
                .Where(d => d.State == DeviceState.Active)
                .ToList();

            audioDeviceList.DataSource = devices;
            audioDeviceList.DisplayMember = "FullName";
            CoreAudioDevice prevDevice = controller.DefaultPlaybackDevice;


            Automation.AddAutomationEventHandler(
        eventId: WindowPattern.WindowOpenedEvent,
        element: AutomationElement.RootElement,
        scope: TreeScope.Children,
        eventHandler: (s, e) =>
        {
            bool isBPRunning = OnWindowOpened(s, e);
            if (isBPRunning && audioDeviceList.InvokeRequired)
            {
                Console.WriteLine("Steam Big Picture Mode started!");
                audioDeviceList.Invoke((MethodInvoker)delegate
                {
                    if (audioDeviceList.SelectedItem is CoreAudioDevice selectedDevice)
                    {
                        prevDevice = controller.DefaultPlaybackDevice;
                        selectedDevice.SetAsDefault();
                    }
                });
            }
        });


            stopWatcher.EventArrived += async (s, e) =>
            {
                Console.WriteLine($"Stopped: {e.NewEvent["ProcessName"]}");

                Process[] steamwebhelperInstances = Process.GetProcessesByName("steamwebhelper");
                bool isBPRunning = steamwebhelperInstances
                    .Any(steamwebhelper => steamwebhelper.MainWindowTitle == "Steam Big Picture Mode");

                if (!isBPRunning)
                {
                    prevDevice.SetAsDefault();
                    if (turnOffBT.Checked) { await TurnOffBluetoothAsync(); }
                    Console.WriteLine($"Stopped: {e.NewEvent["ProcessName"]}");
                }
            };


            //stopWatcher.Start();
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