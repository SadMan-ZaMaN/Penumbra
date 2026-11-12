using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Penumbra
{
    public static class Program
    {
        static string[] _args = Array.Empty<string>();
        static Action? _quit;

        [STAThread]
        public static void Main(string[] args)
        {
            // started by Restart: let the old copy finish closing first (it still holds the single-instance
            // lock, the UDP port and the engine)
            int r = Array.IndexOf(args, "--restart-after");
            if (r >= 0 && r + 1 < args.Length && int.TryParse(args[r + 1], out int oldPid))
            {
                try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(10_000); }
                catch { /* already gone */ }
            }

            using var openPanel = new EventWaitHandle(false, EventResetMode.AutoReset, "Penumbra.OpenPanel");
            using var single = new Mutex(true, "Penumbra.SingleInstance", out bool first);
            if (!first)
            {
                // already running: ask that copy to show its panel (handy when the tray icon is hidden)
                Native.LetAnyAppComeToFront();
                openPanel.Set();
                return;
            }

            // WPF (the panel and the Now playing screen) draws on the CPU. Both are mostly still pictures,
            // and WPF's own GPU engine cost about 100 MB of RAM and 300 MB of GPU memory the moment a window
            // first showed, kept until exit. The glow and lyrics don't use WPF: they stay on the GPU.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = Settings.Load();

            EngineLink link;
            try { link = new EngineLink(47800); }
            catch (Exception e)
            {
                MessageBox.Show("Couldn't open UDP port 47800: " + e.Message, "Penumbra");
                return;
            }

            // start the Python engine unless you're running it yourself (e.g. with --debug)
            Process? engine = null;
            string? engineError = null;
            if (!args.Contains("--no-engine"))
                engine = EngineLauncher.Start(out engineError);

            var glow = new GlowController(settings, link);
            var window = new GlowWindow(settings, glow, black: args.Contains("--black"), testFreeze: args.Contains("--test-freeze"));
            Tray? tray = null;
            _args = args;
            _quit = () => { ControlPanel.Quitting = true; app.Shutdown(); };
            tray = new Tray(settings, glow, link, _quit);
            if (engineError != null)
                tray.Notify("Penumbra: engine not started", engineError);
            if (args.Contains("--panel")) tray.OpenPanel();
            new Thread(() =>
            {
                while (openPanel.WaitOne()) app.Dispatcher.BeginInvoke(new Action(tray.OpenPanel));
            }) { IsBackground = true, Name = "OpenPanel" }.Start();

            // the glow window starts on its own thread; report if it couldn't
            var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            check.Tick += (_, _) =>
            {
                check.Stop();
                if (window.Error != null) tray.Notify("Penumbra: glow couldn't start", window.Error);
            };
            check.Start();

            // Night sky blacks out the whole screen; Esc lifts the black (Esc again brings it back).
            // The key watch is only switched on while the screen is black, or lifted by Esc.
            var esc = new Native.EscWatch(() => window.BlackLifted = !window.BlackLifted);
            var dark = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            dark.Tick += (_, _) => esc.Enabled = window.DarkActive || window.BlackLifted;
            dark.Start();

            // Night sky safety net: the black screen must never outlive a stuck glow window. A thread of
            // its own (so it works even if everything else is stuck) watches the render loop's heartbeat.
            new Thread(() =>
            {
                bool hidden = false;
                while (true)
                {
                    Thread.Sleep(500);
                    double stuck = window.SinceLastFrame;
                    if (!hidden && window.DarkActive && stuck > 3)
                    {
                        Native.SetGlowOpacity(window.Handle, 0);   // first: make the black see-through
                        hidden = true;
                    }
                    else if (hidden && stuck < 1)
                    {
                        Native.SetGlowOpacity(window.Handle, 255); // it came back by itself
                        hidden = false;
                    }
                    else if (hidden && stuck > 10)
                        Process.GetCurrentProcess().Kill();   // last resort: closing always clears it (the engine quits too)
                }
            }) { IsBackground = true, Name = "NightSkySafety" }.Start();

            app.Exit += (_, _) =>
            {
                esc.Dispose();
                window.Dispose();
                tray?.Dispose();
                link.Dispose();
                try { if (engine is { HasExited: false }) engine.Kill(entireProcessTree: true); } catch { }
                settings.Save();
            };
            app.Run();
        }

        /// <summary>The panel's restart button: start a fresh copy (same options, panel open so you can see it's
        /// back), then close this one, engine and all. Settings are saved on the way out and read again.</summary>
        public static void Restart()
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            for (int i = 0; i < _args.Length; i++)
            {
                if (_args[i] == "--restart-after") { i++; continue; }   // (and the old copy's number)
                if (_args[i] != "--panel") psi.ArgumentList.Add(_args[i]);
            }
            psi.ArgumentList.Add("--panel");
            psi.ArgumentList.Add("--restart-after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Native.LetAnyAppComeToFront();   // the new copy may bring its panel to the front
            try { Process.Start(psi); }
            catch { return; }                // couldn't start one: better to keep running than just quit
            _quit?.Invoke();
        }
    }
}
