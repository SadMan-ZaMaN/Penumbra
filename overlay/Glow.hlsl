// The whole glow in one full-screen pass. Every pixel works out how far it is from the
// screen's edge and where along that edge it sits, then lights itself.
// Values come from GlowController each frame (see GlowWindow.Constants).
//
// The look: a thin, solid line of saturated color hugging the edge, with a small soft halo.
// It isn't lit all the way round: "streams" of light grow, travel along the edge and fade,
// and the line swells a little where a stream is strong, rippling as it flows.

cbuffer Glow : register(b0)
{
    float2 Size;        // screen size, px
    float  Radius;      // corner radius, px
    float  Rim;         // line thickness, px
    float  Halo;        // size of the soft halo inside the line, px
    float  Opacity;     // overall intensity 0..1
    float  Time;        // drives the ripples
    float  Phase;       // turns the color gradient around the screen, 0..1
    float4 Primary;
    float4 Secondary;
    float  Base;        // how lit the edge is between streams (0 = dark)
    float  Wave;        // how much the line's thickness ripples
    float  Pulse;       // beat flash 0..1
    float  White;       // a touch of white in the middle of the line
    float4 Streams[8];  // x = centre along the edge 0..1, y = half length 0..0.5, z = brightness
    float  Backdrop;    // 1 = draw on black instead of see-through (LumnWin.exe --black, for tuning)
};

static const float TAU = 6.28318530718;

// one triangle that covers the screen, no vertex buffer needed
float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Coordinates relative to the edge of a rectangle with corners rounded by `radius`:
// d: px from the edge, positive inside the screen
// u: position along the edge by arc length, 0..1 clockwise from the top centre
void EdgeCoords(float2 p, float radius, out float d, out float u)
{
    float2 h = Size * 0.5;
    float r = clamp(radius, 0.5, min(h.x, h.y) - 1);
    float a = h.x - r, b = h.y - r;            // half-lengths of the straight parts
    float L = 4 * a + 4 * b + TAU * r;          // full perimeter
    float2 q = p - h;
    float2 aq = abs(q);
    float s;
    if (aq.x > a && aq.y > b)                   // a corner arc
    {
        float2 v = q - float2(sign(q.x) * a, sign(q.y) * b);
        d = r - length(v);
        float ang = atan2(v.x, -v.y);           // 0 = up, clockwise (y points down)
        if (ang < 0) ang += TAU;
        float start = q.x > 0 ? (q.y < 0 ? a : a + 2 * b)
                              : (q.y > 0 ? 3 * a + 2 * b : 3 * a + 4 * b);
        s = start + r * ang;
    }
    else
    {
        float dTB = aq.x <= a ? h.y - aq.y : 1e9;   // nearest top / bottom
        float dLR = aq.y <= b ? h.x - aq.x : 1e9;   // nearest left / right
        if (dTB <= dLR)
        {
            d = dTB;
            s = q.y < 0 ? (q.x >= 0 ? q.x : L + q.x)
                        : a + 2 * b + 0.5 * TAU * r + (a - q.x);
        }
        else
        {
            d = dLR;
            s = q.x > 0 ? a + 0.25 * TAU * r + (q.y + b)
                        : 3 * a + 2 * b + 0.75 * TAU * r + (b - q.y);
        }
    }
    u = s / L;
}

// Ripples flowing along the edge, roughly -1..1. Wavelengths are about 700 / 400 / 230 px, rounded
// to a whole number per lap so they loop seamlessly; two flow one way and one the other, so they
// keep changing shape instead of just sliding.
float Waves(float u, float t, float seed)
{
    float L = 2 * (Size.x + Size.y);
    float n1 = max(4, round(L / 700)), n2 = max(7, round(L / 400)), n3 = max(11, round(L / 230));
    return 0.50 * sin(TAU * n1 * u - 1.3 * t + seed)
         + 0.32 * sin(TAU * n2 * u + 1.9 * t + 1.7 + seed)
         + 0.18 * sin(TAU * n3 * u - 2.9 * t + 4.1 + seed);
}

// blend two colors without the muddy, darker middle a plain blend gives
float3 MixColors(float3 a, float3 b, float t)
{
    float3 c = lerp(a, b, t);
    float want = lerp(max(a.r, max(a.g, a.b)), max(b.r, max(b.g, b.b)), t);
    return c * want / max(max(c.r, max(c.g, c.b)), 1e-3);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    // distance to each straight edge: top, right, bottom, left
    float4 de = float4(pos.y, Size.x - pos.x, Size.y - pos.y, pos.x);
    float reach = Rim * 2.5 + Halo * 5 + 4;
    if (min(min(de.x, de.y), min(de.z, de.w)) > reach) return float4(0, 0, 0, Backdrop);   // the middle: nothing to draw

    // d follows the real corners for the line. u, the position along the edge, comes from a much
    // rounder shape so colors and streams flow smoothly past the corners.
    float d, u, ignore;
    EdgeCoords(pos.xy, Radius, d, ignore);
    EdgeCoords(pos.xy, reach * 2, ignore, u);

    // how lit this part of the edge is: the streams passing by (flat in the middle, tapered ends)
    float lit = Base;
    [unroll] for (int i = 0; i < 8; i++)
    {
        float du = abs(frac(u - Streams[i].x + 0.5) - 0.5) / max(Streams[i].y, 1e-4);
        lit += Streams[i].z * (1 - smoothstep(0.25, 1.0, du));
    }
    lit = saturate(lit);
    if (lit < 0.003) return float4(0, 0, 0, Backdrop);

    // the line swells where a stream is strong and ripples as it flows
    float ripple = max(0.25, 1 + Wave * Waves(u, Time, 0));
    float width = Rim * (0.35 + 0.75 * lit) * ripple * (1 + 0.3 * Pulse);
    float halo = Halo * (0.5 + 0.5 * lit) * max(0.4, 1 + 0.6 * Wave * Waves(u, Time * 0.8, 1.3));

    // a solid line with a soft 1.5 px edge...
    float stroke;
    if (d >= 0) stroke = 1 - smoothstep(width - 1.5, width + 1.5, d);
    else        stroke = 1 - smoothstep(-1.5, 1.5, -d);          // outside a rounded corner: nothing
    // ...and a small halo inside it. Square corners combine both edges' halos so the corner blends.
    float glow;
    if (Radius < 1)
    {
        float4 e = 1 - exp(-max(de - width, 0) / max(halo, 1));
        glow = 1 - e.x * e.y * e.z * e.w;
    }
    else glow = d >= 0 ? exp(-max(d - width, 0) / max(halo, 1)) : 0;
    float I = saturate(stroke + 0.55 * glow * (1 - stroke)) * lit * Opacity;

    // two-color gradient that slowly travels around the screen (primary covers a bit more)
    float g = 0.5 + 0.5 * cos(TAU * (u - Phase)) + 0.15 * sin(TAU * (2 * u + Phase * 1.7) + 1.3);
    float3 col = MixColors(Primary.rgb, Secondary.rgb, smoothstep(0.42, 0.88, g));
    col = lerp(col, 1, saturate(White * stroke * lit * 0.3));

    // premultiplied and solid: the line covers what's under it with pure color
    return float4(col * I, Backdrop > 0 ? 1 : I);
}
