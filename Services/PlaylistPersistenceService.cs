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
    /// Save the current playlist to JSON.
    /// Each item is saved as a DTO with FilePath and full CueTrack metadata (if applicable).
    /// </summary>
    public void SaveToJson(IEnumerable<PlaylistItem> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(PlaylistFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var dtos = items.Select(i =>
            {
                var dto = new PlaylistEntryDto
                {
                    FilePath = i.AudioFile.FilePath,
                };

                if (i.CueTrack != null)
                {
                    dto.CueFilePath = i.CueTrack.CueFilePath;
                    dto.CueTrackNumber = i.CueTrack.TrackNumber;
                    dto.CueArtist = i.CueTrack.Artist;
                    dto.CueTitle = i.CueTrack.Title;
                    dto.CueAlbum = i.CueTrack.Album;
                    dto.CueStartPosition = FormatTimeSpan(i.CueTrack.StartPosition);
                    dto.CueEndPosition = FormatTimeSpan(i.CueTrack.EndPosition);
                }

                return dto;
            }).ToList();

            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PlaylistFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save playlist: {ex.Message}");
        }
    }

    /// <summary>
    /// Format a TimeSpan as "mm:ss.ff" for JSON serialization.
    /// </summary>
    private static string FormatTimeSpan(TimeSpan ts)
    {
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    /// <summary>
    /// Parse a "mm:ss.ff" string back to a TimeSpan.
    /// Returns TimeSpan.Zero on failure.
    /// </summary>
    private static TimeSpan ParseTimeSpan(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return TimeSpan.Zero;

        try
        {
            var parts = value.Split(':');
            if (parts.Length == 2)
            {
                var secParts = parts[1].Split('.');
                int minutes = int.Parse(parts[0]);
                int seconds = int.Parse(secParts[0]);
                int centiseconds = secParts.Length > 1 ? int.Parse(secParts[1]) : 0;
                return new TimeSpan(0, 0, minutes, seconds, centiseconds * 10);
            }
        }
        catch
        {
            // Fall through
        }

        return TimeSpan.Zero;
    }


    /// <summary>
    /// Load the playlist from JSON. Returns the list of saved DTOs.
    /// </summary>
    public List<PlaylistEntryDto> LoadFromJson()
    {
        try
        {
            if (!File.Exists(PlaylistFilePath))
                return new List<PlaylistEntryDto>();

            var json = File.ReadAllText(PlaylistFilePath);

            // Try to deserialize as the new DTO format first
            try
            {
                var dtos = JsonSerializer.Deserialize<List<PlaylistEntryDto>>(json);
                if (dtos != null && dtos.Count > 0)
                    return dtos;
            }
            catch
            {
                // Fall through to legacy format
            }

            // Legacy format: list of plain file paths (string list)
            var legacyPaths = JsonSerializer.Deserialize<List<string>>(json);
            if (legacyPaths != null && legacyPaths.Count > 0)
            {
                return legacyPaths.Select(p => new PlaylistEntryDto { FilePath = p }).ToList();
            }

            return new List<PlaylistEntryDto>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load playlist: {ex.Message}");
            return new List<PlaylistEntryDto>();
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
