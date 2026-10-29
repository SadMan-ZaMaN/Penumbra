using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace LumnWin
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            using var single = new Mutex(true, "LumnWin.Overlay.SingleInstance", out bool first);
            if (!first) return;   // already running

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = Settings.Load();

            EngineLink link;
            try { link = new EngineLink(47800); }
            catch (Exception e)
            {
                MessageBox.Show("Couldn't open UDP port 47800: " + e.Message, "LumnWin");
                return;
            }

            // start the Python engine unless you're running it yourself (e.g. with --debug)
            Process? engine = null;
            string? engineError = null;
            if (!args.Contains("--no-engine"))
                engine = EngineLauncher.Start(out engineError);

            var glow = new GlowController(settings, link);
            Tray? tray = null;
            tray = new Tray(settings, glow, link, () => app.Shutdown());
            if (engineError != null)
                tray.Notify("LumnWin: engine not started", engineError);

            app.Exit += (_, _) =>
            {
                tray?.Dispose();
                link.Dispose();
                try { if (engine is { HasExited: false }) engine.Kill(entireProcessTree: true); } catch { }
                settings.Save();
            };
            app.Run();
        }
    }
}
