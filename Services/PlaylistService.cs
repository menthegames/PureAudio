using System.Collections.ObjectModel;
using System.IO;
using PureAudio.Models;

namespace PureAudio.Services;

public class PlaylistService
{
    private readonly PlaylistPersistenceService _persistence = new();

    public ObservableCollection<PlaylistItem> Items { get; } = new();
    public int CurrentIndex { get; set; } = -1;
    public PlaylistItem? CurrentItem => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    /// <summary>
    /// Saved named playlists.
    /// </summary>
    public ObservableCollection<UserPlaylist> SavedPlaylists { get; } = new();

    /// <summary>
    /// Name of the currently loaded named playlist, or null if unsaved/temporary.
    /// </summary>
    public string? CurrentPlaylistName { get; set; } = null;


    public void Add(IEnumerable<AudioFile> files)
    {
        foreach (var file in files)
        {
            if (!Items.Any(i => i.AudioFile.FilePath == file.FilePath))
            {
                Items.Add(new PlaylistItem { AudioFile = file, Index = Items.Count });
            }
        }
    }

    public void Add(AudioFile file)
    {
        if (!Items.Any(i => i.AudioFile.FilePath == file.FilePath))
        {
            Items.Add(new PlaylistItem { AudioFile = file, Index = Items.Count });
        }
    }

