using System.IO;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Low-level playback engine responsible for creating IWavePlayer instances,
/// managing buffers, reading audio data, and resampling.
/// 
/// This is the "engine room" of PureAudio — it handles all audio device interaction
/// but has no knowledge of the UI or ViewModel state.
/// State management is delegated to AudioStateManager.
/// </summary>
internal class PlaybackEngine : IDisposable
{
    private IWavePlayer? _outputDevice;
    private AudioFileReader? _audioFileReader;
    private BitPerfectWaveProvider? _bitPerfectProvider;
    private SoxResampler? _resampler;
    private readonly FftService _fftService;
    private readonly FftQueue _fftQueue;
    private readonly DeviceCapabilities _deviceCaps;
    private readonly PlaylistService _playlistService;
    private bool _bitPerfectMode;
    private bool _isPlaying;
    private bool _isPaused;
    private float _volume = 0.5f;
    private float _savedVolume = 0.5f;
    private CancellationTokenSource? _positionCts;
    private TimeSpan _pausePosition;
    private double _pausedProgress;
    private int _playbackId;
    private bool _userStopRequested;

    // CUE track support
    private CueTrack? _currentCueTrack;
    private bool _isCueTrack;

    // ── Events ──
    public event Action<AudioFile, CueTrack?>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public event Action<bool>? BitPerfectModeChanged;
    public event Action<BitPerfectStatus>? BitPerfectStatusChanged;

    // ── Properties exposed to AudioStateManager ──
    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public bool BitPerfectMode => _bitPerfectMode;
    public float Volume => _volume;
    public float SavedVolume => _savedVolume;
    public double PausedProgress => _pausedProgress;
    public DeviceCapabilities DeviceCapabilities => _deviceCaps;

