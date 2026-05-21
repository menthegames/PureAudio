using System.IO;
using System.Text.Json;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Persists AudioSettings (WASAPI, gapless, expanded, theme, hires mode, volume) to JSON.
/// Uses the same app data folder as LibraryService and PlaylistService.
/// </summary>
public class SettingsService
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureAudio", "settings.json");

    private AudioSettings _current;

    public SettingsService()
    {
        _current = Load();
    }

    public AudioSettings Current => _current;

    /// <summary>
    /// Load settings from disk. Returns defaults if file doesn't exist or is corrupt.
    /// </summary>
    private static AudioSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return new AudioSettings();

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AudioSettings>(json);
            return settings ?? new AudioSettings();
        }
        catch
        {
            return new AudioSettings();
        }
    }

    /// <summary>
    /// Save the current settings to disk.
    /// </summary>
    public void Save()
    {
        Save(_current);
    }

    /// <summary>
    /// Save the specified settings to disk.
    /// </summary>
    public void Save(AudioSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
            _current = settings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Update a single property and persist immediately.
    /// </summary>
    public void Update(Action<AudioSettings> update)
    {
        update(_current);
        Save();
    }
}
