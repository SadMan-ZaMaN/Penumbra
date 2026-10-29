"""LumnWin engine: listens to system audio + now-playing info, streams a light state to the overlay.

    python engine.py            # normal
    python engine.py --debug    # print live values in the console
    python engine.py --demo     # fake 120 BPM music, no audio device needed (test the overlay)

Sends ~60 JSON packets/s over UDP to 127.0.0.1:47800, e.g.
    {"level":0.62,"bass":0.8,"mid":0.4,"high":0.2,"pulse":1.0,"beat":true,"bpm":124.0,
     "colors":["#7c5cff","#ff4fa3"],"playing":true,"title":"...","artist":"...",
     "lyric":"current line","next":"next line","lprog":0.4}
"""
import argparse
import json
import math
import queue
import socket
import sys
import threading
import time

import numpy as np

from colors import DEFAULT, dominant_colors
from dsp import BeatDetector
from lyrics import Lyrics
from media import NowPlaying

BLOCK = 1024
FPS = 60


# --------------------------------------------------------------------------- audio sources
class LoopbackSource:
    """System audio via WASAPI loopback (what you hear, never the microphone)."""

    def __init__(self):
        import pyaudiowpatch as pyaudio
        self.pa = pyaudio.PyAudio()
        wasapi = self.pa.get_host_api_info_by_type(pyaudio.paWASAPI)
        dev = self.pa.get_device_info_by_index(wasapi["defaultOutputDevice"])
        if not dev.get("isLoopbackDevice"):
            for lb in self.pa.get_loopback_device_info_generator():
                if dev["name"] in lb["name"]:
                    dev = lb
                    break
            else:
                raise RuntimeError("No loopback device found for " + dev["name"])
        self.name = dev["name"]
        self.rate = int(dev["defaultSampleRate"])
        self.channels = max(1, int(dev["maxInputChannels"]))
        self.q = queue.Queue(maxsize=64)

        def cb(data, frames, info, status):
            x = np.frombuffer(data, dtype=np.float32)
            if self.channels > 1:
                x = x.reshape(-1, self.channels).mean(axis=1)
            try:
                self.q.put_nowait(x)
            except queue.Full:
                pass
            return (None, pyaudio.paContinue)

        self.stream = self.pa.open(format=pyaudio.paFloat32, channels=self.channels,
                                   rate=self.rate, input=True, frames_per_buffer=BLOCK,
                                   input_device_index=dev["index"], stream_callback=cb)

    def blocks(self):
        out = []
        while True:
            try:
                out.append(self.q.get_nowait())
            except queue.Empty:
                return out

    def close(self):
        try:
            self.stream.stop_stream()
            self.stream.close()
        finally:
            self.pa.terminate()


def default_output_name():
    import pyaudiowpatch as pyaudio
    pa = pyaudio.PyAudio()          # fresh instance = fresh device list
    try:
        wasapi = pa.get_host_api_info_by_type(pyaudio.paWASAPI)
        return pa.get_device_info_by_index(wasapi["defaultOutputDevice"])["name"]
    finally:
        pa.terminate()


class AutoLoopback:
    """LoopbackSource that survives errors and follows the default output device
    (plugging in headphones, switching to Bluetooth speakers, ...)."""

    def __init__(self):
        self.src = None
        self.name = "none"
        self.rate = 48000
        self.generation = 0          # bumps whenever the device (and maybe sample rate) changes
        self._lock = threading.Lock()
        self._open()
        threading.Thread(target=self._watch, daemon=True).start()

    def _open(self):
        try:
            src = LoopbackSource()
        except Exception as e:
            print(f"[engine] audio capture failed: {e} (retrying)", file=sys.stderr, flush=True)
            return
        with self._lock:
            old, self.src = self.src, src
            self.name, self.rate = src.name, src.rate
            self.generation += 1
        if old:
            old.close()
        print(f"[engine] audio: {self.name} @ {self.rate} Hz", flush=True)

    def _watch(self):
        while True:
            time.sleep(4)
            try:
                if self.src is None or not self.name.startswith(default_output_name()):
                    self._open()
            except Exception:
                pass

    def blocks(self):
        with self._lock:
            return self.src.blocks() if self.src else []

    def close(self):
        if self.src:
            self.src.close()


