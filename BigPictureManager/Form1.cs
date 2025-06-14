using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;



namespace BigPictureManager
{


    public partial class Form1 : Form
    {

        public async Task<bool> TurnOffBluetoothAsync()
        {
            try
            {
                // Request access to radios
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed)
                {
                    Console.WriteLine("Permission denied to control radios");
                    return false;
                }

                // Get all radios
                var radios = await Radio.GetRadiosAsync();

                // Find the Bluetooth radio
                var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);

                if (bluetoothRadio == null)
                {
                    Console.WriteLine("Bluetooth radio not found");
                    return false;
                }

                // Turn off Bluetooth
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
        public Form1()
        {
            InitializeComponent();
        }

        private async void button1_Click(object sender, EventArgs e)
        {



            bool IsProcessRunning(string processName)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                return processes.Length > 0;
            }

            // Usage:
            //if (IsProcessRunning("notepad"))
            //{
            //    Console.WriteLine("Notepad is running!");
            //}
            //else
            //{
            //    Console.WriteLine("Notepad is NOT running.");
            //}




            async Task DisconnectBluetoothDeviceAsync()
            {
                // Find the Bluetooth device
                string deviceSelector = BluetoothDevice.GetDeviceSelector();
                DeviceInformationCollection bTdevices = await DeviceInformation.FindAllAsync(deviceSelector);

                DeviceInformationCollection PairedBluetoothDevices =
       await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));


                foreach (DeviceInformation d in PairedBluetoothDevices)
                {
                    Console.WriteLine(d.Name);
                }


                // Disconnect
                //bluetoothDevice.Dispose(); // Releases the connection
                //Console.WriteLine($"Disconnected: {targetDevice.Name}");
            }

            await TurnOffBluetoothAsync();

            //IEnumerable<CoreAudioDevice> devices = new CoreAudioController().GetPlaybackDevices();

            //foreach (CoreAudioDevice d in devices)
            //{
            //    if (!d.IsDefaultDevice)
            //    {
            //        Console.WriteLine(d.FullName);
            //        d.SetAsDefault();
            //        return;
            //    }
            //}

        }
    }
}
