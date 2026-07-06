using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using PureAudio.Helpers;


namespace PureAudio.Services;

/// <summary>
/// IWaveProvider that resamples PCM audio using the r8brain free resampler (r8bsrc.dll).
/// r8brain is a high-quality, professional-grade sample rate converter.
/// 
/// This provider wraps a source IWaveProvider and outputs PCM data at the target sample rate.
/// It handles the conversion from source format to target format using r8brain's P/Invoke API.
/// 
/// IMPORTANT: r8bsrc.dll must be present in the application directory.
/// </summary>
internal class R8brainResampler : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _sourceProvider;
    private readonly WaveFormat _outputFormat;
    private readonly double _ratio;
    private bool _disposed;

    // r8brain state
    private IntPtr _srcState;
    private readonly int _maxInputLen;

    // Buffers
    private double[]? _inputBuffer;
    private double[]? _outputDoubleBuffer;
    private byte[]? _pcmOutputBuffer;
    private int _outputBufferPos;
    private int _outputBufferLen;

    // Source format info
    private readonly int _sourceSampleRate;
    private readonly int _sourceChannels;
    private readonly int _sourceBytesPerSample;

    // For reading from source
    private readonly byte[] _readBuffer;
    private const int ReadBufferSize = 65536; // 64KB read chunks

    // Accumulation buffer for feeding r8brain in large enough blocks
    // Using a double[] with position index to avoid ToArray() copies
    private double[] _accumulatedInput = new double[65536];
    private int _accumulatedCount;
    private const int MinFramesPerProcess = 1024; // Minimum frames to feed r8brain at once
    private const int MaxAccumulatedFrames = 65536; // Maximum frames to accumulate before forcing processing
    private readonly int _outputBytesPerFrame;

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

        Logger.Log($"R8brainResampler: r8bsrc.dll {( _dllAvailable ? "found" : "NOT found" )} at {dllPath}");
        return _dllAvailable;
    }

    private static string FindDllPath()
    {
        // Check several possible locations
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] possiblePaths =
        [
            Path.Combine(appDir, "r8bsrc.dll"),
            Path.Combine(appDir, "libs", "r8bsrc.dll"),
            Path.Combine(appDir, "..", "..", "..", "libs", "r8bsrc.dll"), // Development path
            Path.Combine(appDir, "..", "..", "..", "..", "libs", "r8bsrc.dll"),
        ];

        foreach (var path in possiblePaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return fullPath;
        }

        // Default to app directory
        return Path.Combine(appDir, "r8bsrc.dll");
    }

    /// <summary>
    /// Create a new r8brain resampler.
    /// </summary>
    /// <param name="sourceProvider">Source PCM provider (must be IWaveProvider with PCM data).</param>
    /// <param name="outputFormat">Target output format (sample rate, bit depth, channels).</param>
    /// <exception cref="InvalidOperationException">If r8bsrc.dll is not available.</exception>
    public R8brainResampler(IWaveProvider sourceProvider, WaveFormat outputFormat)
    {
        _sourceProvider = sourceProvider;
        _outputFormat = outputFormat;

        var sourceFormat = sourceProvider.WaveFormat;
        _sourceSampleRate = sourceFormat.SampleRate;
        _sourceChannels = sourceFormat.Channels;
        _sourceBytesPerSample = sourceFormat.BitsPerSample / 8;

        if (!IsDllAvailable())
        {
            throw new InvalidOperationException(
                "r8bsrc.dll not found. Please ensure r8bsrc.dll is in the application directory.");
        }

        // Calculate resampling ratio
        _ratio = (double)outputFormat.SampleRate / sourceFormat.SampleRate;

        // Output bytes per frame (one sample per channel)
        _outputBytesPerFrame = (outputFormat.BitsPerSample / 8) * outputFormat.Channels;

        Logger.Log(
            $"R8brainResampler: initializing {sourceFormat.SampleRate}Hz/{sourceFormat.BitsPerSample}bit/{sourceFormat.Channels}ch -> " +
            $"{outputFormat.SampleRate}Hz/{outputFormat.BitsPerSample}bit/{outputFormat.Channels}ch, ratio={_ratio:F6}");

        // Initialize r8brain
        // r8b_create(SrcSampleRate, DstSampleRate, MaxInLen, ReqTransBand, Res)
        // Res: 0=16bit, 1=16bitIR, 2=24bit (24bit is best for audiophile 24/32-bit float)
        int maxInLen = 4096; // Max input samples per channel per call
        double reqTransBand = 2.0; // Transition band in percent (2.0 = good default)
        int res = (int)R8BResamplerRes.R8BRR24; // 24-bit precision (supports 32-bit float)

        Logger.Log($"R8brainResampler: calling r8b_create(srcRate={sourceFormat.SampleRate}, " +
            $"dstRate={outputFormat.SampleRate}, maxInLen={maxInLen}, reqTransBand={reqTransBand}, " +
            $"res={res})");

        // Try calling r8b_create in a separate thread with timeout to detect hangs
        IntPtr state = IntPtr.Zero;
        Exception? createException = null;
        bool completed = false;

        Thread createThread = new Thread(() =>
        {
            try
            {
                Logger.Log("R8brainResampler: r8b_create calling from background thread...");
                state = r8b_create(
                    (double)sourceFormat.SampleRate,
                    (double)outputFormat.SampleRate,
                    maxInLen,
                    reqTransBand,
                    res);
                Logger.Log($"R8brainResampler: r8b_create returned IntPtr=0x{state.ToInt64():X}");
            }
            catch (Exception ex)
            {
                createException = ex;
                Logger.Log($"R8brainResampler: r8b_create threw exception: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                completed = true;
            }
        });
        createThread.IsBackground = true;
        createThread.Start();

        // Wait up to 5 seconds for completion
        DateTime start = DateTime.UtcNow;
        while (!completed && (DateTime.UtcNow - start).TotalSeconds < 5)
        {
            Thread.Sleep(100);
        }

        if (!completed)
        {
            Logger.Log("R8brainResampler: r8b_create TIMEOUT after 5 seconds — aborting thread");
            createThread.Interrupt();
            throw new InvalidOperationException("r8b_create timed out after 5 seconds");
        }

        if (createException != null)
        {
            throw new InvalidOperationException($"r8b_create failed with exception: {createException.Message}", createException);
        }

        _srcState = state;

        if (_srcState == IntPtr.Zero)
        {
            Logger.Log("R8brainResampler: r8b_create returned IntPtr.Zero — FAILED");
            throw new InvalidOperationException("r8b_create failed to create resampler state.");
        }

        Logger.Log("R8brainResampler: r8b_create succeeded");


        // r8brain-free doesn't have GetMaxInputLen — use a reasonable default
        _maxInputLen = 4096;

        // Allocate read buffer
        _readBuffer = new byte[ReadBufferSize];

        Logger.Log("R8brainResampler: initialized successfully");

    }

    public WaveFormat WaveFormat => _outputFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int totalBytesWritten = 0;

        while (totalBytesWritten < count)
        {
            // If we have buffered output data, use it first
            if (_outputBufferPos < _outputBufferLen)
            {
                int bytesToCopy = Math.Min(_outputBufferLen - _outputBufferPos, count - totalBytesWritten);
                Array.Copy(_pcmOutputBuffer!, _outputBufferPos, buffer, offset + totalBytesWritten, bytesToCopy);
                _outputBufferPos += bytesToCopy;
                totalBytesWritten += bytesToCopy;
                continue;
            }

            // Output buffer is empty — need to produce more data
            _outputBufferPos = 0;
            _outputBufferLen = 0;

            // If source is exhausted, try to flush remaining data from r8brain
            if (_sourceExhausted)
            {
                int flushed = FlushRemaining(buffer, offset + totalBytesWritten, count - totalBytesWritten);
                totalBytesWritten += flushed;
                break;
            }

            // Process ALL accumulated data first before reading more from source.
            // This prevents unbounded growth of _accumulatedCount.
            if (_accumulatedCount >= MinFramesPerProcess * _sourceChannels)
            {
                // Process accumulated data in chunks until output buffer fills up
                // or all accumulated data is consumed
                int maxProcessIterations = 100; // Safety limit
                int processIterations = 0;
                while (_accumulatedCount >= MinFramesPerProcess * _sourceChannels && processIterations < maxProcessIterations)
                {
                    processIterations++;
                    if (!ProcessAccumulated())
                        break; // Output buffer is full, will return to caller
                }
                
                // If we produced output, loop back to copy it to the caller's buffer
                if (_outputBufferLen > 0)
                    continue;
                
                // If we processed everything and still no output, we need more source data
                // Fall through to read more
            }

            // Only read more source data if we don't have too much accumulated
            int maxAccumSamples = MaxAccumulatedFrames * _sourceChannels;
            if (_accumulatedCount < maxAccumSamples)
            {
                // Read a large chunk from source (up to ReadBufferSize)
                int bytesRead = _sourceProvider.Read(_readBuffer, 0, _readBuffer.Length);

                if (bytesRead <= 0)
                {
                    _sourceExhausted = true;
                    continue; // Will hit the exhausted branch above next iteration
                }

                // Convert PCM bytes to double samples (interleaved)
                int framesRead = bytesRead / (_sourceBytesPerSample * _sourceChannels);

                if (_inputBuffer == null || _inputBuffer.Length < framesRead * _sourceChannels)
                    _inputBuffer = new double[framesRead * _sourceChannels];

                PcmToDouble(_readBuffer, 0, _inputBuffer, framesRead);

                // Add to accumulation buffer (grow if needed)
                int samplesToAdd = framesRead * _sourceChannels;
                EnsureAccumCapacity(samplesToAdd);
                Array.Copy(_inputBuffer, 0, _accumulatedInput, _accumulatedCount, samplesToAdd);
                _accumulatedCount += samplesToAdd;
            }
            else
            {
                // We have too much accumulated data and still no output.
                // Force process with all accumulated data.
                Logger.Log($"R8brainResampler.Read: forcing process of {_accumulatedCount / _sourceChannels} accumulated frames");
                ProcessAccumulated();
                
                if (_outputBufferLen == 0)
                {
                    // r8brain still didn't produce output — this shouldn't happen.
                    // Clear accumulated data to prevent infinite loop.
                    Logger.Log($"R8brainResampler.Read: WARNING — clearing {_accumulatedCount} orphan samples");
                    _accumulatedCount = 0;
                }
            }
        }

        return totalBytesWritten;
    }


    /// <summary>
    /// Ensure the accumulation buffer has enough capacity for additional samples.
    /// </summary>
    private void EnsureAccumCapacity(int additionalSamples)
    {
        int needed = _accumulatedCount + additionalSamples;
        if (needed > _accumulatedInput.Length)
        {
            int newSize = Math.Max(needed, _accumulatedInput.Length * 2);
            Array.Resize(ref _accumulatedInput, newSize);
        }
    }

    /// <summary>
    /// Process as much accumulated input as possible through r8brain.
    /// Returns false if output buffer filled up and we should stop.
    /// </summary>
    private bool ProcessAccumulated()
    {
        int availableFrames = _accumulatedCount / _sourceChannels;
        if (availableFrames < MinFramesPerProcess)
            return true; // Not enough data yet

        // Process up to _maxInputLen frames at a time
        int framesToProcess = Math.Min(availableFrames, _maxInputLen);
        int samplesToProcess = framesToProcess * _sourceChannels;

        // Pin the accumulated buffer directly (no ToArray copy)
        GCHandle inputHandle = GCHandle.Alloc(_accumulatedInput, GCHandleType.Pinned);
        try
        {
            IntPtr inputPtr = inputHandle.AddrOfPinnedObject();
            IntPtr outputPtr = IntPtr.Zero;

            Logger.Log($"R8brainResampler.Process: calling r8b_process with {framesToProcess} input frames ({samplesToProcess} samples)");
            int outputFrames = r8b_process(_srcState, inputPtr, framesToProcess, ref outputPtr);
            Logger.Log($"R8brainResampler.Process: r8b_process returned {outputFrames} output frames, outputPtr=0x{outputPtr.ToInt64():X}");

            if (outputFrames > 0 && outputPtr != IntPtr.Zero)
            {
                // Copy output double samples
                int totalOutputSamples = outputFrames * _outputFormat.Channels;
                if (_outputDoubleBuffer == null || _outputDoubleBuffer.Length < totalOutputSamples)
                    _outputDoubleBuffer = new double[totalOutputSamples];

                Marshal.Copy(outputPtr, _outputDoubleBuffer, 0, totalOutputSamples);

                // Convert to PCM bytes
                int outputBytes = outputFrames * _outputBytesPerFrame;
                if (_pcmOutputBuffer == null || _pcmOutputBuffer.Length < outputBytes)
                    _pcmOutputBuffer = new byte[outputBytes];

                DoubleToPcm(_outputDoubleBuffer, _pcmOutputBuffer, outputFrames);

                _outputBufferLen = outputBytes;
                _outputBufferPos = 0;

                Logger.Log($"R8brainResampler.Process: produced {outputBytes} bytes ({outputFrames} frames)");

                // Remove processed frames from accumulation buffer by shifting remaining data
                if (samplesToProcess <= _accumulatedCount)
                {
                    int remaining = _accumulatedCount - samplesToProcess;
                    if (remaining > 0)
                    {
                        Array.Copy(_accumulatedInput, samplesToProcess, _accumulatedInput, 0, remaining);
                    }
                    _accumulatedCount = remaining;
                    Logger.Log($"R8brainResampler.Process: removed {samplesToProcess} samples from accum, {_accumulatedCount} remaining");
                }

                // We have output — return false to let caller drain it first
                return false;
            }
            else
            {
                Logger.Log("R8brainResampler.Process: r8b_process returned 0 output frames (needs more input) — keeping accumulated data");
                // Do NOT remove samples from accumulator — r8brain needs more data to produce output
                return true;
            }
        }
        finally
        {
            inputHandle.Free();
        }
    }

    /// <summary>
    /// Flush remaining samples from r8brain by calling with 0 input.
    /// </summary>
    private int FlushRemaining(byte[] buffer, int offset, int maxCount)
    {
        int totalWritten = 0;

        Logger.Log("R8brainResampler.FlushRemaining: flushing r8brain");

        // First, process any remaining accumulated data
        // Use a safety counter to prevent infinite loops
        int safetyCounter = 0;
        while (_accumulatedCount >= _sourceChannels && safetyCounter < 100)
        {
            safetyCounter++;
            ProcessAccumulated();

            if (_outputBufferLen > 0)
            {
                int bytesToCopy = Math.Min(_outputBufferLen, maxCount - totalWritten);
                Array.Copy(_pcmOutputBuffer!, 0, buffer, offset + totalWritten, bytesToCopy);
                totalWritten += bytesToCopy;
                _outputBufferPos = bytesToCopy;
                _outputBufferLen = 0;
                Logger.Log($"R8brainResampler.FlushRemaining: flushed {bytesToCopy} bytes from accum");
            }
            else
            {
                break; // r8brain consumed all accumulated data without producing output
            }
        }

        // If there's still accumulated data that couldn't be processed (less than 1 frame),
        // just discard it — we can't do anything with it
        if (_accumulatedCount > 0 && _accumulatedCount < _sourceChannels)
        {
            Logger.Log($"R8brainResampler.FlushRemaining: discarding {_accumulatedCount} orphan samples");
            _accumulatedCount = 0;
        }

        // Now try to flush r8brain by calling with 0 input
        // r8brain-free: calling r8b_process with 0 input may produce remaining output
        // IMPORTANT: ip0 must be a valid pointer even when l=0 (CDSPProcessor::process expects valid double*)
        double[] flushDummy = new double[1];
        GCHandle flushHandle = GCHandle.Alloc(flushDummy, GCHandleType.Pinned);
        try
        {
            IntPtr flushInputPtr = flushHandle.AddrOfPinnedObject();
            IntPtr outputPtr = IntPtr.Zero;
            int flushFrames = r8b_process(_srcState, flushInputPtr, 0, ref outputPtr);
            Logger.Log($"R8brainResampler.FlushRemaining: r8b_process(0) returned {flushFrames} frames");

            if (flushFrames > 0 && outputPtr != IntPtr.Zero)
            {
                int totalOutputSamples = flushFrames * _outputFormat.Channels;
                if (_outputDoubleBuffer == null || _outputDoubleBuffer.Length < totalOutputSamples)
                    _outputDoubleBuffer = new double[totalOutputSamples];

                Marshal.Copy(outputPtr, _outputDoubleBuffer, 0, totalOutputSamples);

                int outputBytes = flushFrames * _outputBytesPerFrame;
                if (_pcmOutputBuffer == null || _pcmOutputBuffer.Length < outputBytes)
                    _pcmOutputBuffer = new byte[outputBytes];

                DoubleToPcm(_outputDoubleBuffer, _pcmOutputBuffer, flushFrames);

                int bytesToCopy = Math.Min(outputBytes, maxCount - totalWritten);
                Array.Copy(_pcmOutputBuffer, 0, buffer, offset + totalWritten, bytesToCopy);
                totalWritten += bytesToCopy;
                Logger.Log($"R8brainResampler.FlushRemaining: flushed {bytesToCopy} more bytes from r8brain");
            }
        }
        finally
        {
            flushHandle.Free();
        }

        Logger.Log($"R8brainResampler.FlushRemaining: total flushed = {totalWritten} bytes");
        return totalWritten;
    }

    /// <summary>
    /// Convert PCM byte data to double samples (interleaved).
    /// </summary>
    /// <param name="pcmData">Source PCM byte buffer.</param>
    /// <param name="pcmOffset">Offset in pcmData to start reading from.</param>
    /// <param name="doubleBuffer">Destination double buffer (must be at least frameCount * channels in size).</param>
    /// <param name="frameCount">Number of audio frames (samples per channel) to convert.</param>
    private void PcmToDouble(byte[] pcmData, int pcmOffset, double[] doubleBuffer, int frameCount)
    {
        int bitsPerSample = _sourceProvider.WaveFormat.BitsPerSample;
        int channels = _sourceChannels;
        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = frameCount * channels;

        for (int i = 0; i < totalSamples; i++)
        {
            int byteOffset = pcmOffset + i * bytesPerSample;
            if (byteOffset + bytesPerSample > pcmData.Length)
                break;

            double sample;
            switch (bitsPerSample)
            {
                case 16:
                    short s16 = (short)(pcmData[byteOffset] | (pcmData[byteOffset + 1] << 8));
                    sample = s16 / 32768.0;
                    break;
                case 24:
                    int s24 = pcmData[byteOffset] |
                              (pcmData[byteOffset + 1] << 8) |
                              (pcmData[byteOffset + 2] << 16);
                    if ((s24 & 0x800000) != 0)
                        s24 |= unchecked((int)0xFF000000);
                    sample = s24 / 8388608.0;
                    break;
                case 32:
                    int s32 = pcmData[byteOffset] |
                              (pcmData[byteOffset + 1] << 8) |
                              (pcmData[byteOffset + 2] << 16) |
                              (pcmData[byteOffset + 3] << 24);
                    sample = s32 / 2147483648.0;
                    break;
                default:
                    sample = 0;
                    break;
            }

            doubleBuffer[i] = Math.Clamp(sample, -1.0, 1.0);
        }
    }

    /// <summary>
    /// Convert double samples back to PCM byte data (interleaved).
    /// Output format matches _outputFormat.
    /// </summary>
    private void DoubleToPcm(double[] doubleBuffer, byte[] pcmBuffer, int frameCount)
    {
        int bitsPerSample = _outputFormat.BitsPerSample;
        int channels = _outputFormat.Channels;
        int bytesPerSample = bitsPerSample / 8;

        for (int i = 0; i < frameCount * channels; i++)
        {
            int byteOffset = i * bytesPerSample;
            if (byteOffset + bytesPerSample > pcmBuffer.Length)
                break;

            double sample = Math.Clamp(doubleBuffer[i], -1.0, 1.0);

            switch (bitsPerSample)
            {
                case 16:
                    short s16 = (short)(sample * 32767.0);
                    pcmBuffer[byteOffset] = (byte)(s16 & 0xFF);
                    pcmBuffer[byteOffset + 1] = (byte)((s16 >> 8) & 0xFF);
                    break;
                case 24:
                    int s24 = (int)(sample * 8388607.0);
                    pcmBuffer[byteOffset] = (byte)(s24 & 0xFF);
                    pcmBuffer[byteOffset + 1] = (byte)((s24 >> 8) & 0xFF);
                    pcmBuffer[byteOffset + 2] = (byte)((s24 >> 16) & 0xFF);
                    break;
                case 32:
                    int s32 = (int)(sample * 2147483647.0);
                    pcmBuffer[byteOffset] = (byte)(s32 & 0xFF);
                    pcmBuffer[byteOffset + 1] = (byte)((s32 >> 8) & 0xFF);
                    pcmBuffer[byteOffset + 2] = (byte)((s32 >> 16) & 0xFF);
                    pcmBuffer[byteOffset + 3] = (byte)((s32 >> 24) & 0xFF);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_srcState != IntPtr.Zero)
            {
                r8b_delete(_srcState);
                _srcState = IntPtr.Zero;
            }

            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  r8brain P/Invoke declarations
    //  Based on r8brain-free (r8bsrc.dll) exports:
    //    r8b_create, r8b_process, r8b_delete, r8b_clear
    // ════════════════════════════════════════════════════════════════

    private const string DllName = "r8bsrc.dll";

    /// <summary>
    /// Resampler resolution enum (ER8BResamplerRes from r8bsrc.h).
    /// </summary>
    private enum R8BResamplerRes
    {
        /// <summary>16-bit precision resampler.</summary>
        R8BRR16 = 0,
        /// <summary>16-bit precision resampler for impulse responses.</summary>
        R8BRR16IR = 1,
        /// <summary>24-bit precision resampler (including 32-bit floating point).</summary>
        R8BRR24 = 2
    }

    /// <summary>
    /// Create the r8brain resampler.
    /// r8b_create(SrcSampleRate, DstSampleRate, MaxInLen, ReqTransBand, Res) -> void*
    /// </summary>
    /// <param name="srcSampleRate">Source signal sample rate (double).</param>
    /// <param name="dstSampleRate">Destination signal sample rate (double).</param>
    /// <param name="maxInLen">Maximum planned input length in samples (per channel).</param>
    /// <param name="reqTransBand">Required transition band in percent of spectral space (2.0 = good default).</param>
    /// <param name="res">Resampler resolution (0=16bit, 1=16bitIR, 2=24bit).</param>
    /// <returns>Pointer to resampler state, or IntPtr.Zero on failure.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_create")]
    private static extern IntPtr r8b_create(
        double srcSampleRate,
        double dstSampleRate,
        int maxInLen,
        double reqTransBand,
        int res);


    /// <summary>
    /// Process input samples through the resampler.
    /// r8b_process(state, ip0, l, op0) -> int
    /// IMPORTANT: In r8bsrc.h on Windows x64, 'long' is 4 bytes (LLP64 model).
    /// In C#, 'long' is always 8 bytes. Using 'long' would cause stack misalignment,
    /// corrupting the output pointer parameter. Must use 'int' (4 bytes).
    /// Note: op0 is passed by reference (double*& in C++), so we use 'ref IntPtr'.
    /// The output pointer may point to the input buffer or an internal buffer.
    /// </summary>
    /// <param name="state">Resampler state from r8b_create.</param>
    /// <param name="input">Pointer to input samples as doubles (interleaved).</param>
    /// <param name="inputSampleCount">Number of input samples PER CHANNEL (int, 4 bytes on x64 LLP64).</param>
    /// <param name="output">Reference to output pointer for resampled data.</param>
    /// <returns>Number of output samples (per channel) produced.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_process")]
    private static extern int r8b_process(
        IntPtr state,
        IntPtr input,
        int inputSampleCount,  // CRITICAL: 'int' not 'long' — Windows x64 LLP64 uses 4-byte long
        ref IntPtr output);

    /// <summary>
    /// Clear/flush the resampler internal state.
    /// r8b_clear(state)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_clear")]
    private static extern void r8b_clear(IntPtr state);

    /// <summary>
    /// Delete the resampler state and free resources.
    /// r8b_delete(state)
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_delete")]
    private static extern void r8b_delete(IntPtr state);
}
