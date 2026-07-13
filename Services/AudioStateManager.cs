using NAudio.Wave;
using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Manages playback state and exposes properties for the UI layer.
/// Acts as a bridge between PlaybackEngine and AudioService,
/// providing a clean separation of state management from low-level audio logic.
/// 
/// This class has NO knowledge of audio devices, WASAPI, or file reading.
/// It only manages state values and fires events.
/// </summary>
internal class AudioStateManager
{
    private readonly PlaybackEngine _engine;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _bitPerfectMode;
    private float _volume = 0.5f;
    private float _savedVolume = 0.5f;
    private double _pausedProgress;
    private BitPerfectStatus _currentBitPerfectStatus = BitPerfectStatus.Off;

    // ── Events (re-broadcast from PlaybackEngine + own) ──
    public event Action<AudioFile, CueTrack?>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public event Action<bool>? BitPerfectModeChanged;
    public event Action<BitPerfectStatus>? BitPerfectStatusChanged;

    public AudioStateManager(PlaybackEngine engine)
    {
        _engine = engine;

        // Subscribe to engine events and re-broadcast
        _engine.TrackChanged += (track, cue) => TrackChanged?.Invoke(track, cue);
        _engine.PlayStateChanged += (playing) =>
        {
            _isPlaying = playing;
            if (playing)
                _isPaused = false;
            else if (_isPaused)
            {
                // Pause case: _isPaused is already set by OnPause
            }
            else
            {
                // Stop case
                _pausedProgress = 0;
            }
            PlayStateChanged?.Invoke(playing);
        };
        _engine.PositionChanged += (pos) => PositionChanged?.Invoke(pos);
        _engine.DurationChanged += (dur) => DurationChanged?.Invoke(dur);
        _engine.BitrateChanged += (br) => BitrateChanged?.Invoke(br);
        _engine.VolumeChanged += (vol) =>
        {
            _volume = vol;
            VolumeChanged?.Invoke(vol);
        };
        _engine.BitPerfectModeChanged += (enabled) =>
        {
            _bitPerfectMode = enabled;
            BitPerfectModeChanged?.Invoke(enabled);
        };
        _engine.BitPerfectStatusChanged += (status) =>
        {
            _currentBitPerfectStatus = status;
            BitPerfectStatusChanged?.Invoke(status);
        };
    }

    // ── Properties ──

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Whether audio is currently paused.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            if (value) _isPlaying = false;
        }
    }

    /// <summary>
    /// Whether Bit Perfect (Exclusive) mode is active.
    /// </summary>
    public bool BitPerfectMode => _bitPerfectMode;

    /// <summary>
    /// Current playback volume (0.0 to 1.0).
    /// </summary>
    public float Volume => _volume;

    /// <summary>
    /// Volume saved before entering Bit Perfect mode.
    /// </summary>
    public float SavedVolume => _savedVolume;

    /// <summary>
    /// Progress value (0.0 to 1.0) saved at the moment of pause.
    /// Used by UI to keep the progress bar stable during pause in Exclusive mode,
    /// where audio objects are destroyed and position resets to 0.
    /// </summary>
    public double PausedProgress => _pausedProgress;

    /// <summary>
    /// Current Bit Perfect status — indicates whether the track format
    /// matches the device capabilities exactly (Perfect), is limited (Limited),
    /// or Bit Perfect mode is off (Off).
    /// </summary>
    public BitPerfectStatus CurrentBitPerfectStatus => _currentBitPerfectStatus;

    /// <summary>
    /// Current playback position. Delegates to PlaybackEngine.
    /// </summary>
    public TimeSpan CurrentPosition => _engine.CurrentPosition;

    /// <summary>
    /// Total duration of the current track. Delegates to PlaybackEngine.
    /// </summary>
    public TimeSpan Duration => _engine.Duration;

    /// <summary>
    /// Current track bitrate. Delegates to PlaybackEngine.
    /// </summary>
    public int Bitrate => _engine.Bitrate;

    /// <summary>
    /// Current sample rate of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// </summary>
    public int CurrentSampleRate => _engine.CurrentSampleRate;

    /// <summary>
    /// Current bit depth of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// </summary>
    public int CurrentBitDepth => _engine.CurrentBitDepth;

    /// <summary>
    /// Provides access to device capabilities for UI display.
    /// </summary>
    public DeviceCapabilities DeviceCapabilities => _engine.DeviceCapabilities;

    // ── State management methods ──

    /// <summary>
    /// Called by AudioService when pausing — saves the current position and progress.
    /// </summary>
    public void OnPause(TimeSpan position)
    {
        _isPlaying = false;
        _isPaused = true;

        double duration = _engine.Duration.TotalSeconds;
        _pausedProgress = duration > 0 ? position.TotalSeconds / duration : 0;
        Logger.Log($"AudioStateManager.OnPause: saved position = {position.TotalSeconds:F3}s, progress = {_pausedProgress:F4}");
    }

    /// <summary>
    /// Called by AudioService when resuming.
    /// </summary>
    public void OnResume()
    {
        _isPlaying = true;
        _isPaused = false;
    }

    /// <summary>
    /// Called by AudioService when stopping.
    /// </summary>
    public void OnStop()
    {
        _isPlaying = false;
        _isPaused = false;
        _pausedProgress = 0;
    }

    /// <summary>
    /// Called by AudioService when starting playback.
    /// </summary>
    public void OnPlay()
    {
        _isPlaying = true;
        _isPaused = false;
    }

    /// <summary>
    /// Saves the current volume before switching to Bit Perfect mode.
    /// </summary>
    public void SaveVolume(float volume)
    {
        _savedVolume = volume;
    }

    /// <summary>
    /// Updates the Bit Perfect mode flag.
    /// </summary>
    public void SetBitPerfectMode(bool enabled)
    {
        _bitPerfectMode = enabled;
    }
}
