using System;
using System.Diagnostics;
using System.Windows.Automation;

namespace BigPictureManager
{
    /// <summary>
    /// Detects the Steam Big Picture window via UI Automation and raises <see cref="Opened"/> /
    /// <see cref="Closed"/>. Events fire on a UI Automation worker thread.
    /// </summary>
    internal sealed class BigPictureWatcher : IDisposable
    {
        private AutomationElement _targetWindow;

        /// <summary>Raised when the Big Picture window opens.</summary>
        public event EventHandler Opened;

        /// <summary>Raised when the tracked Big Picture window closes.</summary>
        public event EventHandler Closed;

        /// <summary>Begins listening for the Big Picture window to open.</summary>
        public void Start()
        {
            Automation.AddAutomationEventHandler(
                eventId: WindowPattern.WindowOpenedEvent,
                element: AutomationElement.RootElement,
                scope: TreeScope.Children,
                eventHandler: OnWindowOpened
            );
        }

        private void OnWindowOpened(object sender, AutomationEventArgs e)
        {
            if (!IsBigPictureWindow(sender))
            {
                return;
            }

            _targetWindow = sender as AutomationElement;
            Automation.AddAutomationEventHandler(
                eventId: WindowPattern.WindowClosedEvent,
                element: _targetWindow,
                scope: TreeScope.Element,
                eventHandler: OnWindowClosed
            );

            Opened?.Invoke(this, EventArgs.Empty);
        }

        private void OnWindowClosed(object sender, AutomationEventArgs e)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsBigPictureWindow(object sender)
        {
            try
            {
                var element = sender as AutomationElement;
                if (element == null)
                {
                    return false;
                }

                var name = element.Current.Name;
                if (string.IsNullOrEmpty(name)
                    || name.IndexOf(AppConstants.BigPictureWindowNameFragment, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }

                // Guard against any other window that happens to contain "Big Picture" in its title
                // (a text file, a browser tab, etc.): require it to belong to a Steam process.
                return IsSteamWindow(element);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private static bool IsSteamWindow(AutomationElement element)
        {
            try
            {
                using (var process = Process.GetProcessById(element.Current.ProcessId))
                {
                    // Steam renders Big Picture in steamwebhelper.exe; allow steam.exe too so a Steam
                    // version change in window ownership doesn't break detection.
                    return process.ProcessName.StartsWith(AppConstants.SteamProcessNamePrefix, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                // Process already exited or not inspectable — don't claim it as Big Picture.
                return false;
            }
        }

        public void Dispose()
        {
            Automation.RemoveAllEventHandlers();
        }
    }
}
