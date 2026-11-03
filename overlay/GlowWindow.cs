using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Penumbra
{
    /// <summary>
    /// One full-screen, click-through window drawn on the GPU by Glow.hlsl.
    /// Runs on its own thread: create window -> loop { ask GlowController for a frame, draw, present }.
    /// </summary>
    public sealed class GlowWindow : IDisposable
    {
        /// <summary>Must match the cbuffer in Glow.hlsl (16-byte rows).</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct Constants
        {
            public Vector2 Size; public float Radius, Rim;
            public float Halo, Opacity, Time, Phase;
            public Vector4 Primary, Secondary;
            public float Base, Wave, Pulse, White;
            public Vector4 S0, S1, S2, S3, S4, S5, S6, S7;
            public float Backdrop, Inset; Vector2 _pad;
            public Vector4 Dark;
            public Vector4 Extra;
        }

        readonly Settings _settings;
        readonly GlowController _ctl;
        readonly bool _black;
        readonly Thread _thread;
        readonly Native.WndProc _proc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);   // field keeps it alive
        volatile bool _stop;

        IntPtr _hwnd;
        Native.Rect _bounds;
        double _scale = 1;   // pixels per DIP on this monitor
        uint _sync = 1;      // present on every n-th screen refresh (Settings.Smoothness)
        int _band;           // px: width of the edge strips drawn (all the light is inside them)
        int _clearFrames;    // frames that must wipe the whole buffer first (one per swap chain buffer)
        bool _fullPresent;   // this frame changed more than the edge strips
        readonly RawRect[] _dirty = new RawRect[4], _dirtyWithLyrics = new RawRect[5];
        // Where the lyrics were drawn 1 and 2 frames ago. There are two buffers taking turns, so the one
        // about to be drawn still holds the lyrics from 2 frames ago; both get wiped before drawing.
        RawRect? _lyrics1, _lyrics2;
        RawRect? _lyricsDirty;  // what changed this frame because of the lyrics (for the compositor)
        Native.Rect _work;     // the main screen minus the taskbar, relative to the window
        double _backdrop;      // Night sky: how black the screen is right now, 0..1
        Native.Rect _darkRect; // which part goes black: the whole screen, or all but a slid-out taskbar
        string _song = "";     // the song playing, so Esc's lift of the black ends with it

        /// <summary>The screen is (going) black for Night sky (the safety net watches this).</summary>
        public bool DarkActive => _backdrop > 0.01;

        /// <summary>The glow window, for the Night sky safety net (Program.cs).</summary>
        public IntPtr Handle => _hwnd;

        long _heartbeat = Stopwatch.GetTimestamp();   // stamped every pass of the render loop
        readonly bool _testFreeze;                    // --test-freeze: hang once the screen is black (to test the safety net)

        /// <summary>How long since the render loop last came round. Seconds; huge = it's stuck.</summary>
        public double SinceLastFrame => Stopwatch.GetElapsedTime(Interlocked.Read(ref _heartbeat)).TotalSeconds;

        /// <summary>Esc during Night sky: lift the black (the lyrics stay, like Constellation) until Esc
        /// again, or the next song. The saved style stays Night sky.</summary>
        public volatile bool BlackLifted;
        ID2D1Factory1? _d2dFactory;
        ID2D1Device? _d2dDevice;
        ID2D1DeviceContext? _d2d;
        ID2D1Bitmap1? _d2dTarget;   // the back buffer, as something Direct2D can draw into
        LyricsRenderer? _lyrics;
        ID3D11Device? _device;
        ID3D11DeviceContext? _ctx;
        IDXGISwapChain1? _swap;
        IDCompositionDevice? _dcomp;
        IDCompositionTarget? _target;
        IDCompositionVisual? _visual;
        ID3D11RenderTargetView? _rtv;
        ID3D11VertexShader? _vs;
        ID3D11PixelShader? _ps;
        ID3D11Buffer? _cb;

        public string? Error { get; private set; }

        /// <param name="black">draw on black instead of see-through (for judging the look)</param>
        public GlowWindow(Settings settings, GlowController ctl, bool black = false, bool testFreeze = false)
        {
            _settings = settings;
            _ctl = ctl;
            _black = black;
            _testFreeze = testFreeze;
            _thread = new Thread(Run) { IsBackground = true, Name = "Glow" };
            _thread.Start();
        }

        void Run()
        {
            try
            {
                Native.UsePhysicalPixels();
                _bounds = Native.PrimaryMonitor();
                _hwnd = Native.CreateGlowWindow(_proc, _bounds);
                CreateDevice();
                Loop();
            }
            catch (Exception e)
            {
                Error = e.Message;
                Trace.WriteLine("Glow window failed: " + e);
            }
            finally
            {
                ReleaseDevice();
                _lyrics?.Dispose();
                _d2dFactory?.Dispose();
                if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
            }
        }

        void Loop()
        {
            var clock = Stopwatch.StartNew();
            bool shown = false, cleared = false;
            double lastCheck = -1, lastTop = 0, lastFrame = 0, idleRadius = -1, lastT = 0;
            bool? excluded = null;

            while (!_stop)
            {
                Interlocked.Exchange(ref _heartbeat, Stopwatch.GetTimestamp());
                if (_testFreeze && _backdrop >= 0.99) Thread.Sleep(Timeout.Infinite);
                Native.PumpMessages();
                double t = clock.Elapsed.TotalSeconds;

                if (t - lastCheck > 1)           // follow resolution / DPI / setting changes
                {
                    lastCheck = t;
                    var b = Native.PrimaryMonitor();
                    if (!b.Equals(_bounds)) { _bounds = b; Native.MoveTo(_hwnd, b); Resize(); cleared = false; }
                    _scale = Native.DpiScale(_hwnd);
                    int hz = Native.RefreshRate();
                    _sync = (uint)Math.Clamp(_settings.Smoothness switch
                    {
                        Smoothness.Smooth => 1,
                        Smoothness.Saver => (int)Math.Round(hz / 30.0),
                        _ => (int)Math.Round(hz / 60.0),
                    }, 1, 4);
                    bool ex = !_settings.ShowInCapture;
                    if (excluded != ex) { excluded = ex; Native.ExcludeFromCapture(_hwnd, ex); }
                    var wa = Native.PrimaryWorkArea();
                    _work = new Native.Rect { Left = wa.Left - b.Left, Top = wa.Top - b.Top, Right = wa.Right - b.Left, Bottom = wa.Bottom - b.Top };
                }

                var f = _ctl.Update(t);
                double dt = Math.Clamp(t - lastT, 0, 0.1);
                lastT = t;
                var style = _settings.LyricsStyle;
                _inset = OledInset(t);
                if (_lyrics != null) _lyrics.Shift = new Vector2((float)_inset, (float)_inset);   // the lyrics drift too
                _lyrics?.Update(_ctl.Last, _settings.ShowLyrics && _ctl.Last?.sound == true, style,
                                LyricsRenderer.RegionFor(style, _work, _scale), dt, _scale);
                if (_settings.Enabled != shown) { shown = _settings.Enabled; Native.Show(_hwnd, shown); }
                if (!shown) { Thread.Sleep(100); continue; }
                FollowNightSky(style, dt);
                if (t - lastTop > 2)            // stay above the taskbar & co. (but under the panel)
                {
                    lastTop = t;
                    Native.BringToTop(_hwnd);
                    var panel = ControlPanel.ShownHandle;
                    if (panel != IntPtr.Zero) Native.BringToTopAsync(panel);
                }

                // nothing lit (faded out, or a gap between streams): draw once (just the black corners,
                // if rounded), then idle cheaply until there's light again
                bool busy = _lyrics?.Active == true || _lyrics1 != null || _lyrics2 != null || _clearFrames > 0;
                bool lit = busy || f.Base >= 0.003;
                for (int i = 0; i < f.Streams.Length && !lit; i++) lit = f.Streams[i].Brightness >= 0.003;
                if ((f.Opacity < 0.002 && !busy) || !lit)
                {
                    if (!cleared || f.Radius != idleRadius)
                    {
                        Draw(f);
                        Present();
                        cleared = true;
                        idleRadius = f.Radius;
                    }
                    Thread.Sleep(30);
                    continue;
                }
                cleared = false;

                Draw(f);
                Present();
                // Present waits for the screen refresh(es); this only guards against it not waiting
                double spent = clock.Elapsed.TotalSeconds - lastFrame;
                if (spent < 0.005) Thread.Sleep(1);
                lastFrame = clock.Elapsed.TotalSeconds;
            }
        }

        /// <summary>Night sky: the screen fades to black while its lyrics show, and back when they're gone
        /// (or after Esc).</summary>
        void FollowNightSky(LyricsStyle style, double dt)
        {
            double lyrics = !_black && style == LyricsStyle.NightSky && _settings.ShowLyrics ? _lyrics?.SongVisibility ?? 0 : 0;
            var song = _ctl.Last?.title ?? "";
            if (lyrics < 0.003 || song != _song) BlackLifted = false;   // Night sky is over, or a new song: black again
            _song = song;
            double target = BlackLifted ? 0 : lyrics;
            double b = _backdrop + (target - _backdrop) * (1 - Math.Exp(-2.5 * dt));
            if (Math.Abs(b - target) < 0.003) b = target;
            var rect = target > 0 || _backdrop > 0 ? DarkArea() : new Native.Rect { Right = Width, Bottom = Height };
            if (b != _backdrop || !rect.Equals(_darkRect)) _clearFrames = 2;   // repaint all of both buffers
            _backdrop = b;
            _darkRect = rect;
        }

        /// <summary>Which part Night sky blacks out: the whole screen, minus an auto-hidden taskbar while it's
        /// slid out (pointing at the screen edge, or the Windows key). Read every frame, so the gap follows
        /// the taskbar as it slides in and out. A taskbar that's always shown stays under the black.</summary>
        Native.Rect DarkArea()
        {
            var all = new Native.Rect { Right = Width, Bottom = Height };
            // auto-hide: Windows keeps no space for the taskbar, so the work area is the whole screen
            if (!_work.Equals(all) || Native.TaskbarRect() is not { } s) return all;
            var tb = new Native.Rect { Left = s.Left - _bounds.Left, Top = s.Top - _bounds.Top,
                                       Right = s.Right - _bounds.Left, Bottom = s.Bottom - _bounds.Top };
            if (tb.Right <= 0 || tb.Left >= Width || tb.Bottom <= 0 || tb.Top >= Height) return all;

            bool across = tb.Right - tb.Left >= tb.Bottom - tb.Top;                  // along the top or bottom edge
            int gap = across ? Math.Min(tb.Bottom, Height) - Math.Max(tb.Top, 0)     // how much is on screen now
                             : Math.Min(tb.Right, Width) - Math.Max(tb.Left, 0);      // (~2 px when tucked away)
            if (gap <= 4) return all;

            bool far = across ? tb.Top + tb.Bottom > Height : tb.Left + tb.Right > Width;   // bottom / right edge
            if (across) { if (far) all.Bottom = Height - gap; else all.Top = gap; }
            else        { if (far) all.Right = Width - gap; else all.Left = gap; }
            return all;
        }

        // ------------------------------------------------------------------ Direct3D

        void CreateDevice()
        {
            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
                    out _device, out _ctx).Failure)
                D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                    out _device, out _ctx).CheckError();

            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,       // see-through where the shader says so
                Scaling = Scaling.Stretch,
            };
            _swap = factory.CreateSwapChainForComposition(_device, desc);
            CreateTarget();
            _clearFrames = 2;

            // hand the swap chain to the desktop compositor as this window's content
            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _dcomp.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
            _dcomp.CreateVisual(out _visual).CheckError();
            _visual!.SetContent(_swap);
            _target!.SetRoot(_visual);
            _dcomp.Commit();

            string src = LoadShader();
            var vsCode = Compiler.Compile(src, "VS", "Glow.hlsl", "vs_4_0");
            var psCode = Compiler.Compile(src, "PS", "Glow.hlsl", "ps_4_0");
            _vs = _device.CreateVertexShader(vsCode.Span);
            _ps = _device.CreatePixelShader(psCode.Span);
            _cb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer));

            // Direct2D on the same GPU device, for the lyrics
            _d2dFactory ??= D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
            // the lyrics draw each word once into a stencil, so Direct2D's caches (glyphs, effect
            // scratch space) are only needed for a moment when a line arrives: keep them small
            _d2dDevice.MaximumTextureMemory = 24UL << 20;
            _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            _d2d.SetDpi(96, 96);   // work in pixels
            CreateD2DTarget();
            _lyrics ??= new LyricsRenderer(_d2dFactory);
            _lyrics.Attach(_d2d, _d2dDevice);
        }

        void CreateD2DTarget()
        {
            using var surface = _swap!.GetBuffer<IDXGISurface>(0);
            _d2dTarget = _d2d!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        }

        static string LoadShader()
        {
            using var s = typeof(GlowWindow).Assembly.GetManifestResourceStream("Glow.hlsl")
                          ?? throw new InvalidOperationException("Glow.hlsl missing from the build");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        int Width => _bounds.Right - _bounds.Left;
        int Height => _bounds.Bottom - _bounds.Top;

        void CreateTarget()
        {
            using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device!.CreateRenderTargetView(back);
        }

        void Resize()
        {
            _rtv?.Dispose();
            _rtv = null;
            _d2d!.Target = null;
            _d2dTarget?.Dispose();
            _d2dTarget = null;
            _swap!.ResizeBuffers(2, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateTarget();
            CreateD2DTarget();
            _clearFrames = 2;
            _lyrics1 = _lyrics2 = null;
        }

        /// <summary>Width of the edge strips, px. Must cover Reach() + the strips in Glow.hlsl's VS.</summary>
        static int Band(in Constants c) =>
            (int)Math.Min(Math.Ceiling(Math.Max(c.Rim * 3.5 + c.Halo * 5 + 4, c.Radius) + c.Inset) + 2, Math.Min(c.Size.X, c.Size.Y) / 2);

        /// <summary>OLED care: how far in the glow sits right now, px. Goes 0 → 4 → 0 over 15 minutes, evenly
        /// (a triangle wave, so every offset gets the same time), far too slow to see it move.</summary>
        double OledInset(double t) => _settings.OledCare ? 4 * Math.Abs(t / 900 % 1 * 2 - 1) : 0;
        double _inset;

        void Draw(GlowFrame f)
        {
            double px = _scale;
            static Vector4 V(System.Windows.Media.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1);
            static Vector4 S(GlowController.Stream s) => new((float)s.Pos, (float)s.HalfLength, (float)s.Brightness, 0);
            var c = new Constants
            {
                Size = new Vector2(Width, Height),
                Radius = (float)(f.Radius * px),
                Rim = (float)Math.Max(1, f.Rim * px),
                Halo = (float)Math.Max(1, f.Halo * px),
                Opacity = (float)f.Opacity,
                Time = (float)f.Time,
                Phase = (float)f.Phase,
                Primary = V(f.Primary),
                Secondary = V(f.Secondary),
                Base = (float)f.Base,
                Wave = (float)f.Wave,
                Pulse = (float)f.Pulse,
                White = (float)f.White,
                S0 = S(f.Streams[0]), S1 = S(f.Streams[1]), S2 = S(f.Streams[2]), S3 = S(f.Streams[3]),
                S4 = S(f.Streams[4]), S5 = S(f.Streams[5]), S6 = S(f.Streams[6]), S7 = S(f.Streams[7]),
                Backdrop = _black ? 1 : (float)_backdrop,
                Inset = (float)_inset,
                Dark = _black ? new Vector4(0, 0, Width, Height)
                              : new Vector4(_darkRect.Left, _darkRect.Top, _darkRect.Right, _darkRect.Bottom),
            };
            // the glow pass also repaints where the lyrics were, which wipes them (back to the glow,
            // see-through or Night sky's black, whatever belongs there)
            if (Union(_lyrics1, _lyrics2) is RawRect wipe) c.Extra = new Vector4(wipe.Left, wipe.Top, wipe.Right, wipe.Bottom);
            var ctx = _ctx!;

            // Only the edge strips get drawn; the middle of each buffer stays as it was cleared.
            // When the strips change size (or the buffers are new) wipe each buffer once.
            int band = Band(c);
            if (band != _band) { _band = band; _clearFrames = 2; }
            if (_black || _clearFrames > 0)
            {
                ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, _black ? 1 : 0));
                if (!_black && _backdrop > 0)   // Night sky: the black part of the screen
                    FillD2D(new RawRect(_darkRect.Left, _darkRect.Top, _darkRect.Right, _darkRect.Bottom), (float)_backdrop);
                if (_clearFrames > 0) _clearFrames--;
                _fullPresent = true;
            }

            ctx.ClearState();   // Direct2D shares the GPU; start the glow pass from a clean slate
            ctx.UpdateSubresource(in c, _cb!);
            ctx.OMSetRenderTargets(_rtv!);
            ctx.RSSetViewport(0, 0, Width, Height);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs!);
            ctx.VSSetConstantBuffer(0, _cb!);
            ctx.PSSetShader(_ps!);
            ctx.PSSetConstantBuffer(0, _cb!);
            ctx.Draw(30, 0);   // 4 strips + the lyrics wipe, 2 triangles each

            // lyrics on top of the glow
            RawRect? drawn = null;
            if (_lyrics != null && _lyrics.Active)
            {
                _lyrics.Draw(_d2dTarget!, f.Primary, _scale);
                drawn = _lyrics.Drawn;
            }
            _lyricsDirty = Union(_lyrics1, drawn);   // vs. the frame on screen: its lyrics went, these came
            _lyrics2 = _lyrics1;
            _lyrics1 = drawn;
        }

        static RawRect? Union(RawRect? a, RawRect? b) =>
            a is RawRect x ? (b is RawRect y ? Union(x, y) : x) : b;

        /// <summary>Set a rectangle of the back buffer to black at <paramref name="alpha"/> (0 = see-through).</summary>
        void FillD2D(RawRect r, float alpha)
        {
            var d2d = _d2d!;
            d2d.Target = _d2dTarget;
            d2d.BeginDraw();
            d2d.Transform = Matrix3x2.Identity;
            d2d.PushAxisAlignedClip(new RawRectF(r.Left, r.Top, r.Right, r.Bottom), AntialiasMode.Aliased);
            d2d.Clear(new Color4(0, 0, 0, alpha));
            d2d.PopAxisAlignedClip();
            d2d.EndDraw();
        }

        static RawRect Union(RawRect a, RawRect b) =>
            new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

        void Present()
        {
            SharpGen.Runtime.Result hr;
            if (_fullPresent)
                hr = _swap!.Present(_sync, PresentFlags.None);
            else
            {
                // tell the compositor only the edge strips changed, so it doesn't redo the whole screen
                int w = Width, h = Height, b = Math.Min(_band + 2, Math.Min(w, h) / 2);
                _dirty[0] = new RawRect(0, 0, w, b);
                _dirty[1] = new RawRect(0, h - b, w, h);
                _dirty[2] = new RawRect(0, b, b, h - b);
                _dirty[3] = new RawRect(w - b, b, w, h - b);
                var rects = _dirty;
                if (_lyricsDirty is RawRect area)   // ...plus where the lyrics changed
                {
                    Array.Copy(_dirty, _dirtyWithLyrics, 4);
                    _dirtyWithLyrics[4] = new RawRect(Math.Max(0, area.Left), Math.Max(0, area.Top), Math.Min(w, area.Right), Math.Min(h, area.Bottom));
                    rects = _dirtyWithLyrics;
                }
                hr = _swap!.Present1(_sync, PresentFlags.None, new PresentParameters { DirtyRectangles = rects });
            }
            _fullPresent = false;
            if (hr.Failure)   // GPU reset / driver update: start over
            {
                ReleaseDevice();
                CreateDevice();
            }
        }

        void ReleaseDevice()
        {
            if (_d2d != null) _d2d.Target = null;
            _d2dTarget?.Dispose(); _d2d?.Dispose(); _d2dDevice?.Dispose();
            _d2dTarget = null; _d2d = null; _d2dDevice = null;
            _lyrics1 = _lyrics2 = _lyricsDirty = null;
            _cb?.Dispose(); _ps?.Dispose(); _vs?.Dispose(); _rtv?.Dispose();
            _visual?.Dispose(); _target?.Dispose(); _dcomp?.Dispose();
            _swap?.Dispose(); _ctx?.Dispose(); _device?.Dispose();
            _cb = null; _ps = null; _vs = null; _rtv = null; _visual = null; _target = null; _dcomp = null;
            _swap = null; _ctx = null; _device = null;
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
        }
    }
}
