using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumnWin
{
    public enum GlowMode { MusicSync, Steady }

    /// <summary>Where the glow's colors come from. The engine reads this from settings.json too.</summary>
    public enum ColorSource
    {
        Auto,       // album art for music, the screen for videos
        AlbumArt,
        Screen,
        Custom,     // Primary / Secondary below
    }

    /// <summary>User settings, saved to %APPDATA%\LumnWin\settings.json.</summary>
    public class Settings
    {
        public bool Enabled { get; set; } = true;
        public GlowMode Mode { get; set; } = GlowMode.MusicSync;
        public double Thickness { get; set; } = 8;      // bright rim at the very edge (px)
        public double Glow { get; set; } = 90;          // how far the light spills inward (px)
        public double Brightness { get; set; } = 1.0;   // 0..1
        public double Waviness { get; set; } = 1.0;     // 0 = straight, 2 = very wavy
        public double CornerRadius { get; set; } = 0;   // px; 0 = square corners, light reaches right into them
        public ColorSource ColorSource { get; set; } = ColorSource.Auto;
        public string Primary { get; set; } = "#7c5cff";
        public string Secondary { get; set; } = "#ff4fa3";
        public bool IdleGlow { get; set; } = false;     // dim glow when nothing is playing (off: screen looks normal)
        public bool ShowLyrics { get; set; } = false;
        public bool ShowInCapture { get; set; } = false; // visible in screenshots / recordings

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumnWin", "settings.json");

        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },   // "Auto" rather than 0, readable from Python
        };

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
            }
            catch { /* corrupt file: start fresh */ }
            return new Settings();
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