class DemoSource:
    """Synthetic 120 BPM kick + hats + pad, generated in real time."""

    def __init__(self, rate=48000, bpm=120):
        self.rate, self.bpm, self.t = rate, bpm, 0.0
        self.name = "demo"
        self._last = time.monotonic()

    def blocks(self):
        now = time.monotonic()
        n = int((now - self._last) * self.rate) // BLOCK
        out = []
        for _ in range(n):
            t = self.t + np.arange(BLOCK) / self.rate
            beat = 60.0 / self.bpm
            ph = np.mod(t, beat)
            kick = np.sin(2 * np.pi * (50 + 80 * np.exp(-ph * 30)) * ph) * np.exp(-ph * 9)
            hat_ph = np.mod(t + beat / 2, beat)
            hat = np.random.randn(BLOCK) * np.exp(-hat_ph * 60) * 0.15
            pad = 0.1 * np.sin(2 * np.pi * 330 * t) * (0.5 + 0.5 * np.sin(t * 0.5))
            out.append((kick * 0.8 + hat + pad).astype(np.float32))
            self.t += BLOCK / self.rate
            self._last += BLOCK / self.rate
        return out

    def close(self):
        pass


def watch_parent(pid):
    """Quit when the overlay quits (or crashes), so no orphan engine keeps running."""
    import os

    def wait():
        if sys.platform == "win32":
            import ctypes
            k32 = ctypes.windll.kernel32
            h = k32.OpenProcess(0x00100000, False, pid)   # SYNCHRONIZE
            if h:
                k32.WaitForSingleObject(h, 0xFFFFFFFF)
        else:
            while True:
                try:
                    os.kill(pid, 0)
                except OSError:
                    break
                time.sleep(1)
        os._exit(0)
    threading.Thread(target=wait, daemon=True).start()


# --------------------------------------------------------------------------- main loop
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=47800)
    ap.add_argument("--debug", action="store_true")
    ap.add_argument("--demo", action="store_true")
    ap.add_argument("--parent", type=int, default=0,
                    help="exit when this process (the overlay) exits")
    ap.add_argument("--lyrics-offset", type=float, default=0.0,
                    help="seconds; positive shows lines earlier")
    args = ap.parse_args()
    if args.parent:
        watch_parent(args.parent)

    src = DemoSource() if args.demo else AutoLoopback()
    generation = getattr(src, "generation", 0)
    det = BeatDetector(sample_rate=src.rate, block_size=BLOCK)
    media = NowPlaying()
    lyrics = Lyrics()
    colors = list(DEFAULT)
    art_id = -1

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = ("127.0.0.1", args.port)

    frame = 1.0 / FPS
    next_t = time.monotonic()
    last_audio = time.monotonic()
    last_dbg = 0.0
    try:
        while True:
            now = time.monotonic()
            beat = False
            if getattr(src, "generation", 0) != generation:   # audio device switched
                generation = src.generation
                det = BeatDetector(sample_rate=src.rate, block_size=BLOCK)
            blocks = src.blocks()
            for b in blocks:
                beat |= det.process(b)
            if blocks:
                last_audio = now
            elif now - last_audio > 0.1:     # loopback delivers nothing during silence
                det.idle(frame)

            np_state = media.snapshot()
            if np_state["art_id"] != art_id:
                art_id = np_state["art_id"]
                thumb = np_state.get("thumbnail")
                colors = dominant_colors(thumb) if thumb else list(DEFAULT)
            lyrics.set_track(np_state["artist"], np_state["title"],
                             np_state["album"], np_state["duration"])
            cur, nxt, prog = lyrics.at(np_state["position"], args.lyrics_offset)

            if args.demo and not np_state["title"]:
                # animate colors a little so the demo isn't static
                h = (now * 0.03) % 1.0
                colors = ["#%02x%02x%02x" % tuple(int(127 + 127 * math.sin(2 * math.pi * (h + o)))
                                                   for o in (0, 1 / 3, 2 / 3)), colors[1]]

            pkt = {
                "level": round(det.level, 4), "bass": round(det.bass, 4),
                "mid": round(det.mid, 4), "high": round(det.high, 4),
                "pulse": round(det.pulse, 4), "beat": beat, "bpm": round(det.bpm, 1),
                "colors": colors, "playing": bool(np_state["playing"]) or args.demo,
                "title": np_state["title"], "artist": np_state["artist"],
                "lyric": cur, "next": nxt, "lprog": round(prog, 3),
            }
            sock.sendto(json.dumps(pkt, ensure_ascii=False).encode("utf-8"), addr)

            if args.debug and now - last_dbg > 0.1:
                last_dbg = now
                bar = "#" * int(det.level * 30)
                print(f"\r{'*' if det.pulse > 0.8 else ' '} lvl {det.level:4.2f} "
                      f"bpm {det.bpm:5.1f} {bar:<30} {np_state['title'][:30]:<30} {cur[:40]:<40}",
                      end="", flush=True)

            next_t += frame
            sleep = next_t - time.monotonic()
            if sleep > 0:
                time.sleep(sleep)
            else:
                next_t = time.monotonic()
    except KeyboardInterrupt:
        pass
    finally:
        src.close()


if __name__ == "__main__":
    main()
