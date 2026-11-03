using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Penumbra
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            using var openPanel = new EventWaitHandle(false, EventResetMode.AutoReset, "Penumbra.OpenPanel");
            using var single = new Mutex(true, "Penumbra.SingleInstance", out bool first);
            if (!first)
            {
                // already running: ask that copy to show its panel (handy when the tray icon is hidden)
                Native.LetAnyAppComeToFront();
                openPanel.Set();
                return;
            }

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
            var window = new GlowWindow(settings, glow, black: args.Contains("--black"));
            Tray? tray = null;
            tray = new Tray(settings, glow, link, () => { ControlPanel.Quitting = true; app.Shutdown(); });
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

            // Night sky blacks out the whole screen; Esc brings the taskbar back (Esc again hides it).
            // The key watch is only switched on while the screen is black.
            var esc = new Native.EscWatch(() => window.RevealTaskbar = !window.RevealTaskbar);
            var dark = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            dark.Tick += (_, _) => esc.Enabled = window.DarkActive;
            dark.Start();

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
    }
}
