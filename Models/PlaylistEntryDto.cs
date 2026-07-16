namespace PureAudio.Models;

/// <summary>
/// Serializable DTO for persisting playlist items.
/// Stores the physical file path and optional CUE track information.
/// </summary>
public class PlaylistEntryDto
{
    /// <summary>
    /// Path to the physical audio file.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the CUE sheet file, if this item is a CUE track.
    /// </summary>
    public string? CueFilePath { get; set; }

    /// <summary>
    /// Track number within the CUE sheet, if this item is a CUE track.
    /// </summary>
    public int? CueTrackNumber { get; set; }
}
