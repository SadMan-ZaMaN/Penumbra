using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Penumbra
{
    public enum LyricsStyle { Flow, Constellation }

    /// <summary>
    /// Synced lyrics, drawn with Direct2D / DirectWrite into the glow window on the render thread.
    /// The lyrics only time whole lines, so each word gets a share of the line's estimated singing
    /// time (by its length) and lights up when its turn comes.
    ///   Flow:          the current line above the taskbar; words go from dim to bright with a soft
    ///                  colored glow, the word being sung lifts a little, lines slide up and away.
    ///   Constellation: each word appears as a star joined to the previous one by a thin line; the
    ///                  word being sung is big and bright, earlier ones shrink and dim.
    ///
    /// Speed: each word's text and its blurred glow are drawn once, into small white "stencils",
    /// when its line arrives. Every frame then only places, scales and tints those stencils, which
    /// the GPU does almost for free (re-drawing text at a new size each frame is what's expensive).
    /// Everything here is in physical pixels.
    /// </summary>
    public sealed class LyricsRenderer : IDisposable
    {
        sealed class Word
        {
            public float W, H;
            public float Start, Length;     // share of the line's singing time, 0..1
            public Vector2 Pos;             // Flow: top-left within the line block; Constellation: its star
            public int Row;                 // Flow: which row of the line it's on
            public Stencil Text = null!, Glow = null!;   // white text, and its blurred glow, at full size
            public Stencil? Small;          // Constellation: the text again at its settled size (sharper than shrinking)
        }

        /// <summary>A white picture with transparent background; tinted with a brush when drawn.</summary>
        sealed class Stencil : IDisposable
        {
            public ID2D1Bitmap1 Bitmap = null!;
            public float Pad;               // the content starts this far in (room for the glow)
            public float W, H;              // bitmap size
            public void Dispose() => Bitmap.Dispose();
        }

        sealed class Line
        {
            public int Index;
            public string Text = "";
            public LyricsStyle Style;
            public readonly List<Word> Words = new();
            public float BlockTop;          // Flow: where the block of words starts
            public double Age;              // seconds since this line appeared
            public double Leaving = -1;     // seconds since it started to leave (-1 = still current)
            public double SungAt;           // seconds into the line, as last reported
            public double SingFor = 1;      // estimated seconds to sing it
        }

        const string Font = "Segoe UI Variable Display";
        const float Settled = 0.42f;        // Constellation: size of a word once it's been sung, vs. while sung

        readonly IDWriteFactory _dw;
        readonly ID2D1Factory1 _factory;
        readonly ID2D1PathGeometry _star;   // 4-pointed star, radius 1 around (0, 0)
        ID2D1DeviceContext? _ctx;
        ID2D1SolidColorBrush? _brush;
        ID2D1LinearGradientBrush? _wipe;    // Flow: bright white -> dim, slid along the line as it's sung
        ID2D1LinearGradientBrush? _glowWipe;// the same for the glow: glow color -> nothing
        Color4 _glowWipeColor;
        Vortice.Direct2D1.Effects.Shadow? _blur;   // only used while making glow stencils
        Stencil? _dot;                      // soft round glow, for the stars
        IDWriteTextFormat? _format, _settledFormat, _small;
        float _formatPx;
        LyricsStyle _formatStyle;

        Line? _current;
        readonly List<Line> _leaving = new();
        IDWriteTextLayout? _nextLayout;
        double _alpha;                      // whole-lyrics fade in / out
        RawRectF _region;                   // where lyrics go, px (set every frame in Update)

        /// <summary>Pixels this renderer drew into last frame (null = nothing).</summary>
        public RawRect? Drawn { get; private set; }

        public LyricsRenderer(ID2D1Factory1 factory)
        {
            _factory = factory;
            _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            _star = factory.CreatePathGeometry();
            using (var sink = _star.Open())
            {
                sink.BeginFigure(new Vector2(0, -1), FigureBegin.Filled);
                sink.AddLines(new[]
                {
                    new Vector2(0.22f, -0.22f), new Vector2(1, 0), new Vector2(0.22f, 0.22f), new Vector2(0, 1),
                    new Vector2(-0.22f, 0.22f), new Vector2(-1, 0), new Vector2(-0.22f, -0.22f),
                });
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
        }

        /// <summary>Call when the glow window gets a new Direct2D context (startup or GPU reset).</summary>
        public void Attach(ID2D1DeviceContext ctx)
        {
            ReleaseDeviceStuff();
            _ctx = ctx;
            _ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;   // ClearType can't do see-through
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            _blur = new Vortice.Direct2D1.Effects.Shadow(ctx);
            _wipe = MakeWipe(new Color4(1, 1, 1, 1), new Color4(1, 1, 1, Dim));
        }

        const float Dim = 0.35f;   // Flow: brightness of words not sung yet

        /// <summary>A left-to-right gradient; its start/end points get moved to wherever the singing is.</summary>
        ID2D1LinearGradientBrush MakeWipe(Color4 from, Color4 to)
        {
            using var stops = _ctx!.CreateGradientStopCollection(new[] { new GradientStop(0, from), new GradientStop(1, to) });
            return _ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, new Vector2(1, 0)), stops);
        }

        /// <summary>Is there anything to draw (or still fading)?</summary>
        public bool Active => _alpha > 0.002 || _leaving.Count > 0;

        /// <summary>Follow the engine's lyric state. <paramref name="show"/> = lyrics switched on and sound playing;
        /// <paramref name="region"/> = where the lyrics go (see <see cref="RegionFor"/>).</summary>
        public void Update(LightState? s, bool show, LyricsStyle style, RawRectF region, double dt, double scale)
        {
            if (!region.Equals(_region))
            {
                bool moved = _region.Right - _region.Left > 0;
                _region = region;
                if (moved && _current != null)   // display changed: lay the current line out again
                {
                    if (_current.Style == LyricsStyle.Flow) LayoutFlow(_current); else LayoutConstellation(_current, scale);
                }
            }

            bool has = show && s != null && !string.IsNullOrWhiteSpace(s.lyric);
            _alpha += ((has ? 1 : 0) - _alpha) * (1 - Math.Exp(-(has ? 6 : 4) * dt));

            if (has && (_current == null || _current.Index != s!.lidx || _current.Text != s.lyric || _current.Style != style))
            {
                if (_current != null) { _current.Leaving = 0; _leaving.Add(_current); }
                _current = MakeLine(s!.lyric, s.lidx, style, scale);
                SetNext(s.next, style, scale);
            }
            else if (!has && _current != null && _alpha < 0.01)
            {
                _current.Leaving = 0;
                _leaving.Add(_current);
                _current = null;
            }
            if (_current != null && s != null)
            {
                _current.Age += dt;
                _current.SungAt = s.lt;
                _current.SingFor = Math.Max(0.2, s.ld);
            }
            for (int i = _leaving.Count - 1; i >= 0; i--)
            {
                var l = _leaving[i];
                l.Leaving += dt;
                if (l.Leaving > (l.Style == LyricsStyle.Flow ? 0.5 : 0.9)) { Free(l); _leaving.RemoveAt(i); }
            }
        }

        // ------------------------------------------------------------------ making lines

        void EnsureFormats(LyricsStyle style, double scale)
        {
            // Flow: big, clean and bold. Constellation words are made at their biggest (the word
            // being sung) and shrink from there.
            float px = (float)((style == LyricsStyle.Flow ? 34 : 44) * scale);
            if (_format != null && Math.Abs(px - _formatPx) < 0.5f && style == _formatStyle) return;
            _format?.Dispose(); _settledFormat?.Dispose(); _small?.Dispose();
            _format = MakeFormat(px, style == LyricsStyle.Flow ? FontWeight.Bold : FontWeight.SemiBold);
            _settledFormat = MakeFormat(px * Settled, FontWeight.SemiBold);
            _small = MakeFormat(px * 0.58f, FontWeight.SemiBold);
            _formatPx = px;
            _formatStyle = style;
            _dot?.Dispose();
            _dot = null;
        }

        IDWriteTextFormat MakeFormat(float px, FontWeight weight) =>
            _dw.CreateTextFormat(Font, null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, px, "en-us");

        Line MakeLine(string text, int index, LyricsStyle style, double scale)
        {
            EnsureFormats(style, scale);
            var line = new Line { Index = index, Text = text, Style = style };
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float total = 0;
            foreach (var p in parts) total += p.Length + 1;
            float at = 0, blur = _formatPx * 0.22f;
            foreach (var p in parts)
            {
                var word = new Word { Start = at / total, Length = (p.Length + 1) / total };
                word.Text = MakeTextStencil(p, _format!, blur * 3, out word.W, out word.H);
                word.Glow = MakeGlowStencil(word.Text, blur);
                if (style == LyricsStyle.Constellation) word.Small = MakeTextStencil(p, _settledFormat!, 2, out _, out _);
                line.Words.Add(word);
                at += p.Length + 1;
            }
            if (style == LyricsStyle.Flow) LayoutFlow(line);
            else LayoutConstellation(line, scale);
            return line;
        }

        /// <summary>The word in white on transparent, <paramref name="pad"/> px in from each side.</summary>
        Stencil MakeTextStencil(string text, IDWriteTextFormat format, float pad, out float w, out float h)
        {
            using var layout = _dw.CreateTextLayout(text, format, 4000, 1000);
            var m = layout.Metrics;
            w = m.WidthIncludingTrailingWhitespace;
            h = m.Height;
            var st = NewStencil(w + pad * 2, h + pad * 2, pad);
            var ctx = _ctx!;
            ctx.Target = st.Bitmap;
            ctx.BeginDraw();
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(new Color4(0, 0, 0, 0));
            _brush!.Color = new Color4(1, 1, 1, 1);
            ctx.DrawTextLayout(new Vector2(pad, pad), layout, _brush);
            ctx.EndDraw();
            return st;
        }

        /// <summary>A blurred copy of a stencil: its glow.</summary>
        Stencil MakeGlowStencil(Stencil of, float blur)
        {
            var st = NewStencil(of.W, of.H, of.Pad);
            var ctx = _ctx!;
            _blur!.SetInput(0, of.Bitmap, true);
            _blur.BlurStandardDeviation = blur;
            _blur.Color = new Vector4(1, 1, 1, 1);
            ctx.Target = st.Bitmap;
            ctx.BeginDraw();
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(new Color4(0, 0, 0, 0));
            ctx.DrawImage(_blur.Output, Vector2.Zero, null, InterpolationMode.Linear, CompositeMode.SourceOver);
            ctx.EndDraw();
            _blur.SetInput(0, null, true);
            return st;
        }

        Stencil NewStencil(float w, float h, float pad) => new()
        {
            Bitmap = _ctx!.CreateBitmap(new SizeI((int)Math.Ceiling(w), (int)Math.Ceiling(h)), IntPtr.Zero, 0, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target)),
            Pad = pad, W = (float)Math.Ceiling(w), H = (float)Math.Ceiling(h),
        };

        /// <summary>A soft round glow, 64 px across, for under the stars.</summary>
        Stencil Dot()
        {
            if (_dot != null) return _dot;
            var core = NewStencil(64, 64, 32);
            var ctx = _ctx!;
            ctx.Target = core.Bitmap;
            ctx.BeginDraw();
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(new Color4(0, 0, 0, 0));
            _brush!.Color = new Color4(1, 1, 1, 1);
            ctx.FillEllipse(new Ellipse(new Vector2(32, 32), 8, 8), _brush);
            ctx.EndDraw();
            _dot = MakeGlowStencil(core, 8);
            core.Dispose();
            return _dot;
        }

        /// <summary>Flow: words in centred rows; the block sits above the small "next line".</summary>
        void LayoutFlow(Line line)
        {
            float maxW = (_region.Right - _region.Left) * 0.94f, space = _formatPx * 0.28f, rowH = _formatPx * 1.3f;
            var rows = new List<(int from, int to, float width)>();
            int start = 0; float x = 0;
            for (int i = 0; i < line.Words.Count; i++)
            {
                float w = line.Words[i].W;
                if (i > start && x + w > maxW) { rows.Add((start, i, x - space)); start = i; x = 0; }
                x += w + space;
            }
            rows.Add((start, line.Words.Count, x - space));
            float regionW = _region.Right - _region.Left;
            for (int r = 0; r < rows.Count; r++)
            {
                float cx = (regionW - rows[r].width) / 2;
                for (int i = rows[r].from; i < rows[r].to; i++)
                {
                    line.Words[i].Pos = new Vector2(cx, r * rowH);
                    line.Words[i].Row = r;
                    cx += line.Words[i].W + space;
                }
            }
            float regionH = _region.Bottom - _region.Top;
            line.BlockTop = regionH - _formatPx * 1.25f - rows.Count * rowH;
        }

        /// <summary>Constellation: each word's star a hop away from the last, in a direction that keeps it
        /// inside the region and off the other words. Seeded by the line, so it's stable.
        /// Word.Pos is the star; the word sits just to its right.</summary>
        void LayoutConstellation(Line line, double scale)
        {
            float rw = _region.Right - _region.Left, rh = _region.Bottom - _region.Top;
            var rng = new Random(line.Text.GetHashCode() ^ line.Index);
            var boxes = new List<RawRectF>();
            float hop = (float)(80 * scale), pad = (float)(16 * scale);
            RawRectF Box(Vector2 c, Word w, float s) =>   // the star plus its word at size s
                new(c.X - pad, c.Y - w.H * s * 0.6f - pad, c.X + pad + w.W * s + _formatPx * 0.3f, c.Y + w.H * s * 0.6f + pad);

            Vector2 at = new(rw * (0.1f + 0.25f * (float)rng.NextDouble()), rh * (0.3f + 0.4f * (float)rng.NextDouble()));
            Word? prev = null;
            foreach (var w in line.Words)
            {
                Vector2 best = at; float bestScore = float.MaxValue;
                var lo = new Vector2(pad, w.H * 0.6f);
                var hi = new Vector2(Math.Max(pad, rw - w.W - _formatPx * 0.3f - pad), Math.Max(w.H, rh - w.H * 0.6f));
                for (int tries = 0; tries < (prev == null ? 1 : 32); tries++)
                {
                    var c = at;
                    if (prev != null)
                    {
                        double ang = (rng.NextDouble() - 0.5) * Math.PI * 1.3;      // mostly rightwards...
                        if (at.X > rw * 0.65f) ang += Math.PI;                    // ...unless near the right edge
                        float d = hop + prev.W * Settled + (float)rng.NextDouble() * hop * 1.4f;
                        c = at + new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang)) * d;
                    }
                    c = Vector2.Clamp(c, lo, hi);
                    float score = Math.Abs(c.Y - rh * 0.5f) * 0.2f;                 // drift around the middle
                    var settled = Box(c, w, Settled); var big = Box(c, w, 1);
                    foreach (var b in boxes) score += Overlap(settled, b) + 0.25f * Overlap(big, b);
                    if (score < bestScore) { bestScore = score; best = c; }
                }
                w.Pos = best;
                boxes.Add(Box(best, w, Settled));
                at = best;
                prev = w;
            }
        }

        static float Overlap(RawRectF a, RawRectF b) =>
            Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
            Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

        void SetNext(string next, LyricsStyle style, double scale)
        {
            _nextLayout?.Dispose();
            _nextLayout = null;
            if (style != LyricsStyle.Flow || string.IsNullOrEmpty(next)) return;
            EnsureFormats(style, scale);
            _nextLayout = _dw.CreateTextLayout(next, _small!, 4000, 1000);
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>Where lyrics go for a style, px: Flow above the taskbar, Constellation in the middle.</summary>
        internal static RawRectF RegionFor(LyricsStyle style, Native.Rect work, double scale)
        {
            float l = work.Left, t = work.Top, w = work.Right - work.Left, h = work.Bottom - work.Top;
            if (style == LyricsStyle.Flow)
            {
                float width = (float)Math.Min(w * 0.8, 1500 * scale);
                float bottom = work.Bottom - (float)(14 * scale);
                return new RawRectF(l + (w - width) / 2, bottom - (float)(210 * scale), l + (w + width) / 2, bottom);
            }
            return new RawRectF(l + w * 0.12f, t + h * 0.16f, l + w * 0.88f, t + h * 0.8f);
        }

        /// <summary>Draw into the back buffer <paramref name="target"/>. The caller has already cleared
        /// what was drawn last frame (<see cref="Drawn"/>).</summary>
        public void Draw(ID2D1Bitmap1 target, System.Windows.Media.Color primary, double scale)
        {
            if (!Active) { Drawn = null; return; }
            var ctx = _ctx!;
            var glow = new Color4(primary.R / 255f, primary.G / 255f, primary.B / 255f, 1);
            var sung = new Color4(Mix(1, glow.R, 0.22f), Mix(1, glow.G, 0.22f), Mix(1, glow.B, 0.22f), 1);
            var origin = Matrix3x2.CreateTranslation(_region.Left, _region.Top);
            if (_glowWipe == null || Math.Abs(_glowWipeColor.R - glow.R) + Math.Abs(_glowWipeColor.G - glow.G) + Math.Abs(_glowWipeColor.B - glow.B) > 0.02f)
            {
                _glowWipe?.Dispose();   // gradient colors are fixed once made, so remake it as the glow color drifts
                _glowWipe = MakeWipe(glow, new Color4(glow.R, glow.G, glow.B, 0));
                _glowWipeColor = glow;
            }

            ctx.Target = target;
            ctx.BeginDraw();
            // glows first, then everything crisp on top, so a glow never covers a neighbouring word
            foreach (var pass in new[] { true, false })
            {
                foreach (var l in _leaving) DrawLine(ctx, l, pass, sung, glow, scale, origin);
                if (_current != null) DrawLine(ctx, _current, pass, sung, glow, scale, origin);
            }
            if (_nextLayout != null && _current != null && _current.Style == LyricsStyle.Flow)
            {
                var m = _nextLayout.Metrics;
                float rw = _region.Right - _region.Left, rh = _region.Bottom - _region.Top;
                ctx.Transform = origin;
                _brush!.Color = new Color4(1, 1, 1, (float)(Ease(_current.Age / 0.4) * 0.4 * _alpha));
                ctx.DrawTextLayout(new Vector2((rw - m.Width) / 2, rh - m.Height - _formatPx * 0.05f), _nextLayout, _brush);
            }
            ctx.Transform = Matrix3x2.Identity;
            ctx.EndDraw();

            float pad = _formatPx;   // glows and the leaving line spill a little past the region
            Drawn = new RawRect((int)(_region.Left - pad), (int)(_region.Top - pad), (int)(_region.Right + pad), (int)(_region.Bottom + pad));
        }

        /// <summary>Draw a stencil tinted <paramref name="color"/>, its content's top-left at the origin of <paramref name="t"/>.</summary>
        void Paint(ID2D1DeviceContext ctx, Stencil st, Matrix3x2 t, Color4 color)
        {
            if (color.A <= 0.004f) return;
            ctx.Transform = t;
            _brush!.Color = color;
            ctx.AntialiasMode = AntialiasMode.Aliased;   // required for opacity masks
            ctx.FillOpacityMask(st.Bitmap, _brush, new RawRectF(-st.Pad, -st.Pad, st.W - st.Pad, st.H - st.Pad), null);
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        }

        /// <summary>Draw a stencil through a wipe gradient whose soft edge is at <paramref name="x"/> (in the
        /// stencil's own coordinates): brighter to the left, dimmer to the right.</summary>
        void PaintWipe(ID2D1DeviceContext ctx, Stencil st, Matrix3x2 t, ID2D1LinearGradientBrush wipe, float x, float feather, float alpha)
        {
            if (alpha <= 0.004f) return;
            ctx.Transform = t;
            wipe.StartPoint = new Vector2(x - feather, 0);
            wipe.EndPoint = new Vector2(x + feather, 0);
            wipe.Opacity = alpha;
            ctx.AntialiasMode = AntialiasMode.Aliased;
            ctx.FillOpacityMask(st.Bitmap, wipe, new RawRectF(-st.Pad, -st.Pad, st.W - st.Pad, st.H - st.Pad), null);
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        }

        void DrawLine(ID2D1DeviceContext ctx, Line l, bool glowPass, Color4 sung, Color4 glow, double scale, Matrix3x2 origin)
        {
            double sungFrac = l.Leaving >= 0 ? 1 : l.SungAt / l.SingFor;   // how much of the line is sung
            if (l.Style == LyricsStyle.Flow)
            {
                // The new line slides up into place; the old one drifts up, blurs and fades away.
                float rowH = _formatPx * 1.3f;
                bool leaving = l.Leaving >= 0;
                double inT = Ease(l.Age / 0.45), outT = leaving ? Ease(l.Leaving / 0.5) : 0;
                float dy = (float)((1 - inT) * rowH * 0.6 - outT * rowH * 0.8);
                float lineAlpha = (float)(Ease(l.Age / 0.35) * _alpha);

                // where the singing is: a soft-edged sweep at (row, x) that crosses word gaps smoothly
                int wipeRow = int.MaxValue; float wipeX = 0;
                if (!leaving)
                    foreach (var w in l.Words)
                    {
                        double p = (sungFrac - w.Start) / w.Length;
                        if (p < 1) { wipeRow = w.Row; wipeX = w.Pos.X + w.W * (float)Math.Max(0, p); break; }
                    }
                float feather = _formatPx * 0.5f;

                foreach (var w in l.Words)
                {
                    var t = Matrix3x2.CreateTranslation(w.Pos.X, l.BlockTop + w.Pos.Y + dy) * origin;
                    if (leaving)
                    {
                        // blur out: the sharp text fades while its blurred copy shows through
                        if (glowPass) Paint(ctx, w.Glow, t, new Color4(1, 1, 1, (float)(0.8 * outT * (1 - outT)) * lineAlpha));
                        else Paint(ctx, w.Text, t, new Color4(1, 1, 1, (float)(1 - outT) * lineAlpha));
                        continue;
                    }
                    float local = wipeX - w.Pos.X;                   // the sweep, in this word's coordinates
                    bool sungAll = w.Row < wipeRow || (w.Row == wipeRow && local - feather >= w.W);
                    bool sungNone = w.Row > wipeRow || (w.Row == wipeRow && local + feather <= 0);
                    if (glowPass)   // a soft glow, only on what's been sung
                    {
                        if (sungAll) Paint(ctx, w.Glow, t, new Color4(glow.R, glow.G, glow.B, 0.55f * lineAlpha));
                        else if (!sungNone) PaintWipe(ctx, w.Glow, t, _glowWipe!, local, feather, 0.55f * lineAlpha);
                        continue;
                    }
                    if (sungAll) Paint(ctx, w.Text, t, new Color4(1, 1, 1, lineAlpha));
                    else if (sungNone) Paint(ctx, w.Text, t, new Color4(1, 1, 1, Dim * lineAlpha));
                    else PaintWipe(ctx, w.Text, t, _wipe!, local, feather, lineAlpha);
                }
                return;
            }

            // Constellation
            float leaveAlpha = l.Leaving >= 0 ? (float)(1 - Ease(l.Leaving / 0.9)) : 1;
            float star = _formatPx * 0.11f;
            Vector2? prev = null; float prevA = 0;
            for (int i = 0; i < l.Words.Count; i++)
            {
                var w = l.Words[i];
                double since = (sungFrac - w.Start) * l.SingFor;           // seconds since this word's turn came
                if (since < 0) break;                                       // not yet: words appear in order
                double appear = Ease(since / 0.25);
                double after = (sungFrac - (w.Start + w.Length)) * l.SingFor; // seconds since it was sung
                bool active = after < 0;
                double shrink = active ? 0 : Ease(after / 0.5);
                // pops in at half size, is full size while sung, then settles small
                float scaleW = active ? Mix(0.5f, 1, (float)appear) : Mix(1, Settled, (float)shrink);
                float a = (float)(appear * Mix(1, 0.7f, (float)shrink) * leaveAlpha * _alpha);
                var starAt = w.Pos;
                float starR = star * (active ? Mix(1, 2.2f, (float)appear) : Mix(2.2f, 1, (float)shrink));
                // the word, just right of its star, vertically centred on it
                var at = Matrix3x2.CreateTranslation(starAt.X + star * 2.4f + starR, starAt.Y) * origin;

                if (glowPass)
                {
                    var dot = Dot();
                    float dotS = starR / 8 * 1.3f;   // the dot's core is 8 px across
                    Paint(ctx, dot, Matrix3x2.CreateTranslation(-32, -32) * Matrix3x2.CreateScale(dotS) *
                                    Matrix3x2.CreateTranslation(starAt) * origin, new Color4(glow.R, glow.G, glow.B, a));
                    Paint(ctx, w.Glow, Matrix3x2.CreateTranslation(0, -w.H * 0.5f) * Matrix3x2.CreateScale(scaleW) * at,
                          new Color4(glow.R, glow.G, glow.B, (active ? 1.4f : 0.5f) * a));
                    continue;
                }

                if (prev is Vector2 pv)   // a thin line back to the previous star
                {
                    ctx.Transform = origin;
                    _brush!.Color = new Color4(1, 1, 1, 0.35f * Math.Min(a, prevA));
                    ctx.DrawLine(pv, starAt, _brush, (float)Math.Max(1, 1.2 * scale));
                }
                ctx.Transform = Matrix3x2.CreateScale(starR) * Matrix3x2.CreateTranslation(starAt) * origin;
                _brush!.Color = new Color4(glow.R, glow.G, glow.B, a);
                ctx.FillGeometry(_star, _brush);
                if (active)   // a ring around the star of the word being sung
                {
                    ctx.Transform = origin;
                    _brush.Color = new Color4(glow.R, glow.G, glow.B, a * 0.6f);
                    ctx.DrawEllipse(new Ellipse(starAt, starR * 2, starR * 2), _brush, (float)Math.Max(1, 1.3 * scale));
                }

                // big while sung, then settles small and dim (using the small copy once it's close to that size)
                var color = active ? new Color4(1, 1, 1, a) : new Color4(sung.R, sung.G, sung.B, a);
                if (w.Small != null && scaleW < (1 + Settled) / 2)
                    Paint(ctx, w.Small, Matrix3x2.CreateTranslation(0, -w.H * Settled * 0.5f) *
                                        Matrix3x2.CreateScale(scaleW / Settled) * at, color);
                else
                    Paint(ctx, w.Text, Matrix3x2.CreateTranslation(0, -w.H * 0.5f) * Matrix3x2.CreateScale(scaleW) * at, color);

                prev = starAt; prevA = a;
            }
        }

        static float Mix(float a, float b, float t) => a + (b - a) * t;
        static double Ease(double t) { t = Math.Clamp(t, 0, 1); return 1 - (1 - t) * (1 - t) * (1 - t); }
        static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

        static void Free(Line l)
        {
            foreach (var w in l.Words) { w.Text.Dispose(); w.Glow.Dispose(); w.Small?.Dispose(); }
            l.Words.Clear();
        }

        void ReleaseDeviceStuff()
        {
            // stencils belong to the old GPU device: drop the lines, they'll be made again
            if (_current != null) { Free(_current); _current = null; }
            foreach (var l in _leaving) Free(l);
            _leaving.Clear();
            _dot?.Dispose(); _dot = null;
            _wipe?.Dispose(); _wipe = null;
            _glowWipe?.Dispose(); _glowWipe = null;
            _blur?.Dispose(); _blur = null;
            _brush?.Dispose(); _brush = null;
        }

        public void Dispose()
        {
            ReleaseDeviceStuff();
            _nextLayout?.Dispose();
            _format?.Dispose(); _settledFormat?.Dispose(); _small?.Dispose();
            _star.Dispose();
            _dw.Dispose();
        }
    }
}
