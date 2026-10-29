"""Now-playing info from Windows' media session API (SMTC).

Works with anything that shows in the Windows volume/media flyout:
Spotify, Apple Music, browsers (YouTube, SoundCloud...), Windows Media Player, etc.
"""
import asyncio
import datetime as dt
import io
import threading
import time

import numpy as np

try:
    from winrt.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
    )
    from winrt.windows.storage.streams import Buffer, DataReader, InputStreamOptions
    AVAILABLE = True
except Exception:          # not on Windows / packages missing
    AVAILABLE = False

# matched against the app's id (e.g. "Spotify.exe", "Chrome", "VideoLAN.VLC")
MUSIC_APPS = ("spotify", "zunemusic", "applemusic", "itunes", "tidal", "deezer", "amazonmusic",
              "foobar", "musicbee", "aimp", "winamp", "qobuz", "youtube-music", "ytmdesktop")
VIDEO_APPS = ("vlc", "zunevideo", "mpv", "potplayer", "mpc-hc", "mpc-be", "netflix", "disney",
              "primevideo", "plex", "kodi", "stremio")


def is_placeholder(title, artist):
    """Chrome/Edge in Incognito/InPrivate hide what's playing: the title becomes
    "A site is playing media" and the art is the Incognito icon."""
    t = (title or "").strip().lower()
    return t.startswith("a site is playing") or (not t and not (artist or "").strip())


def classify(app, title, artist, album, thumb_size, art_colorful, playback_type):
    """'music' (use the album art) or 'video' (use the screen).

    Browsers don't say what they're playing, so: an album name or square art means music
    (YouTube Music, Spotify web, SoundCloud), wide art or none means a video (YouTube).
    Art without any real color (or Incognito's placeholder) is no use either: use the screen."""
    a = (app or "").lower()
    if any(k in a for k in MUSIC_APPS):
        return "music"
    if any(k in a for k in VIDEO_APPS) or playback_type == 2:    # MediaPlaybackType.VIDEO
        return "video"
    if is_placeholder(title, artist) or not art_colorful:
        return "video"
    if album:
        return "music"
    if thumb_size:
        w, h = thumb_size
        return "music" if h and 0.85 <= w / h <= 1.18 else "video"
    return "video"


async def _read_thumbnail(ref):
    if ref is None:
        return None
    stream = await ref.open_read_async()
    size = int(stream.size)
    if size <= 0:
        return None
    buf = Buffer(size)
    await stream.read_async(buf, buf.capacity, InputStreamOptions.READ_AHEAD)
    try:
        return bytes(buf)                       # newer pywinrt: buffer protocol
    except Exception:
        reader = DataReader.from_buffer(buf)    # older pywinrt
        data = bytearray(buf.length)
        reader.read_bytes(data)
        return bytes(data)


def _image_info(data):
    """(width, height), and whether the image has any real color in it."""
    try:
        from PIL import Image
        from colors import pick_colors
        img = Image.open(io.BytesIO(data))
        size = img.size
        small = img.convert("RGB")
        small.thumbnail((48, 48))
        return size, pick_colors(np.asarray(small)) is not None
    except Exception:
        return None, False


def _pick_session(manager):
    """The session that's actually playing (Windows' "current" one is often a paused app)."""
    current = manager.get_current_session()
    try:
        for s in manager.get_sessions():
            if s.get_playback_info().playback_status == Status.PLAYING:
                return s
    except Exception:
        pass
    return current


class NowPlaying:
    """Polls the current media session on a background thread."""

    def __init__(self, poll_interval=0.5):
        self.poll_interval = poll_interval
        self._lock = threading.Lock()
        self._state = dict(title="", artist="", album="", app="", kind="none", playing=False,
                           position=0.0, duration=0.0, stamp=time.monotonic(),
                           thumbnail=None, track_id=0, art_id=0)
        self._track_id = 0
        if AVAILABLE:
            threading.Thread(target=self._run, daemon=True).start()

    def snapshot(self):
        """Current state with the playback position extrapolated to *now*."""
        with self._lock:
            s = dict(self._state)
        if s["playing"]:
            s["position"] += time.monotonic() - s["stamp"]
            if s["duration"]:
                s["position"] = min(s["position"], s["duration"])
        return s

    def _run(self):
        asyncio.run(self._loop())

    async def _loop(self):
        manager = await MediaManager.request_async()
        last_key = None
        thumb_tries = 0
        thumb_size, art_colorful = None, False
        while True:
            try:
                session = _pick_session(manager)
                if session is None:
                    with self._lock:
                        self._state.update(playing=False, title="", artist="", kind="none")
                else:
                    props = await session.try_get_media_properties_async()
                    info = session.get_playback_info()
                    tl = session.get_timeline_properties()
                    app = session.source_app_user_model_id or ""

                    playing = info.playback_status == Status.PLAYING
                    pos = tl.position.total_seconds()
                    dur = (tl.end_time - tl.start_time).total_seconds()
                    # SMTC only updates position occasionally: catch up from its timestamp
                    if playing and tl.last_updated_time:
                        try:
                            now = dt.datetime.now(dt.timezone.utc)
                            age = (now - tl.last_updated_time).total_seconds()
                            if 0 <= age < 3600:
                                pos += age
                        except Exception:
                            pass
                    try:
                        ptype = int(props.playback_type.value) if props.playback_type is not None else 0
                    except Exception:
                        ptype = 0

                    key = (app, props.artist, props.title)
                    changed = key != last_key
                    if changed:
                        last_key = key
                        self._track_id += 1
                        thumb_tries = 8      # art often arrives a moment after the title
                        thumb_size, art_colorful = None, False
                    thumb = None
                    if thumb_tries > 0:
                        try:
                            thumb = await _read_thumbnail(props.thumbnail)
                        except Exception:
                            thumb = None
                        thumb_tries = 0 if thumb else thumb_tries - 1
                        if thumb:
                            thumb_size, art_colorful = _image_info(thumb)
                        changed = changed or thumb is not None
                    with self._lock:
                        self._state.update(
                            title=props.title or "", artist=props.artist or "",
                            album=props.album_title or "", app=app, playing=playing,
                            kind=classify(app, props.title, props.artist, props.album_title,
                                          thumb_size, art_colorful, ptype),
                            position=pos, duration=dur, stamp=time.monotonic(),
                            track_id=self._track_id)
                        if changed:
                            self._state["thumbnail"] = thumb
                            self._state["art_id"] += 1
            except Exception:
                pass
            await asyncio.sleep(self.poll_interval)
