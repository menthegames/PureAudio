using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Service for parsing CUE sheet files (.cue) and extracting track information.
/// Uses manual parsing for maximum compatibility with various CUE formats.
/// </summary>
public static class CueSheetService
{
    /// <summary>
    /// Checks if the given file path has a .cue extension.
    /// </summary>
    public static bool IsCueFile(string path)
    {
        return Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a CUE sheet file and returns a list of CueTrack objects.
    /// Each track contains the reference to the audio file, artist, title,
    /// album, track number, and start/end positions.
    /// </summary>
    /// <param name="cuePath">Full path to the .cue file.</param>
    /// <returns>List of parsed CueTrack objects. Returns empty list if parsing fails.</returns>
    public static List<CueTrack> ParseCueFile(string cuePath)
    {
        var result = new List<CueTrack>();

        if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath))
            return result;

        try
        {
            var lines = File.ReadAllLines(cuePath);
            if (lines.Length == 0)
                return result;

            // Parse CUE metadata
            string albumArtist = string.Empty;
            string albumTitle = string.Empty;
            string audioFilePath = string.Empty;

            // Parse the FILE line to find the audio file
            string? cueDir = Path.GetDirectoryName(cuePath);
            if (string.IsNullOrEmpty(cueDir))
                return result;

            // Temporary track storage
            var rawTracks = new List<RawCueTrack>();

            RawCueTrack? currentTrack = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))
                {
                    // REM comments — skip
                    continue;
                }

                if (trimmed.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                {
                    string value = ExtractQuotedValue(trimmed);
                    if (!string.IsNullOrEmpty(value))
                    {
                        albumArtist = value;
                        // Also apply to current track if exists
                        if (currentTrack != null && string.IsNullOrEmpty(currentTrack.Artist))
                            currentTrack.Artist = value;
                    }
                    continue;
                }

                if (trimmed.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                {
                    string value = ExtractQuotedValue(trimmed);
                    if (currentTrack != null)
                    {
                        currentTrack.Title = value;
                    }
                    else
                    {
                        albumTitle = value;
                    }
                    continue;
                }

                if (trimmed.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract audio filename from FILE line
                    audioFilePath = ExtractAudioFileName(trimmed, cueDir);
                    continue;
                }

                if (trimmed.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                {
                    // Save previous track
                    if (currentTrack != null)
                        rawTracks.Add(currentTrack);

                    // Parse track number
                    int trackNumber = 0;
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int tn))
                        trackNumber = tn;

                    currentTrack = new RawCueTrack
                    {
                        TrackNumber = trackNumber,
                        Artist = albumArtist
                    };
                    continue;
                }

                if (trimmed.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
                {
                    // Parse INDEX 01 00:00:00 format
                    var indexParts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (indexParts.Length >= 3 && int.TryParse(indexParts[1], out int indexNum))
                    {
                        if (indexNum == 0 && currentTrack.Index00 == TimeSpan.Zero)
                        {
                            currentTrack.Index00 = ParseCueTime(indexParts[2]);
                        }
                        else if (indexNum == 1)
                        {
                            currentTrack.Index01 = ParseCueTime(indexParts[2]);
                        }
                    }
                    continue;
                }
            }

            // Don't forget the last track
            if (currentTrack != null)
                rawTracks.Add(currentTrack);

            // If no audio file found via FILE directive, try to find it in the same directory
            if (string.IsNullOrEmpty(audioFilePath))
            {
                audioFilePath = FindAudioFileInDirectory(cueDir);
            }

            if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
                return result;

            // Get total duration of the audio file for the last track's end position
            TimeSpan totalDuration = GetAudioDuration(audioFilePath);

            // Convert raw tracks to CueTrack objects
            for (int i = 0; i < rawTracks.Count; i++)
            {
                var raw = rawTracks[i];

                // Use INDEX 01 as start position; fall back to INDEX 00 if 01 is missing
                TimeSpan startPos = raw.Index01 != TimeSpan.Zero ? raw.Index01 : raw.Index00;

                // Calculate end position
                TimeSpan endPos;
                if (i < rawTracks.Count - 1)
                {
                    var nextRaw = rawTracks[i + 1];
                    endPos = nextRaw.Index01 != TimeSpan.Zero ? nextRaw.Index01 : nextRaw.Index00;
                }
                else
                {
                    endPos = totalDuration;
                }

                var cueTrack = new CueTrack
                {
                    FilePath = audioFilePath,
                    Artist = !string.IsNullOrEmpty(raw.Artist) ? raw.Artist : "Unknown Artist",
                    Title = !string.IsNullOrEmpty(raw.Title) ? raw.Title : $"Track {raw.TrackNumber}",
                    Album = !string.IsNullOrEmpty(albumTitle) ? albumTitle : "Unknown Album",
                    TrackNumber = raw.TrackNumber,
                    StartPosition = startPos,
                    EndPosition = endPos,
                    CueFilePath = cuePath
                };

                result.Add(cueTrack);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"CueSheetService.ParseCueFile: Error parsing CUE file '{cuePath}': {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Extracts a quoted value from a CUE directive line.
    /// Example: TITLE "Song Name" -> Song Name
    /// </summary>
    private static string ExtractQuotedValue(string line)
    {
        int firstQuote = line.IndexOf('"');
        if (firstQuote < 0)
            return string.Empty;

        int secondQuote = line.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0)
            return string.Empty;

        return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    /// <summary>
    /// Extracts the audio file name from a FILE directive and resolves it to a full path.
    /// </summary>
    private static string ExtractAudioFileName(string fileLine, string cueDir)
    {
        // Format: FILE "filename.wav" WAVE
        string fileName = ExtractQuotedValue(fileLine);

        if (string.IsNullOrEmpty(fileName))
        {
            // Try without quotes: FILE filename.wav WAVE
            var parts = fileLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                fileName = parts[1];
        }

        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        string fullPath = Path.Combine(cueDir, fileName);
        if (File.Exists(fullPath))
            return Path.GetFullPath(fullPath);

        // Try with common extensions if no extension
        if (!Path.HasExtension(fileName))
        {
            foreach (var ext in new[] { ".flac", ".wav", ".ape", ".wv", ".aiff", ".aif", ".mp3", ".m4a", ".ogg", ".wma" })
            {
                fullPath = Path.Combine(cueDir, fileName + ext);
                if (File.Exists(fullPath))
                    return Path.GetFullPath(fullPath);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Tries to find an audio file in the given directory as a fallback.
    /// </summary>
    private static string FindAudioFileInDirectory(string directory)
    {
        try
        {
            var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".flac", ".wav", ".ape", ".wv", ".aiff", ".aif", ".mp3", ".m4a", ".ogg", ".wma"
            };

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (audioExtensions.Contains(Path.GetExtension(file)))
                {
                    // Return the first audio file found
                    return Path.GetFullPath(file);
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses a CUE time string (MM:SS:FF or MM:SS:FF) into a TimeSpan.
    /// CUE time format: minutes:seconds:frames (75 frames per second)
    /// </summary>
    private static TimeSpan ParseCueTime(string timeStr)
    {
        try
        {
            var parts = timeStr.Split(':');
            if (parts.Length == 3)
            {
                int minutes = int.Parse(parts[0]);
                int seconds = int.Parse(parts[1]);
                int frames = int.Parse(parts[2]);

                // 75 frames per second in CD format
                double totalSeconds = minutes * 60 + seconds + (double)frames / 75.0;
                return TimeSpan.FromSeconds(totalSeconds);
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Gets the total duration of an audio file using TagLib.
    /// </summary>
    private static TimeSpan GetAudioDuration(string audioFilePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            return tagFile.Properties.Duration;
        }
        catch
        {
            // If TagLib fails, return a reasonable default (1 hour)
            return TimeSpan.FromHours(1);
        }
    }

    /// <summary>
    /// Internal class for storing raw parsed CUE track data before conversion.
    /// </summary>
    private class RawCueTrack
    {
        public int TrackNumber { get; set; }
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public TimeSpan Index00 { get; set; }  // pregap
        public TimeSpan Index01 { get; set; }  // actual start
    }
}