    public TimeSpan CurrentPosition
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.CurrentTime;
            return _audioFileReader?.CurrentTime ?? TimeSpan.Zero;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.TotalTime;
            return _audioFileReader?.TotalTime ?? TimeSpan.Zero;
        }
    }

    public int Bitrate => _playlistService.CurrentItem?.AudioFile.Bitrate ?? 0;

    /// <summary>
    /// Current sample rate of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// </summary>
    public int CurrentSampleRate
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.WaveFormat.SampleRate;
            return _audioFileReader?.WaveFormat.SampleRate ?? 0;
        }
    }

    /// <summary>
    /// Current bit depth of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// </summary>
    public int CurrentBitDepth
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.WaveFormat.BitsPerSample;
            return _audioFileReader?.WaveFormat.BitsPerSample ?? 0;
        }
    }

    public PlaybackEngine(PlaylistService playlistService, FftService fftService, FftQueue fftQueue, DeviceCapabilities deviceCaps)
    {
        _playlistService = playlistService;
        _fftService = fftService;
        _fftQueue = fftQueue;
        _deviceCaps = deviceCaps;
    }

    /// <summary>
    /// Enable or disable Bit Perfect mode.
    /// Bit Perfect mode uses WASAPI Exclusive with raw PCM output.
    /// Normal mode uses WASAPI Shared (system mixer handles volume).
    /// </summary>
    public async void SetBitPerfectMode(bool enable)
    {
        if (_bitPerfectMode == enable)
            return;

        _bitPerfectMode = enable;

        if (_isPlaying || _isPaused)
        {
            var position = CurrentPosition;
            Logger.Log($"SetBitPerfectMode: switching to {(enable ? "Bit Perfect (Exclusive)" : "Normal (Shared)")}, position={position.TotalSeconds:F3}s");

            if (enable)
                _savedVolume = _volume;

            StopInternal();

            if (enable)
                await Task.Delay(150);

            PlayInternal(position);
        }

        BitPerfectModeChanged?.Invoke(_bitPerfectMode);

        // Update Bit Perfect status
        UpdateBitPerfectStatus();
    }

    /// <summary>
    /// Updates the Bit Perfect status based on current mode and track format.
    /// Uses the SOURCE format (before resampling) to determine the true Bit Perfect status.
    /// </summary>
    private void UpdateBitPerfectStatus()
    {
        // This method is kept in PlaybackEngine because it needs access to
        // _outputDevice, _bitPerfectProvider, _deviceCaps, etc.
        // AudioStateManager will subscribe to BitPerfectStatusChanged event.

        // Защита от гонок: проверяем согласованность флагов
        bool consistent = _bitPerfectMode == (_outputDevice is WasapiExclusivePlayer);
        if (!consistent)
        {
            Logger.Log($"UpdateBitPerfectStatus: race condition detected — bitPerfectMode={_bitPerfectMode}, outputDevice is WasapiExclusivePlayer={_outputDevice is WasapiExclusivePlayer}. Forcing Off.");
            BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
            return;
        }

        if (!_bitPerfectMode || !_isPlaying)
        {
            BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
            return;
        }

        int sr;
        int bd;
        int ch;

        if (_bitPerfectProvider != null)
        {
            sr = _bitPerfectProvider.WaveFormat.SampleRate;
            bd = _bitPerfectProvider.WaveFormat.BitsPerSample;
            ch = _bitPerfectProvider.WaveFormat.Channels;
        }
        else
        {
            sr = CurrentSampleRate;
            bd = CurrentBitDepth;
            ch = 2;
        }

        var newStatus = _deviceCaps.GetBitPerfectStatus(sr, bd, ch);
        BitPerfectStatusChanged?.Invoke(newStatus);
        Logger.Log($"BitPerfectStatus: {newStatus} (source SR={sr}, BD={bd}, CH={ch})");
    }

    /// <summary>
    /// Обновляет статус Bit Perfect с небольшой задержкой после старта трека,
    /// чтобы дать время на полную инициализацию аудио-устройства.
    /// </summary>
    public async Task DelayedBitPerfectStatusUpdate()
    {
        try
        {
            await Task.Delay(80);
            UpdateBitPerfectStatus();
        }
        catch (Exception ex)
        {
            Logger.Log($"DelayedBitPerfectStatusUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (!_bitPerfectMode && _audioFileReader != null)
            _audioFileReader.Volume = _volume;
        VolumeChanged?.Invoke(_volume);
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
            var firstItem = _playlistService.Items.FirstOrDefault();
            if (firstItem == null) return;
            _playlistService.CurrentIndex = 0;
            currentItem = firstItem;
        }

        PlayInternal(TimeSpan.Zero);
    }

    internal void PlayInternal(TimeSpan position)
    {
        Logger.Log($"PlayInternal: requested position = {position.TotalSeconds:F3}s");
        _userStopRequested = false;
        int currentPlaybackId = ++_playbackId;
        StopInternal();

        var currentItem = _playlistService.CurrentItem;
        if (currentItem == null) return;

        // Check if this is a CUE track
        _currentCueTrack = currentItem.CueTrack;
        _isCueTrack = _currentCueTrack != null;
        if (_isCueTrack)
        {
            Logger.Log($"PlayInternal: CUE track detected — file={_currentCueTrack!.FilePath}, start={_currentCueTrack.StartPosition}, end={_currentCueTrack.EndPosition}");
        }

        try
        {
            _fftService.Reset();
            _fftQueue.Clear();

            Logger.Log($"PlayInternal: file {currentItem.AudioFile.FilePath}, Metadata says {currentItem.AudioFile.BitsPerSample} bit / {currentItem.AudioFile.SampleRate} Hz");

            if (_bitPerfectMode)
            {
                // === BIT PERFECT PATH ===
                _bitPerfectProvider = new BitPerfectWaveProvider(
                    currentItem.AudioFile.FilePath, _fftService, _fftQueue);

                // If this is a CUE track, seek to the start position
                if (_isCueTrack && _currentCueTrack != null)
                {
                    Logger.Log($"PlayInternal (Bit Perfect): seeking to CUE start position {_currentCueTrack.StartPosition}");
                    _bitPerfectProvider.Seek(_currentCueTrack.StartPosition);
                }

                int sourceSr = _bitPerfectProvider.WaveFormat.SampleRate;
                int sourceBd = _bitPerfectProvider.WaveFormat.BitsPerSample;
                int sourceCh = _bitPerfectProvider.WaveFormat.Channels;

                Logger.Log($"PlayInternal (Bit Perfect): source format={sourceSr}Hz/{sourceBd}bit/{sourceCh}ch");

                // Проверяем статус Bit Perfect
                var bpStatus = _deviceCaps.GetBitPerfectStatus(sourceSr, sourceBd, sourceCh);
                Logger.Log($"PlayInternal (Bit Perfect): status={bpStatus}");

                IWaveProvider outputProvider = _bitPerfectProvider;

                if (bpStatus == BitPerfectStatus.Limited)
                {
                    // Формат не поддерживается напрямую, ищем ближайший поддерживаемый
                    var bestFormat = _deviceCaps.GetBestSupportedFormat(sourceSr, sourceBd, sourceCh);
                    if (bestFormat != null)
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): resampling from {sourceSr}/{sourceBd} to {bestFormat.SampleRate}/{bestFormat.BitsPerSample}");

                        try
                        {
                            _resampler = new SoxResampler(_bitPerfectProvider, bestFormat!);
                            outputProvider = _resampler;
                            Logger.Log($"PlayInternal (Bit Perfect): SoxResampler created successfully");
                        }
                        catch (Exception resampleEx)
                        {
                            Logger.Log($"PlayInternal (Bit Perfect): SoxResampler failed: {resampleEx.Message}, falling back to Shared");
                            _resampler = null;
                            bpStatus = BitPerfectStatus.Off;
                        }
                    }
                    else
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): no supported format found, falling back to Shared");
                        bpStatus = BitPerfectStatus.Off;
                    }
                }

                if (bpStatus == BitPerfectStatus.Off)
                {
                    // Fallback на Shared режим
                    _bitPerfectProvider.Dispose();
                    _bitPerfectProvider = null;

                    _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
                    _audioFileReader.Volume = _volume;
                    if (position < _audioFileReader.TotalTime)
                    {
                        _audioFileReader.CurrentTime = position;
                    }

                    var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);
                    _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(fftProvider);

                    // Обновляем статус на Off
                    BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
                }
                else
                {
                    // Exclusive режим с конвертацией или без
                    Logger.Log($"PlayInternal (Bit Perfect): starting Exclusive mode, bpStatus={bpStatus}, outputProvider type={outputProvider.GetType().Name}, format={outputProvider.WaveFormat.SampleRate}Hz/{outputProvider.WaveFormat.BitsPerSample}bit/{outputProvider.WaveFormat.Channels}ch");

                    if (position > TimeSpan.Zero && position < _bitPerfectProvider.TotalTime)
                    {
                        _bitPerfectProvider.Seek(position);

                        // Clear resampler internal buffers after seek to prevent stale data
                        if (_resampler != null)
                        {
                            _resampler.Clear();
                            Logger.Log("PlayInternal (Bit Perfect): cleared resampler after seek");
                        }
                    }

                    _outputDevice = CreateWasapiOutput();
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;

                    try
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): calling _outputDevice.Init()...");
                        _outputDevice.Init(outputProvider);
                        BitPerfectStatusChanged?.Invoke(bpStatus);
                        Logger.Log($"PlayInternal (Bit Perfect): Init() succeeded, device is in Exclusive mode");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): Init failed: {ex.GetType().Name}: {ex.Message}. Falling back to Shared.");
                        _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                        _outputDevice.Dispose();

                        _bitPerfectProvider?.Dispose();
                        _bitPerfectProvider = null;

                        _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
                        _audioFileReader.Volume = _volume;
                        if (position < _audioFileReader.TotalTime)
                        {
                            _audioFileReader.CurrentTime = position;
                        }

                        var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);
                        _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                        _outputDevice.PlaybackStopped += OnPlaybackStopped;
                        _outputDevice.Init(fftProvider);

                        BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
                    }
                }

                _outputDevice.Play();
            }
            else
            {
                // === NORMAL (SHARED) PATH ===
                _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
                _audioFileReader.Volume = _volume;

                Logger.Log($"PlayInternal (Shared): opened file, total={_audioFileReader.TotalTime.TotalSeconds:F3}s, format={_audioFileReader.WaveFormat.SampleRate}Hz/{_audioFileReader.WaveFormat.BitsPerSample}bit/{_audioFileReader.WaveFormat.Channels}ch");

                // If this is a CUE track, seek to the start position
                if (_isCueTrack && _currentCueTrack != null)
                {
                    Logger.Log($"PlayInternal (Shared): seeking to CUE start position {_currentCueTrack.StartPosition}");
                    _audioFileReader.CurrentTime = _currentCueTrack.StartPosition;
                }
                else if (position < _audioFileReader.TotalTime)
                {
                    _audioFileReader.CurrentTime = position;
                }

                var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);

                _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                _outputDevice.PlaybackStopped += OnPlaybackStopped;

                _outputDevice.Init(fftProvider);
                Logger.Log($"PlayInternal (Shared): WasapiOut initialized, calling Play()");
                _outputDevice.Play();
                Logger.Log($"PlayInternal (Shared): Play() called successfully");
            }

            _isPlaying = true;
            _isPaused = false;
            TrackChanged?.Invoke(currentItem.AudioFile, _currentCueTrack);
            DurationChanged?.Invoke(Duration);
            BitrateChanged?.Invoke(Bitrate);
            PlayStateChanged?.Invoke(true);

            // Update Bit Perfect status after track starts
            _ = DelayedBitPerfectStatusUpdate();

            StartPositionTracking();
        }
        catch (Exception ex)
        {
            Logger.Log($"PlayInternal: ERROR: {ex.GetType().Name}: {ex.Message}");
            Logger.Log($"PlayInternal: stack trace: {ex.StackTrace}");

            // Fallback to Shared mode if anything fails
            try
            {
                StopInternal();

                _bitPerfectMode = false;
                BitPerfectModeChanged?.Invoke(false);

                var fallbackItem = _playlistService.CurrentItem;
                if (fallbackItem != null)
                {
                    _audioFileReader = new AudioFileReader(fallbackItem.AudioFile.FilePath);
                    _audioFileReader.Volume = _volume;

                    var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);
                    _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(fftProvider);
                    _outputDevice.Play();

                    _isPlaying = true;
                    _isPaused = false;
                    TrackChanged?.Invoke(fallbackItem.AudioFile, _currentCueTrack);
                    DurationChanged?.Invoke(Duration);
                    BitrateChanged?.Invoke(Bitrate);
                    PlayStateChanged?.Invoke(true);
                    StartPositionTracking();
                }
            }
            catch (Exception fallbackEx)
            {
                Logger.Log($"PlayInternal: Fallback also failed: {fallbackEx.Message}");
            }
        }
    }

    private IWavePlayer CreateWasapiOutput()
    {
        if (_bitPerfectMode)
        {
            Logger.Log("CreateWasapiOutput: creating WasapiExclusivePlayer (Bit Perfect mode)");
            try
            {
                var exclusivePlayer = new WasapiExclusivePlayer(100);
                Logger.Log("CreateWasapiOutput: WasapiExclusivePlayer created successfully");
                return exclusivePlayer;
            }
            catch (Exception ex)
            {
                Logger.Log($"CreateWasapiOutput: FAILED to create WasapiExclusivePlayer: {ex.GetType().Name}: {ex.Message}");
                Logger.Log("CreateWasapiOutput: falling back to Shared WasapiOut");
                _bitPerfectMode = false;
                BitPerfectModeChanged?.Invoke(false);
                var fallbackWasapi = new WasapiOut(AudioClientShareMode.Shared, 100);
                Logger.Log("CreateWasapiOutput: Shared WasapiOut created as fallback");
                return fallbackWasapi;
            }
        }

        Logger.Log("CreateWasapiOutput: creating Shared WasapiOut");
        return new WasapiOut(AudioClientShareMode.Shared, 100);
    }

    public void Pause()
    {
        if (_outputDevice != null && _isPlaying)
        {
            _pausePosition = CurrentPosition;
            double duration = Duration.TotalSeconds;
            _pausedProgress = duration > 0 ? _pausePosition.TotalSeconds / duration : 0;
            Logger.Log($"PAUSE: saved position = {_pausePosition.TotalSeconds:F3}s, progress = {_pausedProgress:F4}, bitPerfectMode={_bitPerfectMode}");

            _isPlaying = false;
            _isPaused = true;
            PlayStateChanged?.Invoke(false);

            if (_bitPerfectMode)
            {
                Logger.Log($"PAUSE (Exclusive): using WasapiExclusivePlayer.Pause(), position preserved at {_pausePosition.TotalSeconds:F3}s");

                _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                _outputDevice.Pause();
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
            }
            else
            {
                _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                _outputDevice.Stop();
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
            }
        }
    }

    public void Resume()
    {
        if (_isPaused)
        {
            Logger.Log($"RESUME: restoring position = {_pausePosition.TotalSeconds:F3}s, bitPerfectMode={_bitPerfectMode}");

            if (_bitPerfectMode && _outputDevice != null)
            {
                Logger.Log($"RESUME (Exclusive): calling WasapiExclusivePlayer.Play()");
                _outputDevice.Play();
                _isPlaying = true;
                _isPaused = false;
                PlayStateChanged?.Invoke(true);
            }
            else if (_outputDevice != null && _audioFileReader != null)
            {
                if (_pausePosition < _audioFileReader.TotalTime)
                    _audioFileReader.CurrentTime = _pausePosition;

                _outputDevice.Play();
                _isPlaying = true;
                _isPaused = false;
                PlayStateChanged?.Invoke(true);
            }
            else
            {
                PlayInternal(_pausePosition);
            }
        }
    }

    public void Stop()
    {
        _userStopRequested = true;
        StopInternal();
        _fftService.Reset();
        PlayStateChanged?.Invoke(false);
        UpdateBitPerfectStatus();
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

        if (_resampler != null)
        {
            _resampler.Dispose();
            _resampler = null;
        }

        if (_bitPerfectProvider != null)
        {
            _bitPerfectProvider.Dispose();
            _bitPerfectProvider = null;
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
        if (_bitPerfectMode && _bitPerfectProvider != null)
        {
            TimeSpan totalTime = _bitPerfectProvider.TotalTime;
            TimeSpan newPosition = TimeSpan.FromTicks((long)(totalTime.Ticks * fraction));

            if (_isCueTrack && _currentCueTrack != null)
            {
                TimeSpan cueStart = _currentCueTrack.StartPosition;
                TimeSpan cueEnd = _currentCueTrack.EndPosition;
                TimeSpan cueDuration = cueEnd - cueStart;
                newPosition = cueStart + TimeSpan.FromTicks((long)(cueDuration.Ticks * fraction));
                Logger.Log($"Seek (Bit Perfect, CUE): fraction={fraction:F4}, cueStart={cueStart}, cueEnd={cueEnd}, newPosition={newPosition}");
            }

            _bitPerfectProvider.Seek(newPosition);

            if (_resampler != null)
            {
                var bestFormat = _deviceCaps.GetBestSupportedFormat(
                    _bitPerfectProvider.WaveFormat.SampleRate,
                    _bitPerfectProvider.WaveFormat.BitsPerSample,
                    _bitPerfectProvider.WaveFormat.Channels);

                if (bestFormat != null)
                {
                    _resampler.Dispose();
                    _resampler = new SoxResampler(_bitPerfectProvider, bestFormat);

                    if (_outputDevice != null)
                    {
                        _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                        _outputDevice.Stop();
                        _outputDevice.Dispose();
                    }

                    _outputDevice = CreateWasapiOutput();
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(_resampler);
                    _outputDevice.Play();
                }
            }

            PositionChanged?.Invoke(newPosition);
        }
        else if (_audioFileReader != null)
        {
            TimeSpan totalTime = _audioFileReader.TotalTime;
            TimeSpan newPosition = TimeSpan.FromTicks((long)(totalTime.Ticks * fraction));

            if (_isCueTrack && _currentCueTrack != null)
            {
                TimeSpan cueStart = _currentCueTrack.StartPosition;
                TimeSpan cueEnd = _currentCueTrack.EndPosition;
                TimeSpan cueDuration = cueEnd - cueStart;
                newPosition = cueStart + TimeSpan.FromTicks((long)(cueDuration.Ticks * fraction));
                Logger.Log($"Seek (Shared, CUE): fraction={fraction:F4}, cueStart={cueStart}, cueEnd={cueEnd}, newPosition={newPosition}");
            }

            _audioFileReader.CurrentTime = newPosition;
            PositionChanged?.Invoke(newPosition);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Logger.Log($"OnPlaybackStopped ENTER: isPlaying={_isPlaying}, isPaused={_isPaused}, sender={sender?.GetType().Name}, outputDevice={_outputDevice?.GetType().Name}, sender==outputDevice={sender == _outputDevice}");

        if (_isPaused)
        {
            Logger.Log("OnPlaybackStopped: ignored (paused)");
            return;
        }

        if (sender != _outputDevice)
        {
            Logger.Log("OnPlaybackStopped: ignored (sender != _outputDevice)");
            return;
        }

        if (_userStopRequested)
        {
            Logger.Log("OnPlaybackStopped: ignored (user stop requested)");
            return;
        }

        Logger.Log($"OnPlaybackStopped: proceeding to Next() synchronously");

        try
        {
            Next();
        }
        catch (Exception ex)
        {
            Logger.Log($"OnPlaybackStopped: Next() error: {ex.Message}");
        }
    }

    private async void StartPositionTracking()
    {
        _positionCts?.Cancel();
        _positionCts = new CancellationTokenSource();
        var token = _positionCts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token);

                var currentPos = CurrentPosition;
                PositionChanged?.Invoke(currentPos);

                // Check if CUE track has reached its end position
                if (_isCueTrack && _currentCueTrack != null && currentPos >= _currentCueTrack.EndPosition)
                {
                    Logger.Log($"StartPositionTracking: CUE track reached end position {_currentCueTrack.EndPosition}, advancing to next track");
                    _userStopRequested = false;
                    Next();
                    return;
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
