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
        const long WS_EX_TOPMOST = 0x00000008;
        const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        const uint WS_POPUP = 0x80000000;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        public static void MakeOverlay(IntPtr hwnd)
        {
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }

        public static void BringToTop(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        public static void MoveTo(IntPtr hwnd, Rect r) =>
            SetWindowPos(hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE);

        /// <summary>Hide a window from screenshots and screen recordings (Windows 10 2004+).
        /// The engine samples the screen for video colors, so it mustn't see our own glow.</summary>
        public static void ExcludeFromCapture(IntPtr hwnd, bool exclude) =>
            SetWindowDisplayAffinity(hwnd, exclude ? 0x11u /* WDA_EXCLUDEFROMCAPTURE */ : 0u);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        /// <summary>Dark title bar to match the panel (Windows 10 20H1+ / 11).</summary>
        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }

        // ------------------------------------------------------------------ the glow's own window

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public Rect rcMonitor, rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

        /// <summary>Per-monitor DPI for windows created on this thread, so sizes are real pixels.</summary>
        public static void UsePhysicalPixels() => SetThreadDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2

        public static double DpiScale(IntPtr hwnd) => Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;

        public static Rect PrimaryMonitor()
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */), ref info);
            return info.rcMonitor;
        }

        /// <summary>A borderless, click-through, topmost window with no pixels of its own:
        /// DirectComposition puts the GPU-drawn glow (with per-pixel alpha) on screen.</summary>
        public static IntPtr CreateGlowWindow(WndProc proc, Rect r)
        {
            var inst = GetModuleHandleW(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = inst,
                lpszClassName = "LumnWinGlow",
            };
            RegisterClassExW(ref wc);
            // layered + transparent = clicks pass through; no redirection bitmap = nothing copied through the CPU
            uint ex = (uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
                             | WS_EX_NOREDIRECTIONBITMAP);
            var hwnd = CreateWindowExW(ex, "LumnWinGlow", "LumnWin Glow", WS_POPUP,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            SetLayeredWindowAttributes(hwnd, 0, 255, 2 /* LWA_ALPHA */);
            return hwnd;
        }

        public static void Show(IntPtr hwnd, bool show) => ShowWindow(hwnd, show ? 4 /* SW_SHOWNOACTIVATE */ : 0);

        public static void PumpMessages()
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
    }
}
