namespace PureAudio.Models;

/// <summary>
/// Represents a visual segment in the segmented progress bar for CUE albums.
/// Each segment corresponds to one track in the album.
/// </summary>
public class CueSegment
{
    /// <summary>
    /// Start position as a ratio of the total album duration (0..1).
    /// </summary>
    public double StartRatio { get; set; }

    /// <summary>
    /// End position as a ratio of the total album duration (0..1).
    /// </summary>
    public double EndRatio { get; set; }

    /// <summary>
    /// True if the track is present in the playlist (active).
    /// False if the track has been removed (dimmed/grey).
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Track number from the CUE sheet.
    /// </summary>
    public int TrackNumber { get; set; }

    /// <summary>
    /// Unique identifier for the track: "FilePath|StartPosition.Ticks" for CUE tracks,
    /// or just "FilePath" for regular tracks. Used to track segment identity across playlist changes.
    /// </summary>
    public string TrackId { get; set; } = string.Empty;
}
