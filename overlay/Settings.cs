using System;
using System.IO;
using System.Text.Json;

namespace LumnWin
{
    public enum GlowMode { MusicSync, Steady }

    /// <summary>User settings, saved to %APPDATA%\LumnWin\settings.json.</summary>
    public class Settings
    {
        public bool Enabled { get; set; } = true;
        public GlowMode Mode { get; set; } = GlowMode.MusicSync;
        public double Thickness { get; set; } = 8;      // bright rim at the very edge (px)
        public double Glow { get; set; } = 160;         // how far the light spills inward (px)
        public double Brightness { get; set; } = 0.85;  // 0..1
        public bool FollowAlbumColors { get; set; } = true;
        public string Primary { get; set; } = "#7c5cff";
        public string Secondary { get; set; } = "#ff4fa3";
        public bool IdleGlow { get; set; } = true;      // dim glow while paused
        public bool ShowLyrics { get; set; } = false;

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumnWin", "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { /* corrupt file: start fresh */ }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
