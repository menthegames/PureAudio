namespace PureAudio.Models;

public class AudioSettings
{
    public bool BitPerfectEnabled { get; set; } = false;
    public bool WarningAccepted { get; set; } = false;
    public double Volume { get; set; } = 0.5;
    public bool IsExpanded { get; set; } = false;
    public bool IsDarkTheme { get; set; } = true;
    public bool IsHiresMode { get; set; } = false;
}
