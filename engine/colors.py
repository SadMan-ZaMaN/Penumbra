"""Pick two glow colors from album art or from a frame of the screen.

The primary color is the one that covers the most of the image (vivid colors count a bit extra,
greys / black / white don't count). The secondary is the next biggest color that's clearly different.
"""
import colorsys
import io

import numpy as np
from PIL import Image

DEFAULT = ["#7c5cff", "#ff4fa3"]
NEUTRAL = ["#f3efff", "#b9a8ff"]     # black-and-white covers: a soft white glow
HUE_BINS = 36                        # 10 degrees each


def _to_hex(rgb):
    return "#%02x%02x%02x" % tuple(int(round(max(0, min(255, c)))) for c in rgb)


def _from_hex(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def _glowify(rgb):
    """Keep the hue, but make it a solid, fully bright color (no pastel / washed-out look)."""
    h, s, v = colorsys.rgb_to_hsv(*(c / 255.0 for c in rgb))
    return tuple(c * 255 for c in colorsys.hsv_to_rgb(h, max(s, 0.8), 1.0))


def _hue_chroma(px):
    """px: (n, 3) floats 0..1 -> hue 0..1, chroma (how colorful), value (how bright)."""
    mx, mn = px.max(axis=1), px.min(axis=1)
    c = mx - mn
    r, g, b = px[:, 0], px[:, 1], px[:, 2]
    safe = np.where(c > 1e-6, c, 1.0)
    h = np.where(mx == r, ((g - b) / safe) % 6,
                 np.where(mx == g, (b - r) / safe + 2, (r - g) / safe + 4)) / 6.0
    return h % 1.0, c, mx


def pick_colors(pixels, count=2):
    """pixels: (..., 3) uint8 RGB. Returns `count` hex colors, biggest first,
    or None if the image has no real color in it (greyscale or very dark)."""
    px = np.asarray(pixels, dtype=np.float64).reshape(-1, 3) / 255.0
    hue, chroma, value = _hue_chroma(px)
    use = (chroma > 0.12) & (value > 0.18)
    if use.mean() < 0.008:      # even a small accent on a black cover counts (yellow text on black -> yellow)
        return None

    hue, chroma, px = hue[use], chroma[use], px[use]
    weight = 0.45 + 0.55 * np.clip(chroma / 0.6, 0, 1)   # mostly "how much", a bit "how vivid"
    bins = (hue * HUE_BINS).astype(int) % HUE_BINS
    hist = np.bincount(bins, weights=weight, minlength=HUE_BINS)
    hist = hist + 0.5 * (np.roll(hist, 1) + np.roll(hist, -1))   # neighbouring hues belong together

    chosen = []
    blocked = np.zeros(HUE_BINS, dtype=bool)
    top = None
    for _ in range(count):
        k = int(np.argmax(np.where(blocked, -1, hist)))
        if blocked[k] or (top is not None and hist[k] < 0.15 * top):
            break                       # nothing else big enough
        top = hist[k] if top is None else top
        near = np.abs(((bins - k + HUE_BINS // 2) % HUE_BINS) - HUE_BINS // 2) <= 1
        rgb = (px[near] * weight[near, None]).sum(axis=0) / weight[near].sum() * 255
        chosen.append(_glowify(rgb))
        for j in range(-4, 5):          # the next color must be at least ~40 degrees away
            blocked[(k + j) % HUE_BINS] = True

    while len(chosen) < count:          # single-hue image: a neighbouring shade of the same hue
        h, s, v = colorsys.rgb_to_hsv(*(c / 255.0 for c in chosen[0]))
        chosen.append(tuple(c * 255 for c in colorsys.hsv_to_rgb((h + 0.07) % 1, s * 0.85, v)))
    return [_to_hex(c) for c in chosen]


def dominant_colors(image_bytes, count=2):
    """Colors for album art / a video thumbnail (encoded image bytes)."""
    try:
        img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    except Exception:
        return list(DEFAULT)
    img.thumbnail((64, 64))
    colors = pick_colors(np.asarray(img), count)
    return colors if colors else list(NEUTRAL)


def keep_order(new, old):
    """Swap new's two colors if that lines them up better with the old ones, so the glow doesn't
    flip its colors around when two similar-sized colors trade places on screen."""
    if not old or len(new) != 2 or len(old) != 2:
        return new
    n, o = [_from_hex(c) for c in new], [_from_hex(c) for c in old]
    dist = lambda a, b: sum((x - y) ** 2 for x, y in zip(a, b))
    if dist(n[0], o[1]) + dist(n[1], o[0]) < dist(n[0], o[0]) + dist(n[1], o[1]):
        return [new[1], new[0]]
    return new