    /// <summary>
    /// Add a CUE track (virtual track within a larger audio file) to the playlist.
    /// Uses the physical file path for duplicate detection, but stores CueTrack info
    /// for proper playback (seek to StartPosition, stop at EndPosition).
    /// </summary>
    public void Add(AudioFile file, CueTrack cueTrack)
    {
        if (!Items.Any(i => i.AudioFile.FilePath == file.FilePath && i.CueTrack?.StartPosition == cueTrack.StartPosition))
        {
            Items.Add(new PlaylistItem { AudioFile = file, Index = Items.Count, CueTrack = cueTrack });
        }
    }

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Items.RemoveAt(index);
            Reindex();
        }
    }

    public void MoveUp(int index)
    {
        if (index > 0 && index < Items.Count)
        {
            Items.Move(index, index - 1);
            if (CurrentIndex == index) CurrentIndex = index - 1;
            else if (CurrentIndex == index - 1) CurrentIndex = index;
            Reindex();
        }
    }

    public void MoveDown(int index)
    {
        if (index >= 0 && index < Items.Count - 1)
        {
            Items.Move(index, index + 1);
            if (CurrentIndex == index) CurrentIndex = index + 1;
            else if (CurrentIndex == index + 1) CurrentIndex = index;
            Reindex();
        }
    }

    public void Clear()
    {
        Items.Clear();
        CurrentIndex = -1;
    }

    public PlaylistItem? GetNext()
    {
        if (CurrentIndex < Items.Count - 1)
        {
            CurrentIndex++;
            return Items[CurrentIndex];
        }
        return null;
    }

    public PlaylistItem? GetPrevious()
    {
        if (CurrentIndex > 0)
        {
            CurrentIndex--;
            return Items[CurrentIndex];
        }
        return null;
    }

    private void Reindex()
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].Index = i;
    }

    /// <summary>
    /// Save the current playlist (file paths) to JSON.
    /// Delegated to PlaylistPersistenceService.
    /// </summary>
    public void SaveToJson()
    {
        _persistence.SaveToJson(Items);
    }

    /// <summary>
    /// Load the playlist from JSON. Returns the list of saved DTOs.
    /// Delegated to PlaylistPersistenceService.
    /// </summary>
    public List<PlaylistEntryDto> LoadFromJson()
    {
        return _persistence.LoadFromJson();
    }

    // ════════════════════════════════════════════════════════════════
    //  Named / Saved Playlists
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save the current Items as a named playlist.
    /// If a playlist with this name already exists, it will be overwritten.
    /// For CUE tracks, all metadata is saved to avoid re-parsing on load.
    /// </summary>
    public void SaveCurrentAs(string name)
    {
        var existing = SavedPlaylists.FirstOrDefault(p => p.Name == name);
        if (existing != null)
        {
            existing.Entries = Items.Select(i => ToEntryDto(i)).ToList();
        }
        else
        {
            SavedPlaylists.Add(new UserPlaylist
            {
                Name = name,
                Entries = Items.Select(i => ToEntryDto(i)).ToList()
            });
        }

        CurrentPlaylistName = name;
        _persistence.SaveSavedPlaylistsToJson(SavedPlaylists);
    }

    /// <summary>
    /// Convert a PlaylistItem to a PlaylistEntryDto, preserving full CueTrack metadata.
    /// </summary>
    private static PlaylistEntryDto ToEntryDto(PlaylistItem item)
    {
        var dto = new PlaylistEntryDto
        {
            FilePath = item.AudioFile.FilePath,
        };

        if (item.CueTrack != null)
        {
            dto.CueFilePath = item.CueTrack.CueFilePath;
            dto.CueTrackNumber = item.CueTrack.TrackNumber;
            dto.CueArtist = item.CueTrack.Artist;
            dto.CueTitle = item.CueTrack.Title;
            dto.CueAlbum = item.CueTrack.Album;
            dto.CueStartPosition = FormatTimeSpan(item.CueTrack.StartPosition);
            dto.CueEndPosition = FormatTimeSpan(item.CueTrack.EndPosition);
        }

        return dto;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

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
    /// Load a named playlist into Items.
    /// For CUE tracks, uses saved metadata directly instead of re-parsing the CUE file.
    /// </summary>
    public void LoadPlaylist(string name)
    {
        var playlist = SavedPlaylists.FirstOrDefault(p => p.Name == name);
        if (playlist == null) return;

        Clear();
        CurrentPlaylistName = name;

        // Load entries — files that don't exist are skipped silently
        foreach (var entry in playlist.Entries)
        {
            if (!File.Exists(entry.FilePath))
                continue;

            var audioFile = MetadataService.ReadMetadata(entry.FilePath);

            // If this entry has full CUE metadata saved, restore CueTrack from it
            if (HasFullCueData(entry))
            {
                var cueTrack = RestoreCueTrack(entry);
                if (cueTrack != null)
                {
                    // Override AudioFile metadata with CUE metadata for display
                    audioFile.Artist = cueTrack.Artist;
                    audioFile.Title = cueTrack.Title;
                    audioFile.Album = cueTrack.Album;

                    Items.Add(new PlaylistItem
                    {
                        AudioFile = audioFile,
                        Index = Items.Count,
                        CueTrack = cueTrack
                    });
                    continue;
                }
            }

            // Fallback: add as a regular track
            Items.Add(new PlaylistItem
            {
                AudioFile = audioFile,
                Index = Items.Count
            });
        }
    }

    /// <summary>
    /// Check if a DTO has full CUE metadata (not just track number).
    /// </summary>
    private static bool HasFullCueData(PlaylistEntryDto entry)
    {
        return !string.IsNullOrEmpty(entry.CueArtist)
            && !string.IsNullOrEmpty(entry.CueTitle)
            && entry.CueTrackNumber.HasValue;
    }

    /// <summary>
    /// Restore a CueTrack from saved DTO data without re-parsing the CUE file.
    /// </summary>
    private static CueTrack? RestoreCueTrack(PlaylistEntryDto entry)
    {
        if (string.IsNullOrEmpty(entry.CueArtist) || string.IsNullOrEmpty(entry.CueTitle))
            return null;

        return new CueTrack
        {
            FilePath = entry.FilePath,
            Artist = entry.CueArtist,
            Title = entry.CueTitle,
            Album = entry.CueAlbum ?? string.Empty,
            TrackNumber = entry.CueTrackNumber ?? 0,
            StartPosition = ParseTimeSpan(entry.CueStartPosition),
            EndPosition = ParseTimeSpan(entry.CueEndPosition),
            CueFilePath = entry.CueFilePath ?? string.Empty
        };
    }


    /// <summary>
    /// Delete a named playlist.
    /// </summary>
    public void DeletePlaylist(string name)
    {
        var playlist = SavedPlaylists.FirstOrDefault(p => p.Name == name);
        if (playlist != null)
        {
            SavedPlaylists.Remove(playlist);
            if (CurrentPlaylistName == name)
                CurrentPlaylistName = null;
            _persistence.SaveSavedPlaylistsToJson(SavedPlaylists);
        }
    }

    /// <summary>
    /// Rename a saved playlist.
    /// </summary>
    public void RenamePlaylist(string oldName, string newName)
    {
        var playlist = SavedPlaylists.FirstOrDefault(p => p.Name == oldName);
        if (playlist != null)
        {
            playlist.Name = newName;
            if (CurrentPlaylistName == oldName)
                CurrentPlaylistName = newName;
            _persistence.SaveSavedPlaylistsToJson(SavedPlaylists);
        }
    }

    /// <summary>
    /// Get all saved playlist names.
    /// </summary>
    public List<string> GetPlaylistNames()
    {
        return SavedPlaylists.Select(p => p.Name).ToList();
    }

    /// <summary>
    /// Save all named playlists to JSON.
    /// Delegated to PlaylistPersistenceService.
    /// </summary>
    public void SaveSavedPlaylistsToJson()
    {
        _persistence.SaveSavedPlaylistsToJson(SavedPlaylists);
    }

    /// <summary>
    /// Load all named playlists from JSON.
    /// Delegated to PlaylistPersistenceService.
    /// </summary>
    public void LoadSavedPlaylistsFromJson()
    {
        var playlists = _persistence.LoadSavedPlaylistsFromJson();
        SavedPlaylists.Clear();
        foreach (var p in playlists)
            SavedPlaylists.Add(p);
    }
}
