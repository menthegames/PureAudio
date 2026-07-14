using System.IO;
using System.Text.Json;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Handles all JSON persistence for playlists (current playlist and saved/named playlists).
/// Extracted from PlaylistService to follow Single Responsibility Principle.
/// </summary>
public class PlaylistPersistenceService
{
    private static readonly string PlaylistFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureAudio", "playlist.json");

    private static readonly string SavedPlaylistsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureAudio", "saved_playlists.json");

    /// <summary>
    /// Save the current playlist (file paths) to JSON.
    /// </summary>
    public void SaveToJson(IEnumerable<PlaylistItem> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(PlaylistFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var paths = items.Select(i => i.AudioFile.FilePath).ToList();
            var json = JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PlaylistFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save playlist: {ex.Message}");
        }
    }

    /// <summary>
    /// Load the playlist from JSON. Returns the list of file paths that were loaded.
    /// </summary>
    public List<string> LoadFromJson()
    {
        try
        {
            if (!File.Exists(PlaylistFilePath))
                return new List<string>();

            var json = File.ReadAllText(PlaylistFilePath);
            var paths = JsonSerializer.Deserialize<List<string>>(json);
            return paths ?? new List<string>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load playlist: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Save all named playlists to JSON.
    /// </summary>
    public void SaveSavedPlaylistsToJson(IEnumerable<UserPlaylist> playlists)
    {
        try
        {
            var dir = Path.GetDirectoryName(SavedPlaylistsFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(playlists.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavedPlaylistsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save playlists: {ex.Message}");
        }
    }

    /// <summary>
    /// Load all named playlists from JSON.
    /// </summary>
    public List<UserPlaylist> LoadSavedPlaylistsFromJson()
    {
        try
        {
            if (!File.Exists(SavedPlaylistsFilePath))
                return new List<UserPlaylist>();

            var json = File.ReadAllText(SavedPlaylistsFilePath);
            var playlists = JsonSerializer.Deserialize<List<UserPlaylist>>(json);
            return playlists ?? new List<UserPlaylist>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load saved playlists: {ex.Message}");
            return new List<UserPlaylist>();
        }
    }
}
