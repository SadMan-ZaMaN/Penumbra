using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LumnWin
{
    /// <summary>Turns engine packets + settings into smooth per-frame light on the four edge windows.</summary>
    public class GlowController
    {
        readonly Settings _settings;
        readonly EngineLink _link;
        readonly EdgeWindow[] _edges;
        readonly LyricsWindow _lyrics = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _lastT;

        // smoothed values
        Color _primary, _secondary;
        double _opacity, _spill, _rim, _phase, _phaseKick, _pulse;
        bool _visible;

        public LightState? Last { get; private set; }

        public GlowController(Settings settings, EngineLink link)
        {
            _settings = settings;
            _link = link;
            _edges = new[] { new EdgeWindow(Edge.Top), new EdgeWindow(Edge.Right),
                             new EdgeWindow(Edge.Bottom), new EdgeWindow(Edge.Left) };
            _primary = Parse(settings.Primary, Colors.MediumPurple);
            _secondary = Parse(settings.Secondary, Colors.HotPink);

            ApplyLayout();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
                Application.Current.Dispatcher.BeginInvoke(new Action(ApplyLayout));

            CompositionTarget.Rendering += OnFrame;

            // other "always on top" windows (taskbar, popups) can climb above us; push back up
            var top = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            top.Tick += (_, _) =>
            {
                if (!_visible) return;
                foreach (var e in _edges) Native.BringToTop(e.Handle);
                if (_lyrics.IsVisible) Native.BringToTop(_lyrics.Handle);
            };
            top.Start();
        }

        /// <summary>Call after changing size-related settings or when displays change.</summary>
        public void ApplyLayout()
        {
            var screen = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            foreach (var e in _edges) e.Place(screen, Band);
            _lyrics.Place(SystemParameters.WorkArea);
            SetVisible(_settings.Enabled);
        }

        double Band => _settings.Glow * 1.5 + _settings.Thickness * 2;

        void SetVisible(bool v)
        {
            if (v == _visible) return;
            _visible = v;
            foreach (var e in _edges) { if (v) e.Show(); else e.Hide(); }
            if (!v) _lyrics.Hide();
        }

        void OnFrame(object? sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(t - _lastT, 0.001, 0.1);
            _lastT = t;

            SetVisible(_settings.Enabled);
            if (!_visible) return;

            var (s, beat) = _link.Take();
            Last = s;
            bool playing = s != null && s.playing && (s.level > 0.01 || s.pulse > 0.01);

            // ---- targets
            Color tp = Parse(_settings.Primary, Colors.MediumPurple);
            Color ts = Parse(_settings.Secondary, Colors.HotPink);
            if (_settings.FollowAlbumColors && s != null && s.colors.Count >= 2)
            {
                tp = Parse(s.colors[0], tp);
                ts = Parse(s.colors[1], ts);
            }

            double b = _settings.Brightness, glow = _settings.Glow, band = Band;
            double targetOpacity, targetSpill, targetRim = _settings.Thickness;
            double breathe = 0.5 + 0.5 * Math.Sin(t * 1.1);

            if (_settings.Mode == GlowMode.Steady)
            {
                targetOpacity = b * (0.75 + 0.08 * breathe);
                targetSpill = glow * 0.9;
            }
            else if (playing)
            {
                if (beat) { _pulse = 1; _phaseKick += 0.35; }
                double energy = Math.Clamp(0.65 * s!.level + 0.5 * Math.Max(_pulse, s.pulse), 0, 1);
                targetOpacity = b * (0.3 + 0.7 * energy);
                targetSpill = glow * (0.45 + 0.4 * s.level + 0.35 * _pulse);
                targetRim = _settings.Thickness * (1 + 0.8 * _pulse);
            }
            else
            {
                targetOpacity = _settings.IdleGlow ? b * (0.28 + 0.07 * breathe) : 0;
                targetSpill = glow * 0.6;
            }

            // ---- smoothing (frame-rate independent)
            static double Follow(double cur, double target, double up, double down, double dt)
            {
                double rate = target > cur ? up : down;
                return cur + (target - cur) * (1 - Math.Exp(-rate * dt));
            }
            _pulse *= Math.Exp(-dt / 0.15);
            _opacity = Follow(_opacity, targetOpacity, 30, 6, dt);
            _spill = Follow(_spill, targetSpill, 25, 5, dt);
            _rim = Follow(_rim, targetRim, 30, 8, dt);
            _primary = Lerp(_primary, tp, 1 - Math.Exp(-3 * dt));
            _secondary = Lerp(_secondary, ts, 1 - Math.Exp(-3 * dt));

            // colors slowly orbit around the screen; beats give the orbit a nudge
            double kick = _phaseKick * (1 - Math.Exp(-6 * dt));
            _phaseKick -= kick;
            _phase += dt * (_settings.Mode == GlowMode.Steady ? 0.08 : 0.15) + kick;

            // ---- corner colors so edges meet seamlessly: TL, TR, BR, BL
            var c = new Color[4];
            for (int i = 0; i < 4; i++)
                c[i] = Lerp(_primary, _secondary, 0.5 + 0.5 * Math.Cos(_phase + i * Math.PI / 2));

            double rimF = _rim / band, spillF = _spill / band;
            _edges[0].Render(c[0], c[1], rimF, spillF, _opacity);   // top:    TL -> TR
            _edges[1].Render(c[1], c[2], rimF, spillF, _opacity);   // right:  TR -> BR
            _edges[2].Render(c[3], c[2], rimF, spillF, _opacity);   // bottom: BL -> BR
            _edges[3].Render(c[0], c[3], rimF, spillF, _opacity);   // left:   TL -> BL

            // ---- lyrics
            bool showLyrics = _settings.ShowLyrics && s != null && s.playing && !string.IsNullOrEmpty(s.lyric + s.next);
            if (showLyrics && !_lyrics.IsVisible) _lyrics.Show();
            if (!showLyrics && _lyrics.IsVisible) _lyrics.Hide();
            if (showLyrics) _lyrics.Render(s!.lyric, s.next, _primary, _pulse);
        }

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
