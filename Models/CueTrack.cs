namespace PureAudio.Models;

public class CueTrack
{
    public string FilePath { get; set; } = string.Empty;          // путь к большому аудиофайлу
    public string Artist { get; set; } = string.Empty;            // исполнитель трека
    public string Title { get; set; } = string.Empty;             // название трека
    public string Album { get; set; } = string.Empty;             // название альбома
    public int TrackNumber { get; set; }          // номер трека
    public TimeSpan StartPosition { get; set; }   // позиция начала в файле
    public TimeSpan EndPosition { get; set; }     // позиция конца в файле
    public TimeSpan Duration => EndPosition - StartPosition;
    public string CueFilePath { get; set; } = string.Empty;       // путь к CUE-файлу (для группировки)
}
