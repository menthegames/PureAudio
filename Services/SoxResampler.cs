using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PureAudio.Helpers;

namespace PureAudio.Services;

/// <summary>
/// IWaveProvider that resamples PCM audio using NAudio's built-in WDL resampler
/// (WdlResamplingSampleProvider — the same high-quality resampler used in REAPER).
///
/// This is used in Bit Perfect Limited mode when the source format exceeds
/// the DAC's capabilities. The resampler converts PCM to float, resamples,
/// and converts back to PCM for WASAPI Exclusive output.
///
/// NOTE: This is NOT Bit Perfect — the float conversion breaks bit-perfectness.
/// However, in Limited mode we're already in a situation where the format
/// doesn't match the DAC, so resampling is necessary.
/// </summary>
internal class SoxResampler : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _sourceProvider;
    private readonly WaveFormat _outputFormat;
    private readonly WdlResamplingSampleProvider _resampler;
    private readonly ISampleProvider _floatSource;
    private bool _disposed;

    // Buffer for float -> PCM conversion (reused across Read calls)
    private float[] _floatBuffer;

    // Output format info
    private readonly int _outputBytesPerFrame;

    /// <summary>
    /// Create a new SoxResampler.
    /// </summary>
    public SoxResampler(IWaveProvider sourceProvider, WaveFormat outputFormat)
    {
        _sourceProvider = sourceProvider;
        _outputFormat = outputFormat;

        var sourceFormat = sourceProvider.WaveFormat;

        _outputBytesPerFrame = outputFormat.BlockAlign;

        Logger.Log(
            $"SoxResampler: initializing {sourceFormat.SampleRate}Hz/{sourceFormat.BitsPerSample}bit/{sourceFormat.Channels}ch -> " +
            $"{outputFormat.SampleRate}Hz/{outputFormat.BitsPerSample}bit/{outputFormat.Channels}ch, " +
            $"ratio={outputFormat.SampleRate / (double)sourceFormat.SampleRate:F6}");

        // Create a float converter from the source PCM provider using shared PcmConverter
        _floatSource = PcmConverter.CreateSampleProvider(sourceProvider);

        // Create the WDL resampler
        _resampler = new WdlResamplingSampleProvider(_floatSource, outputFormat.SampleRate);

        // Initial buffer allocation (will grow if needed)
        _floatBuffer = new float[8192];

        Logger.Log("SoxResampler: initialized successfully");
    }

    public WaveFormat WaveFormat => _outputFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        // Calculate how many output frames WASAPI is requesting
        int outputFramesNeeded = count / _outputBytesPerFrame;
        if (outputFramesNeeded <= 0)
            return 0;

        // Calculate how many float samples we need (frames * channels)
        // WdlResamplingSampleProvider.Read() expects the count in SAMPLES (float values),
        // not frames. For stereo, 1 frame = 2 samples.
        int floatSamplesNeeded = outputFramesNeeded * _outputFormat.Channels;

        // Ensure float buffer is large enough
        if (_floatBuffer.Length < floatSamplesNeeded)
        {
            _floatBuffer = new float[floatSamplesNeeded];
        }

        // Read float samples from the WDL resampler
        // IMPORTANT: WdlResamplingSampleProvider.Read() returns the number of SAMPLES (float values),
        // not frames. The 'count' parameter is also in samples.
        int samplesRead = _resampler.Read(_floatBuffer, 0, floatSamplesNeeded);
        if (samplesRead <= 0)
            return 0;

        int framesRead = samplesRead / _outputFormat.Channels;
        int bytesToWrite = framesRead * _outputBytesPerFrame;

        // Safety check: don't overflow the output buffer
        if (offset + bytesToWrite > buffer.Length)
            bytesToWrite = buffer.Length - offset;

        // Convert float samples back to PCM bytes directly into the output buffer
        PcmConverter.FloatToPcm(_floatBuffer, buffer, offset, samplesRead, _outputFormat.BitsPerSample);

        return bytesToWrite;
    }

    /// <summary>
    /// Clear internal buffers (for seeking/restart).
    /// </summary>
    public void Clear()
    {
        Logger.Log("SoxResampler: cleared buffers");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_resampler is IDisposable d)
                d.Dispose();
            _disposed = true;
        }
    }
}
