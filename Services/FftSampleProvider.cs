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

    public FftSampleProvider(ISampleProvider source, FftService fftService)
    {
        _source = source;
        _fftService = fftService;
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
            
            // Feed to spectrum analyzer
            _fftService.ProcessSamples(fftBuffer);
        }
        
        return samplesRead;
    }
}
