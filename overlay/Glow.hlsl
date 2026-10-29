// The whole glow in one full-screen pass. Every pixel works out how far it is from the
// screen's (rounded) edge and where along that edge it sits, then lights itself.
// Values come from GlowController each frame (see GlowWindow.Constants).

cbuffer Glow : register(b0)
{
    float2 Size;       // screen size, px
    float  Radius;     // corner radius, px
    float  Rim;        // bright rim thickness, px
    float  Spread;     // how far the glow reaches inward, px
    float  Opacity;    // overall intensity 0..1
    float  Time;       // drives the wavy motion
    float  Phase;      // turns the color gradient around the screen, 0..1
    float4 Primary;
    float4 Secondary;
    float  Base;       // brightness everywhere along the edge
    float  Wave;       // how wavy the glow's inner edge is
    float  Pulse;      // beat flash 0..1
    float  White;      // how white-hot the rim's core gets
    float4 Lights[6];  // lights moving along the edge: x = position 0..1, y = width, z = strength
    float  Backdrop;   // 1 = draw on black instead of see-through (LumnWin.exe --black, for tuning)
};

static const float TAU = 6.28318530718;

float sq(float x) { return x * x; }

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

// Ripples flowing along the edge, roughly -1..1. Wavelengths are about 1000 / 600 / 350 px, rounded
// to a whole number per lap so they loop seamlessly; two flow one way and one the other, so they
// keep changing shape instead of just sliding.
float Waves(float u, float t, float seed)
{
    float L = 2 * (Size.x + Size.y);
    float n1 = max(3, round(L / 1000)), n2 = max(5, round(L / 600)), n3 = max(8, round(L / 350));
    return 0.50 * sin(TAU * n1 * u - 0.9 * t + seed)
         + 0.32 * sin(TAU * n2 * u + 1.4 * t + 1.7 + seed)
         + 0.18 * sin(TAU * n3 * u - 2.3 * t + 4.1 + seed);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    // distance to each straight edge: top, right, bottom, left
    float4 de = float4(pos.y, Size.x - pos.x, Size.y - pos.y, pos.x);
    float reach = Spread * (1 + Wave) * 2.2 + Rim * 3;
    if (min(min(de.x, de.y), min(de.z, de.w)) > reach) return float4(0, 0, 0, Backdrop);   // the middle: nothing to draw

    // d follows the real (slightly) rounded corners for the crisp rim. u, the position along the
    // edge, comes from a much rounder shape so colors and lights flow smoothly past the corners
    // instead of meeting along a diagonal seam.
    float d, u, ignore;
    EdgeCoords(pos.xy, Radius, d, ignore);
    EdgeCoords(pos.xy, reach, ignore, u);

    float spread = Spread * max(0.15, 1 + Wave * Waves(u, Time, 0));   // the glow's inner edge rolls like waves

    // brightness along the edge: a base level plus the moving lights
    float along = Base;
    [unroll] for (int i = 0; i < 6; i++)
    {
        float du = abs(frac(u - Lights[i].x + 0.5) - 0.5);
        along += Lights[i].z * exp(-sq(du / Lights[i].y));
    }
    along *= saturate(1 + 0.7 * Wave * Waves(u, Time * 1.3, 2.0));   // bright ripples travel along the rim
    along = clamp(along, 0, 1.6);

    // brightness across the edge: a hot thin rim, a soft bloom, a wide faint haze.
    // Bloom and haze add up the light from each edge (like real light) so corners blend
    // smoothly; 1 - (1-a)(1-b)... combines them without going over 1.
    float core = d >= 0 ? exp(-sq(d / Rim)) : exp(-sq(d / (Rim * 0.6 + 1)));
    if (Radius < 1)     // square corners: blend both edges' rims so the corner has no diagonal crease
    {
        float4 ec = 1 - exp(-sq(de / Rim));
        core = 1 - ec.x * ec.y * ec.z * ec.w;
    }
    float4 eb = 1 - exp(-de / (spread * 0.2 + 1));
    float4 eh = 1 - exp(-sq(de / (spread + 1)));
    float bloom = 1 - eb.x * eb.y * eb.z * eb.w;
    float haze = 1 - eh.x * eh.y * eh.z * eh.w;
    float inside = d >= 0 ? 1 : exp(-sq(d / (Rim * 0.8 + 1)));     // past a rounded corner only the rim spills
    float glow = core + inside * (0.75 * bloom + 0.22 * haze);

    // two-color gradient that slowly travels around the screen (primary covers a bit more)
    float g = 0.5 + 0.5 * cos(TAU * (u - Phase)) + 0.15 * sin(TAU * (2 * u + Phase * 1.7) + 1.3);
    float3 col = lerp(Primary.rgb, Secondary.rgb, smoothstep(0.42, 0.88, g));
    col = lerp(col, 1, saturate(White * core * along * 0.4));   // just a hint of white-hot in the rim

    float I = glow * along * Opacity * (1 + 0.25 * Pulse);
    float alpha = saturate(I);
    float3 rgb = col * saturate(I * 1.4);
    // a little noise hides banding in the long dark gradient
    float n = frac(sin(dot(pos.xy, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    rgb = saturate(rgb + n / 255 * step(0.004, alpha));
    // premultiplied, with alpha a bit below the color so the light adds to what's underneath
    return float4(rgb, Backdrop > 0 ? 1 : alpha * 0.85);
}
