using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace LumnWin
{
    /// <summary>Click-through karaoke-style line above the taskbar: current line glowing, next line dim.</summary>
    public class LyricsWindow : Window
    {
        public IntPtr Handle { get; private set; }

        readonly TextBlock _current, _next;
        readonly DropShadowEffect _glow;
        string _shown = "";

        public LyricsWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Title = "LumnWin Lyrics";

            var font = new FontFamily("Segoe UI Variable Display, Segoe UI");
            _glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 22, Opacity = 0.9, Color = Colors.MediumPurple };
            _current = new TextBlock
            {
                FontFamily = font, FontSize = 30, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Effect = _glow,
            };
            _next = new TextBlock
            {
                FontFamily = font, FontSize = 18, Foreground = Brushes.White, Opacity = 0.5,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            panel.Children.Add(_current);
            panel.Children.Add(_next);
            Content = panel;

            SourceInitialized += (_, _) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                Native.MakeOverlay(Handle);
            };
        }

        public void Place(Rect workArea)
        {
            Width = workArea.Width * 0.7;
            Height = 170;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Bottom - Height - 24;
        }

        public void Render(string line, string next, Color glow, double pulse)
        {
            if (line != _shown)
            {
                _shown = line;
                _current.Text = line;
                _next.Text = next;
                _current.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() });
            }
            _glow.Color = glow;
            _glow.BlurRadius = 18 + 14 * pulse;
        }
    }
}
