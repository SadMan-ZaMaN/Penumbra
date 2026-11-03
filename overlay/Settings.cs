using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penumbra
{
    public enum GlowMode { MusicSync, Steady }

    /// <summary>How often the glow redraws. Fewer frames = less CPU, GPU and battery.</summary>
    public enum Smoothness
    {
        Smooth,     // every screen refresh
        Balanced,   // every 2nd refresh on fast screens (45 fps at 90 Hz, 60 at 60 or 120 Hz)
        Saver,      // about 30 fps
    }

    /// <summary>Where the glow's colors come from. The engine reads this from settings.json too.</summary>
    public enum ColorSource
    {
        Auto,       // album art for music, the screen for videos
        AlbumArt,
        Screen,
        Custom,     // Primary / Secondary below
    }

    /// <summary>User settings, saved to %APPDATA%\Penumbra\settings.json.</summary>
    public class Settings
    {
        public bool Enabled { get; set; } = true;
        public GlowMode Mode { get; set; } = GlowMode.MusicSync;
        public double Thickness { get; set; } = 5;      // the solid line along the edge (px)
        public double Glow { get; set; } = 18;          // the soft halo just inside the line (px)
        public double Brightness { get; set; } = 1.0;   // 0..1
        public double Waviness { get; set; } = 1.0;     // how much the line's thickness ripples: 0 = even, 2 = a lot
        public double CornerRadius { get; set; } = 0;   // px; 0 = square corners, light reaches right into them
        public Smoothness Smoothness { get; set; } = Smoothness.Balanced;
        public ColorSource ColorSource { get; set; } = ColorSource.Auto;
        public bool HiddenVideoThumbnail { get; set; } = false;   // Auto: a minimized / covered video uses its thumbnail, not the screen
        public string Primary { get; set; } = "#7c5cff";
        public string Secondary { get; set; } = "#ff4fa3";
        public bool IdleGlow { get; set; } = false;     // dim glow when nothing is playing (off: screen looks normal)
        public bool ShowLyrics { get; set; } = false;
        public LyricsStyle LyricsStyle { get; set; } = LyricsStyle.Flow;
        public double LyricsOffset { get; set; } = 0.3;  // seconds the lyrics run early (negative = late); the engine reads it
        public bool ShowInCapture { get; set; } = false; // visible in screenshots / recordings
        public bool OledCare { get; set; } = true;       // slowly shift the glow a few px so the same pixels don't wear most

        /// <summary>Back to the default look: automatic colors, default thickness, glow, brightness,
        /// waviness, corners, motion and smoothness. Behaviour switches (lyrics, start with Windows...) stay as they are.</summary>
        public void ResetLook()
        {
            var d = new Settings();
            Mode = d.Mode;
            Thickness = d.Thickness;
            Glow = d.Glow;
            Brightness = d.Brightness;
            Waviness = d.Waviness;
            CornerRadius = d.CornerRadius;
            Smoothness = d.Smoothness;
            ColorSource = d.ColorSource;
            Primary = d.Primary;
            Secondary = d.Secondary;
        }

        static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        static string FilePath => Path.Combine(AppData, "Penumbra", "settings.json");
        static string OldFolder => Path.Combine(AppData, "LumnWin");   // before the app was renamed

        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },   // "Auto" rather than 0, readable from Python
        };

        public static Settings Load()
        {
            MoveOldSettings();
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
            }
            catch { /* corrupt file: start fresh */ }
            return new Settings();
        }

        /// <summary>The app used to be called LumnWin: bring its settings over to the Penumbra folder once.</summary>
        static void MoveOldSettings()
        {
            try
            {
                string old = Path.Combine(OldFolder, "settings.json");
                if (File.Exists(FilePath) || !File.Exists(old)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Move(old, FilePath);
                if (Directory.GetFileSystemEntries(OldFolder).Length == 0) Directory.Delete(OldFolder);
            }
            catch { /* keep going with defaults rather than fail to start */ }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch { }
        }
    }
}
