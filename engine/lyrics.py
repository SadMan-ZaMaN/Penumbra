"""Synced lyrics from LRCLIB (free, no API key): https://lrclib.net"""
import bisect
import json
import re
import threading
import time
import urllib.parse
import urllib.request

API = "https://lrclib.net/api"
HEADERS = {"User-Agent": "Penumbra/0.1 (personal ambient light project)"}
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


def _bare(title):
    # "Lose My Mind [From F1® The Movie]" / "Song (feat. Someone)" -> "Song"
    return re.sub(r"\s*[\(\[].*?[\)\]]", "", _clean(title)).strip()


def _artist(artist):
    # YouTube channel names: "Don Toliver - Topic" / "PostMaloneVEVO" -> "Don Toliver" / "PostMalone"
    a = re.sub(r"\s+-\s+Topic$", "", artist or "", flags=re.I)
    return re.sub(r"VEVO$", "", a).strip()


def _get(path, params):
    url = f"{API}/{path}?{urllib.parse.urlencode(params)}"
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=8) as r:
        return json.loads(r.read().decode("utf-8"))


def fetch(artist, title, album="", duration=None):
    """Return parsed synced lyrics or [] (never raises). Tries the exact track first, then searches
    with the title as given, then without any bracketed extras, then title + artist as free text."""
    artist = _artist(artist)
    if duration:
        try:
            d = _get("get", {"artist_name": artist, "track_name": _clean(title),
                             "album_name": album, "duration": int(round(duration))})
            if d.get("syncedLyrics"):
                return parse_lrc(d["syncedLyrics"])
        except Exception:
            pass
    searches = [{"artist_name": artist, "track_name": t} for t in dict.fromkeys([_clean(title), _bare(title)])]
    searches.append({"q": f"{_bare(title)} {artist}"})
    for params in searches:
        try:
            for d in _get("search", params):
                if d.get("syncedLyrics"):
                    # a different length usually means a different version (intro, radio edit): timing would be off
                    if duration and d.get("duration") and abs(d["duration"] - duration) > 6:
                        continue
                    return parse_lrc(d["syncedLyrics"])
        except Exception:
            pass
    return []


class Lyrics:
    """Caches lyrics per track and fetches them in the background."""

    RETRY_AFTER = (3, 10, 30)   # seconds: when nothing was found, look again (the song's length
                                # often arrives a moment after its title, and networks blip)

    def __init__(self):
        self._lock = threading.Lock()
        self._key = None
        self._lines = []
        self._times = []
        self._rate = DEFAULT_RATE
        self._busy = False
        self._tries = 0
        self._last_try = 0.0

    @property
    def status(self):
        """'' (no track), 'searching', 'found' or 'none' (nothing on LRCLIB, as far as we can tell)."""
        with self._lock:
            if not self._key or not self._key[1]:
                return ""
            if self._lines:
                return "found"
            # only the first look counts as searching; the quiet retries after it don't flip it back
            return "searching" if self._tries == 0 or (self._busy and self._tries == 1) else "none"

    def set_track(self, artist, title, album="", duration=None):
        """Call often (every frame is fine): looks lyrics up when the track changes, and again a few
        times if nothing was found."""
        key = (artist, title)
        now = time.monotonic()
        with self._lock:
            if key == self._key:
                if (self._lines or self._busy or not title or self._tries > len(self.RETRY_AFTER)
                        or now - self._last_try < self.RETRY_AFTER[self._tries - 1]):
                    return
            else:
                self._key, self._lines, self._times, self._rate, self._tries = key, [], [], DEFAULT_RATE, 0
                if not title:
                    return
            self._busy, self._tries, self._last_try = True, self._tries + 1, now

        def work():
            lines = fetch(artist, title, album, duration)
            with self._lock:
                if self._key == key:
                    self._lines = lines
                    self._times = [t for t, _ in lines]
                    self._rate = singing_rate(lines)
                    self._last_try = time.monotonic()
                self._busy = False
        threading.Thread(target=work, daemon=True).start()

    def at(self, position, offset=0.0):
        """Where the song is in its lyrics at `position` seconds:
        (current line, next line, progress 0..1 through the line, line index (-1 = none),
         seconds into the line, estimated seconds it takes to sing the line)."""
        with self._lock:
            if not self._lines:
                return "", "", 0.0, -1, 0.0, 0.0
            p = position + offset
            i = bisect.bisect_right(self._times, p) - 1
            if i < 0:
                return "", self._lines[0][1], 0.0, -1, 0.0, 0.0
            cur_t, cur = self._lines[i]
            if i + 1 < len(self._lines):
                nxt_t, nxt = self._lines[i + 1]
                gap = max(0.1, nxt_t - cur_t)
                prog = (p - cur_t) / gap
            else:
                nxt, gap, prog = "", None, 0.0
            return cur, nxt, max(0.0, min(1.0, prog)), i, p - cur_t, sung_duration(cur, gap, self._rate)


DEFAULT_RATE = 16.0     # characters sung per second, until a song's own pace is known


def singing_rate(lines):
    """How fast this song is sung, in characters a second, from how quickly its lines follow each
    other. Singers finish a line before the next one starts (breaths, pauses), so take one of the
    faster lines and a bit more: a rap comes out around 20-25, a ballad around 12-14."""
    rates = sorted(len(t) / (lines[i + 1][0] - t0) for i, (t0, t) in enumerate(lines[:-1])
                   if t and 0.8 < lines[i + 1][0] - t0 < 8)
    if len(rates) < 4:
        return DEFAULT_RATE
    return max(12.0, min(26.0, rates[len(rates) * 3 // 4] * 1.2))


def sung_duration(line, gap, rate=DEFAULT_RATE):
    """Rough time to sing a line, for lighting its words up one by one (LRC only times whole lines):
    its length at the song's pace, but always finished well before the next line starts."""
    est = 0.25 + len(line) / rate
    return est if gap is None else min(0.8 * gap, est)
