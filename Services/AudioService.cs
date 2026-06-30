using NAudio.Wave;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// A sample provider that captures audio samples for FFT processing.
/// </summary>
internal class FftCaptureProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly FftService _fftService;

    public FftCaptureProvider(ISampleProvider source, FftService fftService)
    {
        _source = source;
        _fftService = fftService;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        try
        {
            int samplesRead = _source.Read(buffer, offset, count);
            if (samplesRead > 0)
            {
                // Extract a mono mix for FFT processing
                int channels = _source.WaveFormat.Channels;
                float[] monoBuffer = new float[samplesRead / channels];
                for (int i = 0; i < monoBuffer.Length; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int idx = offset + i * channels + ch;
                        if (idx < offset + samplesRead)
                            sum += buffer[idx];
                    }
                    monoBuffer[i] = sum / channels;
                }
                _fftService.ProcessSamples(monoBuffer);
            }
            return samplesRead;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FftCaptureProvider.Read exception: {ex.Message}");
            return 0;
        }
    }
}

public class AudioService : IDisposable
{
    private IWavePlayer? _outputDevice;
    private AudioFileReader? _audioFileReader;
    private FftCaptureProvider? _fftCaptureProvider;
    private readonly PlaylistService _playlistService;
    private readonly FftService _fftService;
    private bool _wasapiExclusive;
    private bool _isPlaying;
    private bool _isPaused;
    private float _volume = 0.5f; // Default 50% volume for exclusive mode
    private CancellationTokenSource? _positionCts;
    private TimeSpan _pausePosition; // Position saved when pausing in Exclusive mode
    private int _playbackId; // Incremented on each PlayInternal to ignore stale PlaybackStopped events

    public event Action<AudioFile>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public event Action<bool>? WasapiModeChanged;
    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public bool WasapiExclusive => _wasapiExclusive;
    public float Volume => _volume;
    public TimeSpan CurrentPosition => _audioFileReader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _audioFileReader?.TotalTime ?? TimeSpan.Zero;
    public int Bitrate => _playlistService.CurrentItem?.AudioFile.Bitrate ?? 0;

    /// <summary>
    /// Current sample rate of the playing track (0 if not playing).
    /// Used by UI for bit-perfect indicator.
    /// </summary>
    public int CurrentSampleRate => _audioFileReader?.WaveFormat.SampleRate ?? 0;

