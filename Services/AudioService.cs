using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Facade for audio playback. Delegates low-level playback to PlaybackEngine
/// and state management to AudioStateManager.
/// 
/// External consumers (MainViewModel, ExpandedPanelViewModel) interact ONLY with
/// AudioService — the internal architecture is transparent to them.
/// </summary>
public class AudioService : IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly AudioStateManager _state;
    private readonly FftQueue _fftQueue;

    // ── Events (re-broadcast from AudioStateManager) ──
    public event Action<AudioFile, CueTrack?>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public event Action<bool>? BitPerfectModeChanged;
    public event Action<BitPerfectStatus>? BitPerfectStatusChanged;

    /// <summary>
    /// Provides access to device capabilities for UI display.
    /// </summary>
    public DeviceCapabilities DeviceCapabilities => _engine.DeviceCapabilities;

    // ── Cached DAC capabilities strings (updated on device change) ──
    private string _cachedDacCapabilitiesText = "";
    private string _cachedDeviceMaxSampleRateText = "";
    private string _cachedDeviceMaxBitDepthText = "";
    private string _cachedDeviceNameText = "";

    /// <summary>
    /// Cached DAC capabilities text, e.g. "16-24 bit / 44.1-48 kHz".
    /// Updated when device capabilities are invalidated.
    /// </summary>
    public string CachedDacCapabilitiesText => _cachedDacCapabilitiesText;

    /// <summary>
    /// Cached device max sample rate text, e.g. "192 kHz".
    /// </summary>
    public string CachedDeviceMaxSampleRateText => _cachedDeviceMaxSampleRateText;

    /// <summary>
    /// Cached device max bit depth text, e.g. "32 bit".
    /// </summary>
    public string CachedDeviceMaxBitDepthText => _cachedDeviceMaxBitDepthText;

    /// <summary>
    /// Cached device name text.
    /// </summary>
    public string CachedDeviceNameText => _cachedDeviceNameText;

    /// <summary>
    /// Updates all cached DAC capability strings from the current device.
    /// Call this after device capabilities are invalidated/re-probed.
    /// </summary>
    public void RefreshCachedDacInfo()
    {
        var caps = DeviceCapabilities;

        // DacCapabilitiesText
        _cachedDacCapabilitiesText = caps.DacCapabilitiesText;

        // DeviceMaxSampleRateText
        int maxSr = caps.MaxSampleRate;
        if (maxSr > 0)
        {
            double khz = maxSr / 1000.0;
            _cachedDeviceMaxSampleRateText = khz == (int)khz ? $"{(int)khz} kHz" : $"{khz:F1} kHz";
        }
        else
        {
            _cachedDeviceMaxSampleRateText = "";
        }

        // DeviceMaxBitDepthText
        int maxBd = caps.MaxBitDepth;
        _cachedDeviceMaxBitDepthText = maxBd > 0 ? $"{maxBd} bit" : "";

        // DeviceNameText
        string name = caps.DeviceName;
        _cachedDeviceNameText = string.IsNullOrEmpty(name) ? "" : name;
    }

    public bool IsPlaying => _state.IsPlaying;
    public bool IsPaused => _state.IsPaused;
    public bool BitPerfectMode => _state.BitPerfectMode;
    public float Volume => _engine.Volume;

    /// <summary>
    /// Progress value (0.0 to 1.0) saved at the moment of pause.
    /// Used by UI to keep the progress bar stable during pause in Exclusive mode,
    /// where audio objects are destroyed and position resets to 0.
    /// </summary>
    public double PausedProgress => _engine.PausedProgress;

    /// <summary>
    /// Current Bit Perfect status — indicates whether the track format
    /// matches the device capabilities exactly (Perfect), is limited (Limited),
    /// or Bit Perfect mode is off (Off).
    /// </summary>
    public BitPerfectStatus CurrentBitPerfectStatus => _state.CurrentBitPerfectStatus;

    public TimeSpan CurrentPosition => _state.CurrentPosition;
    public TimeSpan Duration => _state.Duration;
    public int Bitrate => _state.Bitrate;

    // ── CUE track properties (forwarded from AudioStateManager) ──
    public bool IsCueTrack => _state.IsCueTrack;
    public CueTrack? CurrentCueTrack => _state.CurrentCueTrack;
    public TimeSpan CurrentTrackPosition => _state.CurrentTrackPosition;
    public TimeSpan CurrentTrackDuration => _state.CurrentTrackDuration;

    /// <summary>
    /// Current sample rate of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// </summary>
    public int CurrentSampleRate => _state.CurrentSampleRate;

    /// <summary>
    /// Current bit depth of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// </summary>
    public int CurrentBitDepth => _state.CurrentBitDepth;

    public AudioService(PlaylistService playlistService, FftService fftService)
    {
        _fftQueue = new FftQueue(fftService);
        var deviceCaps = new DeviceCapabilities();

        _engine = new PlaybackEngine(playlistService, _fftQueue, deviceCaps);
        _state = new AudioStateManager(_engine);

        // Wire up events from AudioStateManager to AudioService
        _state.TrackChanged += (track, cue) => TrackChanged?.Invoke(track, cue);
        _state.PlayStateChanged += (playing) => PlayStateChanged?.Invoke(playing);
        _state.PositionChanged += (pos) => PositionChanged?.Invoke(pos);
        _state.DurationChanged += (dur) => DurationChanged?.Invoke(dur);
        _state.BitrateChanged += (br) => BitrateChanged?.Invoke(br);
        _state.VolumeChanged += (vol) => VolumeChanged?.Invoke(vol);
        _state.BitPerfectModeChanged += (enabled) => BitPerfectModeChanged?.Invoke(enabled);
        _state.BitPerfectStatusChanged += (status) => BitPerfectStatusChanged?.Invoke(status);
    }

    public void SetVolume(float volume)
    {
        _engine.SetVolume(volume);
    }

    public float GetSavedVolume() => _engine.SavedVolume;

    /// <summary>
    /// Enable or disable Bit Perfect mode.
    /// Bit Perfect mode uses WASAPI Exclusive with raw PCM output.
    /// Normal mode uses WASAPI Shared (system mixer handles volume).
    /// </summary>
    public void SetBitPerfectMode(bool enable)
    {
        if (_state.BitPerfectMode == enable)
            return;

        _engine.SetBitPerfectMode(enable);

        // BitPerfectModeChanged is fired by PlaybackEngine -> AudioStateManager -> AudioService
    }

    public void Play()
    {
        if (_state.IsPaused)
        {
            Resume();
            return;
        }

        _engine.Play();
        // PlayInternal in PlaybackEngine fires PlayStateChanged(true) via events
    }

    public void Pause()
    {
        if (_engine.IsPlaying)
        {
            _engine.Pause();
        }
    }

    public void Resume()
    {
        if (_state.IsPaused)
        {
            _engine.Resume();
        }
    }

    public void Stop()
    {
        _engine.Stop();
    }

    public void Next()
    {
        _engine.Next();
    }

    public void Previous()
    {
        _engine.Previous();
    }

    public void Seek(double fraction)
    {
        _engine.Seek(fraction);
    }

    public void Dispose()
    {
        _state.Dispose();
        _engine.Dispose();
        _fftQueue.Dispose();
    }
}
