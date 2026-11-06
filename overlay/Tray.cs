using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Penumbra
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
            MoveOldAutoStart();

            var menu = new ContextMenuStrip();
            _nowPlaying = new ToolStripMenuItem("Penumbra") { Enabled = false };
            menu.Items.Add(_nowPlaying);
            menu.Items.Add(new ToolStripSeparator());

            // the panel has every setting; this menu keeps the quick ones
            _panel = new ControlPanel(s, glow, link, Changed, exit);
            NowPlayingScreen.ShowPanel = () => _panel.Open(toggle: false);   // its ⚙ button: the tray icon is covered
            var open = new ToolStripMenuItem("Open Penumbra…", null, (_, _) => _panel.Open());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripMenuItem("Now playing screen", null, (_, _) =>
            {
                if (NowPlayingScreen.Showing) NowPlayingScreen.CloseIfOpen(); else NowPlayingScreen.Open(glow, link);
            }));
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
                Icon = LoadIcon(),
                Text = "Penumbra",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) _panel.Open();
            };
        }

        public void OpenPanel() => _panel.Open(toggle: false);

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
            if (_panel.IsVisible) _panel.Load();   // a change from this menu shows in the open panel too
        }

        internal static bool IsAutoStart()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Penumbra") != null;
        }

        internal static void SetAutoStart(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Penumbra", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Penumbra", false);
        }

        /// <summary>The app used to be called LumnWin: swap an old "start with Windows" entry
        /// (which points at the old LumnWin.exe) for one that starts this Penumbra.exe.</summary>
        internal static void MoveOldAutoStart()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (k?.GetValue("LumnWin") == null) return;
                k.DeleteValue("LumnWin", false);
                SetAutoStart(true);
            }
            catch { }
        }

        static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

        /// <summary>The app's icon (Penumbra.ico, built in) at the size Windows uses for small icons.
        /// Its small sizes are the plain ring; the big ones (shortcuts, Start menu) are the eclipse.</summary>
        internal static Icon LoadIcon()
        {
            using var s = typeof(Tray).Assembly.GetManifestResourceStream("Penumbra.ico")!;
            return new Icon(s, SystemInformation.SmallIconSize);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
