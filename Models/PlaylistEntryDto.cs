namespace PureAudio.Models;

/// <summary>
/// Serializable DTO for persisting playlist items.
/// Stores the physical file path and optional CUE track information.
/// For CUE tracks, all metadata (Artist, Title, Album, positions) is stored
/// directly so that on reload we don't need to re-parse the CUE file.
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

    // ──────────────────────────────────────────────────────────────
    //  Full CueTrack metadata (serialized to avoid re-parsing CUE)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Artist name from the CUE sheet (e.g. "Pink Floyd").
    /// </summary>
    public string? CueArtist { get; set; }

    /// <summary>
    /// Track title from the CUE sheet (e.g. "Comfortably Numb").
    /// </summary>
    public string? CueTitle { get; set; }

    /// <summary>
    /// Album name from the CUE sheet.
    /// </summary>
    public string? CueAlbum { get; set; }

    /// <summary>
    /// Start position of this track within the audio file (as "mm:ss.ff").
    /// </summary>
    public string? CueStartPosition { get; set; }

    /// <summary>
    /// End position of this track within the audio file (as "mm:ss.ff").
    /// </summary>
    public string? CueEndPosition { get; set; }
}
