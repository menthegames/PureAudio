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

/// <summary>
/// A sample provider that chains multiple AudioFileReaders together for gapless playback.
/// When the current reader runs out of data, it seamlessly opens the next track.
/// This runs on the NAudio playback thread, so it must be thread-safe and not touch UI.
/// </summary>
internal class GaplessSampleProvider : ISampleProvider
{
    private AudioFileReader? _currentReader;
    private readonly PlaylistService _playlistService;
    private readonly Func<bool> _isGaplessEnabled;
    private readonly Func<float> _getVolume;
    private readonly Func<bool> _getWasapiExclusive;
    private bool _disposed;
    private bool _hasAdvanced; // Prevents multiple advances in a single Read() call

    // Event raised on the NAudio playback thread when a track ends and next begins.
    // AudioService must handle this to update _audioFileReader and notify UI on correct thread.
    internal event Action<AudioFile>? TrackAdvanced;

    public GaplessSampleProvider(
        AudioFileReader initialReader,
        PlaylistService playlistService,
        Func<bool> isGaplessEnabled,
        Func<float> getVolume,
        Func<bool> getWasapiExclusive)
    {
        _currentReader = initialReader;
        _playlistService = playlistService;
        _isGaplessEnabled = isGaplessEnabled;
        _getVolume = getVolume;
        _getWasapiExclusive = getWasapiExclusive;
    }

    public WaveFormat WaveFormat => _currentReader?.WaveFormat ?? new WaveFormat(44100, 16, 2);

