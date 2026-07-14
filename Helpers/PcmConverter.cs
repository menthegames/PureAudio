using NAudio.Wave;

namespace PureAudio.Helpers;

/// <summary>
/// Utility class for converting between PCM byte data and float samples.
/// Supports 8-bit, 16-bit, 24-bit, and 32-bit PCM formats.
/// 
/// Used by BitPerfectWaveProvider (for FFT data) and SoxResampler (for resampling).
/// Centralizes PCM conversion logic to avoid code duplication.
/// </summary>
public static class PcmConverter
{
    /// <summary>
    /// Converts a buffer of PCM bytes to mono-mixed float samples.
    /// Handles multi-channel audio by averaging all channels into a single mono sample per frame.
    /// </summary>
    /// <param name="pcmBuffer">Source PCM byte buffer.</param>
    /// <param name="offset">Offset into the PCM buffer.</param>
    /// <param name="bytesRead">Number of valid PCM bytes.</param>
    /// <param name="bitsPerSample">Bit depth (8, 16, 24, or 32).</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="floatBuffer">Pre-allocated float buffer (will be resized if too small).</param>
    /// <returns>The number of float samples written.</returns>
    public static int PcmToFloatMono(byte[] pcmBuffer, int offset, int bytesRead,
        int bitsPerSample, int channels, ref float[]? floatBuffer)
    {
        if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
            return 0;

        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = bytesRead / bytesPerSample;
        int frames = totalSamples / channels;
        if (frames <= 0) return 0;

        // Allocate or resize float buffer
        if (floatBuffer == null || floatBuffer.Length < frames)
            floatBuffer = new float[frames];

        for (int i = 0; i < frames; i++)
        {
            float sample = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int byteOffset = offset + (i * channels + ch) * bytesPerSample;
                if (byteOffset + bytesPerSample > offset + bytesRead)
                    break;

                float chSample = bitsPerSample switch
                {
                    8 => (pcmBuffer[byteOffset] - 128) / 128f,
                    16 => Pcm16ToFloat(pcmBuffer, byteOffset),
                    24 => Pcm24ToFloat(pcmBuffer, byteOffset),
                    32 => Pcm32ToFloat(pcmBuffer, byteOffset),
                    _ => 0f
                };
                sample += chSample;
            }
            floatBuffer[i] = Math.Clamp(sample / channels, -1f, 1f);
        }

        return frames;
    }

    /// <summary>
    /// Converts a buffer of PCM bytes to per-channel float samples (non-mixed).
    /// Each channel's samples are placed sequentially in the output buffer.
    /// </summary>
    public static int PcmToFloatInterleaved(byte[] pcmBuffer, int offset, int bytesRead,
        int bitsPerSample, float[] floatBuffer, int floatOffset)
    {
        if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
            return 0;

        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = bytesRead / bytesPerSample;

        for (int i = 0; i < totalSamples; i++)
        {
            int byteOffset = offset + i * bytesPerSample;
            if (byteOffset + bytesPerSample > offset + bytesRead)
                return i;

            floatBuffer[floatOffset + i] = bitsPerSample switch
            {
                8 => (pcmBuffer[byteOffset] - 128) / 128f,
                16 => Pcm16ToFloat(pcmBuffer, byteOffset),
                24 => Pcm24ToFloat(pcmBuffer, byteOffset),
                32 => Pcm32ToFloat(pcmBuffer, byteOffset),
                _ => 0f
            };
        }

        return totalSamples;
    }

    /// <summary>
    /// Converts float samples back to PCM bytes.
    /// </summary>
    public static void FloatToPcm(float[] floatSamples, byte[] pcmBuffer, int dstOffset, int count, int bitsPerSample)
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
                    FloatToPcm16(sample, pcmBuffer, byteOffset);
                    break;
                case 24:
                    FloatToPcm24(sample, pcmBuffer, byteOffset);
                    break;
                case 32:
                    FloatToPcm32(sample, pcmBuffer, byteOffset);
                    break;
            }
        }
    }

    /// <summary>
    /// Creates an ISampleProvider that wraps a PCM IWaveProvider.
    /// Converts PCM bytes to float samples on-the-fly.
    /// </summary>
    public static ISampleProvider CreateSampleProvider(IWaveProvider source)
    {
        return new PcmToSampleProviderWrapper(source);
    }

    // ── Private helpers ──

    private static float Pcm16ToFloat(byte[] buffer, int offset)
    {
        short s16 = (short)(buffer[offset] | (buffer[offset + 1] << 8));
        return s16 / 32768f;
    }

    private static float Pcm24ToFloat(byte[] buffer, int offset)
    {
        int s24 = buffer[offset] |
                  (buffer[offset + 1] << 8) |
                  (buffer[offset + 2] << 16);
        if ((s24 & 0x800000) != 0)
            s24 |= unchecked((int)0xFF000000);
        return s24 / 8388608f;
    }

    private static float Pcm32ToFloat(byte[] buffer, int offset)
    {
        int s32 = buffer[offset] |
                  (buffer[offset + 1] << 8) |
                  (buffer[offset + 2] << 16) |
                  (buffer[offset + 3] << 24);
        return s32 / 2147483648f;
    }

    private static void FloatToPcm16(float sample, byte[] buffer, int offset)
    {
        short s16 = (short)(sample * 32767.0f);
        buffer[offset] = (byte)(s16 & 0xFF);
        buffer[offset + 1] = (byte)((s16 >> 8) & 0xFF);
    }

    private static void FloatToPcm24(float sample, byte[] buffer, int offset)
    {
        int s24 = (int)(sample * 8388607.0f);
        buffer[offset] = (byte)(s24 & 0xFF);
        buffer[offset + 1] = (byte)((s24 >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((s24 >> 16) & 0xFF);
    }

    private static void FloatToPcm32(float sample, byte[] buffer, int offset)
    {
        int s32 = (int)(sample * 2147483647.0f);
        buffer[offset] = (byte)(s32 & 0xFF);
        buffer[offset + 1] = (byte)((s32 >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((s32 >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((s32 >> 24) & 0xFF);
    }

    /// <summary>
    /// Internal ISampleProvider that wraps a PCM IWaveProvider.
    /// Replaces the duplicated PcmToSampleProvider in SoxResampler.
    /// </summary>
    private class PcmToSampleProviderWrapper : ISampleProvider
    {
        private readonly IWaveProvider _source;
        private readonly WaveFormat _waveFormat;
        private readonly int _bytesPerSample;
        private readonly int _bitsPerSample;
        private readonly byte[] _readBuffer;

        public PcmToSampleProviderWrapper(IWaveProvider source)
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

                    PcmToFloatInterleaved(_readBuffer, 0, bytesRead, _bitsPerSample, buffer, offset + totalFloats);
                    totalFloats += framesRead;
                }
                return totalFloats;
            }

            int bytesRead2 = _source.Read(_readBuffer, 0, bytesNeeded);
            int framesRead2 = bytesRead2 / _bytesPerSample;

            if (framesRead2 <= 0)
                return 0;

            PcmToFloatInterleaved(_readBuffer, 0, bytesRead2, _bitsPerSample, buffer, offset);
            return framesRead2;
        }
    }
}
