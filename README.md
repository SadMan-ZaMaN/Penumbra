# LumnWin

An ambient glow around the edges of your Windows screen. It pulses to whatever is playing, takes its colors from the album art (music) or from the screen (videos), and can show synced lyrics.

```
 ┌──────── engine (Python) ────────┐   UDP 127.0.0.1:47800   ┌──────── overlay (C# / WPF) ─────────┐
 │ system audio (WASAPI loopback)  │   ~60 JSON packets/s    │ 1 full-screen click-through window, │
 │   → FFT → bass/mid/high, beats  │ ──────────────────────► │   drawn on the GPU by Glow.hlsl     │
 │ now playing (Windows media API) │                         │ streams of light along the edge     │
 │   music → album art → 2 colors  │                         │ lyrics line above the taskbar       │
 │   video → screen → 2 colors     │                         │ tray menu + settings                │
 │ LRCLIB → synced lyrics          │                         │ starts/stops the engine             │
 └─────────────────────────────────┘                         └─────────────────────────────────────┘
```

## Setup (once)

You need:
- **Python 3.10+** from python.org (tick "Add to PATH")
- **.NET 8 SDK**: `winget install Microsoft.DotNet.SDK.8`

Then double-click **`setup.bat`**. It creates `engine\.venv`, installs the Python packages and builds the overlay.

## Run

- **`run.bat`** starts it. A ring icon appears in the tray.
  - Left-click the icon to pop up the **Penumbra panel** with all the settings, just above the tray. Click anywhere else (or press Esc) and it tucks away again.
  - Can't see the tray icon (Windows may hide it)? Run **`run.bat`** again while Penumbra is running: that opens the panel too.
  - Right-click the icon for quick options: on/off, color source, start with Windows, exit.
- **`stop.bat`** quits it (same as **Quit Penumbra** in the panel or **Exit** in the tray menu).
- The glow only shows while something is making sound. When everything is silent for 1.5 seconds, it fades out and the screen looks normal.
- **`debug.bat`** runs the engine in a console with live readouts: level, BPM, whether it thinks it's playing music or a video, which colors it picked and from where, the app, the title and the lyric.
- **`debug.bat --demo`** plays a fake 120 BPM beat, so you can test without music.

Play something in Spotify, a browser or any media app that appears in the Windows volume flyout.

## The panel

The top of the panel shows what's playing and the colors the glow is using right now, and where they came from.

| Setting | What it does |
|---|---|
| On/off switch | Top right. Turns the glow on or off |
| Colors | **Automatic**: album art for music, the screen for videos. Or always **Album art**, always **Screen**, or **My colors**. Click the Primary/Secondary swatches to pick your own, or click a preset |
| Thickness | Width of the solid line of light along the edge |
| Glow | Size of the soft halo just inside the line (0 = just the line) |
| Brightness | Overall intensity |
| Waviness | How much the line's thickness ripples as the light flows (0 = even) |
| Corners | Square (the default, light reaches right into the corners), rounded or very round |
| Motion | **Music sync** reacts to the beat and only shows while there's sound. **Steady** is a calm, slow light that stays on |
| Glow when nothing is playing | Keep a dim glow during silence instead of fading out |
| Show lyrics | Synced lyric line above the taskbar (from lrclib.net) |
| Show in screenshots & recordings | Off by default, so the screen sampling never sees the glow itself. Turn it on to record a demo of the glow |
| Start with Windows | Adds or removes a registry Run entry |

### How the colors are picked

- **Music or video?** Spotify, Apple Music and other music apps count as music, and VLC and other players count as video. In a browser, a track with an album name or square cover art is music (YouTube Music, Spotify web). Wide or missing art is a video (YouTube).
- **Incognito / InPrivate windows** hide what's playing from Windows: the title becomes "A site is playing media" and the art is the Incognito icon. Those, and any art with no real color in it, use the screen's colors instead.
- **Music:** the color that covers the most of the cover art wins, with vivid colors counting a bit extra. Grey, black and white don't count, so a black cover with yellow text glows yellow. A completely black-and-white cover glows soft white.
- **Videos:** the middle of the screen is sampled 6 times a second, and the same rule picks the colors. Dark frames keep the previous colors.

Settings are saved in `%APPDATA%\LumnWin\settings.json`.

## Files

```
engine/
  engine.py    main loop: audio → analysis → UDP; --demo, --debug, --lyrics-offset
  dsp.py       FFT band energies, beat detection, BPM, smoothing   ← the signal processing
  media.py     now playing + album art via Windows SMTC (pywinrt), music-or-video guess
  colors.py    image → the 2 colors that cover the most of it, made glow-friendly
  screen.py    samples the middle of the screen a few times a second (for videos)
  lyrics.py    LRCLIB fetch + .lrc parsing + "which line is now"
overlay/
  Program.cs        startup, single instance, engine process
  EngineLink.cs     UDP receiver + engine launcher
  GlowController.cs per-frame light logic: targets, smoothing, moving lights, beat sparks
  GlowWindow.cs     the full-screen click-through window + Direct3D 11 / DirectComposition
  ControlPanel.xaml the settings window (layout + styles); ControlPanel.xaml.cs wires it up
  Glow.hlsl         the look itself: rim, bloom, haze, waves, colors (runs on the GPU)
  LyricsWindow.cs   karaoke line
  Tray.cs           tray icon + menu
  Native.cs         Win32: click-through, no taskbar entry, always on top, hidden from capture
```

## Tuning

Most of the "feel" lives in a few numbers:
- **Beat sensitivity**: in `dsp.py`, `thresh = mean + max(1.2 * std, 0.35 * mean)`. Lower the numbers for more beats.
- **Bass range**: in `dsp.py`, `bands["bass"]` (20–150 Hz).
- **Pulse size and decay**: in `GlowController.cs`, the `playing` branch and `_pulse *= Math.Exp(-dt / 0.15)`.
- **Streams of light**: in `GlowController.cs`, `Spawn` (length, speed, how long each lives) and the `_nextSpawn` timing (how often new ones appear).
- **Shape of the light**: in `Glow.hlsl`, `width` (the line), `halo` and the `I = ...` mix. The `Waves` function controls the ripples.
- **Lyrics early or late**: run `engine.py --lyrics-offset 0.3`.
- **See the glow on black**: run `LumnWin.exe --black` to judge the look without your desktop behind it. Close it with `stop.bat`.

## Known limits / next steps

- **Primary monitor only.** For multi-monitor support, create one `GlowWindow` per monitor.
- **Exclusive-fullscreen games** draw over everything, so use borderless-windowed mode. Fullscreen videos in browsers work.
- **DRM video** (Netflix and similar in some browsers) captures as black, so the glow keeps its previous colors there.