    /// <summary>
    /// Current bit depth of the playing track (0 if not playing).
    /// Used by UI for bit-perfect indicator.
    /// </summary>
    public int CurrentBitDepth => _audioFileReader?.WaveFormat.BitsPerSample ?? 0;

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (_audioFileReader != null)
        {
            // In Exclusive mode, WASAPI bypasses the system mixer, so the system
            // volume slider has no effect. We apply software volume control here
            // so the user can still adjust volume via the app's slider.
            // In Shared mode, the system volume slider works, so we keep
            // AudioFileReader.Volume at 1.0f (no software modification).
            if (_wasapiExclusive)
            {
                _audioFileReader.Volume = _volume * _volume * _volume; // Cubic curve for smoother control
            }
        }
        VolumeChanged?.Invoke(_volume);
    }

    public AudioService(PlaylistService playlistService, FftService fftService)
    {
        _playlistService = playlistService;
        _fftService = fftService;
    }

    public void SetWasapiMode(bool exclusive)
    {
        _wasapiExclusive = exclusive;
        if (_isPlaying || _isPaused)
        {
            var position = CurrentPosition;
            System.Diagnostics.Debug.WriteLine($"SetWasapiMode: switching to {(exclusive ? "Exclusive" : "Shared")}, position={position.TotalSeconds:F3}s");
            StopInternal();

            // When switching to Exclusive mode, the old WasapiOut may still hold
            // the audio device exclusively. A small delay ensures the device is
            // released before creating a new WasapiOut in Exclusive mode.
            if (exclusive)
            {
                System.Threading.Thread.Sleep(150);
            }

            PlayInternal(position);
        }
    }

    public void Play()
    {
        if (_isPaused)
        {
            Resume();
            return;
        }

        var currentItem = _playlistService.CurrentItem;
        if (currentItem == null)
        {
            // Try to start from first item
            var firstItem = _playlistService.Items.FirstOrDefault();
            if (firstItem == null) return;
            _playlistService.CurrentIndex = 0;
            currentItem = firstItem;
        }

        PlayInternal(TimeSpan.Zero);
    }

    private void PlayInternal(TimeSpan position)
    {
        System.Diagnostics.Debug.WriteLine($"PlayInternal: requested position = {position.TotalSeconds:F3}s");
        int currentPlaybackId = ++_playbackId;
        StopInternal();

        var currentItem = _playlistService.CurrentItem;
        if (currentItem == null) return;

        try
        {
            // Reset FFT state for new playback
            _fftService.Reset();

            _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
            System.Diagnostics.Debug.WriteLine($"PlayInternal: opened file, total={_audioFileReader.TotalTime.TotalSeconds:F3}s");
            // Inform FFT service about the actual sample rate for accurate frequency bin mapping
            _fftService.SetSampleRate(_audioFileReader.WaveFormat.SampleRate);
            if (position < _audioFileReader.TotalTime)
            {
                _audioFileReader.CurrentTime = position;
                System.Diagnostics.Debug.WriteLine($"PlayInternal: set position to {_audioFileReader.CurrentTime.TotalSeconds:F3}s");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"PlayInternal: position {position.TotalSeconds:F3}s >= total {_audioFileReader.TotalTime.TotalSeconds:F3}s, starting from beginning");
            }

            // In WASAPI Exclusive mode, the system mixer is bypassed, so the
            // system volume slider has no effect. We apply software volume
            // control via AudioFileReader.Volume so the user can still adjust
            // volume through the app's slider.
            // In Shared mode, the system volume slider works, so we keep
            // AudioFileReader.Volume at 1.0f (no software modification).
            if (_wasapiExclusive)
            {
                _audioFileReader.Volume = _volume * _volume * _volume; // Cubic curve for smoother control
            }
            else
            {
                _audioFileReader.Volume = 1.0f;
            }

            // Wrap with FFT capture provider
            _fftCaptureProvider = new FftCaptureProvider(_audioFileReader, _fftService);

            _outputDevice = CreateWasapiOutput();
            _outputDevice.PlaybackStopped += OnPlaybackStopped;
            _outputDevice.Init(_fftCaptureProvider);
            _outputDevice.Play();

            _isPlaying = true;
            _isPaused = false;
            TrackChanged?.Invoke(currentItem.AudioFile);
            DurationChanged?.Invoke(_audioFileReader.TotalTime);
            BitrateChanged?.Invoke(Bitrate);
            PlayStateChanged?.Invoke(true);

            StartPositionTracking();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Playback error (playbackId={currentPlaybackId}): {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
        }
    }

    private IWavePlayer CreateWasapiOutput()
    {
        if (_wasapiExclusive)
        {
            System.Diagnostics.Debug.WriteLine("CreateWasapiOutput: creating Exclusive WasapiOut");
            try
            {
                var wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Exclusive, 100);
                System.Diagnostics.Debug.WriteLine("CreateWasapiOutput: Exclusive WasapiOut created successfully");
                return wasapi;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateWasapiOutput: FAILED to create Exclusive WasapiOut: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
                // Fall back to Shared mode
                System.Diagnostics.Debug.WriteLine("CreateWasapiOutput: falling back to Shared WasapiOut");
                _wasapiExclusive = false;
                WasapiModeChanged?.Invoke(false);
                var wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                System.Diagnostics.Debug.WriteLine("CreateWasapiOutput: Shared WasapiOut created as fallback");
                return wasapi;
            }
        }
        System.Diagnostics.Debug.WriteLine("CreateWasapiOutput: creating Shared WasapiOut");
        return new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
    }

    public void Pause()
    {
        if (_outputDevice != null && _isPlaying)
        {
            // Save the current position before pausing
            _pausePosition = _audioFileReader?.CurrentTime ?? TimeSpan.Zero;
            System.Diagnostics.Debug.WriteLine($"PAUSE: saved position = {_pausePosition.TotalSeconds:F3}s");

            // Set flags BEFORE stopping to prevent OnPlaybackStopped from
            // triggering Next() or Stop() when the device stops.
            _isPlaying = false;
            _isPaused = true;
            PlayStateChanged?.Invoke(false);

            // Unsubscribe from PlaybackStopped temporarily to prevent stale events
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;

            // Stop the output device. WasapiOut.Pause() can cause audio looping
            // in Exclusive mode, so we use Stop() instead.
            _outputDevice.Stop();

            // Re-subscribe so that natural end-of-track still works
            _outputDevice.PlaybackStopped += OnPlaybackStopped;
        }
    }

    public void Resume()
    {
        if (_isPaused)
        {
            System.Diagnostics.Debug.WriteLine($"RESUME: restoring position = {_pausePosition.TotalSeconds:F3}s");

            if (_outputDevice != null && _audioFileReader != null)
            {
                // Reset the reader to the saved pause position before resuming.
                // The output device was stopped but not disposed, so we can just
                // call Play() to resume from where we left off.
                if (_pausePosition < _audioFileReader.TotalTime)
                {
                    _audioFileReader.CurrentTime = _pausePosition;
                }
                _outputDevice.Play();
                _isPlaying = true;
                _isPaused = false;
                PlayStateChanged?.Invoke(true);
            }
            else
            {
                // Device was disposed (e.g., mode switch) — recreate from saved position
                PlayInternal(_pausePosition);
            }
        }
    }

    public void Stop()
    {
        StopInternal();
        _fftService.Reset();
        PlayStateChanged?.Invoke(false);
    }

    private void StopInternal()
    {
        _positionCts?.Cancel();
        _isPlaying = false;
        _isPaused = false;

        if (_outputDevice != null)
        {
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
            _outputDevice.Stop();
            _outputDevice.Dispose();
            _outputDevice = null;
        }

        if (_audioFileReader != null)
        {
            _audioFileReader.Dispose();
            _audioFileReader = null;
        }
    }

    public void Next()
    {
        var next = _playlistService.GetNext();
        if (next != null)
        {
            PlayInternal(TimeSpan.Zero);
        }
        else
        {
            Stop();
        }
    }

    public void Previous()
    {
        var prev = _playlistService.GetPrevious();
        if (prev != null)
        {
            PlayInternal(TimeSpan.Zero);
        }
        else
        {
            Stop();
        }
    }

    public void Seek(double fraction)
    {
        if (_audioFileReader != null)
        {
            var newPosition = TimeSpan.FromTicks((long)(_audioFileReader.TotalTime.Ticks * fraction));
            _audioFileReader.CurrentTime = newPosition;
            PositionChanged?.Invoke(newPosition);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Ignore stale PlaybackStopped events from previous PlayInternal calls.
        if (!_isPlaying || _isPaused)
            return;

        // If the sender is not the current output device, ignore this event.
        if (sender != _outputDevice)
            return;

        System.Diagnostics.Debug.WriteLine(
            $"OnPlaybackStopped: isPlaying={_isPlaying}, isPaused={_isPaused}");

        // IMPORTANT: Do NOT call Next() synchronously here.
        // We are inside the PlaybackStopped event handler of the old WasapiOut.
        // Calling StopInternal() -> Dispose() on the current _outputDevice from
        // within its own event handler can cause deadlocks or leave the new
        // device in a broken state (track appears to pause instead of playing).
        //
        // Instead, schedule the next track on a background thread so that the
        // old device's event handler can complete cleanly before we create a
        // new output device.
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Next();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Async Next() error: {ex.Message}");
            }
        });
    }

    private async void StartPositionTracking()
    {
        _positionCts?.Cancel();
        _positionCts = new CancellationTokenSource();
        var token = _positionCts.Token;

        try
        {
            while (!token.IsCancellationRequested && _audioFileReader != null)
            {
                await Task.Delay(250, token);
                if (_audioFileReader != null)
                {
                    PositionChanged?.Invoke(_audioFileReader.CurrentTime);
                }
            }
        }
        catch (TaskCanceledException) { }
    }

    public void Dispose()
    {
        StopInternal();
    }
}
