using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LumnWin
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
            public float Spread, Opacity, Time, Phase;
            public Vector4 Primary, Secondary;
            public float Base, Wave, Pulse, White;
            public Vector4 L0, L1, L2, L3, L4, L5;
            public float Backdrop; Vector3 _pad;
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
        public GlowWindow(Settings settings, GlowController ctl, bool black = false)
        {
            _settings = settings;
            _ctl = ctl;
            _black = black;
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
                if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
            }
        }

        void Loop()
        {
            var clock = Stopwatch.StartNew();
            bool shown = false, cleared = false;
            double lastCheck = -1, lastTop = 0, lastFrame = 0;
            bool? excluded = null;

            while (!_stop)
            {
                Native.PumpMessages();
                double t = clock.Elapsed.TotalSeconds;

                if (t - lastCheck > 1)           // follow resolution / DPI / setting changes
                {
                    lastCheck = t;
                    var b = Native.PrimaryMonitor();
                    if (!b.Equals(_bounds)) { _bounds = b; Native.MoveTo(_hwnd, b); Resize(); }
                    _scale = Native.DpiScale(_hwnd);
                    bool ex = !_settings.ShowInCapture;
                    if (excluded != ex) { excluded = ex; Native.ExcludeFromCapture(_hwnd, ex); }
                }

                var f = _ctl.Update(t);
                if (_settings.Enabled != shown) { shown = _settings.Enabled; Native.Show(_hwnd, shown); }
                if (!shown) { Thread.Sleep(100); continue; }
                if (t - lastTop > 2) { lastTop = t; Native.BringToTop(_hwnd); }   // stay above the taskbar & co.

                if (f.Opacity < 0.002)           // fully faded out: clear once, then idle cheaply
                {
                    if (!cleared) { Clear(); Present(); cleared = true; }
                    Thread.Sleep(30);
                    continue;
                }
                cleared = false;

                Draw(f);
                Present();
                // Present waits for the next screen refresh; this only guards against it not waiting
                double spent = clock.Elapsed.TotalSeconds - lastFrame;
                if (spent < 0.005) Thread.Sleep(1);
                lastFrame = clock.Elapsed.TotalSeconds;
            }
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
            _swap!.ResizeBuffers(2, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateTarget();
        }

        void Clear() => _ctx!.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 0));

        void Draw(GlowFrame f)
        {
            double px = _scale;
            static Vector4 V(System.Windows.Media.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1);
            static Vector4 L(GlowController.Light l) => new((float)l.Pos, (float)l.Width, (float)l.Strength, 0);
            var c = new Constants
            {
                Size = new Vector2(Width, Height),
                Radius = (float)(f.Radius * px),
                Rim = (float)Math.Max(1, f.Rim * px),
                Spread = (float)Math.Max(2, f.Spread * px),
                Opacity = (float)f.Opacity,
                Time = (float)f.Time,
                Phase = (float)f.Phase,
                Primary = V(f.Primary),
                Secondary = V(f.Secondary),
                Base = (float)f.Base,
                Wave = (float)f.Wave,
                Pulse = (float)f.Pulse,
                White = (float)f.White,
                L0 = L(f.Lights[0]), L1 = L(f.Lights[1]), L2 = L(f.Lights[2]),
                L3 = L(f.Lights[3]), L4 = L(f.Lights[4]), L5 = L(f.Lights[5]),
                Backdrop = _black ? 1 : 0,
            };
            var ctx = _ctx!;
            ctx.UpdateSubresource(in c, _cb!);
            ctx.OMSetRenderTargets(_rtv!);
            ctx.RSSetViewport(0, 0, Width, Height);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs!);
            ctx.PSSetShader(_ps!);
            ctx.PSSetConstantBuffer(0, _cb!);
            ctx.Draw(3, 0);
        }

        void Present()
        {
            var hr = _swap!.Present(1, PresentFlags.None);
            if (hr.Failure)   // GPU reset / driver update: start over
            {
                ReleaseDevice();
                CreateDevice();
            }
        }

        void ReleaseDevice()
        {
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
