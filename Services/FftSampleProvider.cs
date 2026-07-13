using NAudio.Wave;

namespace PureAudio.Services;

/// <summary>
/// A simple ISampleProvider wrapper that intercepts float audio data 
/// and feeds it to the FftService for spectrum visualization.
/// Used in Shared mode where AudioFileReader outputs 32-bit float.
/// </summary>
internal class FftSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly FftService _fftService;
    private readonly FftQueue _fftQueue;

    public FftSampleProvider(ISampleProvider source, FftService fftService, FftQueue? fftQueue = null)
    {
        _source = source;
        _fftService = fftService;
        _fftQueue = fftQueue ?? new FftQueue(fftService);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        
        if (samplesRead > 0)
        {
            // Copy the read samples to a temporary buffer for FFT processing
            float[] fftBuffer = new float[samplesRead];
            System.Array.Copy(buffer, offset, fftBuffer, 0, samplesRead);
            
            // Feed to spectrum analyzer via thread-safe queue
            _fftQueue.Enqueue(fftBuffer, samplesRead);
        }
        
        return samplesRead;
    }
}
