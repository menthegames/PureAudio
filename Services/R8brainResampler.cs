using System.IO;
using System.Runtime.InteropServices;
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
    private double[]? _outputBuffer;
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

        Logger.Log(
            $"R8brainResampler: initializing {sourceFormat.SampleRate}Hz/{sourceFormat.BitsPerSample}bit/{sourceFormat.Channels}ch -> " +
            $"{outputFormat.SampleRate}Hz/{outputFormat.BitsPerSample}bit/{outputFormat.Channels}ch, ratio={_ratio:F6}");

        // Initialize r8brain
        // R8BRAIN_Initialize(ratio, srcSampleRate, dstSampleRate, channels, quality)
        // quality: 0=fast, 1=medium, 2=best (we use best for audiophile quality)
        int quality = 2; // Best quality
        _srcState = R8BRAIN_Initialize(_ratio, sourceFormat.SampleRate, outputFormat.SampleRate,
            sourceFormat.Channels, quality);

        if (_srcState == IntPtr.Zero)
        {
            throw new InvalidOperationException("R8BRAIN_Initialize failed to create resampler state.");
        }

        // Calculate max input length for the resampler
        _maxInputLen = R8BRAIN_GetMaxInputLen(_srcState);

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

            // Need more data — read from source
            _outputBufferPos = 0;
            _outputBufferLen = 0;

            // Calculate how many input samples we need
            int inputSamplesNeeded = _maxInputLen;
            int inputBytesNeeded = inputSamplesNeeded * _sourceBytesPerSample * _sourceChannels;

            // Read from source
            int bytesRead = _sourceProvider.Read(_readBuffer, 0, Math.Min(inputBytesNeeded, _readBuffer.Length));

            if (bytesRead <= 0)
            {
                // No more data — flush remaining samples
                FlushResampler(buffer, offset + totalBytesWritten, count - totalBytesWritten, out int flushed);
                totalBytesWritten += flushed;
                break;
            }

            // Convert PCM bytes to double samples for r8brain
            int sampleCount = bytesRead / (_sourceBytesPerSample * _sourceChannels);
            if (sampleCount <= 0) continue;

            if (_inputBuffer == null || _inputBuffer.Length < sampleCount * _sourceChannels)
                _inputBuffer = new double[sampleCount * _sourceChannels];

            PcmToDouble(_readBuffer, 0, _inputBuffer, sampleCount);

            // Process through r8brain
            int outputSamples = R8BRAIN_Process(_srcState, _inputBuffer, sampleCount, out IntPtr outputPtr);

            if (outputSamples > 0 && outputPtr != IntPtr.Zero)
            {
                // Convert double samples back to PCM
                int outputSampleCount = outputSamples * _sourceChannels;
                if (_outputBuffer == null || _outputBuffer.Length < outputSampleCount)
                    _outputBuffer = new double[outputSampleCount];

                Marshal.Copy(outputPtr, _outputBuffer, 0, outputSampleCount);

                // Convert to PCM bytes
                int outputBytes = outputSamples * (_outputFormat.BitsPerSample / 8) * _outputFormat.Channels;
                if (_pcmOutputBuffer == null || _pcmOutputBuffer.Length < outputBytes)
                    _pcmOutputBuffer = new byte[outputBytes];

                DoubleToPcm(_outputBuffer, _pcmOutputBuffer, outputSamples);

                _outputBufferLen = outputBytes;
            }
            else
            {
                // No output yet — r8brain needs more input samples
                continue;
            }
        }

        return totalBytesWritten;
    }

    private void FlushResampler(byte[] buffer, int offset, int maxCount, out int written)
    {
        written = 0;

        // Flush remaining samples from r8brain
        int outputSamples = R8BRAIN_Flush(_srcState, out IntPtr outputPtr);

        if (outputSamples > 0 && outputPtr != IntPtr.Zero)
        {
            int outputSampleCount = outputSamples * _sourceChannels;
            if (_outputBuffer == null || _outputBuffer.Length < outputSampleCount)
                _outputBuffer = new double[outputSampleCount];

            Marshal.Copy(outputPtr, _outputBuffer, 0, outputSampleCount);

            int outputBytes = outputSamples * (_outputFormat.BitsPerSample / 8) * _outputFormat.Channels;
            if (_pcmOutputBuffer == null || _pcmOutputBuffer.Length < outputBytes)
                _pcmOutputBuffer = new byte[outputBytes];

            DoubleToPcm(_outputBuffer, _pcmOutputBuffer, outputSamples);

            int bytesToCopy = Math.Min(outputBytes, maxCount);
            Array.Copy(_pcmOutputBuffer, 0, buffer, offset, bytesToCopy);
            written = bytesToCopy;
        }
    }

    /// <summary>
    /// Convert PCM byte data to double samples (interleaved).
    /// </summary>
    private void PcmToDouble(byte[] pcmData, int pcmOffset, double[] doubleBuffer, int sampleCount)
    {
        int bitsPerSample = _sourceProvider.WaveFormat.BitsPerSample;
        int channels = _sourceChannels;
        int bytesPerSample = bitsPerSample / 8;

        for (int i = 0; i < sampleCount * channels; i++)
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
    private void DoubleToPcm(double[] doubleBuffer, byte[] pcmBuffer, int sampleCount)
    {
        int bitsPerSample = _outputFormat.BitsPerSample;
        int channels = _outputFormat.Channels;
        int bytesPerSample = bitsPerSample / 8;

        for (int i = 0; i < sampleCount * channels; i++)
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
                R8BRAIN_Delete(_srcState);
                _srcState = IntPtr.Zero;
            }
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  r8brain P/Invoke declarations
    // ════════════════════════════════════════════════════════════════

    private const string DllName = "r8bsrc.dll";

    /// <summary>
    /// Initialize the r8brain resampler.
    /// </summary>
    /// <param name="ratio">Resampling ratio (outputSampleRate / inputSampleRate).</param>
    /// <param name="srcSampleRate">Source sample rate in Hz.</param>
    /// <param name="dstSampleRate">Destination sample rate in Hz.</param>
    /// <param name="channels">Number of channels (1 or 2).</param>
    /// <param name="quality">Quality: 0=fast, 1=medium, 2=best.</param>
    /// <returns>Pointer to resampler state, or IntPtr.Zero on failure.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr R8BRAIN_Initialize(
        double ratio,
        int srcSampleRate,
        int dstSampleRate,
        int channels,
        int quality);

    /// <summary>
    /// Process input samples through the resampler.
    /// </summary>
    /// <param name="state">Resampler state from R8BRAIN_Initialize.</param>
    /// <param name="input">Input samples as doubles (interleaved).</param>
    /// <param name="inputSampleCount">Number of input samples (per channel).</param>
    /// <param name="output">Output pointer for resampled data.</param>
    /// <returns>Number of output samples (per channel) produced.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int R8BRAIN_Process(
        IntPtr state,
        [In] double[] input,
        int inputSampleCount,
        out IntPtr output);

    /// <summary>
    /// Flush remaining samples from the resampler (call when input is exhausted).
    /// </summary>
    /// <param name="state">Resampler state.</param>
    /// <param name="output">Output pointer for remaining resampled data.</param>
    /// <returns>Number of output samples (per channel) produced.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int R8BRAIN_Flush(
        IntPtr state,
        out IntPtr output);

    /// <summary>
    /// Delete the resampler state and free resources.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void R8BRAIN_Delete(IntPtr state);

    /// <summary>
    /// Get the maximum input length (in samples per channel) that the resampler
    /// can process in one call.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int R8BRAIN_GetMaxInputLen(IntPtr state);
}
