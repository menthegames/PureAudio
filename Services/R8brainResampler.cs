using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using PureAudio.Helpers;

namespace PureAudio.Services;

/// <summary>
/// IWaveProvider that resamples PCM audio using the r8brain free resampler (r8bsrc.dll).
/// r8brain is a high-quality, professional-grade sample rate converter.
///
/// IMPORTANT: r8brain-free is a MONO resampler (CDSPResampler works with one channel).
/// For multi-channel audio (stereo, 5.1, etc.), this class creates N instances of the
/// resampler (one per channel), deinterleaves input, processes each channel separately,
/// and interleaves the output back.
///
/// IMPORTANT: r8bsrc.dll must be present in the application directory.
/// </summary>
internal class R8brainResampler : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _sourceProvider;
    private readonly WaveFormat _outputFormat;
    private bool _disposed;

    // r8brain state — one instance PER CHANNEL (r8brain is mono)
    private readonly IntPtr[] _instances;

    // Source format info
    private readonly int _sourceChannels;
    private readonly int _sourceBytesPerSample;
    private readonly int _sourceBitsPerSample;

    // For reading from source
    private readonly byte[] _readBuffer;
    private const int ReadBufferSize = 65536; // 64KB read chunks

    // Per-channel deinterleaved double buffers (accumulated input)
    private double[][] _channelInputs = [];
    private int[] _channelCounts = [];

    // Per-channel output from r8b_process (copied immediately via Marshal.Copy)
    private double[][] _channelOutputs = [];
    private int[] _channelOutputFrames = [];

    // Interleaved PCM output buffer
    private byte[]? _pcmOutputBuffer;
    private int _outputBufferPos;
    private int _outputBufferLen;
    private readonly int _outputBytesPerFrame;
    private readonly int _outputBitsPerSample;

    // End-of-stream flag
    private bool _sourceExhausted;

    private static bool _dllChecked;
    private static bool _dllAvailable;

    /// <summary>
    /// Check if r8bsrc.dll is available in the application directory.
    /// </summary>
    public static bool IsDllAvailable()
    {
        if (_dllChecked)
            return _dllAvailable;

        string dllPath = FindDllPath();
        _dllAvailable = File.Exists(dllPath);
        _dllChecked = true;

        Logger.Log($"R8brainResampler: r8bsrc.dll {(_dllAvailable ? "found" : "NOT found")} at {dllPath}");
        return _dllAvailable;
    }

    private static string FindDllPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] possiblePaths =
        [
            Path.Combine(appDir, "r8bsrc.dll"),
            Path.Combine(appDir, "libs", "r8bsrc.dll"),
            Path.Combine(appDir, "..", "..", "..", "libs", "r8bsrc.dll"),
            Path.Combine(appDir, "..", "..", "..", "..", "libs", "r8bsrc.dll"),
        ];

        foreach (var path in possiblePaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return Path.Combine(appDir, "r8bsrc.dll");
    }

    /// <summary>
    /// Create a new r8brain resampler with per-channel instances.
    /// </summary>
    public R8brainResampler(IWaveProvider sourceProvider, WaveFormat outputFormat)
    {
        _sourceProvider = sourceProvider;
        _outputFormat = outputFormat;

        var sourceFormat = sourceProvider.WaveFormat;
        _sourceChannels = sourceFormat.Channels;
        _sourceBytesPerSample = sourceFormat.BitsPerSample / 8;
        _sourceBitsPerSample = sourceFormat.BitsPerSample;

        if (!IsDllAvailable())
        {
            throw new InvalidOperationException(
                "r8bsrc.dll not found. Please ensure r8bsrc.dll is in the application directory.");
        }

        _outputBytesPerFrame = (outputFormat.BitsPerSample / 8) * outputFormat.Channels;
        _outputBitsPerSample = outputFormat.BitsPerSample;

        Logger.Log(
            $"R8brainResampler: initializing {sourceFormat.SampleRate}Hz/{sourceFormat.BitsPerSample}bit/{sourceFormat.Channels}ch -> " +
            $"{outputFormat.SampleRate}Hz/{outputFormat.BitsPerSample}bit/{outputFormat.Channels}ch, " +
            $"ratio={outputFormat.SampleRate / (double)sourceFormat.SampleRate:F6}, " +
            $"creating {_sourceChannels} mono resampler instances");

        // r8b_create parameters
        int maxInLen = 4096;
        double reqTransBand = 2.0;
        int res = (int)R8BResamplerRes.R8BRR24;

        // Create ONE resampler instance per channel
        _instances = new IntPtr[_sourceChannels];
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            Logger.Log($"R8brainResampler: creating instance {ch + 1}/{_sourceChannels}...");
            _instances[ch] = CreateResamplerInstance(
                sourceFormat.SampleRate,
                outputFormat.SampleRate,
                maxInLen,
                reqTransBand,
                res);
        }

        // Allocate per-channel buffers
        _channelInputs = new double[_sourceChannels][];
        _channelCounts = new int[_sourceChannels];
        _channelOutputs = new double[_sourceChannels][];
        _channelOutputFrames = new int[_sourceChannels];

        int initialCapacity = Math.Max(maxInLen * 2, 8192);
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            _channelInputs[ch] = new double[initialCapacity];
            _channelOutputs[ch] = new double[initialCapacity * 2];
        }

        _readBuffer = new byte[ReadBufferSize];

        Logger.Log($"R8brainResampler: initialized successfully with {_sourceChannels} instances");
    }

    /// <summary>
    /// Call r8b_create with timeout protection.
    /// </summary>
    private static IntPtr CreateResamplerInstance(
        int srcRate, int dstRate, int maxInLen, double reqTransBand, int res)
    {
        IntPtr state = IntPtr.Zero;
        Exception? createException = null;
        bool completed = false;

        Thread createThread = new Thread(() =>
        {
            try
            {
                state = r8b_create(srcRate, dstRate, maxInLen, reqTransBand, res);
            }
            catch (Exception ex)
            {
                createException = ex;
            }
            finally
            {
                completed = true;
            }
        });
        createThread.IsBackground = true;
        createThread.Start();

        DateTime start = DateTime.UtcNow;
        while (!completed && (DateTime.UtcNow - start).TotalSeconds < 5)
        {
            Thread.Sleep(100);
        }

        if (!completed)
        {
            createThread.Interrupt();
            throw new InvalidOperationException("r8b_create timed out after 5 seconds");
        }

        if (createException != null)
        {
            throw new InvalidOperationException(
                $"r8b_create failed: {createException.Message}", createException);
        }

        if (state == IntPtr.Zero)
        {
            throw new InvalidOperationException("r8b_create returned IntPtr.Zero");
        }

        return state;
    }

    public WaveFormat WaveFormat => _outputFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int totalBytesWritten = 0;

        while (totalBytesWritten < count)
        {
            // Drain buffered PCM output first
            if (_outputBufferPos < _outputBufferLen)
            {
                int bytesToCopy = Math.Min(
                    _outputBufferLen - _outputBufferPos,
                    count - totalBytesWritten);
                Array.Copy(_pcmOutputBuffer!, _outputBufferPos,
                    buffer, offset + totalBytesWritten, bytesToCopy);
                _outputBufferPos += bytesToCopy;
                totalBytesWritten += bytesToCopy;
                continue;
            }

            _outputBufferPos = 0;
            _outputBufferLen = 0;

            // Source exhausted — flush remaining data
            if (_sourceExhausted)
            {
                int flushed = FlushRemaining(buffer, offset + totalBytesWritten,
                    count - totalBytesWritten);
                totalBytesWritten += flushed;
                break;
            }

            // Try to process accumulated per-channel data
            if (TryProcessAllChannels())
            {
                continue;
            }

            // Read more source data
            int bytesRead = _sourceProvider.Read(_readBuffer, 0, _readBuffer.Length);
            if (bytesRead <= 0)
            {
                _sourceExhausted = true;
                continue;
            }

            int frameSize = _sourceBytesPerSample * _sourceChannels;
            int framesRead = bytesRead / frameSize;
            if (framesRead == 0) continue;

            // Deinterleave PCM bytes into per-channel double buffers
            DeinterleavePcmToDouble(_readBuffer, 0, framesRead);
        }

        return totalBytesWritten;
    }

    /// <summary>
    /// Try to process accumulated data through all per-channel resamplers.
    /// Returns true if output was produced (caller should drain before reading more).
    /// </summary>
    private bool TryProcessAllChannels()
    {
        // Find minimum accumulated frames across all channels
        int availableFrames = int.MaxValue;
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            availableFrames = Math.Min(availableFrames, _channelCounts[ch]);
        }

        // Need at least 1 frame to process
        if (availableFrames < 1)
            return false;

        // Process up to _maxInputLen frames per channel
        int framesToProcess = Math.Min(availableFrames, 4096);

        // Process each channel independently
        int maxOutputFrames = 0;
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            GCHandle inputHandle = GCHandle.Alloc(_channelInputs[ch], GCHandleType.Pinned);
            try
            {
                IntPtr inputPtr = inputHandle.AddrOfPinnedObject();
                IntPtr outputPtr = IntPtr.Zero;

                int outputFrames = r8b_process(
                    _instances[ch], inputPtr, framesToProcess, ref outputPtr);

                if (outputFrames > 0 && outputPtr != IntPtr.Zero)
                {
                    // IMMEDIATELY copy the output buffer — r8brain reuses internal buffers
                    if (_channelOutputs[ch].Length < outputFrames)
                        _channelOutputs[ch] = new double[outputFrames];

                    Marshal.Copy(outputPtr, _channelOutputs[ch], 0, outputFrames);
                    _channelOutputFrames[ch] = outputFrames;
                    maxOutputFrames = Math.Max(maxOutputFrames, outputFrames);
                }
                else
                {
                    _channelOutputFrames[ch] = 0;
                }
            }
            finally
            {
                inputHandle.Free();
            }

            // Remove processed frames from this channel's accumulation buffer
            int samplesConsumed = framesToProcess;
            int remaining = _channelCounts[ch] - samplesConsumed;
            if (remaining > 0)
            {
                Array.Copy(_channelInputs[ch], samplesConsumed,
                    _channelInputs[ch], 0, remaining);
            }
            _channelCounts[ch] = remaining;
        }

        // If any channel produced output, interleave and convert to PCM
        if (maxOutputFrames > 0)
        {
            // Ensure all channels have the same number of output frames
            // (pad shorter channels with silence)
            for (int ch = 0; ch < _sourceChannels; ch++)
            {
                if (_channelOutputFrames[ch] < maxOutputFrames)
                {
                    int oldLen = _channelOutputFrames[ch];
                    if (_channelOutputs[ch].Length < maxOutputFrames)
                        Array.Resize(ref _channelOutputs[ch], maxOutputFrames);
                    // Fill extra with silence (0.0)
                    Array.Clear(_channelOutputs[ch], oldLen, maxOutputFrames - oldLen);
                    _channelOutputFrames[ch] = maxOutputFrames;
                }
            }

            // Interleave and convert to PCM
            int outputBytes = maxOutputFrames * _outputBytesPerFrame;
            if (_pcmOutputBuffer == null || _pcmOutputBuffer.Length < outputBytes)
                _pcmOutputBuffer = new byte[outputBytes];

            InterleaveDoubleToPcm(_channelOutputs, _pcmOutputBuffer,
                maxOutputFrames, _sourceChannels, _outputBitsPerSample);

            _outputBufferLen = outputBytes;
            _outputBufferPos = 0;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Deinterleave PCM bytes from source into per-channel double buffers.
    /// </summary>
    private void DeinterleavePcmToDouble(byte[] pcmData, int pcmOffset, int frameCount)
    {
        int totalSamples = frameCount * _sourceChannels;

        for (int i = 0; i < totalSamples; i++)
        {
            int byteOffset = pcmOffset + i * _sourceBytesPerSample;
            if (byteOffset + _sourceBytesPerSample > pcmData.Length)
                break;

            double sample = BytesToDouble(pcmData, byteOffset, _sourceBitsPerSample);
            int ch = i % _sourceChannels;
            int frameIdx = i / _sourceChannels;

            // Ensure buffer capacity
            int neededIndex = _channelCounts[ch] + frameIdx;
            if (neededIndex >= _channelInputs[ch].Length)
            {
                int newSize = Math.Max(
                    _channelInputs[ch].Length * 2,
                    neededIndex + 1024);
                Array.Resize(ref _channelInputs[ch], newSize);
            }

            _channelInputs[ch][_channelCounts[ch] + frameIdx] = sample;
        }

        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            _channelCounts[ch] += frameCount;
        }
    }

    /// <summary>
    /// Interleave per-channel double buffers into interleaved PCM bytes.
    /// </summary>
    private static void InterleaveDoubleToPcm(
        double[][] channelBuffers, byte[] pcmBuffer,
        int frameCount, int channels, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;

        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                double sample = Math.Clamp(channelBuffers[ch][frame], -1.0, 1.0);
                int byteOffset = (frame * channels + ch) * bytesPerSample;

                switch (bitsPerSample)
                {
                    case 16:
                    {
                        short s16 = (short)(sample * 32767.0);
                        pcmBuffer[byteOffset] = (byte)(s16 & 0xFF);
                        pcmBuffer[byteOffset + 1] = (byte)((s16 >> 8) & 0xFF);
                        break;
                    }
                    case 24:
                    {
                        int s24 = (int)(sample * 8388607.0);
                        pcmBuffer[byteOffset] = (byte)(s24 & 0xFF);
                        pcmBuffer[byteOffset + 1] = (byte)((s24 >> 8) & 0xFF);
                        pcmBuffer[byteOffset + 2] = (byte)((s24 >> 16) & 0xFF);
                        break;
                    }
                    case 32:
                    {
                        int s32 = (int)(sample * 2147483647.0);
                        pcmBuffer[byteOffset] = (byte)(s32 & 0xFF);
                        pcmBuffer[byteOffset + 1] = (byte)((s32 >> 8) & 0xFF);
                        pcmBuffer[byteOffset + 2] = (byte)((s32 >> 16) & 0xFF);
                        pcmBuffer[byteOffset + 3] = (byte)((s32 >> 24) & 0xFF);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Convert bytes to double sample based on bit depth.
    /// </summary>
    private static double BytesToDouble(byte[] data, int offset, int bitsPerSample)
    {
        switch (bitsPerSample)
        {
            case 16:
            {
                short s16 = (short)(data[offset] | (data[offset + 1] << 8));
                return s16 / 32768.0;
            }
            case 24:
            {
                int s24 = data[offset] |
                          (data[offset + 1] << 8) |
                          (data[offset + 2] << 16);
                // Sign-extend 24-bit to 32-bit
                if ((s24 & 0x800000) != 0)
                    s24 |= unchecked((int)0xFF000000);
                return s24 / 8388608.0;
            }
            case 32:
            {
                int s32 = data[offset] |
                          (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) |
                          (data[offset + 3] << 24);
                return s32 / 2147483648.0;
            }
            default:
                return 0.0;
        }
    }

    /// <summary>
    /// Flush remaining samples from all per-channel resamplers.
    /// Called when source is exhausted.
    /// </summary>
    private int FlushRemaining(byte[] buffer, int offset, int maxCount)
    {
        int totalWritten = 0;

        // Process any remaining accumulated data
        int safetyCounter = 0;
        while (safetyCounter < 100)
        {
            safetyCounter++;
            if (!TryProcessAllChannels())
                break;

            int bytesToCopy = Math.Min(_outputBufferLen, maxCount - totalWritten);
            if (bytesToCopy <= 0) break;

            Array.Copy(_pcmOutputBuffer!, 0, buffer, offset + totalWritten, bytesToCopy);
            totalWritten += bytesToCopy;
            _outputBufferPos = bytesToCopy;
            _outputBufferLen = 0;
        }

        // Discard orphan samples (less than 1 frame per channel)
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            if (_channelCounts[ch] > 0)
            {
                _channelCounts[ch] = 0;
            }
        }

        // Flush each resampler instance by calling with 0 input
        // r8brain's r8b_process with inputSampleCount=0 flushes internal buffers
        // We pass a valid pointer (to a dummy double) even with 0 count
        double[] flushDummy = new double[1];
        GCHandle flushHandle = GCHandle.Alloc(flushDummy, GCHandleType.Pinned);
        try
        {
            IntPtr flushInputPtr = flushHandle.AddrOfPinnedObject();

            int maxFlushFrames = 0;
            for (int ch = 0; ch < _sourceChannels; ch++)
            {
                IntPtr outputPtr = IntPtr.Zero;
                int flushFrames = r8b_process(
                    _instances[ch], flushInputPtr, 0, ref outputPtr);

                if (flushFrames > 0 && outputPtr != IntPtr.Zero)
                {
                    if (_channelOutputs[ch].Length < flushFrames)
                        _channelOutputs[ch] = new double[flushFrames];
                    Marshal.Copy(outputPtr, _channelOutputs[ch], 0, flushFrames);
                    _channelOutputFrames[ch] = flushFrames;
                    maxFlushFrames = Math.Max(maxFlushFrames, flushFrames);
                }
                else
                {
                    _channelOutputFrames[ch] = 0;
                }
            }

            if (maxFlushFrames > 0)
            {
                // Pad shorter channels
                for (int ch = 0; ch < _sourceChannels; ch++)
                {
                    if (_channelOutputFrames[ch] < maxFlushFrames)
                    {
                        int oldLen = _channelOutputFrames[ch];
                        if (_channelOutputs[ch].Length < maxFlushFrames)
                            Array.Resize(ref _channelOutputs[ch], maxFlushFrames);
                        Array.Clear(_channelOutputs[ch], oldLen, maxFlushFrames - oldLen);
                        _channelOutputFrames[ch] = maxFlushFrames;
                    }
                }

                int outputBytes = maxFlushFrames * _outputBytesPerFrame;
                if (_pcmOutputBuffer == null || _pcmOutputBuffer.Length < outputBytes)
                    _pcmOutputBuffer = new byte[outputBytes];

                InterleaveDoubleToPcm(_channelOutputs, _pcmOutputBuffer,
                    maxFlushFrames, _sourceChannels, _outputBitsPerSample);

                int bytesToCopy = Math.Min(outputBytes, maxCount - totalWritten);
                if (bytesToCopy > 0)
                {
                    Array.Copy(_pcmOutputBuffer, 0, buffer, offset + totalWritten, bytesToCopy);
                    totalWritten += bytesToCopy;
                }
            }
        }
        finally
        {
            flushHandle.Free();
        }

        return totalWritten;
    }

    /// <summary>
    /// Clear all internal resampler state (for seeking/restart).
    /// </summary>
    public void Clear()
    {
        for (int ch = 0; ch < _instances.Length; ch++)
        {
            if (_instances[ch] != IntPtr.Zero)
            {
                r8b_clear(_instances[ch]);
            }
        }

        // Reset all buffers
        for (int ch = 0; ch < _sourceChannels; ch++)
        {
            _channelCounts[ch] = 0;
            _channelOutputFrames[ch] = 0;
        }

        _outputBufferPos = 0;
        _outputBufferLen = 0;
        _sourceExhausted = false;

        Logger.Log("R8brainResampler: cleared all instances and buffers");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            for (int ch = 0; ch < _instances.Length; ch++)
            {
                if (_instances[ch] != IntPtr.Zero)
                {
                    r8b_delete(_instances[ch]);
                    _instances[ch] = IntPtr.Zero;
                }
            }

            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  r8brain P/Invoke declarations
    // ════════════════════════════════════════════════════════════════

    private const string DllName = "r8bsrc.dll";

    private enum R8BResamplerRes
    {
        R8BRR16 = 0,
        R8BRR16IR = 1,
        R8BRR24 = 2
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_create")]
    private static extern IntPtr r8b_create(
        double srcSampleRate,
        double dstSampleRate,
        int maxInLen,
        double reqTransBand,
        int res);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_process")]
    private static extern int r8b_process(
        IntPtr state,
        IntPtr input,
        int inputSampleCount,
        ref IntPtr output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_clear")]
    private static extern void r8b_clear(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_delete")]
    private static extern void r8b_delete(IntPtr state);
}
