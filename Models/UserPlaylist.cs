namespace PureAudio.Models;

/// <summary>
/// A named, saved playlist — a list of playlist entries with a user-assigned name.
/// Each entry stores the file path and optional CUE track information.
/// </summary>
public class UserPlaylist
{
    public string Name { get; set; } = "New Playlist";
    public List<PlaylistEntryDto> Entries { get; set; } = new();
}
