using System.Text.Json.Serialization;

namespace PureAudio.Models;

/// <summary>
/// Serializable cache of the entire library tree and file metadata.
/// Saved to disk alongside sources.json for fast startup.
/// </summary>
public class LibraryCacheData
{
    public string Version { get; set; } = "1.1";
    public DateTime LastUpdated { get; set; }
    public List<CachedFileEntry> Files { get; set; } = new();
    public List<CachedTreeNode> Tree { get; set; } = new();
}

/// <summary>
/// Serializable metadata entry for a single audio file.
/// </summary>
public class CachedFileEntry
{
    public string FilePath { get; set; } = "";
    public string Artist { get; set; } = "Unknown Artist";
    public string Title { get; set; } = "Unknown Title";
    public string Album { get; set; } = "Unknown Album";
    public double DurationSeconds { get; set; }
    public int SampleRate { get; set; }
    public int BitsPerSample { get; set; }
    public int Bitrate { get; set; }
    public string? CoverPath { get; set; }
}

/// <summary>
/// Serializable tree node — mirrors LibraryNode structure but without
/// ObservableCollection or INotifyPropertyChanged dependencies.
/// </summary>
public class CachedTreeNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFolder { get; set; }
    public List<CachedTreeNode> Children { get; set; } = new();

    /// <summary>
    /// For file nodes: the file path used to look up CachedFileEntry.
    /// Null for folder nodes.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// For CUE sheet track nodes: serialized CueTrack data.
    /// Null for regular file nodes and folder nodes.
    /// </summary>
    public CachedCueTrack? CueTrack { get; set; }
}

/// <summary>
/// Serializable version of CueTrack for cache persistence.
/// </summary>
public class CachedCueTrack
{
    public string FilePath { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public int TrackNumber { get; set; }
    public double StartPositionSeconds { get; set; }
    public double EndPositionSeconds { get; set; }
    public string CueFilePath { get; set; } = "";
}


