using NAudio.Dsp;

namespace PureAudio.Services;

public class FftService
{
    // FFT size 2048 for better frequency resolution (~21.5 Hz per bin at 44.1kHz)
    // Overlap processing (every 128 samples) reduces effective latency to ~2.9ms
    private const int FftLength = 2048;
    private const int OverlapStep = 128;          // Process FFT every 128 samples (93.75% overlap)
    private const int BinCount = 48;
    private readonly float[] _currentData = new float[BinCount];
    private readonly float[] _peakData = new float[BinCount];
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private readonly float[] _sampleBuffer = new float[FftLength];
    private int _sampleIndex;
    private int _samplesSinceLastFft;
    private bool _hasData;

    // Per-band adaptive normalization: each band tracks its own max
    private readonly float[] _bandMax = new float[BinCount];
    private const float BandMaxAttack = 1.0f;   // Instant attack
    private const float BandMaxDecay = 0.93f;    // Faster decay so bands drop quicker after peaks

    // EMA smoothing — slightly increased for smoother response
    private const float SmoothingAlpha = 0.88f;  // Smoother response (was 0.9)

    // Peak hold with moderate falloff — peaks rise instantly, then decay at a natural rate
    // Fast enough to track transients, slow enough to be visually noticeable
    private const float PeakDecay = 0.85f;       // Faster decay for more natural peak falloff (was 0.92)

    // Contrast boost exponent — moderate contrast for smoother transitions between bands
    private const float ContrastPower = 1.1f;    // Minimal contrast to avoid "sticking" to top (was 1.6)

    // High-frequency boost factor — compensates for lower energy in upper bands
    private const float HfBoostMax = 0.8f;       // Max boost applied to highest band

    // Global input attenuation for FFT processing.
    // Reduces overall spectrum sensitivity for both Shared and Exclusive modes.
    // Lower values = less sensitive spectrum (more dynamic range visible).
    private const float InputAttenuation = 0.15f;

    // Sample rate assumption for frequency calculations
    private float _sampleRate = 44100f;

    /// <summary>
    /// Pre-computed logarithmic bin ranges across the audible frequency spectrum (20 Hz – 20 000 Hz).
    /// Maps display bins to FFT bin indices using log-spaced frequency boundaries.
    /// </summary>
    private readonly (int start, int end)[] _binRanges;

    public FftService()
    {
        _binRanges = ComputeLogFreqRanges();
    }

    /// <summary>
    /// Computes FFT bin ranges using logarithmic frequency spacing.
    /// Formula: bins[i] = Round(exp(log(minFreq) + (i / numBands) * (log(maxFreq) - log(minFreq))) / binWidth)
    /// where binWidth = sampleRate / fftSize.
    /// </summary>
    private (int start, int end)[] ComputeLogFreqRanges()
    {
        double minFreq = 20.0;     // 20 Hz
        double maxFreq = 20000.0;  // 20 kHz
        double binWidth = _sampleRate / FftLength; // Hz per FFT bin
        int usableBins = FftLength / 2; // 512

        var ranges = new (int start, int end)[BinCount];

        double logMin = Math.Log(minFreq);
        double logMax = Math.Log(maxFreq);

        for (int i = 0; i < BinCount; i++)
        {
            // Logarithmic frequency boundaries for this band
            double freqStart = Math.Exp(logMin + (logMax - logMin) * i / BinCount);
            double freqEnd = Math.Exp(logMin + (logMax - logMin) * (i + 1) / BinCount);

            // Convert frequencies to FFT bin indices
            int startBin = (int)Math.Round(freqStart / binWidth);
            int endBin = (int)Math.Round(freqEnd / binWidth);

            // Clamp to valid range
            if (startBin < 1) startBin = 1;
            if (endBin > usableBins) endBin = usableBins;
            if (endBin <= startBin) endBin = startBin + 1;

            ranges[i] = (startBin, endBin);
        }

        return ranges;
    }

    /// <summary>
    /// Updates the sample rate used for frequency bin calculations.
    /// Called when the audio source changes.
    /// </summary>
    public void SetSampleRate(float sampleRate)
    {
        if (sampleRate > 0 && Math.Abs(_sampleRate - sampleRate) > 1)
        {
            _sampleRate = sampleRate;
            // Recompute bin ranges with the new sample rate
            var newRanges = ComputeLogFreqRanges();
            for (int i = 0; i < BinCount; i++)
            {
                _binRanges[i] = newRanges[i];
            }
        }
    }

