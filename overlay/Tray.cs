using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumnWin
{
    /// <summary>System tray icon + menu with all the settings.</summary>
    public class Tray : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly Settings _s;
        readonly GlowController _glow;
        readonly EngineLink _link;
        readonly ToolStripMenuItem _nowPlaying;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public Tray(Settings s, GlowController glow, EngineLink link, Action exit)
        {
            _s = s; _glow = glow; _link = link;

            var menu = new ContextMenuStrip();
            _nowPlaying = new ToolStripMenuItem("LumnWin") { Enabled = false };
            menu.Items.Add(_nowPlaying);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Toggle("Enabled", () => s.Enabled, v => s.Enabled = v));
            menu.Items.Add(Choice("Mode",
                ("Music sync", () => s.Mode == GlowMode.MusicSync, () => s.Mode = GlowMode.MusicSync),
                ("Steady glow", () => s.Mode == GlowMode.Steady, () => s.Mode = GlowMode.Steady)));
            menu.Items.Add(Choice("Thickness",
                Opt("Hairline", 3, () => s.Thickness, v => s.Thickness = v),
                Opt("Thin", 8, () => s.Thickness, v => s.Thickness = v),
                Opt("Medium", 16, () => s.Thickness, v => s.Thickness = v),
                Opt("Thick", 30, () => s.Thickness, v => s.Thickness = v)));
            menu.Items.Add(Choice("Glow",
                Opt("Whisper", 60, () => s.Glow, v => s.Glow = v),
                Opt("Soft", 160, () => s.Glow, v => s.Glow = v),
                Opt("Strong", 280, () => s.Glow, v => s.Glow = v),
                Opt("Fill the room", 450, () => s.Glow, v => s.Glow = v)));
            menu.Items.Add(Choice("Brightness",
                Opt("40%", 0.4, () => s.Brightness, v => s.Brightness = v),
                Opt("70%", 0.7, () => s.Brightness, v => s.Brightness = v),
                Opt("85%", 0.85, () => s.Brightness, v => s.Brightness = v),
                Opt("100%", 1.0, () => s.Brightness, v => s.Brightness = v)));

            var colors = new ToolStripMenuItem("Colors");
            colors.DropDownItems.Add(Toggle("Follow album art", () => s.FollowAlbumColors, v => s.FollowAlbumColors = v));
            colors.DropDownItems.Add(new ToolStripMenuItem("Pick primary…", null, (_, _) => Pick(true)));
            colors.DropDownItems.Add(new ToolStripMenuItem("Pick secondary…", null, (_, _) => Pick(false)));
            menu.Items.Add(colors);

            menu.Items.Add(Toggle("Glow while paused", () => s.IdleGlow, v => s.IdleGlow = v));
            menu.Items.Add(Toggle("Show lyrics", () => s.ShowLyrics, v => s.ShowLyrics = v));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Toggle("Start with Windows", IsAutoStart, SetAutoStart));
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

            menu.Opening += (_, _) =>
            {
                var st = _glow.Last;
                _nowPlaying.Text = !_link.Connected ? "Engine not running"
                    : st == null || string.IsNullOrEmpty(st.title) ? "Listening…"
                    : Trim($"♪ {st.title} — {st.artist}", 48);
                Refresh(menu.Items);
            };

            _icon = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "LumnWin",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                s.Enabled = !s.Enabled; Changed();
            };
        }

        public void Notify(string title, string text) =>
            _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

        // ---------- menu helpers: every item knows how to read its own checked state
        ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Tag = get };
            item.Click += (_, _) => { set(!get()); Changed(); };
            return item;
        }

        static (string, Func<bool>, Action) Opt(string text, double value, Func<double> get, Action<double> set) =>
            (text, () => Math.Abs(get() - value) < 1e-6, () => set(value));

        ToolStripMenuItem Choice(string text, params (string text, Func<bool> isOn, Action pick)[] options)
        {
            var parent = new ToolStripMenuItem(text);
            foreach (var (t, isOn, pick) in options)
            {
                var item = new ToolStripMenuItem(t) { Tag = isOn };
                item.Click += (_, _) => { pick(); Changed(); };
                parent.DropDownItems.Add(item);
            }
            return parent;
        }

        static void Refresh(ToolStripItemCollection items)
        {
            foreach (ToolStripItem i in items)
            {
                if (i is not ToolStripMenuItem m) continue;
                if (m.Tag is Func<bool> on) m.Checked = on();
                Refresh(m.DropDownItems);
            }
        }

        void Changed()
        {
            _s.Save();
            _glow.ApplyLayout();
        }

        void Pick(bool primary)
        {
            using var dlg = new ColorDialog { FullOpen = true };
            var cur = GlowController.Parse(primary ? _s.Primary : _s.Secondary, System.Windows.Media.Colors.White);
            dlg.Color = Color.FromArgb(cur.R, cur.G, cur.B);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            string hex = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
            if (primary) _s.Primary = hex; else _s.Secondary = hex;
            _s.FollowAlbumColors = false;   // picking a color means "use mine"
            Changed();
        }

        static bool IsAutoStart()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("LumnWin") != null;
        }

        static void SetAutoStart(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("LumnWin", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("LumnWin", false);
        }

        static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

        /// <summary>Draws a little glowing ring so we don't need an .ico file.</summary>
        static Icon MakeIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                    Color.FromArgb(124, 92, 255), Color.FromArgb(255, 79, 163), 45f);
                using var pen = new Pen(brush, 5);
                g.DrawEllipse(pen, 4, 4, 24, 24);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
