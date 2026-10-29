using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace LumnWin
{
    /// <summary>One packet from the Python engine (see engine/engine.py).</summary>
    public class LightState
    {
        public double level { get; set; }
        public double bass { get; set; }
        public double mid { get; set; }
        public double high { get; set; }
        public double pulse { get; set; }
        public bool beat { get; set; }
        public double bpm { get; set; }
        public List<string> colors { get; set; } = new();
        public bool playing { get; set; }
        public string title { get; set; } = "";
        public string artist { get; set; } = "";
        public string lyric { get; set; } = "";
        public string next { get; set; } = "";
        public double lprog { get; set; }
    }

    /// <summary>Receives engine packets on UDP 127.0.0.1:port on a background thread.</summary>
    public class EngineLink : IDisposable
    {
        readonly UdpClient _udp;
        readonly Thread _thread;
        volatile LightState? _latest;
        long _lastTicks;
        int _beats;   // beats since last Take(); a beat can fall between rendered frames

        public EngineLink(int port = 47800)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _thread = new Thread(Loop) { IsBackground = true, Name = "EngineLink" };
            _thread.Start();
        }

        /// <summary>Latest state (null if the engine has been silent for 2 s) and whether a beat happened since last call.</summary>
        public (LightState? state, bool beat) Take()
        {
            bool beat = Interlocked.Exchange(ref _beats, 0) > 0;
            var age = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks));
            return (age.TotalSeconds < 2 ? _latest : null, beat);
        }

        public bool Connected => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks)).TotalSeconds < 2;

        void Loop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try
                {
                    var data = _udp.Receive(ref any);
                    var s = JsonSerializer.Deserialize<LightState>(data);
                    if (s == null) continue;
                    if (s.beat) Interlocked.Increment(ref _beats);
                    _latest = s;
                    Interlocked.Exchange(ref _lastTicks, Stopwatch.GetTimestamp());
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                catch { /* bad packet, ignore */ }
            }
        }

        public void Dispose() => _udp.Dispose();
    }

    /// <summary>Starts engine/engine.py next to the app and stops it on exit.</summary>
    public static class EngineLauncher
    {
        public static Process? Start(out string? error)
        {
            error = null;
            string? script = FindEngine();
            if (script == null) { error = "engine\\engine.py not found"; return null; }
            string engineDir = Path.GetDirectoryName(script)!;

            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("LUMN_PYTHON");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
            candidates.Add(Path.Combine(engineDir, ".venv", "Scripts", "pythonw.exe"));
            candidates.Add("pythonw");
            candidates.Add("pyw");

            foreach (var exe in candidates)
            {
                if (exe.Contains(Path.DirectorySeparatorChar) && !File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        WorkingDirectory = engineDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add(script);
                    psi.ArgumentList.Add("--parent");
                    psi.ArgumentList.Add(Environment.ProcessId.ToString());
                    var p = Process.Start(psi);
                    if (p != null) return p;
                }
                catch { /* try next */ }
            }
            error = "couldn't start Python (set up engine\\.venv or LUMN_PYTHON)";
            return null;
        }

        static string? FindEngine()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            {
                var f = Path.Combine(dir.FullName, "engine", "engine.py");
                if (File.Exists(f)) return f;
            }
            return null;
        }
    }
}
