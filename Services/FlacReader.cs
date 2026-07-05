using System.IO;
using NAudio.Wave;

namespace PureAudio.Services;

/// <summary>
/// FlacReader - a FLAC decoder that implements IWaveProvider.
/// Uses BunLabs.NAudio.Flac (NAudio.Flac.FlacReader) to decode FLAC to original PCM
/// without float conversion, enabling true bit-perfect playback.
/// </summary>
internal class FlacReader : IWaveProvider, IDisposable
{
    private readonly NAudio.Flac.FlacReader _reader;
    private bool _disposed;

    /// <summary>
    /// Creates a FlacReader for the specified FLAC file.
    /// </summary>
    public FlacReader(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("FLAC file not found", filePath);

        _reader = new NAudio.Flac.FlacReader(filePath);
    }

    public WaveFormat WaveFormat => _reader.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        return _reader.Read(buffer, offset, count);
    }

    /// <summary>
    /// Total PCM data length in bytes.
    /// </summary>
    public long TotalPcmBytes => _reader.Length;

    /// <summary>
    /// Set the current position in PCM bytes.
    /// </summary>
    public void SetPosition(long position)
    {
        position = Math.Clamp(position, 0, TotalPcmBytes);
        _reader.Position = position;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reader?.Dispose();
            _disposed = true;
        }
    }
}
