namespace PureAudio.Models;

public class AudioFile
{
    public string FilePath { get; set; } = "";
    public string Artist { get; set; } = "Unknown Artist";
    public string Title { get; set; } = "Unknown Title";
    public string Album { get; set; } = "Unknown Album";
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public int Bitrate { get; set; } = 0;
    public int SampleRate { get; set; } = 0;
    public int BitsPerSample { get; set; } = 16;
    public string? CoverPath { get; set; }
    public bool IsHighRes => SampleRate > 48000 || BitsPerSample > 16;
}