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

    public ObservableCollection<PlaylistItem> Items { get; } = new();
    public int CurrentIndex { get; set; } = -1;
    public PlaylistItem? CurrentItem => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

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
}
