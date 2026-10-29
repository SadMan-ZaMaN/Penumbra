"""Beat / energy analysis. Pure numpy, no Windows dependencies (easy to test).

Pipeline per audio block:
  mono -> Hann window -> FFT -> band energies (bass / mid / high)
  bass energy vs. its ~1 s history -> beat?
  everything normalised against a slowly decaying running max -> 0..1 values
"""
import time
from collections import deque

import numpy as np

AUDIBLE_RMS = 3e-4      # about -70 dBFS: quieter than this counts as silence


class BeatDetector:
    def __init__(self, sample_rate=48000, fft_size=2048, history_sec=1.0,
                 block_size=1024, min_beat_gap=0.22):
        self.sr = sample_rate
        self.n = fft_size
        self.window = np.hanning(fft_size).astype(np.float32)
        self.buf = np.zeros(fft_size, dtype=np.float32)   # rolling input buffer
        freqs = np.fft.rfftfreq(fft_size, 1.0 / sample_rate)
        self.bands = {
            "bass": (freqs >= 20) & (freqs < 150),
            "mid": (freqs >= 150) & (freqs < 2000),
            "high": (freqs >= 2000) & (freqs < 12000),
        }
        hist_len = max(8, int(history_sec * sample_rate / block_size))
        self.bass_hist = deque(maxlen=hist_len)
        self.peak = {k: 1e-6 for k in self.bands}          # running maxima for normalisation
        self.min_gap = min_beat_gap
        self.last_beat = 0.0

        # public, smoothed outputs (0..1)
        self.level = 0.0     # overall loudness feel (bass-weighted)
        self.bass = 0.0
        self.mid = 0.0
        self.high = 0.0
        self.pulse = 0.0     # jumps to 1 on a beat, decays quickly
        self.bpm = 0.0
        self.last_sound = -1e9   # last time anything audible played (absolute loudness, not normalised)
        self._beat_times = deque(maxlen=16)
        self._last_t = time.monotonic()

    def process(self, samples, now=None):
        """samples: 1-D float32 mono block (any length). Returns True if a beat fired."""
        now = time.monotonic() if now is None else now
        dt = max(1e-3, now - self._last_t)
        self._last_t = now

        samples = np.asarray(samples, dtype=np.float32)
        if len(samples) and float(np.sqrt(np.mean(samples * samples))) > AUDIBLE_RMS:
            self.last_sound = now
        k = len(samples)
        if k >= self.n:
            self.buf[:] = samples[-self.n:]
        else:
            self.buf = np.roll(self.buf, -k)
            self.buf[-k:] = samples

        spec = np.abs(np.fft.rfft(self.buf * self.window)) ** 2
        raw = {name: float(spec[mask].mean()) for name, mask in self.bands.items()}

        # normalise: running max that decays ~1%/frame-ish (time based)
        decay = 0.5 ** (dt / 4.0)          # half-life 4 s
        norm = {}
        for name, e in raw.items():
            self.peak[name] = max(e, self.peak[name] * decay, 1e-6)
            norm[name] = min(1.0, (e / self.peak[name]) ** 0.5)

        silent = raw["bass"] + raw["mid"] + raw["high"] < 1e-7

        # beat: bass energy clearly above its recent average
        beat = False
        if len(self.bass_hist) >= self.bass_hist.maxlen // 2 and not silent:
            hist = np.fromiter(self.bass_hist, dtype=np.float64)
            mean, std = hist.mean(), hist.std()
            thresh = mean + max(1.2 * std, 0.35 * mean)
            if raw["bass"] > thresh and now - self.last_beat >= self.min_gap:
                beat = True
                self.last_beat = now
                self._beat_times.append(now)
                if len(self._beat_times) >= 4:
                    gaps = np.diff(np.fromiter(self._beat_times, dtype=np.float64))
                    med = float(np.median(gaps))
                    if med > 0:
                        self.bpm = 60.0 / med
        self.bass_hist.append(raw["bass"])

        # smoothing: fast attack, slower release
        def follow(cur, target, attack=0.6, release=0.12):
            a = attack if target > cur else release
            a = 1.0 - (1.0 - a) ** (dt * 60)   # frame-rate independent
            return cur + (target - cur) * a

        if silent:
            norm = {k: 0.0 for k in norm}
        self.bass = follow(self.bass, norm["bass"])
        self.mid = follow(self.mid, norm["mid"])
        self.high = follow(self.high, norm["high"], 0.7, 0.2)
        self.level = follow(self.level, 0.6 * norm["bass"] + 0.3 * norm["mid"] + 0.1 * norm["high"])

        self.pulse *= 0.5 ** (dt / 0.12)    # half-life 120 ms
        if beat:
            self.pulse = 1.0
        return beat

    def sound(self, now=None, hold=1.5):
        """True while something audible is playing (and for `hold` seconds after, so short
        pauses between songs don't make the glow blink off)."""
        now = time.monotonic() if now is None else now
        return now - self.last_sound < hold

    def idle(self, dt):
        """Called when no audio arrives (WASAPI loopback goes quiet when nothing plays)."""
        f = 0.5 ** (dt / 0.25)
        self.level *= f
        self.bass *= f
        self.mid *= f
        self.high *= f
        self.pulse *= 0.5 ** (dt / 0.12)
