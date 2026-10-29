# LumnWin

An ambient glow around the edges of your Windows screen. It pulses to whatever is playing, takes its colors from the album art, and can show synced lyrics.

```
 ┌──────── engine (Python) ────────┐   UDP 127.0.0.1:47800   ┌──────── overlay (C# / WPF) ────────┐
 │ system audio (WASAPI loopback)  │   ~60 JSON packets/s    │ 4 click-through edge windows       │
 │   → FFT → bass/mid/high, beats  │ ──────────────────────► │ lyrics line above the taskbar      │
 │ now playing (Windows media API) │                         │ smoothing / color orbit at 60+ fps │
 │   → album art → 2 colors        │                         │ tray menu + settings               │
 │ LRCLIB → synced lyrics          │                         │ starts/stops the engine            │
 └─────────────────────────────────┘                         └────────────────────────────────────┘
```

## Setup (once)

You need:
- **Python 3.10+** from python.org (tick "Add to PATH")
- **.NET 8 SDK**: `winget install Microsoft.DotNet.SDK.8`

Then double-click **`setup.bat`**. It creates `engine\.venv`, installs the Python packages and builds the overlay.

## Run

- **`run.bat`** starts it. A ring icon appears in the tray.
  - Right-click the icon for settings.
  - Left-click it to switch the glow on or off.
- **`debug.bat`** runs the engine in a console with live level, BPM and lyric readouts.
- **`debug.bat --demo`** plays a fake 120 BPM beat, so you can test without music.

Play something in Spotify, a browser or any media app that appears in the Windows volume flyout.

## Tray menu

| Setting | What it does |
|---|---|
| Mode | **Music sync** reacts to the beat. **Steady glow** is a calm, slow breathing light. |
| Thickness | Width of the bright rim at the edge |
| Glow | How far the light spills toward the middle of the screen |
| Brightness | Overall intensity |
| Colors | Follow album art, or pick your own two colors |
| Glow while paused | Dim idle glow instead of fading out |
| Show lyrics | Synced lyric line above the taskbar (from lrclib.net) |
| Start with Windows | Adds or removes a registry Run entry |

Settings are saved in `%APPDATA%\LumnWin\settings.json`.

## Files

```
engine/
  engine.py    main loop: audio → analysis → UDP; --demo, --debug, --lyrics-offset
  dsp.py       FFT band energies, beat detection, BPM, smoothing   ← the signal processing
  media.py     now playing + album art via Windows SMTC (pywinrt)
  colors.py    album art → 2 glow-friendly colors
  lyrics.py    LRCLIB fetch + .lrc parsing + "which line is now"
overlay/
  Program.cs        startup, single instance, engine process
  EngineLink.cs     UDP receiver + engine launcher
  GlowController.cs per-frame light logic (targets, smoothing, colors orbiting the screen)
  EdgeWindow.cs     one glowing edge: color gradient along it × alpha gradient across it
  LyricsWindow.cs   karaoke line
  Tray.cs           tray icon + menu
  Native.cs         Win32: click-through, no taskbar entry, always on top
```

## Tuning

Most of the "feel" lives in a few numbers:
- **Beat sensitivity**: in `dsp.py`, `thresh = mean + max(1.2 * std, 0.35 * mean)`. Lower the numbers for more beats.
- **Bass range**: in `dsp.py`, `bands["bass"]` (20–150 Hz).
- **Pulse size and decay**: in `GlowController.cs`, the `playing` branch and `_pulse *= Math.Exp(-dt / 0.15)`.
- **Lyrics early or late**: run `engine.py --lyrics-offset 0.3`.

## Known limits / next steps

- **Primary monitor only.** For multi-monitor support, loop over `System.Windows.Forms.Screen.AllScreens` and create four edge windows per screen.
- **Exclusive-fullscreen games** draw over everything, so use borderless-windowed mode. Fullscreen videos in browsers work.
- **"Follow what's on screen" colors** (like Lumn's screen mode) aren't built in. You could add them by sampling the screen edges with `Windows.Graphics.Capture` or `mss` in Python.
