using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Penumbra
{
    /// <summary>Flow = one line above the taskbar; Constellation = words as stars; NightSky = the
    /// constellation on a pitch-black screen (the glow stays on around the edges).</summary>
    public enum LyricsStyle { Flow, Constellation, NightSky }

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
            public int Rows = 1;            // Flow: how many rows its words take
        }

        const string Font = "Segoe UI Variable Display";
        const float Settled = 0.42f;        // Constellation: size of a word once it's been sung, vs. while sung

        readonly IDWriteFactory _dw;
        readonly ID2D1Factory1 _factory;
        readonly ID2D1PathGeometry _star;   // 4-pointed star, radius 1 around (0, 0)
        ID2D1DeviceContext? _ctx;
        ID2D1Device? _device;
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
        Stencil? _next;                     // Flow: the next line, small and dim
        float _nextW, _nextH;
        bool _alphaOnly = true;             // text stencils as 1-byte alpha (falls back to full color if the GPU says no)
        float _x0, _y0, _x1, _y1;           // what this frame has drawn so far, px (for Drawn)
        readonly List<(Word w, Vector2 star, float starR, float scale, float a, bool active)> _shown = new();
        double _alpha;                      // whole-lyrics fade in / out
        RawRectF _region;                   // where lyrics go, px (set every frame in Update)

        // ---- the Now playing screen's Flow: the song as a list (like YouTube Music or Spotify), the line
        // being sung word by word as usual, the others whole and dim. Those are drawn once each into a
        // stencil (made in Update: Direct2D can't make one in the middle of drawing a frame).
        /// <summary>Draw Flow as a list (the glow window sets this while the Now playing screen is open).</summary>
        public bool ListMode { get; set; }
        /// <summary>All the song's lines, from the engine (lidx indexes them).</summary>
        public SongLyrics? Song { get; set; }
        bool _list;                         // drawing the list right now (ListMode, and Flow)
        readonly Dictionary<int, Stencil> _blocks = new();   // song lines, whole, wrapped to the region's width
        int _blocksFor = int.MinValue;      // ...for this Song.Id
        float _blocksWidth;                 // ...and this width
        int _listIdx = int.MinValue;        // the line in the sung spot (being sung, or a break)
        double _scroll;                     // px the list still has to glide up after a line change (eases to 0)
        double _listAlpha;                  // the list's fade in / out
        bool _drawingList;                  // DrawLine is drawing the sung line inside the list
        float ListGap => _formatPx * 0.55f; // between lines in the list

        /// <summary>Pixels this renderer drew into last frame (null = nothing).</summary>
        public RawRect? Drawn { get; private set; }

        /// <summary>OLED care: nudges everything a few px (set by the glow window, drifts very slowly).</summary>
        public Vector2 Shift { get; set; }

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
        public void Attach(ID2D1DeviceContext ctx, ID2D1Device device)
        {
            ReleaseDeviceStuff();
            _ctx = ctx;
            _device = device;
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
        public bool Active => _alpha > 0.002 || _leaving.Count > 0 || _listAlpha > 0.002;

        /// <summary>How visible the lyrics are right now, 0..1 (fades in and out).</summary>
        public double Visibility => _alpha;

        /// <summary>Night sky's black, 0..1. It comes up with the first sung line, then stays while the
        /// music plays on: through breaks between lines, the outro, the switch to the next song and its
        /// intro, as long as the songs have lyrics (or they're still being looked up). It goes when the
        /// music is paused, or a few seconds into a song without lyrics.</summary>
        public double SongVisibility => _songAlpha;
        double _songAlpha;
        bool _night;     // Night sky is on (before the fade)
        double _lapse;   // seconds without a reason to stay; a little grace for hiccups between songs

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

            bool list = ListMode && style == LyricsStyle.Flow;
            if (list != _list)   // switching between the list and the usual layouts: start afresh
            {
                _list = list;
                FreeBlocks();
                _listIdx = int.MinValue;
                _scroll = 0;
                if (_current != null) { Free(_current); _current = null; }
                foreach (var l in _leaving) Free(l);
                _leaving.Clear();
            }

            bool has = show && s != null && !string.IsNullOrWhiteSpace(s.lyric);
            // the list keeps its line through a pause (the whole list fades instead)
            bool hasLine = list ? s != null && !string.IsNullOrWhiteSpace(s.lyric) : has;
            _alpha += ((has ? 1 : 0) - _alpha) * (1 - Math.Exp(-(has ? 6 : 4) * dt));
            // reasons to stay: this song has lyrics or they're being looked up, or (for --demo, which has
            // no lookups) it's a break after the singing started with another line still to come
            bool keep = s != null && s.playing && (s.lstat is "found" or "searching"
                                                   || s.lidx >= 0 && !string.IsNullOrWhiteSpace(s.next));
            _lapse = keep ? 0 : _lapse + dt;
            _night = has || _night && _lapse < 2.5;
            _songAlpha += ((_night ? 1 : 0) - _songAlpha) * (1 - Math.Exp(-(_night ? 6 : 4) * dt));

            if (hasLine && (_current == null || _current.Index != s!.lidx || _current.Text != s.lyric || _current.Style != style))
            {
                if (_current != null)
                {
                    if (list) Free(_current);   // it carries on as a dim line of the list
                    else { _current.Leaving = 0; _leaving.Add(_current); }
                }
                _current = MakeLine(s!.lyric, s.lidx, style, scale);
                SetNext(list ? "" : s.next, style, scale);
                _device?.ClearResources(0);   // the line's stencils are made: let Direct2D drop its scratch memory
            }
            else if (!hasLine && _current != null && (list || _alpha < 0.01))
            {
                if (list) Free(_current);
                else { _current.Leaving = 0; _leaving.Add(_current); }
                _current = null;
            }
            if (list) UpdateList(s, show, scale, dt);
            else _listAlpha = 0;
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
            // (the Now playing screen's list: a little bigger, like the mockup)
            float px = (float)((_list ? 37 : style == LyricsStyle.Flow ? 34 : 44) * scale);
            if (_format != null && Math.Abs(px - _formatPx) < 0.5f && style == _formatStyle) return;
            FreeBlocks();   // they're made in this font
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
                word.Text = MakeTextStencil(p, _format!, 2, out word.W, out word.H);
                word.Glow = MakeGlowStencil(word.Text, blur);
                if (style != LyricsStyle.Flow) word.Small = MakeTextStencil(p, _settledFormat!, 2, out _, out _);
                line.Words.Add(word);
                at += p.Length + 1;
            }
            if (style == LyricsStyle.Flow) LayoutFlow(line);
            else LayoutConstellation(line, scale);
            return line;
        }

        /// <summary>The text in white on transparent, <paramref name="pad"/> px in from each side.
        /// Stored as alpha only (1 byte a pixel instead of 4) unless this GPU can't draw text that way.</summary>
        Stencil MakeTextStencil(string text, IDWriteTextFormat format, float pad, out float w, out float h, float maxWidth = 4000)
        {
            using var layout = _dw.CreateTextLayout(text, format, maxWidth, 2000);   // wraps if wider than maxWidth
            var m = layout.Metrics;
            float tw = w = m.WidthIncludingTrailingWhitespace, th = h = m.Height;
            void Text(ID2D1DeviceContext ctx) => ctx.DrawTextLayout(new Vector2(pad, pad), layout, _brush!);
            if (_alphaOnly)
            {
                Stencil? st = null;
                try
                {
                    st = NewStencil(tw + pad * 2, th + pad * 2, pad, 1, alphaOnly: true);
                    if (Render(st, Text)) return st;
                }
                catch { }
                st?.Dispose();
                _alphaOnly = false;   // this GPU said no: full color stencils from now on
            }
            var full = NewStencil(tw + pad * 2, th + pad * 2, pad, 1, alphaOnly: false);
            Render(full, Text);
            return full;
        }

        const float GlowRes = 0.5f;   // glow stencils are stored at half size: they're a blur anyway

        /// <summary>A blurred copy of a stencil, with room around it for the blur: its glow.</summary>
        Stencil MakeGlowStencil(Stencil of, float blur)
        {
            float pad = blur * 3, w = of.W - of.Pad * 2 + pad * 2, h = of.H - of.Pad * 2 + pad * 2;
            var st = NewStencil(w, h, pad, GlowRes, alphaOnly: false);
            // the shape, at half size, into a scratch stencil...
            using var small = NewStencil(w, h, pad, GlowRes, alphaOnly: false);
            Render(small, ctx =>
            {
                ctx.Transform = Matrix3x2.CreateScale(GlowRes);
                ctx.AntialiasMode = AntialiasMode.Aliased;   // required for opacity masks
                ctx.FillOpacityMask(of.Bitmap, _brush!, new RawRectF(pad - of.Pad, pad - of.Pad, pad - of.Pad + of.W, pad - of.Pad + of.H), null);
                ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            });
            // ...then blurred into the glow
            _blur!.SetInput(0, small.Bitmap, true);
            _blur.BlurStandardDeviation = blur * GlowRes;
            _blur.Color = new Vector4(1, 1, 1, 1);
            Render(st, ctx => ctx.DrawImage(_blur.Output, Vector2.Zero, null, InterpolationMode.Linear, CompositeMode.SourceOver));
            _blur.SetInput(0, null, true);
            return st;
        }

        /// <summary>A transparent stencil of w x h px (stored at <paramref name="res"/> of that size).</summary>
        Stencil NewStencil(float w, float h, float pad, float res, bool alphaOnly)
        {
            var size = new SizeI(Math.Max(1, (int)Math.Ceiling(w * res)), Math.Max(1, (int)Math.Ceiling(h * res)));
            var format = alphaOnly ? Vortice.DXGI.Format.A8_UNorm : Vortice.DXGI.Format.B8G8R8A8_UNorm;
            var bitmap = _ctx!.CreateBitmap(size, IntPtr.Zero, 0, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(format, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            return new Stencil { Bitmap = bitmap, Pad = pad, W = size.Width / res, H = size.Height / res };
        }

        /// <summary>Clear a stencil and draw into it in white. False if the GPU couldn't.</summary>
        bool Render(Stencil st, Action<ID2D1DeviceContext> draw)
        {
            var ctx = _ctx!;
            ctx.Target = st.Bitmap;
            ctx.BeginDraw();
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(new Color4(0, 0, 0, 0));
            _brush!.Color = new Color4(1, 1, 1, 1);
            draw(ctx);
            ctx.Transform = Matrix3x2.Identity;
            return ctx.EndDraw().Success;
        }

        /// <summary>A soft round glow, 64 px across, for under the stars.</summary>
        Stencil Dot()
        {
            if (_dot != null) return _dot;
            using var core = NewStencil(64, 64, 32, 1, alphaOnly: false);
            Render(core, ctx => ctx.FillEllipse(new Ellipse(new Vector2(32, 32), 8, 8), _brush!));
            _dot = MakeGlowStencil(core, 8);
            return _dot;
        }

        /// <summary>Flow: words in centred rows; the block sits above the small "next line".
        /// In the list: rows from the left edge, as wide as the region (its top is set when drawn).</summary>
        void LayoutFlow(Line line)
        {
            float maxW = (_region.Right - _region.Left) * (_list ? 1 : 0.94f), space = _formatPx * 0.28f, rowH = _formatPx * 1.3f;
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
            line.Rows = rows.Count;
            for (int r = 0; r < rows.Count; r++)
            {
                float cx = _list ? 0 : (regionW - rows[r].width) / 2;
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
            float hop = (float)(104 * scale), pad = (float)(16 * scale);   // roomy: words spread over the area
            RawRectF Box(Vector2 c, Word w, float s) =>   // the star plus its word at size s
                new(c.X - pad, c.Y - w.H * s * 0.6f - pad, c.X + pad + w.W * s + _formatPx * 0.3f, c.Y + w.H * s * 0.6f + pad);

            Vector2 at = new(rw * (0.05f + 0.25f * (float)rng.NextDouble()), rh * (0.15f + 0.7f * (float)rng.NextDouble()));
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
                    float score = Math.Abs(c.Y - rh * 0.5f) * 0.05f;                // only a gentle pull to the middle
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

        /// <summary>The list: which line is in the sung spot, the glide after a line change, the fade,
        /// and the stencils for the lines around it (made here, before drawing).</summary>
        void UpdateList(LightState? s, bool show, double scale, double dt)
        {
            EnsureFormats(LyricsStyle.Flow, scale);
            var song = Song != null && s != null && Song.Id == s.lyr_id ? Song : null;   // not an old song's lines
            bool on = show && song != null && song.Lines.Length > 0;
            _listAlpha += ((on ? 1 : 0) - _listAlpha) * (1 - Math.Exp(-(on ? 6 : 4) * dt));
            if (song == null) return;

            int idx = s!.lidx;
            if (idx != _listIdx)
            {
                // simply the next line: glide up by the one just sung; anything else (a seek, a new song): jump
                _scroll = idx == _listIdx + 1 && _listIdx >= 0 ? BlockHeight(_listIdx) + ListGap : 0;
                _listIdx = idx;
                foreach (var k in new List<int>(_blocks.Keys))
                    if (k < idx - 3 || k > idx + 14) { _blocks[k].Dispose(); _blocks.Remove(k); }
            }
            _scroll *= Math.Exp(-7 * dt);
            for (int k = -2; k <= 10; k++) Block(_listIdx + k);   // the lines that can show, made if they aren't yet
        }

        /// <summary>The list: line <paramref name="i"/> of the song, whole, wrapped to the region's width
        /// (a break shows as ♪). Made on first use, so only call it outside Draw.</summary>
        Stencil? Block(int i)
        {
            var song = Song;
            if (song == null || i < 0 || i >= song.Lines.Length) return null;
            float width = _region.Right - _region.Left;
            if (song.Id != _blocksFor || Math.Abs(width - _blocksWidth) > 0.5f)
            {
                FreeBlocks();
                _blocksFor = song.Id;
                _blocksWidth = width;
            }
            if (_blocks.TryGetValue(i, out var b)) return b;
            string text = string.IsNullOrWhiteSpace(song.Lines[i]) ? "♪" : song.Lines[i];
            return _blocks[i] = MakeTextStencil(text, _format!, 2, out _, out _, width);
        }

        /// <summary>A line's stencil if it's already made (for Draw, which can't make one).</summary>
        Stencil? Made(int i) => _blocks.TryGetValue(i, out var b) ? b : null;

        float BlockHeight(int i) => Block(i) is Stencil b ? b.H - b.Pad * 2 : 0;

        void FreeBlocks()
        {
            foreach (var b in _blocks.Values) b.Dispose();
            _blocks.Clear();
            _blocksFor = int.MinValue;
        }

        void SetNext(string next, LyricsStyle style, double scale)
        {
            _next?.Dispose();
            _next = null;
            if (style != LyricsStyle.Flow || string.IsNullOrEmpty(next)) return;
            EnsureFormats(style, scale);
            _next = MakeTextStencil(next, _small!, 2, out _nextW, out _nextH);
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
            // Constellation / Night sky: the middle 70% of the screen, clear of the corners
            return new RawRectF(l + w * 0.15f, t + h * 0.15f, l + w * 0.85f, t + h * 0.85f);
        }

        /// <summary>Draw into the back buffer <paramref name="target"/>. The caller has already cleared
        /// what was drawn last frame (<see cref="Drawn"/>).</summary>
        public void Draw(ID2D1Bitmap1 target, System.Windows.Media.Color primary, double scale)
        {
            if (!Active) { Drawn = null; return; }
            var ctx = _ctx!;
            var glow = new Color4(primary.R / 255f, primary.G / 255f, primary.B / 255f, 1);
            var sung = new Color4(Mix(1, glow.R, 0.22f), Mix(1, glow.G, 0.22f), Mix(1, glow.B, 0.22f), 1);
            var origin = Matrix3x2.CreateTranslation(_region.Left + Shift.X, _region.Top + Shift.Y);
            if (_glowWipe == null || Math.Abs(_glowWipeColor.R - glow.R) + Math.Abs(_glowWipeColor.G - glow.G) + Math.Abs(_glowWipeColor.B - glow.B) > 0.02f)
            {
                _glowWipe?.Dispose();   // gradient colors are fixed once made, so remake it as the glow color drifts
                _glowWipe = MakeWipe(glow, new Color4(glow.R, glow.G, glow.B, 0));
                _glowWipeColor = glow;
            }

            ctx.Target = target;
            ctx.BeginDraw();
            ctx.AntialiasMode = AntialiasMode.Aliased;   // stencils need it; switched only around the few vector shapes
            _x0 = _y0 = float.MaxValue; _x1 = _y1 = float.MinValue;
            if (_list) DrawList(ctx, sung, glow, scale, origin);
            // glows first, then everything crisp on top, so a glow never covers a neighbouring word
            else for (int pass = 0; pass < 2; pass++)
            {
                foreach (var l in _leaving) DrawLine(ctx, l, pass == 0, sung, glow, scale, origin);
                if (_current != null) DrawLine(ctx, _current, pass == 0, sung, glow, scale, origin);
            }
            if (!_list && _next != null && _current != null && _current.Style == LyricsStyle.Flow)
            {
                float rw = _region.Right - _region.Left, rh = _region.Bottom - _region.Top;
                Paint(ctx, _next, Matrix3x2.CreateTranslation((rw - _nextW) / 2, rh - _nextH - _formatPx * 0.05f) * origin,
                      new Color4(1, 1, 1, (float)(Ease(_current.Age / 0.4) * 0.4 * _alpha)));
            }
            ctx.Transform = Matrix3x2.Identity;
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            ctx.EndDraw();

            // only where something was actually drawn gets wiped and redrawn next frame
            Drawn = _x1 < _x0 ? null : new RawRect((int)Math.Floor(_x0) - 2, (int)Math.Floor(_y0) - 2,
                                                   (int)Math.Ceiling(_x1) + 2, (int)Math.Ceiling(_y1) + 2);
        }

        /// <summary>The list: the line being sung about a fifth of the way down (word by word, as in Flow),
        /// the two before it dimmer above, the ones to come below, fading out towards the bottom.</summary>
        void DrawList(ID2D1DeviceContext ctx, Color4 sung, Color4 glow, double scale, Matrix3x2 origin)
        {
            float a = (float)_listAlpha;
            if (a <= 0.004f) return;
            float rh = _region.Bottom - _region.Top, gap = ListGap, rowH = _formatPx * 1.3f;
            float y = rh * 0.22f + (float)_scroll;
            ctx.Transform = Matrix3x2.Identity;
            ctx.PushAxisAlignedClip(new RawRectF(_region.Left + Shift.X, _region.Top + Shift.Y,
                                                 _region.Right + Shift.X, _region.Bottom + Shift.Y), AntialiasMode.Aliased);
            float Height(Stencil b) => b.H - b.Pad * 2;

            // the line being sung (glows first, then the text), or a break's ♪
            float h0 = 0;
            if (_current != null && _current.Index == _listIdx)
            {
                _current.BlockTop = y;
                _drawingList = true;
                DrawLine(ctx, _current, true, sung, glow, scale, origin);
                DrawLine(ctx, _current, false, sung, glow, scale, origin);
                _drawingList = false;
                h0 = _current.Rows * rowH;
            }
            else if (Made(_listIdx) is Stencil b0)
            {
                Paint(ctx, b0, Matrix3x2.CreateTranslation(0, y) * origin, new Color4(1, 1, 1, 0.5f * a));
                h0 = Height(b0);
            }

            float up = y;   // the lines just sung, fading out by the top (gone before they're cut off)
            for (int k = 1; k <= 2 && Made(_listIdx - k) is Stencil b; k++)
            {
                up -= gap + Height(b);
                float fade = (float)Smooth(up / (rh * 0.12));
                Paint(ctx, b, Matrix3x2.CreateTranslation(0, up) * origin, new Color4(1, 1, 1, (k == 1 ? 0.3f : 0.15f) * fade * a));
            }
            float down = y + h0;   // the lines to come, fading out by the bottom of the area (gone as they touch it)
            for (int k = 1; down < rh && Made(_listIdx + k) is Stencil b; k++)
            {
                down += gap;
                float fade = (float)Smooth((rh - down - Height(b)) / (rh * 0.3));
                Paint(ctx, b, Matrix3x2.CreateTranslation(0, down) * origin,
                      new Color4(1, 1, 1, Math.Max(0.14f, 0.42f - 0.07f * (k - 1)) * fade * a));
                down += Height(b);
            }
            ctx.Transform = Matrix3x2.Identity;
            ctx.PopAxisAlignedClip();
        }

        /// <summary>Note that <paramref name="r"/> (in the coordinates of <paramref name="t"/>) is being drawn.</summary>
        void Grow(RawRectF r, Matrix3x2 t)
        {
            Corner(new Vector2(r.Left, r.Top), t); Corner(new Vector2(r.Right, r.Top), t);
            Corner(new Vector2(r.Left, r.Bottom), t); Corner(new Vector2(r.Right, r.Bottom), t);
        }

        void Corner(Vector2 p, Matrix3x2 t)
        {
            var q = Vector2.Transform(p, t);
            _x0 = Math.Min(_x0, q.X); _y0 = Math.Min(_y0, q.Y);
            _x1 = Math.Max(_x1, q.X); _y1 = Math.Max(_y1, q.Y);
        }

        /// <summary>Draw a stencil tinted <paramref name="color"/>, its content's top-left at the origin of <paramref name="t"/>.</summary>
        void Paint(ID2D1DeviceContext ctx, Stencil st, Matrix3x2 t, Color4 color)
        {
            if (color.A <= 0.004f) return;
            var dest = new RawRectF(-st.Pad, -st.Pad, st.W - st.Pad, st.H - st.Pad);
            ctx.Transform = t;
            _brush!.Color = color;
            ctx.FillOpacityMask(st.Bitmap, _brush, dest, null);
            Grow(dest, t);
        }

        /// <summary>Draw a stencil through a wipe gradient whose soft edge is at <paramref name="x"/> (in the
        /// stencil's own coordinates): brighter to the left, dimmer to the right.</summary>
        void PaintWipe(ID2D1DeviceContext ctx, Stencil st, Matrix3x2 t, ID2D1LinearGradientBrush wipe, float x, float feather, float alpha)
        {
            if (alpha <= 0.004f) return;
            var dest = new RawRectF(-st.Pad, -st.Pad, st.W - st.Pad, st.H - st.Pad);
            ctx.Transform = t;
            wipe.StartPoint = new Vector2(x - feather, 0);
            wipe.EndPoint = new Vector2(x + feather, 0);
            wipe.Opacity = alpha;
            ctx.FillOpacityMask(st.Bitmap, wipe, dest, null);
            Grow(dest, t);
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
                // (in the list it doesn't slide or fade in: it was already there, dim, and the list glides)
                float dy = _drawingList ? 0 : (float)((1 - inT) * rowH * 0.6 - outT * rowH * 0.8);
                float lineAlpha = _drawingList ? (float)_listAlpha : (float)(Ease(l.Age / 0.35) * _alpha);

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

            // Constellation / Night sky: work out each shown word's look once...
            float leaveAlpha = l.Leaving >= 0 ? (float)(1 - Ease(l.Leaving / 0.9)) : 1;
            float star = _formatPx * 0.11f;
            _shown.Clear();
            foreach (var w in l.Words)
            {
                double since = (sungFrac - w.Start) * l.SingFor;           // seconds since this word's turn came
                if (since < 0) break;                                       // not yet: words appear in order
                double appear = Ease(since / 0.25);
                double after = (sungFrac - (w.Start + w.Length)) * l.SingFor; // seconds since it was sung
                bool active = after < 0;
                double shrink = active ? 0 : Ease(after / 0.5);
                // pops in at half size, is full size while sung, then settles small
                float scaleW = active ? Mix(0.5f, 1, (float)appear) : Mix(1, Settled, (float)shrink);
                float a = (float)(appear * Mix(1, 0.7f, (float)shrink) * leaveAlpha * _alpha);
                float starR = star * (active ? Mix(1, 2.2f, (float)appear) : Mix(2.2f, 1, (float)shrink));
                _shown.Add((w, w.Pos, starR, scaleW, a, active));
            }

            // ...then draw it: glows; or lines, stars and rings together, then all the words
            if (glowPass)
            {
                var dot = Dot();
                foreach (var (w, at, r, s, a, active) in _shown)
                {
                    Paint(ctx, dot, Matrix3x2.CreateScale(r / 8 * 1.3f) * Matrix3x2.CreateTranslation(at) * origin,   // dot core: 8 px
                          new Color4(glow.R, glow.G, glow.B, a));
                    Paint(ctx, w.Glow, Matrix3x2.CreateTranslation(0, -w.H * 0.5f) * Matrix3x2.CreateScale(s) * WordAt(at, r, star, origin),
                          new Color4(glow.R, glow.G, glow.B, (active ? 1.4f : 0.5f) * a));
                }
                return;
            }

            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            float lineW = (float)Math.Max(1, 1.2 * scale), ringW = (float)Math.Max(1, 1.3 * scale);
            for (int i = 0; i < _shown.Count; i++)
            {
                var (_, at, r, _, a, active) = _shown[i];
                if (i > 0)   // a thin line back to the previous star
                {
                    var pv = _shown[i - 1].star;
                    ctx.Transform = origin;
                    _brush!.Color = new Color4(1, 1, 1, 0.35f * Math.Min(a, _shown[i - 1].a));
                    ctx.DrawLine(pv, at, _brush, lineW);
                    Grow(new RawRectF(Math.Min(pv.X, at.X) - lineW, Math.Min(pv.Y, at.Y) - lineW,
                                      Math.Max(pv.X, at.X) + lineW, Math.Max(pv.Y, at.Y) + lineW), origin);
                }
                ctx.Transform = Matrix3x2.CreateScale(r) * Matrix3x2.CreateTranslation(at) * origin;
                _brush!.Color = new Color4(glow.R, glow.G, glow.B, a);
                ctx.FillGeometry(_star, _brush);
                float ring = active ? r * 2 + ringW : r;   // a ring around the star of the word being sung
                if (active)
                {
                    ctx.Transform = origin;
                    _brush.Color = new Color4(glow.R, glow.G, glow.B, a * 0.6f);
                    ctx.DrawEllipse(new Ellipse(at, r * 2, r * 2), _brush, ringW);
                }
                Grow(new RawRectF(at.X - ring, at.Y - ring, at.X + ring, at.Y + ring), origin);
            }
            ctx.AntialiasMode = AntialiasMode.Aliased;

            foreach (var (w, at, r, s, a, active) in _shown)
            {
                // big while sung, then settles small and dim (using the small copy once it's close to that size)
                var color = active ? new Color4(1, 1, 1, a) : new Color4(sung.R, sung.G, sung.B, a);
                var t = WordAt(at, r, star, origin);
                if (w.Small != null && s < (1 + Settled) / 2)
                    Paint(ctx, w.Small, Matrix3x2.CreateTranslation(0, -w.H * Settled * 0.5f) * Matrix3x2.CreateScale(s / Settled) * t, color);
                else
                    Paint(ctx, w.Text, Matrix3x2.CreateTranslation(0, -w.H * 0.5f) * Matrix3x2.CreateScale(s) * t, color);
            }
        }

        /// <summary>Where a constellation word goes: just right of its star, vertically centred on it.</summary>
        static Matrix3x2 WordAt(Vector2 star, float starR, float starSize, Matrix3x2 origin) =>
            Matrix3x2.CreateTranslation(star.X + starSize * 2.4f + starR, star.Y) * origin;

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
            _next?.Dispose(); _next = null;
            FreeBlocks();
            _dot?.Dispose(); _dot = null;
            _wipe?.Dispose(); _wipe = null;
            _glowWipe?.Dispose(); _glowWipe = null;
            _blur?.Dispose(); _blur = null;
            _brush?.Dispose(); _brush = null;
        }

        public void Dispose()
        {
            ReleaseDeviceStuff();
            _format?.Dispose(); _settledFormat?.Dispose(); _small?.Dispose();
            _star.Dispose();
            _dw.Dispose();
        }
    }
}
