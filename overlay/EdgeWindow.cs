using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LumnWin
{
    public enum Edge { Top, Right, Bottom, Left }

    /// <summary>
    /// A thin transparent, click-through window glued to one screen edge.
    /// Four of these (instead of one fullscreen window) keep the per-frame work small.
    ///
    /// The light = a color gradient running ALONG the edge (corner color -> corner color),
    /// masked by an alpha gradient running ACROSS the edge (bright rim -> soft spill -> nothing).
    /// </summary>
    public class EdgeWindow : Window
    {
        public Edge Edge { get; }
        public IntPtr Handle { get; private set; }

        readonly Rectangle _rect = new();
        readonly GradientStop _c0 = new(Colors.White, 0), _c1 = new(Colors.White, 1);
        // alpha mask stops: rim, rim end, soft spill, fade end
        readonly GradientStop _m0 = new(Color.FromArgb(255, 0, 0, 0), 0);
        readonly GradientStop _m1 = new(Color.FromArgb(235, 0, 0, 0), 0.05);
        readonly GradientStop _m2 = new(Color.FromArgb(90, 0, 0, 0), 0.3);
        readonly GradientStop _m3 = new(Color.FromArgb(0, 0, 0, 0), 0.8);
        readonly GradientStop _m4 = new(Color.FromArgb(0, 0, 0, 0), 1);

        public EdgeWindow(Edge edge)
        {
            Edge = edge;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Title = "LumnWin " + edge;

            bool horizontal = edge is Edge.Top or Edge.Bottom;
            // along-edge colors: chosen so corners line up (see GlowController.CornerColors)
            var along = new LinearGradientBrush
            {
                StartPoint = horizontal ? new Point(0, 0.5) : new Point(0.5, 0),
                EndPoint = horizontal ? new Point(1, 0.5) : new Point(0.5, 1),
            };
            along.GradientStops.Add(_c0);
            along.GradientStops.Add(_c1);

            var (from, to) = edge switch
            {
                Edge.Top => (new Point(0.5, 0), new Point(0.5, 1)),
                Edge.Bottom => (new Point(0.5, 1), new Point(0.5, 0)),
                Edge.Left => (new Point(0, 0.5), new Point(1, 0.5)),
                _ => (new Point(1, 0.5), new Point(0, 0.5)),
            };
            var mask = new LinearGradientBrush { StartPoint = from, EndPoint = to };
            foreach (var s in new[] { _m0, _m1, _m2, _m3, _m4 }) mask.GradientStops.Add(s);

            _rect.Fill = along;
            _rect.OpacityMask = mask;
            Content = _rect;

            SourceInitialized += (_, _) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                Native.MakeOverlay(Handle);
            };
        }

        /// <summary>Place this window on its edge of the screen area (in WPF device-independent pixels).</summary>
        public void Place(Rect screen, double band)
        {
            switch (Edge)
            {
                case Edge.Top: Left = screen.Left; Top = screen.Top; Width = screen.Width; Height = band; break;
                case Edge.Bottom: Left = screen.Left; Top = screen.Bottom - band; Width = screen.Width; Height = band; break;
                case Edge.Left: Left = screen.Left; Top = screen.Top; Width = band; Height = screen.Height; break;
                case Edge.Right: Left = screen.Right - band; Top = screen.Top; Width = band; Height = screen.Height; break;
            }
        }

        /// <summary>Per-frame update.</summary>
        /// <param name="start">color at the start of the edge (left / top end)</param>
        /// <param name="end">color at the end of the edge (right / bottom end)</param>
        /// <param name="rim">bright rim thickness as a fraction of the band (0..1)</param>
        /// <param name="spill">how far the glow reaches as a fraction of the band (0..1)</param>
        /// <param name="opacity">overall intensity 0..1</param>
        public void Render(Color start, Color end, double rim, double spill, double opacity)
        {
            _c0.Color = start;
            _c1.Color = end;
            spill = Math.Clamp(spill, 0.05, 1.0);
            rim = Math.Clamp(rim, 0.005, spill * 0.6);
            _m1.Offset = rim;
            _m2.Offset = rim + (spill - rim) * 0.3;
            _m3.Offset = spill;
            _rect.Opacity = Math.Clamp(opacity, 0, 1);
        }
    }
}
