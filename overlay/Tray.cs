using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumnWin
{
    /// <summary>System tray icon: left-click opens the panel, right-click has the quick settings.</summary>
    public class Tray : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly Settings _s;
        readonly GlowController _glow;
        readonly EngineLink _link;
        readonly ToolStripMenuItem _nowPlaying;
        readonly ControlPanel _panel;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public Tray(Settings s, GlowController glow, EngineLink link, Action exit)
        {
            _s = s; _glow = glow; _link = link;

            var menu = new ContextMenuStrip();
            _nowPlaying = new ToolStripMenuItem("LumnWin") { Enabled = false };
            menu.Items.Add(_nowPlaying);
            menu.Items.Add(new ToolStripSeparator());

            // the panel has every setting; this menu keeps the quick ones
            _panel = new ControlPanel(s, glow, link, Changed, exit);
            var open = new ToolStripMenuItem("Open Penumbra…", null, (_, _) => _panel.Open());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(Toggle("Enabled", () => s.Enabled, v => s.Enabled = v));
            menu.Items.Add(Choice("Colors",
                Src("Automatic (album art for music, screen for videos)", ColorSource.Auto),
                Src("Album art", ColorSource.AlbumArt),
                Src("What's on screen", ColorSource.Screen),
                Src("My colors", ColorSource.Custom)));
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
                if (e.Button == MouseButtons.Left) _panel.Open();
            };
        }

        public void OpenPanel() => _panel.Open();

        public void Notify(string title, string text) =>
            _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

        // ---------- menu helpers: every item knows how to read its own checked state
        ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Tag = get };
            item.Click += (_, _) => { set(!get()); Changed(); };
            return item;
        }

        (string, Func<bool>, Action) Src(string text, ColorSource value) =>
            (text, () => _s.ColorSource == value, () => _s.ColorSource = value);

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
            if (_panel.IsVisible) _panel.Load();   // a change from this menu shows in the open panel too
        }

        internal static bool IsAutoStart()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("LumnWin") != null;
        }

        internal static void SetAutoStart(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("LumnWin", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("LumnWin", false);
        }

        static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

        /// <summary>Draws a little glowing ring so we don't need an .ico file.</summary>
        internal static Icon MakeIcon()
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
