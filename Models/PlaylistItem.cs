namespace PureAudio.Models;

public class PlaylistItem
{
    public AudioFile AudioFile { get; set; } = new();
    public int Index { get; set; }
}