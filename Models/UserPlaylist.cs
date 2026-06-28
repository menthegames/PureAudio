namespace PureAudio.Models;

/// <summary>
/// A named, saved playlist — a list of audio file paths with a user-assigned name.
/// </summary>
public class UserPlaylist
{
    public string Name { get; set; } = "New Playlist";
    public List<string> FilePaths { get; set; } = new();
}
