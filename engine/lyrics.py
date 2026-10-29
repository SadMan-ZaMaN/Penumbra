"""Synced lyrics from LRCLIB (free, no API key): https://lrclib.net"""
import bisect
import json
import re
import threading
import urllib.parse
import urllib.request

API = "https://lrclib.net/api"
HEADERS = {"User-Agent": "LumnWin/0.1 (personal ambient light project)"}
_TAG = re.compile(r"\[(\d+):(\d+(?:\.\d+)?)\]")


def parse_lrc(text):
    """'[01:02.50]line' -> sorted [(62.5, 'line'), ...]. Handles multiple tags per line."""
    out = []
    for raw in (text or "").splitlines():
        tags = _TAG.findall(raw)
        if not tags:
            continue
        line = _TAG.sub("", raw).strip()
        for m, s in tags:
            out.append((int(m) * 60 + float(s), line))
    out.sort(key=lambda x: x[0])
    return out


def _clean(title):
    # "Song (Official Video)" / "Song - Remastered 2011" -> "Song"
    title = re.sub(r"\s*[\(\[].*?(official|video|audio|lyrics?|remaster|visualizer).*?[\)\]]", "",
                   title, flags=re.I)
    title = re.sub(r"\s+-\s+.*remaster.*$", "", title, flags=re.I)
    return title.strip()


def _get(path, params):
    url = f"{API}/{path}?{urllib.parse.urlencode(params)}"
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=8) as r:
        return json.loads(r.read().decode("utf-8"))


def fetch(artist, title, album="", duration=None):
    """Return parsed synced lyrics or [] (never raises)."""
    title = _clean(title)
    try:
        if duration:
            try:
                d = _get("get", {"artist_name": artist, "track_name": title,
                                 "album_name": album, "duration": int(round(duration))})
                if d.get("syncedLyrics"):
                    return parse_lrc(d["syncedLyrics"])
            except Exception:
                pass
        results = _get("search", {"artist_name": artist, "track_name": title})
        for d in results:
            if d.get("syncedLyrics"):
                if duration and d.get("duration") and abs(d["duration"] - duration) > 6:
                    continue
                return parse_lrc(d["syncedLyrics"])
    except Exception:
        pass
    return []


class Lyrics:
    """Caches lyrics per track and fetches them in the background."""

    def __init__(self):
        self._lock = threading.Lock()
        self._key = None
        self._lines = []
        self._times = []

    def set_track(self, artist, title, album="", duration=None):
        key = (artist, title)
        with self._lock:
            if key == self._key:
                return
            self._key, self._lines, self._times = key, [], []
        if not title:
            return

        def work():
            lines = fetch(artist, title, album, duration)
            with self._lock:
                if self._key == key:
                    self._lines = lines
                    self._times = [t for t, _ in lines]
        threading.Thread(target=work, daemon=True).start()

    def at(self, position, offset=0.0):
        """(current_line, next_line, progress 0..1 through current line) at `position` seconds."""
        with self._lock:
            if not self._lines:
                return "", "", 0.0
            p = position + offset
            i = bisect.bisect_right(self._times, p) - 1
            if i < 0:
                return "", self._lines[0][1], 0.0
            cur_t, cur = self._lines[i]
            if i + 1 < len(self._lines):
                nxt_t, nxt = self._lines[i + 1]
                prog = (p - cur_t) / max(0.1, nxt_t - cur_t)
            else:
                nxt, prog = "", 0.0
            return cur, nxt, max(0.0, min(1.0, prog))
