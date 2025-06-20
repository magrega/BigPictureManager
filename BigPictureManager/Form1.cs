using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Windows.Devices.Radios;

namespace BigPictureManager
{
    public partial class Form1 : Form
    {
        static private readonly string BPWindowName = "Steam Big Picture Mode";
        private AutomationElement _targetWindow;
        private CoreAudioDevice prevDevice;
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
        private static bool IsBigPictureWindow(object sender)
        {
            try
            {
                var element = sender as AutomationElement;
                if (element != null && element.Current.Name == BPWindowName) return true;
                return false;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        public Form1() { InitializeComponent(); }


        private void Form1_Load(object se, EventArgs ev)
        {
            var controller = new CoreAudioController();
            var devices = controller.GetPlaybackDevices()
                .Where(d => d.State == DeviceState.Active)
                .ToList();

            audioDeviceList.DataSource = devices;
            audioDeviceList.DisplayMember = "FullName";
            prevDevice = controller.DefaultPlaybackDevice;


            Automation.AddAutomationEventHandler(
        eventId: WindowPattern.WindowOpenedEvent,
        element: AutomationElement.RootElement,
        scope: TreeScope.Children,
        eventHandler: (s, e) =>
        {
            bool isBP = IsBigPictureWindow(s);
            if (isBP && audioDeviceList.InvokeRequired)
            {
                Console.WriteLine("Steam Big Picture Mode started!");
                audioDeviceList.Invoke((MethodInvoker)delegate
                {
                    if (audioDeviceList.SelectedItem is CoreAudioDevice selectedDevice)
                    {
                        prevDevice = controller.DefaultPlaybackDevice;
                        selectedDevice.SetAsDefault();
                    }

                    _targetWindow = s as AutomationElement;
                    Automation.AddAutomationEventHandler(
                       eventId: WindowPattern.WindowClosedEvent,
                       element: _targetWindow,
                       scope: TreeScope.Element,
                       eventHandler: OnWindowClosed
                       );
                }
                );
            }
        });

            async void OnWindowClosed(object sender, AutomationEventArgs e)
            {
                Console.WriteLine("Target window closed!");
                if (audioDeviceList.InvokeRequired) audioDeviceList.Invoke((MethodInvoker)(() => prevDevice.SetAsDefault()));

                if (turnOffBT.Checked) await TurnOffBluetoothAsync();

                //Automation.RemoveAutomationEventHandler(
                //    WindowPattern.WindowClosedEvent,
                //    sender as AutomationElement,
                //    OnWindowClosed);
            }
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Automation.RemoveAllEventHandlers();
        }
    }
}