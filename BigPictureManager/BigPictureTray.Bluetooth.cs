using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace BigPictureManager
{
    internal sealed partial class BigPictureTray
    {
        private static async Task<Radio> RequestBluetoothDeviceAsync()
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                BpmLog.WriteLine("[Error] [Bluetooth] Permission denied to control radios.");
                return null;
            }

            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
        }

        private async Task ManageBluetoothAsync(RadioState radioState)
        {
            try
            {
                var bluetoothRadio = await RequestBluetoothDeviceAsync();

                if (bluetoothRadio == null)
                {
                    BpmLog.WriteLine("[Bluetooth] Bluetooth radio not found.");
                }
                else
                {
                    await bluetoothRadio.SetStateAsync(radioState);
                    BpmLog.WriteLine("[Bluetooth] Bluetooth state is set to " + radioState + ".");
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Bluetooth] " + ex.Message);
            }
        }
    }
}
