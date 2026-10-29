using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LumnWin
{
    /// <summary>Everything the shader needs for one frame. Sizes are in DIPs; GlowWindow converts to pixels.</summary>
    public class GlowFrame
    {
        public double Opacity, Rim, Spread, Radius, Time, Phase, Base, Wave, Pulse, White;
        public Color Primary, Secondary;
        public GlowController.Light[] Lights = Array.Empty<GlowController.Light>();
    }

    /// <summary>Turns engine packets + settings into smooth per-frame light.
    /// Update() runs on the glow's render thread; the lyrics line is driven from the UI thread.</summary>
    public class GlowController
    {
        /// <summary>A bright patch that travels along the edge. Pos/Width are fractions of the perimeter.</summary>
        public struct Light { public double Pos, Speed, Width, Strength; }

        readonly Settings _settings;
        readonly EngineLink _link;
        readonly LyricsWindow _lyrics = new();
        readonly Random _rng = new();
        double _lastT = -1;

        // smoothed values
        Color _primary, _secondary;
        double _opacity, _spread, _rim, _phase, _phaseKick, _pulse, _wave, _waveT, _base, _speedKick;
        // 0-3 drift around the screen all the time, 4-5 are sparks that split away from each other on a beat
        readonly Light[] _lights = new Light[6];
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

            double[] speeds = { 0.042, -0.032, 0.025, -0.054 };   // laps of the screen per second
            double[] widths = { 0.10, 0.08, 0.12, 0.07 };
            for (int i = 0; i < 4; i++)
                _lights[i] = new Light { Pos = i * 0.25 + 0.05, Speed = speeds[i], Width = widths[i] };
            _lights[4].Width = _lights[5].Width = 0.03;
            _frame.Lights = new Light[6];

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

            // ---- target colors
            Color tp = Parse(_settings.Primary, Colors.MediumPurple);
            Color ts = Parse(_settings.Secondary, Colors.HotPink);
            if (_settings.ColorSource != ColorSource.Custom && s != null && s.colors.Count >= 2)
            {
                tp = Parse(s.colors[0], tp);
                ts = Parse(s.colors[1], ts);
            }

            // ---- targets for this moment
            double b = _settings.Brightness, glow = _settings.Glow;
            double breathe = 0.5 + 0.5 * Math.Sin(t * 1.1);
            double level = playing ? s!.level : 0;
            double tOpacity, tSpread, tRim = _settings.Thickness, tBase, tWave, energy = 0;
            var strength = new double[4];

            if (steady)
            {
                tOpacity = b * (0.85 + 0.08 * breathe);
                tSpread = glow * 0.9;
                tBase = 0.4;
                tWave = 0.35;
                for (int i = 0; i < 4; i++) strength[i] = 0.5 + 0.15 * Math.Sin(t * 0.4 + i * 1.7);
            }
            else if (playing)
            {
                if (beat)
                {
                    _pulse = 1;
                    _phaseKick += 0.35;
                    _speedKick = 1;
                    SpawnSparks(s!.level);
                }
                energy = Math.Clamp(0.65 * s!.level + 0.5 * Math.Max(_pulse, s.pulse), 0, 1);
                tOpacity = b * (0.55 + 0.45 * energy);
                tSpread = glow * (0.55 + 0.35 * s.level + 0.3 * _pulse);
                tRim = _settings.Thickness * (1 + 0.6 * _pulse);
                tBase = 0.08 + 0.25 * s.level;
                tWave = 0.4 + 0.3 * s.level + 0.3 * _pulse;
                // each drifting light listens to a different part of the sound
                double[] bands = { s.bass, s.mid, s.high, s.level };
                for (int i = 0; i < 4; i++) strength[i] = 0.15 + 0.85 * Math.Clamp(0.5 * energy + 0.6 * bands[i], 0, 1);
            }
            else
            {
                tOpacity = _settings.IdleGlow ? b * (0.55 + 0.08 * breathe) : 0;
                tSpread = glow * 0.7;
                tBase = 0.22;
                tWave = 0.3;
                for (int i = 0; i < 4; i++) strength[i] = 0.3;
            }

            // ---- smoothing (frame-rate independent)
            static double Follow(double cur, double target, double up, double down, double dt)
            {
                double rate = target > cur ? up : down;
                return cur + (target - cur) * (1 - Math.Exp(-rate * dt));
            }
            _pulse *= Math.Exp(-dt / 0.15);
            _speedKick *= Math.Exp(-dt / 0.4);
            _opacity = Follow(_opacity, tOpacity, 30, 6, dt);
            _spread = Follow(_spread, tSpread, 25, 5, dt);
            _rim = Follow(_rim, tRim, 30, 8, dt);
            _base = Follow(_base, tBase, 20, 4, dt);
            _wave = Follow(_wave, tWave * _settings.Waviness, 10, 3, dt);
            double colorRate = s?.source == "screen" ? 4 : 2.5;   // follow videos a bit faster than album art
            _primary = Lerp(_primary, tp, 1 - Math.Exp(-colorRate * dt));
            _secondary = Lerp(_secondary, ts, 1 - Math.Exp(-colorRate * dt));

            // ---- motion: lights drift around the screen, faster with louder music and on beats
            double speed = steady ? 1 : 1 + 2.5 * level + 3 * _speedKick;
            for (int i = 0; i < 4; i++)
            {
                _lights[i].Pos = Wrap(_lights[i].Pos + _lights[i].Speed * speed * dt);
                _lights[i].Strength = Follow(_lights[i].Strength, strength[i], 25, 4, dt);
            }
            for (int i = 4; i < 6; i++)
            {
                _lights[i].Pos = Wrap(_lights[i].Pos + _lights[i].Speed * dt);
                _lights[i].Strength *= Math.Exp(-dt / 0.55);
                _lights[i].Width = Math.Min(0.09, _lights[i].Width + dt * 0.05);
            }
            _waveT += dt * (1.0 + 1.5 * level);

            // colors slowly turn around the screen; beats give them a nudge
            double kick = _phaseKick * (1 - Math.Exp(-6 * dt));
            _phaseKick -= kick;
            _phase = Wrap(_phase + dt * (steady ? 0.02 : 0.035) + kick * 0.15);

            var f = _frame;
            f.Opacity = _opacity;
            f.Rim = Math.Max(0.5, _rim * 0.6);
            f.Spread = Math.Max(4, _spread);
            f.Radius = _settings.CornerRadius;
            f.Time = _waveT;
            f.Phase = _phase;
            f.Base = _base;
            f.Wave = _wave;
            f.Pulse = _pulse;
            f.White = 0.2 + 0.3 * _pulse;
            f.Primary = _primary;
            f.Secondary = _secondary;
            Array.Copy(_lights, f.Lights, 6);
            return f;
        }

        /// <summary>A beat sends two sparks racing apart from somewhere along the edge.</summary>
        void SpawnSparks(double level)
        {
            double at = _rng.NextDouble();
            double v = 0.18 + 0.12 * level;
            _lights[4] = new Light { Pos = at, Speed = v, Width = 0.025, Strength = 0.9 + 0.4 * level };
            _lights[5] = new Light { Pos = at, Speed = -v, Width = 0.025, Strength = 0.9 + 0.4 * level };
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
