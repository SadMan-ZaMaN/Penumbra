using System;
using System.Runtime.InteropServices;

namespace Penumbra
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

        /// <summary>The same for a window that belongs to another thread, without waiting for it.</summary>
        public static void BringToTopAsync(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | 0x4000 /* SWP_ASYNCWINDOWPOS */);

        // ------------------------------------------------------------------ Esc during Night sky

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        /// <summary>Notices the Esc key in any app, without taking it away from that app, while Enabled.
        /// Night sky's black screen has no focus of its own, so this is how Esc can lift the black.
        /// Only on while Night sky is showing. Create and use it on the UI thread.</summary>
        public sealed class EscWatch : IDisposable
        {
            readonly HookProc _proc;   // field keeps the callback alive
            readonly Action _onEsc;
            IntPtr _hook;
            long _last;

            public EscWatch(Action onEsc) { _onEsc = onEsc; _proc = Hook; }

            public bool Enabled
            {
                get => _hook != IntPtr.Zero;
                set
                {
                    if (value == Enabled) return;
                    if (value) _hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandleW(null), 0);
                    else { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                }
            }

            IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
            {
                if (code >= 0 && wParam == (IntPtr)0x100 /* WM_KEYDOWN */ && Marshal.ReadInt32(lParam) == 0x1B /* VK_ESCAPE */
                    && Environment.TickCount64 - _last > 400)   // holding Esc down shouldn't flip it back and forth
                {
                    _last = Environment.TickCount64;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(_onEsc);
                }
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            public void Dispose() => Enabled = false;
        }

        public static void MoveTo(IntPtr hwnd, Rect r) =>
            SetWindowPos(hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE);

        /// <summary>Hide a window from screenshots and screen recordings (Windows 10 2004+).
        /// The engine samples the screen for video colors, so it mustn't see our own glow.</summary>
        public static void ExcludeFromCapture(IntPtr hwnd, bool exclude) =>
            SetWindowDisplayAffinity(hwnd, exclude ? 0x11u /* WDA_EXCLUDEFROMCAPTURE */ : 0u);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>Called by a second launch so the running copy may bring its panel to the front.</summary>
        public static void LetAnyAppComeToFront() => AllowSetForegroundWindow(-1 /* ASFW_ANY */);

        /// <summary>Rounded corners and a dark frame for the borderless pop-up (Windows 11; ignored on 10).</summary>
        public static void UseRoundedCorners(IntPtr hwnd)
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetPropW(IntPtr hWnd, string name, IntPtr data);
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);

        /// <summary>The main screen's refresh rate in Hz (60 if Windows won't say).</summary>
        public static int RefreshRate()
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettingsW(null, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmDisplayFrequency > 1
                ? dm.dmDisplayFrequency : 60;
        }

        public static Rect PrimaryMonitor()
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */), ref info);
            return info.rcMonitor;
        }

        /// <summary>The main screen minus the taskbar.</summary>
        public static Rect PrimaryWorkArea()
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */), ref info);
            return info.rcWork;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out Rect r);
        static IntPtr _taskbar;

        /// <summary>Where the main taskbar's window is right now, in screen pixels (null if there's none).
        /// An auto-hidden taskbar keeps its size and slides off the screen edge, so this follows it as it
        /// slides. It only reads the position, so it never waits on Explorer (safe on the render thread).</summary>
        public static Rect? TaskbarRect()
        {
            if (!IsWindow(_taskbar)) _taskbar = FindWindowW("Shell_TrayWnd", null);   // again if Explorer restarted
            return _taskbar != IntPtr.Zero && GetWindowRect(_taskbar, out var r) ? r : null;
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
                lpszClassName = "PenumbraGlow",
            };
            RegisterClassExW(ref wc);
            // layered + transparent = clicks pass through; no redirection bitmap = nothing copied through the CPU
            uint ex = (uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
                             | WS_EX_NOREDIRECTIONBITMAP);
            var hwnd = CreateWindowExW(ex, "PenumbraGlow", "Penumbra Glow", WS_POPUP,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            SetLayeredWindowAttributes(hwnd, 0, 255, 2 /* LWA_ALPHA */);
            // A topmost window covering the whole screen looks like a full-screen app to Windows, which
            // then won't slide out an auto-hidden taskbar and holds back notifications. This property
            // tells the shell it isn't one.
            SetPropW(hwnd, "NonRudeHWND", new IntPtr(1));
            return hwnd;
        }

        public static void Show(IntPtr hwnd, bool show) => ShowWindow(hwnd, show ? 4 /* SW_SHOWNOACTIVATE */ : 0);

        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        /// <summary>Let Windows' compositor fade a whole window (0 = invisible, 255 = solid), on the GPU, without
        /// the window drawing anything again. Call once before <see cref="SetWindowAlpha"/>.</summary>
        public static void MakeFadeable(IntPtr hwnd)
        {
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
        }

        public static void SetWindowAlpha(IntPtr hwnd, byte alpha) => SetLayeredWindowAttributes(hwnd, 0, alpha, 2 /* LWA_ALPHA */);

        /// <summary>Hide a window from the screen without hiding it from itself ("cloaked"): it still draws, the
        /// compositor just doesn't show it. Lets a window draw its first frame out of sight.</summary>
        public static void Cloak(IntPtr hwnd, bool cloak)
        {
            int on = cloak ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref on, sizeof(int));
        }

        /// <summary>Wait for the compositor's next frame (one step of a fade per screen refresh).</summary>
        public static void WaitForFrame()
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            DwmFlush();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds < 2) System.Threading.Thread.Sleep(4);   // nothing to wait for
        }

        /// <summary>Make the whole glow window see-through (0) or normal (255). Works from any thread,
        /// even if the window's own thread is stuck, which is what the Night sky safety net needs.</summary>
        public static void SetGlowOpacity(IntPtr hwnd, byte alpha) => SetLayeredWindowAttributes(hwnd, 0, alpha, 2 /* LWA_ALPHA */);

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
