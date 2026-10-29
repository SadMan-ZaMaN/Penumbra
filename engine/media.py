"""Now-playing info from Windows' media session API (SMTC).

Works with anything that shows in the Windows volume/media flyout:
Spotify, Apple Music, browsers (YouTube, SoundCloud...), Windows Media Player, etc.
"""
import asyncio
import datetime as dt
import threading
import time

try:
    from winrt.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
    )
    from winrt.windows.storage.streams import Buffer, DataReader, InputStreamOptions
    AVAILABLE = True
except Exception:          # not on Windows / packages missing
    AVAILABLE = False


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


class NowPlaying:
    """Polls the current media session on a background thread."""

    def __init__(self, poll_interval=0.5):
        self.poll_interval = poll_interval
        self._lock = threading.Lock()
        self._state = dict(title="", artist="", album="", playing=False,
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
        while True:
            try:
                session = manager.get_current_session()
                if session is None:
                    with self._lock:
                        self._state.update(playing=False, title="", artist="")
                else:
                    props = await session.try_get_media_properties_async()
                    info = session.get_playback_info()
                    tl = session.get_timeline_properties()

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

                    key = (props.artist, props.title)
                    changed = key != last_key
                    if changed:
                        last_key = key
                        self._track_id += 1
                        thumb_tries = 8      # art often arrives a moment after the title
                    thumb = None
                    if thumb_tries > 0:
                        try:
                            thumb = await _read_thumbnail(props.thumbnail)
                        except Exception:
                            thumb = None
                        thumb_tries = 0 if thumb else thumb_tries - 1
                        changed = changed or thumb is not None
                    with self._lock:
                        self._state.update(
                            title=props.title or "", artist=props.artist or "",
                            album=props.album_title or "", playing=playing,
                            position=pos, duration=dur, stamp=time.monotonic(),
                            track_id=self._track_id)
                        if changed:
                            self._state["thumbnail"] = thumb
                            self._state["art_id"] += 1
            except Exception:
                pass
            await asyncio.sleep(self.poll_interval)