    public float[] ProcessSamples(ReadOnlySpan<float> samples)
    {
        try
        {
            if (samples.Length == 0)
                return _currentData;

            // Apply global input attenuation to reduce overall spectrum sensitivity
            // for both Shared and Exclusive modes.
            foreach (var sample in samples)
            {
                _sampleBuffer[_sampleIndex] = sample * InputAttenuation;
                _sampleIndex = (_sampleIndex + 1) % FftLength;
            }

            // Overlap processing: only run FFT every OverlapStep samples
            // This gives 75% overlap (FFT every 256 samples with 1024 buffer)
            // Effective latency: ~5.8ms at 44.1kHz instead of ~23ms
            _samplesSinceLastFft += samples.Length;
            if (_samplesSinceLastFft < OverlapStep)
                return _currentData;
            _samplesSinceLastFft = 0;

            // Copy buffer to FFT input (in order) with Hamming window
            for (int i = 0; i < FftLength; i++)
            {
                int idx = (_sampleIndex + i) % FftLength;
                _fftBuffer[i].X = (float)(_sampleBuffer[idx] * FastFourierTransform.HammingWindow(i, FftLength));
                _fftBuffer[i].Y = 0;
            }

            // Perform FFT
            FastFourierTransform.FFT(true, (int)Math.Log2(FftLength), _fftBuffer);

            // Compute magnitude for each display bin
            Span<float> currentData = _currentData.AsSpan();
            Span<float> peakData = _peakData.AsSpan();
            Span<float> bandMax = _bandMax.AsSpan();

            for (int i = 0; i < BinCount; i++)
            {
                var (startBin, endBin) = _binRanges[i];

                // Combined aggregation: 70% max (for transient response) + 30% average (for even energy)
                float maxMagnitude = 0;
                float sumMagnitude = 0;
                int binCount = endBin - startBin;
                for (int b = startBin; b < endBin; b++)
                {
                    float magnitude = (float)Math.Sqrt(
                        _fftBuffer[b].X * _fftBuffer[b].X +
                        _fftBuffer[b].Y * _fftBuffer[b].Y);
                    sumMagnitude += magnitude;
                    if (magnitude > maxMagnitude)
                        maxMagnitude = magnitude;
                }
                float avgMagnitude = sumMagnitude / binCount;
                float combinedMagnitude = maxMagnitude * 0.7f + avgMagnitude * 0.3f;

                // High-frequency boost: linearly increase boost for higher bands
                // Band 0 gets 0 boost, highest band gets HfBoostMax (0.8) boost
                float hfBoost = 1.0f + HfBoostMax * i / (BinCount - 1);
                combinedMagnitude *= hfBoost;

                // Per-band adaptive normalization
                if (combinedMagnitude > bandMax[i])
                {
                    bandMax[i] = bandMax[i] * (1 - BandMaxAttack) + combinedMagnitude * BandMaxAttack;
                }
                else
                {
                    bandMax[i] *= BandMaxDecay;
                }

                if (bandMax[i] < 0.0001f)
                    bandMax[i] = 0.0001f;

                // Normalize by this band's own max
                float normalized = combinedMagnitude / bandMax[i];

                // Light compression: raise to 0.7 power instead of sqrt (0.5)
                // This preserves more transient detail while still compressing range
                float compressed = (float)Math.Pow(normalized, 0.7f);

                // Apply contrast boost: amplify differences between bands
                // Values < 0.5 get pushed down, values > 0.5 get pushed up
                float contrasted = (float)Math.Pow(compressed, ContrastPower);

                // Clamp
                float newValue = Math.Clamp(contrasted, 0, 1);

                // Soft ceiling: prevent bands from reaching 100% to preserve headroom
                newValue = Math.Min(newValue, 0.9f);

                // --- Exponential Moving Average smoothing ---
                // newColumn[i] = oldColumn[i] * (1 - alpha) + currentColumn[i] * alpha
                currentData[i] = currentData[i] * (1 - SmoothingAlpha) + newValue * SmoothingAlpha;

                // --- Peak hold with exponential decay ---
                // peak[i] = Max(currentColumn[i], peak[i] * decay)
                peakData[i] = Math.Max(currentData[i], peakData[i] * PeakDecay);
            }

            // --- Inter-band smoothing (3-tap moving average) ---
            // Smooths out abrupt jumps between adjacent bands for a more natural look.
            // Each band becomes: 25% left neighbor + 50% self + 25% right neighbor.
            // This eliminates the "picket fence" effect while preserving the overall shape.
            for (int i = 0; i < BinCount; i++)
            {
                int left = i > 0 ? i - 1 : i;
                int right = i < BinCount - 1 ? i + 1 : i;
                float smoothed = currentData[left] * 0.25f + currentData[i] * 0.5f + currentData[right] * 0.25f;
                currentData[i] = smoothed;
            }

            _hasData = true;
            return _currentData;
        }
        catch (Exception)
        {
            return _currentData;
        }
    }

    public float[] GetPlaceholderData()
    {
        var copy = new float[BinCount];
        Array.Copy(_currentData, copy, BinCount);
        return copy;
    }

    public float[] GetPeakData()
    {
        var copy = new float[BinCount];
        Array.Copy(_peakData, copy, BinCount);
        return copy;
    }

    public bool HasData => _hasData;
    public int BinCountValue => BinCount;

    public void Reset()
    {
        Array.Clear(_currentData, 0, _currentData.Length);
        Array.Clear(_peakData, 0, _peakData.Length);
        Array.Clear(_bandMax, 0, _bandMax.Length);
        Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
        Array.Clear(_sampleBuffer, 0, _sampleBuffer.Length);
        _sampleIndex = 0;
        _hasData = false;
    }
}
