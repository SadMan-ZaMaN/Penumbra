using System;
using System.Runtime.InteropServices;

namespace LumnWin
{
    /// <summary>Win32 bits WPF doesn't expose: click-through + always-on-top tool windows.</summary>
    internal static class Native
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x00000020;   // mouse clicks pass through
        const long WS_EX_TOOLWINDOW = 0x00000080;    // no taskbar / Alt+Tab entry
        const long WS_EX_LAYERED = 0x00080000;
        const long WS_EX_NOACTIVATE = 0x08000000;    // never steals focus

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void MakeOverlay(IntPtr hwnd)
        {
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }

        public static void BringToTop(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
