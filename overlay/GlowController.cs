using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LumnWin
{
    /// <summary>Everything the shader needs for one frame. Sizes are in DIPs; GlowWindow converts to pixels.</summary>
    public class GlowFrame
    {
        public double Opacity, Rim, Halo, Radius, Time, Phase, Base, Wave, Pulse, White;
        public Color Primary, Secondary;
        public GlowController.Stream[] Streams = Array.Empty<GlowController.Stream>();
    }

    /// <summary>Turns engine packets + settings into smooth per-frame light.
    /// Update() runs on the glow's render thread; the lyrics line is driven from the UI thread.</summary>
    public class GlowController
    {
        /// <summary>A stretch of light that grows, travels along the edge and fades.
        /// Pos and HalfLength are fractions of the screen's perimeter.</summary>
        public struct Stream
        {
            public double Pos, Speed, HalfLength, TargetLength, Brightness, Strength, Age, Life;
            public bool Alive;
        }

        const int MaxStreams = 8;

        readonly Settings _settings;
        readonly EngineLink _link;
        readonly LyricsWindow _lyrics = new();
        readonly Random _rng = new();
        double _lastT = -1;

        // smoothed values
        Color _primary, _secondary;
        double _opacity, _rim, _halo, _phase, _phaseKick, _pulse, _wave, _waveT, _base, _speedKick, _energy, _nextSpawn;
        readonly Stream[] _streams = new Stream[MaxStreams];
        readonly GlowFrame _frame = new();

        public LightState? Last { get; private set; }
        public Color Primary => _primary;       // what's on screen right now (for the panel)
        public Color Secondary => _secondary;

        public GlowController(Settings settings, EngineLink link)
        {
            _settings = settings;
            _link = link;
            _primary = Parse(settings.Primary, Colors.MediumPurple);
            _secondary = Parse(settings.Secondary, Colors.HotPink);
            _frame.Streams = new Stream[MaxStreams];

            ApplyLayout();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
                Application.Current.Dispatcher.BeginInvoke(new Action(ApplyLayout));

            var lyricsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            lyricsTimer.Tick += (_, _) => UpdateLyrics();
            lyricsTimer.Start();
        }

        /// <summary>Call after changing settings or when displays change.</summary>
        public void ApplyLayout()
        {
            _lyrics.Place(SystemParameters.WorkArea);
            if (_lyrics.Handle != IntPtr.Zero) Native.ExcludeFromCapture(_lyrics.Handle, !_settings.ShowInCapture);
        }

        /// <summary>Advance to time t (seconds) and return the frame to draw.</summary>
        public GlowFrame Update(double t)
        {
            double dt = _lastT < 0 ? 1 / 60.0 : Math.Clamp(t - _lastT, 0.001, 0.1);
            _lastT = t;

            var (s, beat) = _link.Take();
            Last = s;
            bool playing = s != null && s.sound;     // any audible sound; silence fades the glow out
            bool steady = _settings.Mode == GlowMode.Steady;
            bool calm = steady || (!playing && _settings.IdleGlow);

            // ---- target colors
            Color tp = Parse(_settings.Primary, Colors.MediumPurple);
            Color ts = Parse(_settings.Secondary, Colors.HotPink);
            if (_settings.ColorSource != ColorSource.Custom && s != null && s.colors.Count >= 2)
            {
                tp = Parse(s.colors[0], tp);
                ts = Parse(s.colors[1], ts);
            }

            // ---- targets. Loud music changes how bright and lively the streams are, not how big the glow is.
            double b = _settings.Brightness;
            double tOpacity, tBase, tWave;
            if (steady)
            {
                tOpacity = b;
                tBase = 0.12;
                tWave = 0.3;
                _energy = 0.3;
            }
            else if (playing)
            {
                if (beat)
                {
                    _pulse = 1;
                    _phaseKick += 0.35;
                    _speedKick = 1;
                    Spawn(big: true);
                }
                double e = Math.Clamp(0.65 * s!.level + 0.5 * Math.Max(_pulse, s.pulse), 0, 1);
                _energy += (e - _energy) * (1 - Math.Exp(-8 * dt));
                tOpacity = b;
                tBase = 0;                      // between streams the edge is dark
                tWave = 0.3 + 0.25 * _energy;
            }
            else
            {
                tOpacity = _settings.IdleGlow ? b * 0.6 : 0;
                tBase = 0.08;
                tWave = 0.25;
                _energy = 0.2;
            }

            // ---- smoothing (frame-rate independent)
            static double Follow(double cur, double target, double up, double down, double dt)
            {
                double rate = target > cur ? up : down;
                return cur + (target - cur) * (1 - Math.Exp(-rate * dt));
            }
            _pulse *= Math.Exp(-dt / 0.15);
            _speedKick *= Math.Exp(-dt / 0.4);
            _opacity = Follow(_opacity, tOpacity, 8, 3, dt);
            _rim = Follow(_rim, _settings.Thickness * (1 + 0.25 * _pulse), 30, 8, dt);
            _halo = Follow(_halo, _settings.Glow * (1 + 0.15 * _pulse), 30, 8, dt);
            _base = Follow(_base, tBase, 4, 4, dt);
            _wave = Follow(_wave, tWave * _settings.Waviness, 10, 3, dt);
            double colorRate = s?.source == "screen" ? 4 : 2.5;   // follow videos a bit faster than album art
            _primary = Lerp(_primary, tp, 1 - Math.Exp(-colorRate * dt));
            _secondary = Lerp(_secondary, ts, 1 - Math.Exp(-colorRate * dt));

            // ---- streams: new ones keep appearing (more often when it's loud), each grows, travels, fades
            _nextSpawn -= dt;
            if (_nextSpawn <= 0 && (playing || calm))
            {
                Spawn(big: false);
                _nextSpawn = calm ? 2.5 + 2 * _rng.NextDouble()
                                  : (1.1 - 0.7 * _energy) * (0.6 + 0.8 * _rng.NextDouble());
            }
            double speedUp = calm ? 0.5 : 1 + 1.5 * _energy + 2.5 * _speedKick;
            for (int i = 0; i < MaxStreams; i++)
            {
                ref var st = ref _streams[i];
                if (!st.Alive) { st.Brightness = 0; continue; }
                st.Age += dt;
                st.Pos = Wrap(st.Pos + st.Speed * speedUp * dt);
                st.HalfLength += (st.TargetLength - st.HalfLength) * (1 - Math.Exp(-2.5 * dt));   // grows out from a point
                double fadeIn = Math.Clamp(st.Age / 0.3, 0, 1);
                double fadeOut = Math.Clamp((st.Life - st.Age) / 0.8, 0, 1);
                double liveliness = calm ? 0.8 : 0.55 + 0.45 * _energy + 0.35 * _pulse;
                st.Brightness = st.Strength * fadeIn * fadeOut * liveliness;
                if (st.Age >= st.Life) st.Alive = false;
            }
            _waveT += dt * (1.0 + 1.5 * _energy);

            // colors slowly turn around the screen; beats give them a nudge
            double kick = _phaseKick * (1 - Math.Exp(-6 * dt));
            _phaseKick -= kick;
            _phase = Wrap(_phase + dt * (steady ? 0.02 : 0.035) + kick * 0.15);

            var f = _frame;
            f.Opacity = _opacity;
            f.Rim = Math.Max(0.5, _rim);
            f.Halo = Math.Max(1, _halo);
            f.Radius = _settings.CornerRadius;
            f.Time = _waveT;
            f.Phase = _phase;
            f.Base = _base;
            f.Wave = _wave;
            f.Pulse = _pulse;
            f.White = 0.15 + 0.3 * _pulse;
            f.Primary = _primary;
            f.Secondary = _secondary;
            Array.Copy(_streams, f.Streams, MaxStreams);
            return f;
        }

        /// <summary>Start a new stream somewhere along the edge (replacing the faintest one if all are busy).</summary>
        void Spawn(bool big)
        {
            int slot = -1;
            double faintest = double.MaxValue;
            for (int i = 0; i < MaxStreams; i++)
            {
                if (!_streams[i].Alive) { slot = i; break; }
                if (_streams[i].Brightness < faintest) { faintest = _streams[i].Brightness; slot = i; }
            }
            bool calm = _settings.Mode == GlowMode.Steady;
            double dir = _rng.Next(2) == 0 ? -1 : 1;
            _streams[slot] = new Stream
            {
                Alive = true,
                Pos = _rng.NextDouble(),
                Speed = dir * (calm ? 0.015 : 0.03 + 0.05 * _rng.NextDouble()),
                HalfLength = 0.005,
                TargetLength = (calm ? 0.10 : 0.05) + (big ? 0.09 : 0.06) * _rng.NextDouble(),
                Strength = big ? 1.0 : 0.75 + 0.25 * _rng.NextDouble(),
                Life = calm ? 6 + 4 * _rng.NextDouble() : 1.4 + 1.8 * _rng.NextDouble(),
            };
        }

        void UpdateLyrics()
        {
            var s = Last;
            bool show = _settings.Enabled && _settings.ShowLyrics && s != null && s.sound
                        && !string.IsNullOrEmpty(s.lyric + s.next);
            if (show && !_lyrics.IsVisible)
            {
                _lyrics.Show();
                Native.ExcludeFromCapture(_lyrics.Handle, !_settings.ShowInCapture);
            }
            if (!show && _lyrics.IsVisible) _lyrics.Hide();
            if (show) _lyrics.Render(s!.lyric, s.next, _primary, _pulse);
        }

        static double Wrap(double x) => x - Math.Floor(x);

        static Color Lerp(Color a, Color b, double t)
        {
            static byte Mix(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));
        }

        public static Color Parse(string? hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }
    }
}