    public AudioFileReader? CurrentReader => _currentReader;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed || _currentReader == null)
            return 0;

        int totalRead = 0;
        _hasAdvanced = false;

        while (totalRead < count)
        {
            int remaining = count - totalRead;
            int samplesRead = _currentReader.Read(buffer, offset + totalRead, remaining);

            if (samplesRead > 0)
            {
                totalRead += samplesRead;
            }

            // Current reader has finished — try to advance to next track
            if (!_hasAdvanced && (_currentReader.TotalTime - _currentReader.CurrentTime <= TimeSpan.Zero || samplesRead == 0))
            {
                // If gapless is disabled, stop here and let the caller handle it
                if (!_isGaplessEnabled())
                    break;

                // Try to get the next track
                var nextItem = _playlistService.GetNext();
                if (nextItem == null)
                    break; // No more tracks

                // Open the next file
                var nextReader = OpenNextReader(nextItem.AudioFile);
                if (nextReader == null)
                    break;

                // Dispose old reader and switch to new one
                var oldReader = _currentReader;
                _currentReader = nextReader;
                oldReader.Dispose();

                _hasAdvanced = true;

                // Notify AudioService about the track change (it will update _audioFileReader)
                TrackAdvanced?.Invoke(nextItem.AudioFile);

                // Continue reading from the new track into the same buffer
                // (this is what makes it truly gapless — no gap in the output stream)
            }
            else
            {
                // Reader still has data, we've read what we can
                break;
            }
        }

        return totalRead;
    }

    private AudioFileReader? OpenNextReader(AudioFile audioFile)
    {
        try
        {
            var reader = new AudioFileReader(audioFile.FilePath);
            float vol = _getVolume();
            bool excl = _getWasapiExclusive();
            reader.Volume = excl ? vol * vol * vol : 1.0f;
            return reader;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Gapless: failed to open next track: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replace the current reader (e.g., when user clicks Next/Previous or seeks).
    /// </summary>
    public void ReplaceReader(AudioFileReader newReader)
    {
        var oldReader = _currentReader;
        _currentReader = newReader;
        oldReader?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _currentReader?.Dispose();
        _currentReader = null;
    }
}

public class AudioService : IDisposable
{
    private IWavePlayer? _outputDevice;
    private AudioFileReader? _audioFileReader;
    private FftCaptureProvider? _fftCaptureProvider;
    private GaplessSampleProvider? _gaplessProvider;
    private readonly PlaylistService _playlistService;
    private readonly FftService _fftService;
    private bool _wasapiExclusive;
    private bool _gaplessEnabled;
    private bool _isPlaying;
    private bool _isPaused;
    private float _volume = 0.5f; // Default 50% volume for exclusive mode
    private CancellationTokenSource? _gaplessCts;
    private TimeSpan _pausePosition; // Position saved when pausing in Exclusive mode
    private int _playbackId; // Incremented on each PlayInternal to ignore stale PlaybackStopped events


    public event Action<AudioFile>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public bool WasapiExclusive => _wasapiExclusive;
    public bool GaplessEnabled => _gaplessEnabled;
    public float Volume => _volume;
    public TimeSpan CurrentPosition => _audioFileReader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _audioFileReader?.TotalTime ?? TimeSpan.Zero;
    public int Bitrate => _playlistService.CurrentItem?.AudioFile.Bitrate ?? 0;

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (_audioFileReader != null)
            _audioFileReader.Volume = _volume * _volume * _volume; // Cubic curve for smoother control
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
            StopInternal();
            PlayInternal(position);
        }
    }

    public void SetGaplessMode(bool enabled)
    {
        _gaplessEnabled = enabled;
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

            // In WASAPI Exclusive mode, the system mixer is bypassed so we need
            // software volume control via AudioFileReader.Volume.
            // In Shared mode, the system volume slider works, so we set Volume=1.0.
            // Apply cubic curve for smooth perception (same as SetVolume).
            _audioFileReader.Volume = _wasapiExclusive ? _volume * _volume * _volume : 1.0f;

            ISampleProvider sourceProvider;

            if (_gaplessEnabled)
            {
                // Use GaplessSampleProvider which auto-advances to next track
                _gaplessProvider = new GaplessSampleProvider(
                    _audioFileReader,
                    _playlistService,
                    () => _gaplessEnabled,
                    () => _volume,
                    () => _wasapiExclusive);

                // When the gapless provider advances to the next track on the audio thread,
                // update _audioFileReader and marshal UI notifications to the main thread.
                _gaplessProvider.TrackAdvanced += track => {
                    // Update _audioFileReader to point to the new reader
                    _audioFileReader = _gaplessProvider.CurrentReader;
                    // Fire events on the main thread via System.Windows.Application.Current.Dispatcher
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TrackChanged?.Invoke(track);
                        DurationChanged?.Invoke(_audioFileReader?.TotalTime ?? TimeSpan.Zero);
                        BitrateChanged?.Invoke(track.Bitrate);
                    });
                };

                sourceProvider = _gaplessProvider;
            }
            else
            {
                _gaplessProvider = null;
                sourceProvider = _audioFileReader;
            }

            // Wrap with FFT capture provider
            _fftCaptureProvider = new FftCaptureProvider(sourceProvider, _fftService);

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
            System.Diagnostics.Debug.WriteLine($"Playback error: {ex.Message}");
        }
    }

    private IWavePlayer CreateWasapiOutput()
    {
        if (_wasapiExclusive)
        {
            return new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Exclusive, 100);
        }
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
        _gaplessCts?.Cancel();
        _isPlaying = false;
        _isPaused = false;

        if (_outputDevice != null)
        {
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
            _outputDevice.Stop();
            _outputDevice.Dispose();
            _outputDevice = null;
        }

        _gaplessProvider?.Dispose();
        _gaplessProvider = null;

        if (_audioFileReader != null)
        {
            _audioFileReader.Dispose();
            _audioFileReader = null;
        }
    }

    public void Next()
    {
        if (_gaplessEnabled && _gaplessProvider != null && _isPlaying)
        {
            // In gapless mode, force-switch the provider to the next track immediately
            var nextItem = _playlistService.GetNext();
            if (nextItem == null)
            {
                Stop();
                return;
            }

            try
            {
                var newReader = new AudioFileReader(nextItem.AudioFile.FilePath);
                newReader.Volume = _wasapiExclusive ? _volume * _volume * _volume : 1.0f;
                _gaplessProvider.ReplaceReader(newReader);
                _audioFileReader = newReader;

                TrackChanged?.Invoke(nextItem.AudioFile);
                DurationChanged?.Invoke(_audioFileReader.TotalTime);
                BitrateChanged?.Invoke(nextItem.AudioFile.Bitrate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Next error (gapless): {ex.Message}");
                Stop();
            }
        }
        else
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
        // When Pause() calls StopInternal() and then Resume() calls PlayInternal(),
        // a PlaybackStopped event from the old device may arrive after the new one
        // has already started playing. Without this check, it would incorrectly
        // trigger Next() or Stop() on the new playback.
        if (!_isPlaying || _isPaused)
            return;

        // If the sender is not the current output device, ignore this event.
        // This prevents stale PlaybackStopped events from a previous device
        // (e.g., after Pause/Resume recreates the device) from triggering
        // Next() or Stop() on the new playback.
        if (sender != _outputDevice)
            return;

        if (_gaplessEnabled && _gaplessProvider != null)
        {
            // In gapless mode, the GaplessSampleProvider handles auto-advance seamlessly.
            // PlaybackStopped fires when the output device runs out of data.
            // This can happen in two scenarios:
            // 1. The gapless provider has already switched to the next track (normal gapless transition)
            // 2. The playlist has ended (no more tracks)
            //
            // Check if the gapless provider still has a valid reader with data remaining.
            var currentReader = _gaplessProvider.CurrentReader;
            if (currentReader != null && currentReader.CurrentTime < currentReader.TotalTime)
            {
                // The gapless provider is still playing the next track — ignore this event.
                return;
            }

            // No more data — stop playback
            Stop();
        }
        else
        {
            // Auto-advance to next track (non-gapless: there will be a small gap)
            Next();
        }
    }

    private async void StartPositionTracking()
    {
        _gaplessCts?.Cancel();
        _gaplessCts = new CancellationTokenSource();
        var token = _gaplessCts.Token;

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
