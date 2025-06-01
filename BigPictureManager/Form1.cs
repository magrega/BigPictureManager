using AudioSwitcher.AudioApi.CoreAudio;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

public class XboxController
{
    [DllImport("XInput1_4.dll", EntryPoint = "#103")]
    private static extern int FnOff(int controllerIndex);

    public static void TurnOff(int controllerIndex = 0)
    {
        FnOff(controllerIndex); // Tries to turn off controller 0-3
    }
}



namespace BigPictureManager
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            XboxController.TurnOff();
            bool IsProcessRunning(string processName)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                return processes.Length > 0;
            }

            // Usage:
            if (IsProcessRunning("notepad"))
            {
                Console.WriteLine("Notepad is running!");
            }
            else
            {
                Console.WriteLine("Notepad is NOT running.");
            }




            async Task DisconnectBluetoothDeviceAsync()
            {
                // Find the Bluetooth device
                string deviceSelector = BluetoothDevice.GetDeviceSelector();
                DeviceInformationCollection bTdevices = await DeviceInformation.FindAllAsync(deviceSelector);

                foreach (DeviceInformation d in bTdevices)
                {
                    Console.WriteLine(d.Name);
                }


                // Disconnect
                //bluetoothDevice.Dispose(); // Releases the connection
                //Console.WriteLine($"Disconnected: {targetDevice.Name}");
            }


            await DisconnectBluetoothDeviceAsync();

            IEnumerable<CoreAudioDevice> devices = new CoreAudioController().GetPlaybackDevices();

            foreach (CoreAudioDevice d in devices)
            {
                if (!d.IsDefaultDevice)
                {
                    Console.WriteLine(d.FullName);
                    d.SetAsDefault();
                    return;
                }
            }

        }
    }
}
