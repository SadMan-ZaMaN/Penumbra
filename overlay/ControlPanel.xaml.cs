using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LumnWin
{
    /// <summary>The settings pop-up (left-click the tray icon). It sits above the tray and hides as soon
    /// as you click anywhere else. Changes show on screen immediately; saving to settings.json is
    /// batched so dragging a slider doesn't write the file 60 times.</summary>
    public partial class ControlPanel : Window
    {
        /// <summary>Set before quitting so closing the window really closes it (normally it just hides).</summary>
        public static bool Quitting;

        static readonly (string name, string primary, string secondary)[] PresetColors =
        {
            ("Neon", "#7c5cff", "#ff4fa3"), ("Ocean", "#00c6ff", "#3a5bff"), ("Sunset", "#ff5e3a", "#ffb347"),
            ("Mint", "#00f5a0", "#00d9f5"), ("Fire", "#ff2d2d", "#ffb300"), ("Ice", "#dff6ff", "#6fd3ff"),
            ("Lime", "#b6ff00", "#00ff9d"), ("Rose", "#ff3d7f", "#ffb3d1"), ("Gold", "#ffd34d", "#ff8a00"),
            ("Violet", "#b14dff", "#5b6cff"),
        };

        readonly Settings _s;
        readonly GlowController _glow;
        readonly EngineLink _link;
        readonly DispatcherTimer _save, _live;
        bool _loading, _picking;
        long _hiddenAt;

        public ControlPanel(Settings s, GlowController glow, EngineLink link, Action changed, Action quit)
        {
            InitializeComponent();
            _s = s;
            _glow = glow;
            _link = link;
            _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _save.Tick += (_, _) => { _save.Stop(); changed(); };
            _live = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _live.Tick += (_, _) => UpdateLive();
            MaxHeight = SystemParameters.WorkArea.Height - 24;

            Enabled.Click += (_, _) => Set(() => _s.Enabled = Enabled.IsChecked == true);
            foreach (RadioButton r in SourceChoices.Children)
                r.Checked += (_, _) => Set(() => _s.ColorSource = Enum.Parse<ColorSource>((string)r.Tag));
            foreach (RadioButton r in CornerChoices.Children)
                r.Checked += (_, _) => Set(() => _s.CornerRadius = double.Parse((string)r.Tag, CultureInfo.InvariantCulture));
            foreach (RadioButton r in ModeChoices.Children)
                r.Checked += (_, _) => Set(() => _s.Mode = Enum.Parse<GlowMode>((string)r.Tag));
            Thickness.ValueChanged += (_, e) => Set(() => _s.Thickness = e.NewValue);
            Glow.ValueChanged += (_, e) => Set(() => _s.Glow = e.NewValue);
            Brightness.ValueChanged += (_, e) => Set(() => _s.Brightness = e.NewValue);
            Waviness.ValueChanged += (_, e) => Set(() => _s.Waviness = e.NewValue);
            IdleGlow.Click += (_, _) => Set(() => _s.IdleGlow = IdleGlow.IsChecked == true);
            ShowLyrics.Click += (_, _) => Set(() => _s.ShowLyrics = ShowLyrics.IsChecked == true);
            ShowInCapture.Click += (_, _) => Set(() => _s.ShowInCapture = ShowInCapture.IsChecked == true);
            AutoStart.Click += (_, _) => Tray.SetAutoStart(AutoStart.IsChecked == true);
            PrimarySwatch.Click += (_, _) => Pick(true);
            SecondarySwatch.Click += (_, _) => Pick(false);
            Quit.Click += (_, _) => quit();

            foreach (var (name, a, b) in PresetColors)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("Swatch"), Width = 24, Height = 18, Margin = new Thickness(0, 0, 4, 4),
                    Background = Gradient(a, b), ToolTip = name,
                };
                btn.Click += (_, _) => UseColors(a, b);
                Presets.Children.Add(btn);
            }

            Closing += (_, e) => { if (!Quitting) { e.Cancel = true; Hide(); } };
            Deactivated += (_, _) =>          // clicked somewhere else: tuck away
            {
                if (_picking || !IsVisible) return;
                Hide();
                _hiddenAt = Environment.TickCount64;
            };
            KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
            IsVisibleChanged += (_, _) => { if (IsVisible) { Load(); UpdateLive(); _live.Start(); } else _live.Stop(); };
            SourceInitialized += (_, _) => Native.UseRoundedCorners(new WindowInteropHelper(this).Handle);
            Icon = Imaging.CreateBitmapSourceFromHIcon(Tray.MakeIcon().Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        /// <summary>Pop up above the tray, or tuck away if it's already open.</summary>
        public void Open()
        {
            // clicking the tray icon while open first hides the pop-up (it loses focus): don't reopen it
            if (Environment.TickCount64 - _hiddenAt < 300) return;
            if (IsVisible) { Hide(); return; }
            Show();
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 12;
            Top = Math.Max(wa.Top + 12, wa.Bottom - ActualHeight - 12);
            Activate();
        }

        /// <summary>Copy the current settings into the controls.</summary>
        public void Load()
        {
            _loading = true;
            Enabled.IsChecked = _s.Enabled;
            foreach (RadioButton r in SourceChoices.Children) r.IsChecked = (string)r.Tag == _s.ColorSource.ToString();
            foreach (RadioButton r in CornerChoices.Children)
                r.IsChecked = Math.Abs(double.Parse((string)r.Tag, CultureInfo.InvariantCulture) - _s.CornerRadius) < 0.5;
            foreach (RadioButton r in ModeChoices.Children) r.IsChecked = (string)r.Tag == _s.Mode.ToString();
            Thickness.Value = _s.Thickness;
            Glow.Value = _s.Glow;
            Brightness.Value = _s.Brightness;
            Waviness.Value = _s.Waviness;
            PrimarySwatch.Background = new SolidColorBrush(GlowController.Parse(_s.Primary, Colors.MediumPurple));
            SecondarySwatch.Background = new SolidColorBrush(GlowController.Parse(_s.Secondary, Colors.HotPink));
            IdleGlow.IsChecked = _s.IdleGlow;
            ShowLyrics.IsChecked = _s.ShowLyrics;
            ShowInCapture.IsChecked = _s.ShowInCapture;
            AutoStart.IsChecked = Tray.IsAutoStart();
            _loading = false;
        }

        void Set(Action apply)
        {
            if (_loading) return;
            apply();
            _save.Stop();
            _save.Start();
        }

        void UseColors(string primary, string secondary)
        {
            _s.Primary = primary;
            _s.Secondary = secondary;
            _s.ColorSource = ColorSource.Custom;   // picking colors means "use mine"
            Load();
            Set(() => { });
        }

        void Pick(bool primary)
        {
            var cur = GlowController.Parse(primary ? _s.Primary : _s.Secondary, Colors.White);
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B),
            };
            _picking = true;                  // the dialog takes focus; don't hide the pop-up for that
            var result = dlg.ShowDialog();
            _picking = false;
            Activate();
            if (result != System.Windows.Forms.DialogResult.OK) return;
            string hex = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
            UseColors(primary ? hex : _s.Primary, primary ? _s.Secondary : hex);
        }

        /// <summary>What's playing, and the colors the glow is showing right now.</summary>
        void UpdateLive()
        {
            var st = _glow.Last;
            bool sound = st != null && st.sound;
            NowPlaying.Text =
                !_link.Connected ? "Engine not running"
                : st == null ? "Listening…"
                : st.title.StartsWith("A site is playing", StringComparison.OrdinalIgnoreCase) ? "♪ Something in a private window (title hidden)"
                : !string.IsNullOrEmpty(st.title) ? (st.artist.Length > 0 ? $"♪ {st.title} — {st.artist}" : $"♪ {st.title}")
                : sound ? "♪ Sound playing"
                : "Nothing playing";

            string from = _s.ColorSource == ColorSource.Custom ? "Your colors"
                : st?.source == "art" ? "From the album art"
                : st?.source == "screen" ? "From the screen"
                : "Default colors";
            if (!_s.Enabled) from += " · glow is switched off";
            else if (!sound && !_s.IdleGlow && _s.Mode == GlowMode.MusicSync) from += " · glow waits for sound";
            ColorInfo.Text = from;
            LiveColors.Background = new LinearGradientBrush(_glow.Primary, _glow.Secondary, 0);
        }

        static Brush Gradient(string a, string b) =>
            new LinearGradientBrush(GlowController.Parse(a, Colors.White), GlowController.Parse(b, Colors.White), 0);
    }
}
