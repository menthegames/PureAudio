using NAudio.Wave;
using PureAudio.Helpers;

namespace PureAudio.Services;

/// <summary>
/// An IWaveProvider wrapper that intercepts PCM audio data for FFT spectrum analysis
/// without modifying the audio stream.
///
/// Used in both Shared and Exclusive modes to provide a unified FFT pipeline:
/// - In Shared mode: wraps AudioFileReader (or any IWaveProvider)
/// - In Exclusive mode: wraps BitPerfectWaveProvider (or SoxResampler)
///
/// This replaces the old approach where FFT was handled separately:
/// - FftSampleProvider (ISampleProvider, Shared mode only)
/// - BitPerfectWaveProvider.FeedFft() (Exclusive mode only)
///
/// Now FFT processing is unified in one place, and the audio data passes through unchanged.
/// </summary>
internal class FftWaveProvider : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _source;
    private readonly FftQueue _fftQueue;
    private readonly int _bitsPerSample;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private float[]? _floatBuffer;
    private bool _disposed;

    public FftWaveProvider(IWaveProvider source, FftQueue fftQueue)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _fftQueue = fftQueue ?? throw new ArgumentNullException(nameof(fftQueue));

        var fmt = source.WaveFormat;
        _bitsPerSample = fmt.BitsPerSample;
        _channels = fmt.Channels;
        _bytesPerSample = _bitsPerSample / 8;

        Logger.Log($"FftWaveProvider: created, format={fmt.SampleRate}Hz/{_bitsPerSample}bit/{_channels}ch");
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = _source.Read(buffer, offset, count);

        if (bytesRead > 0)
        {
            FeedFft(buffer, offset, bytesRead);
        }

        return bytesRead;
    }

    /// <summary>
    /// Converts PCM byte data to float samples and feeds them to FFT queue.
    /// Uses PcmConverter for the actual conversion logic.
    /// This does NOT modify the audio data — only "peeks" at it for visualization.
    /// </summary>
    private void FeedFft(byte[] buffer, int offset, int bytesRead)
    {
        try
        {
            int frames = PcmConverter.PcmToFloatMono(buffer, offset, bytesRead,
                _bitsPerSample, _channels, ref _floatBuffer);

            if (frames > 0)
                _fftQueue.Enqueue(_floatBuffer, frames);
        }
        catch (Exception ex)
        {
            Logger.Log($"FftWaveProvider.FeedFft: Exception - {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // NOTE: We do NOT dispose _source here because the owner (PlaybackEngine)
            // manages the lifecycle of _bitPerfectProvider, _resampler, and _audioFileReader.
            // Disposing _source here would cause double-dispose issues.
            _disposed = true;
        }
    }
}
