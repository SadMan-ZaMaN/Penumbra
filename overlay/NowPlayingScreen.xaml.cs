using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice;

namespace Penumbra
{
    /// <summary>The Now playing screen: a full-screen player with the song's cover on a blurred copy of
    /// it, and the lyrics beside it (drawn by the glow window, which stays on top). A normal window, not
    /// always-on-top, so Alt+Tab still works; Esc closes it.</summary>
    public partial class NowPlayingScreen : Window
    {
        /// <summary>Open right now (the glow window puts the lyrics in the right-hand area and keeps
        /// Night sky's black off while it is).</summary>
        public static volatile bool IsOpen;
        static NowPlayingScreen? _open;

        /// <summary>Night sky, 0..1 (set by the glow window): how far the blurred background has faded
        /// to black. The cover card and the lyrics stay.</summary>
        public static volatile float Night;
        double _backdropTo = 1;

        // where the lyrics go, on the 1440 x 900 design canvas (see the XAML)
        const float CanvasW = 1440, CanvasH = 900;
        static readonly RawRectF LyricsBox = new(740, 100, 1240, 665);

        readonly GlowController _glow;
        readonly EngineLink _link;
        readonly DispatcherTimer _tick;
        string _key = "\0";              // artist + title on screen
        int _artShown = int.MinValue;    // the engine's cover that's showing
        bool _sharp;                     // the sharp cover from the shop is showing
        CancellationTokenSource? _lookup;
        static (string key, byte[] jpeg)? _sharpCache;   // the last sharp cover found (one song's worth)

        /// <summary>Opens the settings panel (set by the tray, which owns it): the screen covers the tray icon.</summary>
        public static Action? ShowPanel;

        public static void Open(GlowController glow, EngineLink link)
        {
            _open ??= new NowPlayingScreen(glow, link);
            _open.Show();
            _open.Activate();
        }

        /// <summary>Close it (with its fade), if it's open.</summary>
        public static void CloseIfOpen() => _open?.FadeAndClose();

        /// <summary>Open and not on its way out (for the panel's open / close button).</summary>
        public static bool Showing => _open != null && !_open._closing;

        /// <summary>The lyrics area in screen pixels, for a window <paramref name="w"/> x <paramref name="h"/>
        /// (the canvas is scaled to fit and centred, like the Viewbox does).</summary>
        public static RawRectF LyricsArea(int w, int h)
        {
            float s = Math.Min(w / CanvasW, h / CanvasH);
            float ox = (w - CanvasW * s) / 2, oy = (h - CanvasH * s) / 2;
            return new RawRectF(ox + LyricsBox.Left * s, oy + LyricsBox.Top * s, ox + LyricsBox.Right * s, oy + LyricsBox.Bottom * s);
        }

