using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace Penumbra
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
        public string source { get; set; } = "";   // where colors came from: art / screen / default
        public string kind { get; set; } = "";     // what's playing: music / video / none
        public bool sound { get; set; }            // something audible is playing right now
        public bool playing { get; set; }
        public string title { get; set; } = "";
        public string artist { get; set; } = "";
        public string lyric { get; set; } = "";
        public string next { get; set; } = "";
        public double lprog { get; set; }
        public int lidx { get; set; } = -1;   // which lyric line (a repeated chorus line still counts as new)
        public double lt { get; set; }         // seconds into the line
        public double ld { get; set; }         // estimated seconds it takes to sing the line
        public string lstat { get; set; } = "";   // lyrics for this song: found / searching / plain (untimed only) / none / private
        public int art_id { get; set; } = -1;   // which cover the song has (changes with the song / when its art arrives)
        public string art { get; set; } = "";    // that cover as a base64 JPEG, in about one packet a second
        public double pos { get; set; }          // seconds into the song (for the Now playing screen's bar)
        public double dur { get; set; }          // the song's length in seconds (0 = unknown)
        public int lyr_id { get; set; } = -1;    // which lyrics the song has (changes with the song / when they arrive)
        public List<string>? lines { get; set; } // all of them (lidx indexes these), in about one packet a second
    }

    /// <summary>A cover from the engine: its id and JPEG bytes (base64).</summary>
    public sealed record CoverArt(int Id, string Jpeg);

    /// <summary>All of a song's lyric lines, for the Now playing screen's list.</summary>
    public sealed record SongLyrics(int Id, string[] Lines);

    /// <summary>Receives engine packets on UDP 127.0.0.1:port on a background thread.</summary>
    public class EngineLink : IDisposable
    {
        readonly UdpClient _udp;
        readonly Thread _thread;
        volatile LightState? _latest;
        long _lastTicks;
        int _beats;   // beats since last Take(); a beat can fall between rendered frames

        readonly IPEndPoint _buttons;   // where the engine listens for the Now playing screen's buttons

        public EngineLink(int port = 47800)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _buttons = new IPEndPoint(IPAddress.Loopback, port + 10);
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

        /// <summary>Ask the engine to "toggle" (play / pause), "next", "previous", or "seek" to
        /// <paramref name="pos"/> seconds, in the song that's playing.</summary>
        public void Send(string cmd, double? pos = null)
        {
            try
            {
                string json = pos is double p
                    ? $"{{\"cmd\":\"{cmd}\",\"pos\":{p.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}"
                    : $"{{\"cmd\":\"{cmd}\"}}";
                var data = System.Text.Encoding.UTF8.GetBytes(json);
                _udp.Send(data, data.Length, _buttons);
            }
            catch { /* engine not running: nothing to do */ }
        }

        /// <summary>The last cover the engine sent. Kept apart from the latest packet, because only about one
        /// packet a second carries it and the readers don't see every packet.</summary>
        public CoverArt? Art => _art;
        volatile CoverArt? _art;

        /// <summary>The last full set of lyric lines the engine sent (kept apart for the same reason).</summary>
        public SongLyrics? Lyrics => _lyrics;
        volatile SongLyrics? _lyrics;

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
                    if (s.art.Length > 0)
                    {
                        if (_art?.Id != s.art_id) _art = new CoverArt(s.art_id, s.art);
                        s.art = "";   // the frame-by-frame state doesn't need to carry it around
                    }
                    if (s.lines != null)
                    {
                        var old = _lyrics;
                        if (old == null || old.Id != s.lyr_id || old.Lines.Length != s.lines.Count)
                            _lyrics = new SongLyrics(s.lyr_id, s.lines.ToArray());
                        s.lines = null;
                    }
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
            var env = Environment.GetEnvironmentVariable("PENUMBRA_PYTHON");
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
            error = "couldn't start Python (set up engine\\.venv or PENUMBRA_PYTHON)";
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
