using System;
using System.Runtime.InteropServices;

namespace BigPictureManager
{
    internal static class NativeMethods
    {
        internal const int WM_APPCOMMAND = 0x0319;

        // APPCOMMAND codes (passed in the high word of lParam, i.e. value << 16).
        internal const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        internal const int APPCOMMAND_MEDIA_PAUSE = 47;
        internal const int APPCOMMAND_MEDIA_STOP = 13;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }
}
