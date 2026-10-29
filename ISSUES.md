# Goal

Look as good as Lumn's demo videos, or better: [instagram.com/arkcurates](https://www.instagram.com/arkcurates/) and [trylumn.xyz](https://www.trylumn.xyz/).

What Lumn's site says about its look:
- **Thickness:** "A hairline of light, a broad band, or anything between."
- **Spread:** "From a whisper along the bezel to a wash that fills the whole room."
- **Colors:** a two-color gradient, primary 60% and secondary 40%. It follows the album art, with an option to override it.
- **Motion:** either reacts to every beat or holds one steady, idle glow.
- **Other:** settings for each display, and a lock-screen card with a liquid-glass look.

Reference screenshots go in `reference/`.

# Issues to fix

Found on the first real run (2026-10-29). We fix these one at a time, and each fix gets tested before the next one starts.

## Round 3 (2026-10-30): matching Lumn's video (`reference/Video-instagram.mp4`)

What the video shows: a thin, solid, fully saturated line; stretches of light that grow, travel along the edge and fade, with full darkness in between; the line gets a little thicker as a stream passes; only a small halo. The dropdown menu and the star-and-lines lyrics in the video are from **Verci**, a separate lyrics app, not Lumn.

- [ ] **Streams, not bars.** *Done, needs your judgment.* Up to 8 streams at once. Loud music brings more of them, faster and brighter. Beats start new ones.
- [ ] **Less space when it's loud.** *Done, needs your judgment.* Loudness changes brightness and motion only. The line and halo keep their size.
- [ ] **Solid, bright colors (not dim or glassy).** *Done, needs your judgment.* The line is drawn fully opaque, and colors are pushed to full brightness with at least 80% saturation (the yellow cover now gives `#ffe633`, not a pastel).
- [x] **Control panel that isn't distracting.** It's now a compact pop-up above the tray icon that hides when you click anywhere else. No taskbar button, no title bar.
- [x] **Color doesn't follow the song.** Tested live on 2026-10-30: an Incognito Chrome tab playing music gave `video->screen` with yellow `#fff033` / lime `#c3ff52` taken from the screen.
  The earlier whitish glow came from Chrome **Incognito**, which hides what's playing and sends its Incognito icon as the "album art". That now falls back to the screen's colors.

## Round 2 (2026-10-29)

- [ ] **No glow when silent.** *Done, needs testing.* The glow fades out after 1.5 s of silence and comes back with any sound. "Glow when nothing is playing" in the panel brings back the dim idle glow.
- [x] **Wavier, moving light.** Replaced in round 3 by streams with rippling thickness.
- [x] **Colors too dim.** Replaced in round 3 by solid colors.
- [x] **Settings panel.** Left-click the tray icon: colors (automatic / album art / screen / mine, swatches, presets), sliders, corners, motion, switches.

## Round 1 (2026-10-29)

- [x] **Glow looks boxy.** The edges looked like squares or boxes, not a soft light.
  Replaced the four rectangles with one GPU-drawn glow: a crisp rim, a soft wavy bloom, moving lights, and sparks on beats. Corners are square by default and the light reaches right into them. Rounded is in the tray menu.
- [ ] **Colors look off.** Even apart from the YouTube problem, the colors don't look good.
  *Reworked, needs your judgment.* The color that covers the most of the image wins (a black cover with yellow text glows yellow), and colors are pushed bright and saturated.
- [x] **Too much memory.** `LumnWin.exe` used about 500 MB of RAM. It now uses about 130 MB.
- [ ] **CPU use.** The glow window uses about 11% of one CPU core (it redraws 90 times a second on this screen). Could drop further by capping the frame rate.
- [ ] **Rename to Penumbra.** The code still says LumnWin / LumnOverlay (exe name, project file, settings folder `%APPDATA%\LumnWin`).
- [ ] **Other changes.** There are more things to change that aren't listed yet. Add them here.

# Features to add later

## From the original plan

These come from `lumnwin-chat.txt`.

- [ ] **Multi-monitor.** The glow only shows on the primary monitor.
- [x] **Follow screen colors.** Done: Colors → "What's on screen" in the tray, and Automatic uses it for videos.
- [ ] **Lock-screen player.** A glass-style now-playing card, like Lumn has.
- [ ] **Lyric effects.** Pulse or shift the color on each new lyric line.

## Ideas

These are Claude's suggestions. Remove any you don't want.

**Settings**
- [x] **Settings window.** Done: the Penumbra panel.
- [ ] **Beat sensitivity slider.** Right now this can only be changed in `dsp.py`.
- [ ] **Lyrics timing slider.** Right now this is only a command-line flag (`--lyrics-offset`).
- [ ] **Presets.** Save and switch between favorite looks.

**Look**
- [ ] **Pick which edges glow.** For example, only the bottom edge.
- [x] **Rounded corners.** Done: Corners → Rounded / Very round in the tray. Square is the default.
- [ ] **Constellation lyrics.** Lumn's demos show lyric words as stars joined by lines, with the current word big (see `reference/`).
- [ ] **More reactive effects.** React to vocals and highs too, not just the bass.

**Behavior**
- [ ] **Hotkey.** A keyboard shortcut to turn the glow on and off.
- [ ] **Auto-hide.** Hide the glow during fullscreen games and on battery power.
- [ ] **Per-app rules.** Only react to chosen apps, for example Spotify but not Discord sounds.

**Shipping**
- [ ] **Installer.** One setup file, so nobody needs Python or .NET installed separately.
- [ ] **Auto-update.**
