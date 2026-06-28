using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using PureAudio.Models;

namespace PureAudio.Services;

public class PlaylistService
{
    private static readonly string PlaylistFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureAudio", "playlist.json");

    private static readonly string SavedPlaylistsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureAudio", "saved_playlists.json");

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
    /// </summary>
    public void SaveToJson()
    {
        try
        {
            var dir = Path.GetDirectoryName(PlaylistFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var paths = Items.Select(i => i.AudioFile.FilePath).ToList();
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

    // ════════════════════════════════════════════════════════════════
    //  Named / Saved Playlists
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save the current Items as a named playlist.
    /// If a playlist with this name already exists, it will be overwritten.
    /// </summary>
    public void SaveCurrentAs(string name)
    {
        var existing = SavedPlaylists.FirstOrDefault(p => p.Name == name);
        if (existing != null)
        {
            existing.FilePaths = Items.Select(i => i.AudioFile.FilePath).ToList();
        }
        else
        {
            SavedPlaylists.Add(new UserPlaylist
            {
                Name = name,
                FilePaths = Items.Select(i => i.AudioFile.FilePath).ToList()
            });
        }

        CurrentPlaylistName = name;
        SaveSavedPlaylistsToJson();
    }

    /// <summary>
    /// Load a named playlist into Items.
    /// </summary>
    public void LoadPlaylist(string name)
    {
        var playlist = SavedPlaylists.FirstOrDefault(p => p.Name == name);
        if (playlist == null) return;

        Clear();
        CurrentPlaylistName = name;

        // Load file paths — files that don't exist are skipped silently
        foreach (var path in playlist.FilePaths)
        {
            if (File.Exists(path))
            {
                // Read metadata so Artist/Title/etc. are populated correctly
                var audioFile = MetadataService.ReadMetadata(path);
                Items.Add(new PlaylistItem
                {
                    AudioFile = audioFile,
                    Index = Items.Count
                });
            }
        }
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
            SaveSavedPlaylistsToJson();
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
            SaveSavedPlaylistsToJson();
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
    /// </summary>
    public void SaveSavedPlaylistsToJson()
    {
        try
        {
            var dir = Path.GetDirectoryName(SavedPlaylistsFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(SavedPlaylists.ToList(),
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
    public void LoadSavedPlaylistsFromJson()
    {
        try
        {
            if (!File.Exists(SavedPlaylistsFilePath))
                return;

            var json = File.ReadAllText(SavedPlaylistsFilePath);
            var playlists = JsonSerializer.Deserialize<List<UserPlaylist>>(json);
            if (playlists != null)
            {
                SavedPlaylists.Clear();
                foreach (var p in playlists)
                    SavedPlaylists.Add(p);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load saved playlists: {ex.Message}");
        }
    }
}


