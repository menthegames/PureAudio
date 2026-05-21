using System.Diagnostics;
using System.IO;
using PureAudio.Models;
using TagLib;

namespace PureAudio.Services;

public static class MetadataService
{
    public static AudioFile ReadMetadata(string filePath)
    {
        var audioFile = new AudioFile { FilePath = filePath };

        try
        {
            using var file = TagLib.File.Create(filePath);
            audioFile.Artist = file.Tag.FirstPerformer ?? "Unknown Artist";
            audioFile.Title = file.Tag.Title ?? System.IO.Path.GetFileNameWithoutExtension(filePath);
            audioFile.Album = file.Tag.Album ?? "Unknown Album";
            audioFile.Duration = file.Properties.Duration;
            audioFile.SampleRate = file.Properties.AudioSampleRate;
            audioFile.BitsPerSample = GetBitsPerSample(filePath);

            // Get accurate source bitrate
            audioFile.Bitrate = GetAccurateBitrate(filePath, file);

            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (dir != null)
            {
                var externalCover = TryGetExternalCover(dir);
                if (externalCover != null)
                {
                    audioFile.CoverPath = externalCover;
                }
                else
                {
                    audioFile.CoverPath = ExtractEmbeddedCover(file, filePath);
                }
            }
        }
        catch
        {
            audioFile.Title = System.IO.Path.GetFileNameWithoutExtension(filePath);
        }

        return audioFile;
    }

    private static readonly HashSet<string> LosslessFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif", ".dsf", ".dff", ".ape", ".wv"
    };

    /// <summary>
    /// Gets the accurate source bitrate (not PCM-decoded bitrate).
    /// For lossless formats (FLAC, WAV, etc.): TagLib returns PCM bitrate
    /// (sampleRate * bitsPerSample * channels), which is misleading.
    /// We compute actual bitrate from file size and duration instead.
    /// For lossy formats (MP3, AAC, etc.): TagLib returns source bitrate
    /// correctly for CBR. For VBR, we fall back to file size computation.
    /// </summary>
    private static int GetAccurateBitrate(string filePath, TagLib.File tagFile)
    {
        if (!System.IO.File.Exists(filePath))
            return 0;

        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        bool isLossless = LosslessFormats.Contains(ext);

        if (isLossless)
        {
            // For lossless formats, TagLib returns PCM bitrate which is misleading.
            // Compute actual bitrate from file size and duration.
            return ComputeBitrateFromFileSize(filePath, tagFile);
        }
        else
        {
            // For lossy formats (MP3, AAC, etc.), TagLib returns source bitrate correctly for CBR.
            int raw = tagFile.Properties.AudioBitrate;
            if (raw > 0 && raw <= 1000)
                return raw; // CBR: already in kbps

            // VBR or unknown: compute from file size
            return ComputeBitrateFromFileSize(filePath, tagFile);
        }
    }

    private static int ComputeBitrateFromFileSize(string filePath, TagLib.File tagFile)
    {
        try
        {
            var fileInfo = new System.IO.FileInfo(filePath);
            long fileSizeBytes = fileInfo.Length;
            double durationSeconds = tagFile.Properties.Duration.TotalSeconds;

            // File logging for debugging
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PureAudio", "bitrate_debug.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
            System.IO.File.AppendAllText(logPath,
                $"[BitrateDebug] filePath={filePath}\r\n" +
                $"  fileSizeBytes={fileSizeBytes}\r\n" +
                $"  durationSeconds={durationSeconds}\r\n");

            if (durationSeconds > 0)
            {
                // Bitrate = file size in bits / duration in seconds / 1000
                int bitrate = (int)((fileSizeBytes * 8) / durationSeconds / 1000);
                System.IO.File.AppendAllText(logPath, $"  computed bitrate={bitrate} kbps\r\n");
                if (bitrate > 0)
                    return bitrate;
            }
            else
            {
                System.IO.File.AppendAllText(logPath, $"  durationSeconds <= 0, cannot compute\r\n");
            }
        }
        catch (Exception ex)
        {
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PureAudio", "bitrate_debug.txt");
            System.IO.File.AppendAllText(logPath, $"[BitrateDebug] exception: {ex.Message}\r\n");
        }
        return 0;
    }

    private static int GetBitsPerSample(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".flac" => 24,
            ".wav" => 16,
            ".mp3" => 16,
            _ => 16
        };
    }

    private static string? TryGetExternalCover(string directory)
    {
        var coverNames = new[] { "folder.jpg", "Folder.jpg", "cover.jpg", "Cover.jpg", "folder.png", "Folder.png", "cover.png", "Cover.png" };
        foreach (var name in coverNames)
        {
            var path = System.IO.Path.Combine(directory, name);
            if (System.IO.File.Exists(path))
                return path;
        }
        return null;
    }

    private static string? ExtractEmbeddedCover(TagLib.File file, string audioPath)
    {
        try
        {
            var pictures = file.Tag.Pictures;
            if (pictures.Length > 0)
            {
                var coverDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PureAudio", "Covers");
                System.IO.Directory.CreateDirectory(coverDir);
                var coverPath = System.IO.Path.Combine(coverDir, $"{System.IO.Path.GetFileNameWithoutExtension(audioPath)}.jpg");
                if (!System.IO.File.Exists(coverPath))
                {
                    using var stream = new FileStream(coverPath, FileMode.Create);
                    stream.Write(pictures[0].Data.Data, 0, pictures[0].Data.Data.Length);
                }
                return coverPath;
            }
        }
        catch { }
        return null;
    }
}