"""Samples the screen a few times a second so the glow can follow the colors of a video.

Uses plain GDI: one StretchBlt of the screen into a tiny bitmap. The overlay hides its own
windows from screen capture, so the glow never sees (and feeds back on) itself.
"""
import ctypes
import sys
import threading
import time

import numpy as np

from colors import keep_order, pick_colors

if sys.platform == "win32":
    from ctypes import wintypes

    _user32, _gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
    _H = ctypes.c_void_p
    _user32.GetDC.restype = _H
    _user32.GetDC.argtypes = [_H]
    _user32.ReleaseDC.argtypes = [_H, _H]
    _gdi32.CreateCompatibleDC.restype = _H
    _gdi32.CreateCompatibleDC.argtypes = [_H]
    _gdi32.CreateCompatibleBitmap.restype = _H
    _gdi32.CreateCompatibleBitmap.argtypes = [_H, ctypes.c_int, ctypes.c_int]
    _gdi32.SelectObject.restype = _H
    _gdi32.SelectObject.argtypes = [_H, _H]
    _gdi32.DeleteObject.argtypes = [_H]
    _gdi32.DeleteDC.argtypes = [_H]
    _gdi32.SetStretchBltMode.argtypes = [_H, ctypes.c_int]
    _gdi32.StretchBlt.argtypes = [_H] + [ctypes.c_int] * 4 + [_H] + [ctypes.c_int] * 4 + [wintypes.DWORD]
    _gdi32.GetDIBits.argtypes = [_H, _H, ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]

    class _BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                    ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD),
                    ("biCompression", wintypes.DWORD), ("biSizeImage", wintypes.DWORD),
                    ("biXPelsPerMeter", ctypes.c_long), ("biYPelsPerMeter", ctypes.c_long),
                    ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD)]


def grab(width=64, height=40, margin=0.1):
    """The primary screen shrunk to width x height RGB pixels, skipping `margin` of each side
    (where the glow lives and where taskbars / menus sit)."""
    sw, sh = _user32.GetSystemMetrics(0), _user32.GetSystemMetrics(1)
    x0, y0 = int(sw * margin), int(sh * margin)
    screen = _user32.GetDC(None)
    mem = _gdi32.CreateCompatibleDC(screen)
    bmp = _gdi32.CreateCompatibleBitmap(screen, width, height)
    old = _gdi32.SelectObject(mem, bmp)
    try:
        _gdi32.SetStretchBltMode(mem, 3)    # COLORONCOLOR: fast, plenty for picking colors
        _gdi32.StretchBlt(mem, 0, 0, width, height, screen, x0, y0, sw - 2 * x0, sh - 2 * y0, 0x00CC0020)
        info = _BITMAPINFOHEADER(ctypes.sizeof(_BITMAPINFOHEADER), width, -height, 1, 32, 0, 0, 0, 0, 0, 0)
        buf = (ctypes.c_ubyte * (width * height * 4))()
        _gdi32.GetDIBits(mem, bmp, 0, height, buf, ctypes.byref(info), 0)
    finally:
        _gdi32.SelectObject(mem, old)
        _gdi32.DeleteObject(bmp)
        _gdi32.DeleteDC(mem)
        _user32.ReleaseDC(None, screen)
    return np.frombuffer(buf, dtype=np.uint8).reshape(height, width, 4)[..., 2::-1]   # BGRA -> RGB


class ScreenSampler:
    """Background thread that keeps `colors` up to date while `active` is True."""

    def __init__(self, rate=6.0):
        self.rate = rate
        self.active = False
        self.colors = None          # latest two hex colors, or None before the first useful frame
        if sys.platform == "win32":
            # real pixels on high-DPI screens (this process has no windows, so this is safe)
            try:
                _user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
            except Exception:
                pass
            threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            if not self.active:
                time.sleep(0.2)
                continue
            start = time.monotonic()
            try:
                found = pick_colors(grab())
                if found:           # dark or colorless frame: keep the last colors
                    self.colors = keep_order(found, self.colors)
            except Exception:
                pass
            time.sleep(max(0.0, 1.0 / self.rate - (time.monotonic() - start)))
