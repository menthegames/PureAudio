using System;
using System.IO;
using NAudio.Wave;
using PureAudio.Helpers;

namespace PureAudio.Services;

/// <summary>
/// A PCM IWaveProvider that delivers original PCM data for WASAPI Exclusive output.
/// 
/// For WAV/FLAC files, reads original PCM data directly without any conversion.
/// For other formats (MP3, AAC), uses AudioFileReader and converts float back to PCM.
/// 
/// This is the key component for Bit Perfect playback — it delivers raw PCM
/// data directly to the audio driver/DAC in the original format.
/// 
/// Supports position tracking and seeking.
/// NOTE: FFT data is now handled by FftWaveProvider wrapping this provider.
/// </summary>
internal class BitPerfectWaveProvider : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _sourceProvider;
    private readonly IDisposable? _disposableSource;
    private readonly AudioFileReader? _audioFileReader; // For non-PCM formats (MP3, AAC, etc.)
    private bool _disposed;

    // For position tracking with PCM sources (WaveFileReader, FlacReader)
    private readonly long _totalPcmBytes;
    private long _pcmPosition;

    // For position tracking with AudioFileReader
    private readonly bool _useAudioFileReader;

    // The original PCM format of the source
    private readonly WaveFormat _pcmFormat;

    public BitPerfectWaveProvider(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        switch (ext)
        {
            case ".wav":
            {
                var reader = new WaveFileReader(filePath);
                _sourceProvider = reader;
                _disposableSource = reader;
                _totalPcmBytes = reader.Length;
                _useAudioFileReader = false;
                break;
            }

            case ".flac":
            {
                var reader = new FlacReader(filePath);
                var format = reader.WaveFormat;
                
                // ВАЛИДАЦИЯ: Проверяем корректность формата
                // Разрешаем любые стандартные битности (8, 16, 24, 32) и любое количество каналов (1-8)
                // Sample rate может быть любым — FlacReader сам знает, что он декодирует
                bool isValidFormat = (format.BitsPerSample == 8 || format.BitsPerSample == 16 || 
                                      format.BitsPerSample == 24 || format.BitsPerSample == 32) &&
                                     (format.Channels >= 1 && format.Channels <= 8);
                
                if (!isValidFormat)
                {
                    Logger.Log($"BitPerfectWaveProvider: FLAC has invalid format {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch, falling back to AudioFileReader");
                    reader.Dispose();
                    
                    // Fallback на AudioFileReader (декодирует в 32-bit Float)
                    var audioReader = new AudioFileReader(filePath);
                    audioReader.Volume = 1.0f;
                    _sourceProvider = audioReader;
                    _disposableSource = audioReader;
                    _audioFileReader = audioReader;
                    _useAudioFileReader = true;
                    _totalPcmBytes = 0; // Неизвестно для AudioFileReader
                }
                else
                {
                    _sourceProvider = reader;
                    _disposableSource = reader;
                    _totalPcmBytes = reader.TotalPcmBytes;
                    _useAudioFileReader = false;
                }
                
                Logger.Log($"BitPerfectWaveProvider: FLAC final source format = {_sourceProvider.WaveFormat.SampleRate}Hz/{_sourceProvider.WaveFormat.BitsPerSample}bit/{_sourceProvider.WaveFormat.Channels}ch");
                break;
            }

            default:
            {
                // For MP3, AAC, etc., use AudioFileReader (MediaFoundation).
                // These formats are lossy anyway.
                var reader = new AudioFileReader(filePath);
                reader.Volume = 1.0f;
                _sourceProvider = reader;
                _disposableSource = reader;
                _audioFileReader = reader;
                _useAudioFileReader = true;

                Logger.Log(
                    $"BitPerfectWaveProvider: {ext} - {reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.BitsPerSample}bit/{reader.WaveFormat.Channels}ch");
                break;
            }
        }

        _pcmFormat = _sourceProvider.WaveFormat;

        // ВАЛИДАЦИЯ: Проверяем, что формат реалистичный
        if (_pcmFormat.SampleRate < 8000 || _pcmFormat.SampleRate > 384000 ||
            _pcmFormat.BitsPerSample < 8 || _pcmFormat.BitsPerSample > 32 ||
            _pcmFormat.Channels < 1 || _pcmFormat.Channels > 8)
        {
            Logger.Log($"BitPerfectWaveProvider: INVALID FORMAT detected! " +
                $"SR={_pcmFormat.SampleRate}, BPS={_pcmFormat.BitsPerSample}, CH={_pcmFormat.Channels}");
            throw new InvalidOperationException($"Invalid audio format: {_pcmFormat.SampleRate}Hz/{_pcmFormat.BitsPerSample}bit/{_pcmFormat.Channels}ch");
        }
    }

    public WaveFormat WaveFormat => _pcmFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = _sourceProvider.Read(buffer, offset, count);

        if (bytesRead > 0 && !_useAudioFileReader)
        {
            // Track position in PCM bytes
            _pcmPosition += bytesRead;
        }

        return bytesRead;
    }

    /// <summary>
    /// Current playback position as TimeSpan.
    /// </summary>
    public TimeSpan CurrentTime
    {
        get
        {
            if (_useAudioFileReader && _audioFileReader != null)
                return _audioFileReader.CurrentTime;

            if (_totalPcmBytes > 0)
            {
                long bytesPerSecond = 0;
                if (_disposableSource is WaveFileReader wfr)
                    bytesPerSecond = wfr.WaveFormat.AverageBytesPerSecond;
                else if (_disposableSource is FlacReader fr)
                    bytesPerSecond = fr.WaveFormat.AverageBytesPerSecond;

                if (bytesPerSecond > 0)
                    return TimeSpan.FromSeconds((double)_pcmPosition / bytesPerSecond);
            }

            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Total duration as TimeSpan.
    /// </summary>
    public TimeSpan TotalTime
    {
        get
        {
            if (_useAudioFileReader && _audioFileReader != null)
                return _audioFileReader.TotalTime;

            if (_totalPcmBytes > 0)
            {
                long bytesPerSecond = 0;
                if (_disposableSource is WaveFileReader wfr)
                    bytesPerSecond = wfr.WaveFormat.AverageBytesPerSecond;
                else if (_disposableSource is FlacReader fr)
                    bytesPerSecond = fr.WaveFormat.AverageBytesPerSecond;

                if (bytesPerSecond > 0)
                    return TimeSpan.FromSeconds((double)_totalPcmBytes / bytesPerSecond);
            }

            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Seek to a specific position.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (_useAudioFileReader && _audioFileReader != null)
        {
            _audioFileReader.CurrentTime = position;
            return;
        }

        long bytesPerSecond = 0;
        if (_disposableSource is WaveFileReader waveReader)
        {
            bytesPerSecond = waveReader.WaveFormat.AverageBytesPerSecond;
            if (bytesPerSecond > 0)
            {
                long targetByte = (long)(position.TotalSeconds * bytesPerSecond);
                targetByte = Math.Clamp(targetByte, 0, _totalPcmBytes);
                int blockAlign = waveReader.WaveFormat.BlockAlign;
                if (blockAlign > 0)
                    targetByte = (targetByte / blockAlign) * blockAlign;
                waveReader.CurrentTime = position;
                _pcmPosition = waveReader.Position;
            }
        }
        else if (_disposableSource is FlacReader flacReader)
        {
            bytesPerSecond = flacReader.WaveFormat.AverageBytesPerSecond;
            if (bytesPerSecond > 0)
            {
                long targetByte = (long)(position.TotalSeconds * bytesPerSecond);
                targetByte = Math.Clamp(targetByte, 0, _totalPcmBytes);
                int blockAlign = flacReader.WaveFormat.BlockAlign;
                if (blockAlign > 0)
                    targetByte = (targetByte / blockAlign) * blockAlign;
                flacReader.SetPosition(targetByte);
                _pcmPosition = targetByte;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposableSource?.Dispose();
            _disposed = true;
        }
    }
}