        NowPlayingScreen(GlowController glow, EngineLink link)
        {
            InitializeComponent();
            _glow = glow;
            _link = link;
            Left = 0; Top = 0;                                   // the main screen, all of it
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Icon = Imaging.CreateBitmapSourceFromHIcon(Tray.LoadIcon().Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) FadeAndClose();
                else if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
            };
            SettingsButton.Click += (_, _) => ShowPanel?.Invoke();
            CloseButton.Click += (_, _) => FadeAndClose();
            // Opening: hidden ("cloaked") while it draws its first frame, with everything already in place; then
            // Windows' compositor fades the finished picture in (smooth, and nothing is drawn again). WPF
            // clears the "layered" style a fade needs when it shows a window, so that's set only after.
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.Cloak(_hwnd, true);
                HwndSource.FromHwnd(_hwnd)?.AddHook(KeepFadeable);
            };
            ContentRendered += (_, _) =>
            {
                _fadeable = true;
                Native.MakeFadeable(_hwnd);
                Native.SetWindowAlpha(_hwnd, 0);
                var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };   // its repaint as a layered window
                later.Tick += (_, _) =>
                {
                    later.Stop();
                    Native.Cloak(_hwnd, false);
                    _revealed = true;
                    FadeWindow(true, 0.25);
                };
                later.Start();
            };
            // the cursor hides after a few seconds without moving, like a video player
            MouseMove += (_, e) =>
            {
                var p = PointToScreen(e.GetPosition(this));
                if (Math.Abs(p.X - _mouseAt.X) + Math.Abs(p.Y - _mouseAt.Y) < 3) return;   // not a real move
                _mouseAt = p;
                _lastMove = DateTime.UtcNow;
                if (ForceCursor) { ForceCursor = false; Cursor = null; }
            };
            Prev.Click += (_, _) => _link.Send("previous");
            Next.Click += (_, _) => _link.Send("next");
            PlayPause.Click += (_, _) => TogglePlay();

            // the progress bar: press, drag (the bar follows the mouse), let go to jump there
            BarHit.MouseLeftButtonDown += (_, e) => { _dragging = true; BarHit.CaptureMouse(); DragTo(e.GetPosition(Progress).X); e.Handled = true; };
            BarHit.MouseMove += (_, e) => { if (_dragging) DragTo(e.GetPosition(Progress).X); };
            BarHit.MouseLeftButtonUp += (_, e) =>
            {
                if (!_dragging) return;
                _dragging = false;
                BarHit.ReleaseMouseCapture();
                SeekToDragged();
            };
            BarHit.LostMouseCapture += (_, _) => _dragging = false;   // e.g. Alt+Tab mid-drag: just stop

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (_, _) => Refresh();
            IsOpen = true;
            Closed += (_, _) =>
            {
                IsOpen = false;
                _tick.Stop();
                _lookup?.Cancel();
                _open = null;
            };
            Refresh();
            _tick.Start();
        }

        /// <summary>Follow the song: its name, and its cover (the engine's small one at once, a sharp one
        /// when the lookup finds it).</summary>
        void Refresh()
        {
            var s = _glow.Last;
            string title = s?.title ?? "", artist = s?.artist ?? "";
            bool hidden = title.StartsWith("A site is playing", StringComparison.OrdinalIgnoreCase);   // Incognito
            string key = artist + "\n" + title;
            if (key != _key)
            {
                _key = key;
                _sharp = false;
                _artShown = int.MinValue;
                _coverFor = "";                       // the old song's cover stays until the new one arrives
                _songAt = DateTime.UtcNow;
                SongTitle.Text = title.Length == 0 ? "Nothing playing" : hidden ? "Playing in a private window" : title;
                SongArtist.Text = hidden ? "" : artist;
                if (title.Length == 0) ShowCover(null);
                _lookup?.Cancel();
                _lookup = null;
                if (title.Length > 0 && !hidden) FindSharpCover(artist, title, key);
            }

            var art = _link.Art;
            if (!_sharp && s != null && art != null && art.Id == s.art_id && art.Id != _artShown)
            {
                _artShown = art.Id;
                var bmp = Decode(Convert.FromBase64String(art.Jpeg), 0);
                if (bmp != null) ShowCover(bmp);
            }
            // no cover at all for this song after a moment: the placeholder
            if (_coverFor != _key && DateTime.UtcNow - _songAt > TimeSpan.FromSeconds(2.5)) ShowCover(null);

            if (!_dragging && !ForceCursor && IsActive && DateTime.UtcNow - _lastMove > TimeSpan.FromSeconds(3))
            {
                ForceCursor = true;                   // (overrides the buttons' hand cursor too)
                Cursor = Cursors.None;
            }

            // the progress bar and times (hidden when the length isn't known, e.g. a live stream)
            if (s != null && s.dur > 1)
            {
                double pos = s.pos;
                if (_dragging) pos = _dragFrac * s.dur;   // follows the mouse
                else if (DateTime.UtcNow < _seekHeld)     // just jumped: hold the new spot until the player confirms it
                {
                    pos = _seekTo + (s.playing ? (DateTime.UtcNow - _seekAt).TotalSeconds : 0);
                    if (Math.Abs(s.pos - pos) < 1.5) _seekHeld = DateTime.MinValue;
                }
                ShowBar(pos, s.dur);
                Progress.Visibility = Visibility.Visible;
            }
            else Progress.Visibility = Visibility.Hidden;

            // play or pause icon: what's happening, unless a click just changed it (the engine confirms within a second)
            bool playing = s?.playing == true;
            if (DateTime.UtcNow > _iconHeld && playing != _playingShown) ShowPlaying(playing);

            // Night sky: the blurred background fades to black (and back) over the window's black
            double want = 1 - Night;
            if (Math.Abs(want - _backdropTo) > 0.02 || ((want == 0 || want == 1) && want != _backdropTo))
            {
                _backdropTo = want;
                BackdropLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(want, TimeSpan.FromSeconds(0.6)));
            }
        }

        bool _playingShown = true;
        DateTime _iconHeld;
        string _coverFor = "\0";                 // the song whose cover is showing
        DateTime _songAt, _lastMove = DateTime.UtcNow;
        Point _mouseAt;
        bool _closing;

        IntPtr _hwnd;
        bool _revealed;      // the fade-in has started: from now on changes are animated

        /// <summary>WPF strips the "layered" style (which the compositor fade needs) from windows that aren't
        /// its own transparent kind, the moment it's set. This sees the style change first and keeps it.</summary>
        IntPtr KeepFadeable(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_STYLECHANGING = 0x007C, GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000;
            if (msg == WM_STYLECHANGING && wParam.ToInt64() == GWL_EXSTYLE && _fadeable)
            {
                int styleNew = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);   // STYLESTRUCT.styleNew
                System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 4, styleNew | WS_EX_LAYERED);
                handled = true;
            }
            return IntPtr.Zero;
        }
        bool _fadeable;
        int _fadeId;         // the latest fade (a newer one stops an older one)

        /// <summary>Fade out, then close.</summary>
        void FadeAndClose()
        {
            if (_closing) return;
            _closing = true;
            FadeWindow(false, 0.2, Close);
        }

        /// <summary>Fade the whole window in or out with the compositor: a background thread sets the window's
        /// transparency once per screen refresh. The picture itself isn't drawn again, so it costs almost
        /// nothing, and it can't stutter when the UI thread is busy.</summary>
        void FadeWindow(bool show, double seconds, Action? done = null)
        {
            var hwnd = _hwnd;
            int id = ++_fadeId;
            Native.MakeFadeable(hwnd);   // (in case WPF cleared it since)
            new Thread(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (id == _fadeId)
                {
                    double t = Math.Min(1, clock.Elapsed.TotalSeconds / seconds);
                    double eased = 1 - Math.Pow(1 - t, 3);   // quick start, gentle finish
                    Native.SetWindowAlpha(hwnd, (byte)Math.Round(255 * (show ? eased : 1 - eased)));
                    if (t >= 1) break;
                    Native.WaitForFrame();
                }
                if (id == _fadeId && done != null) Dispatcher.BeginInvoke(done);
            }) { IsBackground = true, Name = "NowPlayingFade" }.Start();
        }

        static void Fade(UIElement e, double from, double to, double seconds) =>
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)));

        void TogglePlay()
        {
            _link.Send("toggle");
            ShowPlaying(!_playingShown);                      // show it at once...
            _iconHeld = DateTime.UtcNow.AddSeconds(1.5);      // ...and give the player a moment to catch up
        }

        void ShowPlaying(bool playing)
        {
            _playingShown = playing;
            PlayPause.Content = playing ? "" : "";   // pause : play
        }

        bool _dragging;
        double _dragFrac, _seekTo;
        DateTime _seekAt, _seekHeld;

        void ShowBar(double pos, double dur)
        {
            double f = Math.Clamp(pos / dur, 0, 1);
            BarFill.Width = 360 * f;
            System.Windows.Controls.Canvas.SetLeft(BarKnob, 360 * f - 6.5);
            Elapsed.Text = Clock(pos);
            Remaining.Text = "-" + Clock(dur - pos);
        }

        void DragTo(double x)
        {
            _dragFrac = Math.Clamp(x / 360, 0, 1);
            if (_glow.Last is { dur: > 1 } s) ShowBar(_dragFrac * s.dur, s.dur);
        }

        void SeekToDragged()
        {
            if (_glow.Last is not { dur: > 1 } s) return;
            _seekTo = _dragFrac * s.dur;
            _link.Send("seek", _seekTo);
            _seekAt = DateTime.UtcNow;
            _seekHeld = _seekAt.AddSeconds(2);
            ShowBar(_seekTo, s.dur);
        }

        static string Clock(double seconds)
        {
            int s = (int)Math.Max(0, seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        async void FindSharpCover(string artist, string title, string key)
        {
            byte[]? jpeg = _sharpCache is { } c && c.key == key ? c.jpeg : null;
            if (jpeg == null)
            {
                var cts = _lookup = new CancellationTokenSource();
                jpeg = await CoverLookup.FindAsync(artist, title, cts.Token);
                if (cts.IsCancellationRequested || jpeg == null) return;
                _sharpCache = (key, jpeg);
            }
            if (key != _key) return;                 // the song changed meanwhile
            var bmp = Decode(jpeg, 720);             // the cover is about 680 px on a 200% screen
            if (bmp == null) return;
            _sharp = true;
            ShowCover(bmp, sharper: _coverFor == _key);   // this song's small cover is up: just sharpen it
        }

        static readonly Brush Placeholder = MakePlaceholder();

        static Brush MakePlaceholder()
        {
            var b = new LinearGradientBrush(Color.FromRgb(0x7c, 0x5c, 0xff), Color.FromRgb(0xff, 0x4f, 0xa3), 45);
            b.Freeze();
            return b;
        }

        /// <summary>Show a cover (null: the placeholder) and the blurred background made from it, each
        /// fading in over the last one. Before the screen appears, just put them in place (no animation).
        /// <paramref name="sharper"/>: the same song's cover, only sharper: the blurred background would look
        /// the same, so it's left alone (fading the whole screen is the expensive part).</summary>
        void ShowCover(BitmapSource? bmp, bool sharper = false)
        {
            _coverFor = _key;
            var brush = bmp != null ? new ImageBrush(bmp) { Stretch = Stretch.UniformToFill } : Placeholder;
            if (ReferenceEquals(brush, CoverBox.Background)) return;   // the placeholder again: nothing changes
            CoverOld.Background = _revealed ? CoverBox.Background : null;
            CoverBox.Background = brush;
            if (_revealed)
            {
                Fade(CoverBox, 0, 1, 0.4);
                CoverNote.BeginAnimation(OpacityProperty, new DoubleAnimation(bmp != null ? 0 : 0.55, TimeSpan.FromSeconds(0.4)));
            }
            else CoverNote.Opacity = bmp != null ? 0 : 0.55;
            if (sharper && Backdrop.Source != null) return;

            BackdropOld.BeginAnimation(OpacityProperty, null);
            BackdropOld.Opacity = 1;
            BackdropOld.Source = _revealed ? Backdrop.Source : null;
            Backdrop.Source = bmp != null ? MakeBackdrop(bmp) : null;
            if (!_revealed) return;
            if (Backdrop.Source != null) Fade(Backdrop, 0, 1, 0.6);   // the new one over the old
            else Fade(BackdropOld, 1, 0, 0.6);                        // none: the old one fades to black
        }

        static BitmapSource? Decode(byte[] bytes, int width)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.StreamSource = new MemoryStream(bytes);
                if (width > 0) b.DecodePixelWidth = width;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        /// <summary>The background: the cover, cropped to the screen's shape, shrunk to 144 x 90, blurred,
        /// a bit more colorful and darker (so white text reads on it), then stretched over the screen.
        /// Made once per cover, so it costs nothing while it's showing.</summary>
        static BitmapSource MakeBackdrop(BitmapSource src)
        {
            const int W = 144, H = 90;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double k = Math.Max(W / (double)src.PixelWidth, H / (double)src.PixelHeight);
                double w = src.PixelWidth * k, h = src.PixelHeight * k;
                dc.DrawImage(src, new Rect((W - w) / 2, (H - h) / 2, w, h));
            }
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[W * H * 4];
            rtb.CopyPixels(px, W * 4, 0);
            for (int i = 0; i < 3; i++) { BoxBlur(px, W, H, 6, true); BoxBlur(px, W, H, 6, false); }   // ~ a Gaussian blur
            for (int i = 0; i < px.Length; i += 4)
            {
                double grey = (px[i] + px[i + 1] + px[i + 2]) / 3.0;
                for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Clamp((grey + (px[i + c] - grey) * 1.35) * 0.55, 0, 255);
                px[i + 3] = 255;
            }
            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>One pass of a box blur along rows (or columns), radius r, edges repeated.</summary>
        static void BoxBlur(byte[] px, int w, int h, int r, bool rows)
        {
            int lines = rows ? h : w, len = rows ? w : h;
            var line = new int[len * 3];
            for (int a = 0; a < lines; a++)
            {
                int Index(int i) => rows ? (a * w + i) * 4 : (i * w + a) * 4;
                for (int i = 0; i < len; i++) { int p = Index(i); line[i * 3] = px[p]; line[i * 3 + 1] = px[p + 1]; line[i * 3 + 2] = px[p + 2]; }
                for (int i = 0; i < len; i++)
                {
                    int s0 = 0, s1 = 0, s2 = 0;
                    for (int j = -r; j <= r; j++)
                    {
                        int q = Math.Clamp(i + j, 0, len - 1) * 3;
                        s0 += line[q]; s1 += line[q + 1]; s2 += line[q + 2];
                    }
                    int p = Index(i), n = 2 * r + 1;
                    px[p] = (byte)(s0 / n); px[p + 1] = (byte)(s1 / n); px[p + 2] = (byte)(s2 / n);
                }
            }
        }
    }
}
