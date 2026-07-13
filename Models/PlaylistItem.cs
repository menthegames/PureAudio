namespace PureAudio.Models;

public class PlaylistItem
{
    public AudioFile AudioFile { get; set; } = new();
    public int Index { get; set; }
    
    /// <summary>
    /// If set, this item represents a virtual CUE track within a larger audio file.
    /// </summary>
    public CueTrack? CueTrack { get; set; }
}
