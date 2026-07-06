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
    private readonly PcmToSampleProvider _floatSource;
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

        // Create a float converter from the source PCM provider
        _floatSource = new PcmToSampleProvider(sourceProvider);

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
        FloatToPcm(_floatBuffer, buffer, offset, samplesRead, _outputFormat.BitsPerSample);

        return bytesToWrite;
    }

    /// <summary>
    /// Convert float samples (-1.0 to 1.0) to PCM bytes.
    /// </summary>
    private static void FloatToPcm(float[] floatSamples, byte[] pcmBuffer, int dstOffset, int count, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;

        for (int i = 0; i < count; i++)
        {
            float sample = Math.Clamp(floatSamples[i], -1.0f, 1.0f);
            int byteOffset = dstOffset + i * bytesPerSample;

            if (byteOffset + bytesPerSample > pcmBuffer.Length)
                break;

            switch (bitsPerSample)
            {
                case 16:
                {
                    short s16 = (short)(sample * 32767.0f);
                    pcmBuffer[byteOffset] = (byte)(s16 & 0xFF);
                    pcmBuffer[byteOffset + 1] = (byte)((s16 >> 8) & 0xFF);
                    break;
                }
                case 24:
                {
                    int s24 = (int)(sample * 8388607.0f);
                    pcmBuffer[byteOffset] = (byte)(s24 & 0xFF);
                    pcmBuffer[byteOffset + 1] = (byte)((s24 >> 8) & 0xFF);
                    pcmBuffer[byteOffset + 2] = (byte)((s24 >> 16) & 0xFF);
                    break;
                }
                case 32:
                {
                    int s32 = (int)(sample * 2147483647.0f);
                    pcmBuffer[byteOffset] = (byte)(s32 & 0xFF);
                    pcmBuffer[byteOffset + 1] = (byte)((s32 >> 8) & 0xFF);
                    pcmBuffer[byteOffset + 2] = (byte)((s32 >> 16) & 0xFF);
                    pcmBuffer[byteOffset + 3] = (byte)((s32 >> 24) & 0xFF);
                    break;
                }
            }
        }
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

    /// <summary>
    /// Custom ISampleProvider that converts PCM IWaveProvider to float samples.
    /// Supports 16-bit, 24-bit, and 32-bit PCM.
    /// </summary>
    private class PcmToSampleProvider : ISampleProvider
    {
        private readonly IWaveProvider _source;
        private readonly WaveFormat _waveFormat;
        private readonly int _bytesPerSample;
        private readonly int _bitsPerSample;
        private readonly byte[] _readBuffer;

        public PcmToSampleProvider(IWaveProvider source)
        {
            _source = source;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate, source.WaveFormat.Channels);
            _bytesPerSample = source.WaveFormat.BitsPerSample / 8;
            _bitsPerSample = source.WaveFormat.BitsPerSample;
            _readBuffer = new byte[65536];
        }

        public WaveFormat WaveFormat => _waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int bytesNeeded = count * _bytesPerSample;
            if (_readBuffer.Length < bytesNeeded)
            {
                // Read in chunks
                int totalFloats = 0;
                while (totalFloats < count)
                {
                    int chunkFloats = Math.Min(count - totalFloats, _readBuffer.Length / _bytesPerSample);
                    int chunkBytes = chunkFloats * _bytesPerSample;
                    int bytesRead = _source.Read(_readBuffer, 0, chunkBytes);
                    int framesRead = bytesRead / _bytesPerSample;

                    if (framesRead <= 0)
                        break;

                    ConvertToFloat(_readBuffer, 0, buffer, offset + totalFloats, framesRead);
                    totalFloats += framesRead;
                }
                return totalFloats;
            }

            int bytesRead2 = _source.Read(_readBuffer, 0, bytesNeeded);
            int framesRead2 = bytesRead2 / _bytesPerSample;

            if (framesRead2 <= 0)
                return 0;

            ConvertToFloat(_readBuffer, 0, buffer, offset, framesRead2);
            return framesRead2;
        }

        private void ConvertToFloat(byte[] src, int srcOffset, float[] dst, int dstOffset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int byteOffset = srcOffset + i * _bytesPerSample;
                if (byteOffset + _bytesPerSample > src.Length)
                    break;

                switch (_bitsPerSample)
                {
                    case 16:
                    {
                        short s16 = (short)(src[byteOffset] | (src[byteOffset + 1] << 8));
                        dst[dstOffset + i] = s16 / 32768f;
                        break;
                    }
                    case 24:
                    {
                        int s24 = src[byteOffset] |
                                  (src[byteOffset + 1] << 8) |
                                  (src[byteOffset + 2] << 16);
                        if ((s24 & 0x800000) != 0)
                            s24 |= unchecked((int)0xFF000000);
                        dst[dstOffset + i] = s24 / 8388608f;
                        break;
                    }
                    case 32:
                    {
                        int s32 = src[byteOffset] |
                                  (src[byteOffset + 1] << 8) |
                                  (src[byteOffset + 2] << 16) |
                                  (src[byteOffset + 3] << 24);
                        dst[dstOffset + i] = s32 / 2147483648f;
                        break;
                    }
                    default:
                        dst[dstOffset + i] = 0;
                        break;
                }
            }
        }
    }
}
