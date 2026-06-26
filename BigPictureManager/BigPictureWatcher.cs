using System;
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
                return element != null && element.Current.Name == AppConstants.BigPictureWindowName;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            Automation.RemoveAllEventHandlers();
        }
    }
}
