"""Pick two glow-friendly colors from album art."""
import colorsys
import io

import numpy as np
from PIL import Image

DEFAULT = ["#7c5cff", "#ff4fa3"]


def _to_hex(rgb):
    return "#%02x%02x%02x" % tuple(int(max(0, min(255, c))) for c in rgb)


def _glowify(rgb):
    """Push a color toward something that glows nicely: saturated and bright."""
    h, l, s = colorsys.rgb_to_hls(*(c / 255.0 for c in rgb))
    s = min(1.0, max(s, 0.55) * 1.15)
    l = min(0.68, max(l, 0.5))
    return tuple(c * 255 for c in colorsys.hls_to_rgb(h, l, s))


def _hue_dist(a, b):
    d = abs(a - b)
    return min(d, 1.0 - d)


def dominant_colors(image_bytes, count=2):
    """Return `count` hex colors, or DEFAULT if the image is unusable."""
    try:
        img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    except Exception:
        return list(DEFAULT)

    img = img.resize((64, 64))
    q = img.quantize(colors=10, method=Image.Quantize.MEDIANCUT)
    palette = np.array(q.getpalette()[:30], dtype=np.float64).reshape(-1, 3)
    counts = np.bincount(np.asarray(q).ravel(), minlength=len(palette))

    candidates = []
    for rgb, n in zip(palette, counts):
        if n == 0:
            continue
        h, l, s = colorsys.rgb_to_hls(*(rgb / 255.0))
        # vivid colors win, but very common colors still count
        score = n * (0.15 + s) * (1.0 - abs(l - 0.5))
        candidates.append((score, h, s, l, tuple(rgb)))
    candidates.sort(reverse=True)

    chosen = []
    for score, h, s, l, rgb in candidates:
        if s < 0.12 or l < 0.08 or l > 0.95:
            continue   # skip greys / near-black / near-white
        if all(_hue_dist(h, ch) > 0.06 for ch, _ in chosen):
            chosen.append((h, rgb))
        if len(chosen) == count:
            break

    if not chosen:            # greyscale cover: tint with defaults
        return list(DEFAULT)
    out = [_to_hex(_glowify(rgb)) for _, rgb in chosen]
    while len(out) < count:   # single-hue cover: shift hue for the 2nd color
        h, l, s = colorsys.rgb_to_hls(*(int(out[0][i:i + 2], 16) / 255 for i in (1, 3, 5)))
        out.append(_to_hex(c * 255 for c in colorsys.hls_to_rgb((h + 0.08) % 1, l, s)))
    return out
