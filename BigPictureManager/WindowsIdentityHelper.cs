using System;
using System.Security.Principal;

namespace BigPictureManager
{
    internal static class WindowsIdentityHelper
    {
        internal static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                BpmLog.WriteLine("[Error] [Main] Could not determine elevation status: " + ex.Message);
                return false;
            }
        }
    }
}
