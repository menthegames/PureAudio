using System.IO;
using PureAudio.Models;
using PureAudio.Helpers;
using TagLib;
using NAudio.Wave;

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
            audioFile.BitsPerSample = GetRealBitsPerSample(filePath);

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

            Logger.Log($"MetadataService: file {filePath} -> {audioFile.BitsPerSample} bit / {audioFile.SampleRate} Hz");
        }
        catch (Exception ex)
        {
            Logger.Log($"MetadataService: error reading {filePath}: {ex.Message}");
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

            if (durationSeconds > 0)
            {
                // Bitrate = file size in bits / duration in seconds / 1000
                int bitrate = (int)((fileSizeBytes * 8) / durationSeconds / 1000);
                if (bitrate > 0)
                    return bitrate;
            }
        }
        catch
        {
        }
        return 0;
    }

    /// <summary>
    /// Reads the actual bits-per-sample from the audio file using NAudio readers.
    /// - .flac: uses BunLabs.NAudio.Flac (NAudio.Flac.FlacReader) for accurate bit depth
    /// - .wav: uses NAudio.WaveFileReader for accurate bit depth
    /// - .aiff/.aif: uses NAudio.AiffFileReader for accurate bit depth
    /// - .mp3, .aac, .ogg, .wma: lossy formats, always return 16
    /// - .dsf, .dff: DSD formats, return 1 (1-bit DSD)
    /// Falls back to 16 on any error.
    /// </summary>
    private static int GetRealBitsPerSample(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            switch (ext)
            {
                case ".flac":
                    using (var flacReader = new NAudio.Flac.FlacReader(filePath))
                    {
                        int bits = flacReader.WaveFormat.BitsPerSample;
                        Logger.Log($"GetRealBitsPerSample: FlacReader reports {bits} bit for {filePath}");
                        return bits;
                    }

                case ".wav":
                    using (var wavReader = new WaveFileReader(filePath))
                    {
                        int bits = wavReader.WaveFormat.BitsPerSample;
                        Logger.Log($"GetRealBitsPerSample: WaveFileReader reports {bits} bit for {filePath}");
                        return bits;
                    }

                case ".aiff":
                case ".aif":
                    // NAudio does not have a built-in AiffFileReader in all versions.
                    // Use AudioFileReader as fallback — it converts to float internally,
                    // but WaveFormat.BitsPerSample may report 32. We'll try to detect
                    // the actual bit depth from the file header manually.
                    try
                    {
                        using (var reader = new AudioFileReader(filePath))
                        {
                            int bits = reader.WaveFormat.BitsPerSample;
                            // AudioFileReader always outputs IEEE float (32 bit).
                            // For AIFF, the actual source bit depth is typically 16 or 24.
                            // We'll try to read the SSND chunk to determine real bit depth.
                            int realBits = ReadAiffBitsPerSample(filePath);
                            Logger.Log($"GetRealBitsPerSample: AIFF reader reports {bits} bit, detected {realBits} bit for {filePath}");
                            return realBits > 0 ? realBits : 16;
                        }
                    }
                    catch
                    {
                        Logger.Log($"GetRealBitsPerSample: AIFF fallback to 16 bit for {filePath}");
                        return 16;
                    }

                case ".mp3":
                case ".aac":
                case ".m4a":
                case ".mp4":
                case ".ogg":
                case ".wma":
                    Logger.Log($"GetRealBitsPerSample: lossy format {ext}, returning 16 bit for {filePath}");
                    return 16;

                case ".dsf":
                case ".dff":
                    Logger.Log($"GetRealBitsPerSample: DSD format {ext}, returning 1 bit for {filePath}");
                    return 1;

                default:
                    Logger.Log($"GetRealBitsPerSample: unknown format {ext}, defaulting to 16 bit for {filePath}");
                    return 16;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"GetRealBitsPerSample: error reading {filePath}: {ex.Message}, defaulting to 16");
            return 16;
        }
    }

    /// <summary>
    /// Reads the actual bits-per-sample from an AIFF file by parsing the header.
    /// AIFF files store the sample size in the Common Chunk (COMM) at byte offset 20 from chunk start.
    /// </summary>
    private static int ReadAiffBitsPerSample(string filePath)
    {
        try
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                // AIFF header: "FORM" (4) + size (4) + "AIFF" (4) = 12 bytes
                byte[] header = reader.ReadBytes(12);
                if (header.Length < 12)
                    return 0;

                string formType = System.Text.Encoding.ASCII.GetString(header, 0, 4);
                string aiffType = System.Text.Encoding.ASCII.GetString(header, 8, 4);
                if (formType != "FORM" || (aiffType != "AIFF" && aiffType != "AIFC"))
                    return 0;

                // Scan chunks for COMM (Common Chunk)
                while (fs.Position < fs.Length - 8)
                {
                    byte[] chunkHeader = reader.ReadBytes(8);
                    if (chunkHeader.Length < 8)
                        break;

                    string chunkId = System.Text.Encoding.ASCII.GetString(chunkHeader, 0, 4);
                    uint chunkSize = ReadBigEndianUInt32(chunkHeader, 4);

                    if (chunkId == "COMM")
                    {
                        // COMM chunk: numChannels(2) + numSampleFrames(4) + sampleSize(2) + sampleRate(10)
                        byte[] commData = reader.ReadBytes(18);
                        if (commData.Length < 18)
                            return 0;

                        int sampleSize = (commData[6] << 8) | commData[7];
                        return sampleSize;
                    }

                    // Skip to next chunk (pad to even byte boundary)
                    long skip = chunkSize;
                    if (skip % 2 != 0) skip++;
                    fs.Seek(skip, SeekOrigin.Current);
                }
            }
        }
        catch
        {
        }
        return 0;
    }

    /// <summary>
    /// Reads a 4-byte big-endian unsigned integer from a byte array.
    /// </summary>
    private static uint ReadBigEndianUInt32(byte[] buffer, int offset)
    {
        return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }

    /// <summary>
    /// Reads the actual bits-per-sample from the file header.
    /// For WAV: reads the fmt chunk to get real bit depth.
    /// For FLAC: reads the STREAMINFO block to get real bit depth.
    /// For other formats: falls back to GetBitsPerSample().
    /// </summary>
    private static int GetRealBitsPerSample(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        
        try
        {
            switch (ext)
            {
                case ".wav":
                    return GetWavBitsPerSample(filePath);
                case ".flac":
                    return GetFlacBitsPerSample(filePath);
                default:
                    return GetBitsPerSample(filePath);
            }
        }
        catch
        {
            return GetBitsPerSample(filePath);
        }
    }

    /// <summary>
    /// Reads the actual bit depth from a WAV file's fmt chunk.
    /// </summary>
    private static int GetWavBitsPerSample(string filePath)
    {
        using var stream = System.IO.File.OpenRead(filePath);
        using var reader = new System.IO.BinaryReader(stream);
        
        // RIFF header
        var riff = new string(reader.ReadChars(4));
        if (riff != "RIFF") return 16;
        
        reader.ReadInt32(); // file size
        var wave = new string(reader.ReadChars(4));
        if (wave != "WAVE") return 16;
        
        // Find fmt chunk
        while (stream.Position < stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();
            
            if (chunkId == "fmt ")
            {
                reader.ReadInt16(); // audio format
                reader.ReadInt16(); // channels
                reader.ReadInt32(); // sample rate
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                short bitsPerSample = reader.ReadInt16(); // bits per sample
                return bitsPerSample;
            }
            
            // Skip to next chunk
            if (chunkSize < 0 || chunkSize > stream.Length - stream.Position)
                break;
            stream.Position += chunkSize;
        }
        
        return 16;
    }

    /// <summary>
    /// Reads the actual bit depth from a FLAC file's STREAMINFO metadata block.
    /// </summary>
    private static int GetFlacBitsPerSample(string filePath)
    {
        using var stream = System.IO.File.OpenRead(filePath);
        using var reader = new System.IO.BinaryReader(stream);
        
        // "fLaC" marker
        var marker = new string(reader.ReadChars(4));
        if (marker != "fLaC") return 24;
        
        // Read metadata blocks
        bool isLast = false;
        while (!isLast && stream.Position < stream.Length - 4)
        {
            byte blockHeader = reader.ReadByte();
            isLast = (blockHeader & 0x80) != 0;
            int blockType = blockHeader & 0x7F;
            
            // Read block length (3 bytes, big-endian)
            int blockLength = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
            
            if (blockType == 0) // STREAMINFO
            {
                // Skip: min/max block size (4 bytes), min/max frame size (6 bytes)
                stream.Position += 10;
                
                // Read sample rate (20 bits), channels (3 bits), bits per sample (5 bits), total samples (36 bits)
                byte[] buf = reader.ReadBytes(8); // 64 bits of audio data
                
                // Sample rate: first 20 bits of buf
                int sampleRate = (buf[0] << 12) | (buf[1] << 4) | (buf[2] >> 4);
                
                // Channels: next 3 bits (bits 20-22)
                int channels = ((buf[2] & 0x0E) >> 1) + 1;
                
                // Bits per sample: next 5 bits (bits 23-27)
                int bitsPerSample = ((buf[2] & 0x01) << 4) | (buf[3] >> 4);
                bitsPerSample++; // stored as (bits per sample - 1)
                
                return bitsPerSample;
            }
            
            // Skip to next metadata block
            if (blockLength < 0 || blockLength > stream.Length - stream.Position)
                break;
            stream.Position += blockLength;
        }
        
        return 24;
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

    /// <summary>
    /// Maximum age for cached cover images (7 days).
    /// Covers older than this will be re-extracted on next access.
    /// </summary>
    private static readonly TimeSpan CoverCacheMaxAge = TimeSpan.FromDays(7);

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

                // Check if cached cover exists and is fresh enough
                if (System.IO.File.Exists(coverPath))
                {
                    var lastWrite = System.IO.File.GetLastWriteTime(coverPath);
                    if (DateTime.Now - lastWrite < CoverCacheMaxAge)
                    {
                        return coverPath; // Cache is fresh, use it
                    }
                    // Cache is stale — will re-extract below
                }

                // Extract cover from file
                using var stream = new FileStream(coverPath, FileMode.Create);
                stream.Write(pictures[0].Data.Data, 0, pictures[0].Data.Data.Length);
                return coverPath;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Clean up stale cover cache files older than the max age.
    /// Call this periodically (e.g., on app startup) to prevent unbounded cache growth.
    /// </summary>
    public static void CleanCoverCache()
    {
        try
        {
            var coverDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PureAudio", "Covers");
            if (!System.IO.Directory.Exists(coverDir))
                return;

            foreach (var file in System.IO.Directory.GetFiles(coverDir, "*.jpg"))
            {
                try
                {
                    var lastWrite = System.IO.File.GetLastWriteTime(file);
                    if (DateTime.Now - lastWrite > CoverCacheMaxAge)
                    {
                        System.IO.File.Delete(file);
                    }
                }
                catch
                {
                    // Skip files we can't access
                }
            }
        }
        catch
        {
            // Silently fail — cache cleanup is non-critical
        }
    }
}