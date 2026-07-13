using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Manages the playback state (IsPlaying, IsPaused, CurrentPosition, Duration, 
/// Bit Perfect status, etc.) and exposes events for the ViewModel layer.
/// 
/// This class subscribes to PlaybackEngine events and maintains its own state,
/// providing a clean separation between low-level audio engine and UI-facing state.
/// </summary>
internal class AudioStateManager : IDisposable
{
    private readonly PlaybackEngine _playbackEngine;
    private readonly DeviceCapabilities _deviceCaps;

    // ── State properties ──
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan CurrentPosition { get; private set; }
    public TimeSpan Duration { get; private set; }
    public int CurrentBitDepth { get; private set; }
    public int CurrentSampleRate { get; private set; }
    public int Bitrate { get; private set; }
    public bool BitPerfectMode { get; private set; }
    public BitPerfectStatus CurrentBitPerfectStatus { get; private set; } = BitPerfectStatus.Off;
    public double PausedProgress { get; private set; }

    // ── Events for ViewModel layer ──
    public event Action<AudioFile, CueTrack?>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public event Action<bool>? BitPerfectModeChanged;
    public event Action<BitPerfectStatus>? BitPerfectStatusChanged;

    public AudioStateManager(PlaybackEngine playbackEngine, DeviceCapabilities deviceCaps)
    {
        _playbackEngine = playbackEngine;
        _deviceCaps = deviceCaps;

        // Subscribe to PlaybackEngine events
        _playbackEngine.TrackChanged += OnTrackChanged;
        _playbackEngine.PlayStateChanged += OnPlayStateChanged;
        _playbackEngine.PositionChanged += OnPositionChanged;
        _playbackEngine.DurationChanged += OnDurationChanged;
        _playbackEngine.BitrateChanged += OnBitrateChanged;
        _playbackEngine.VolumeChanged += OnVolumeChanged;
        _playbackEngine.BitPerfectModeChanged += OnBitPerfectModeChanged;
        _playbackEngine.BitPerfectStatusChanged += OnBitPerfectStatusChanged;
    }

    // ── Event handlers that update state and re-raise events ──

    private void OnTrackChanged(AudioFile audioFile, CueTrack? cueTrack)
    {
        // Update format info from the engine
        CurrentSampleRate = _playbackEngine.CurrentSampleRate;
        CurrentBitDepth = _playbackEngine.CurrentBitDepth;
        Bitrate = _playbackEngine.Bitrate;

        TrackChanged?.Invoke(audioFile, cueTrack);
    }

    private void OnPlayStateChanged(bool isPlaying)
    {
        IsPlaying = isPlaying;
        IsPaused = _playbackEngine.IsPaused;

        // Update format info when playback state changes
        if (isPlaying)
        {
            CurrentSampleRate = _playbackEngine.CurrentSampleRate;
            CurrentBitDepth = _playbackEngine.CurrentBitDepth;
            Bitrate = _playbackEngine.Bitrate;
        }

        PlayStateChanged?.Invoke(isPlaying);
    }

    private void OnPositionChanged(TimeSpan position)
    {
        CurrentPosition = position;
        PositionChanged?.Invoke(position);
    }

    private void OnDurationChanged(TimeSpan duration)
    {
        Duration = duration;
        DurationChanged?.Invoke(duration);
    }

    private void OnBitrateChanged(int bitrate)
    {
        Bitrate = bitrate;
        BitrateChanged?.Invoke(bitrate);
    }

    private void OnVolumeChanged(float volume)
    {
        VolumeChanged?.Invoke(volume);
    }

    private void OnBitPerfectModeChanged(bool enabled)
    {
        BitPerfectMode = enabled;
        BitPerfectModeChanged?.Invoke(enabled);
    }

    private void OnBitPerfectStatusChanged(BitPerfectStatus status)
    {
        CurrentBitPerfectStatus = status;
        BitPerfectStatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Updates the Bit Perfect status by querying the engine's current format
    /// and comparing it against device capabilities.
    /// </summary>
    public void UpdateBitPerfectStatus()
    {
        if (!BitPerfectMode || !IsPlaying)
        {
            CurrentBitPerfectStatus = BitPerfectStatus.Off;
            BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
            return;
        }

        int sr = CurrentSampleRate;
        int bd = CurrentBitDepth;
        int ch = 2;

        var newStatus = _deviceCaps.GetBitPerfectStatus(sr, bd, ch);
        CurrentBitPerfectStatus = newStatus;
        BitPerfectStatusChanged?.Invoke(newStatus);
        Logger.Log($"AudioStateManager: BitPerfectStatus={newStatus} (SR={sr}, BD={bd}, CH={ch})");
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
            Logger.Log($"AudioStateManager.DelayedBitPerfectStatusUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronizes state from the engine after a mode switch or fallback.
    /// </summary>
    public void SyncState()
    {
        IsPlaying = _playbackEngine.IsPlaying;
        IsPaused = _playbackEngine.IsPaused;
        BitPerfectMode = _playbackEngine.BitPerfectMode;
        PausedProgress = _playbackEngine.PausedProgress;
        CurrentSampleRate = _playbackEngine.CurrentSampleRate;
        CurrentBitDepth = _playbackEngine.CurrentBitDepth;
        Bitrate = _playbackEngine.Bitrate;
    }

    public void Dispose()
    {
        _playbackEngine.TrackChanged -= OnTrackChanged;
        _playbackEngine.PlayStateChanged -= OnPlayStateChanged;
        _playbackEngine.PositionChanged -= OnPositionChanged;
        _playbackEngine.DurationChanged -= OnDurationChanged;
        _playbackEngine.BitrateChanged -= OnBitrateChanged;
        _playbackEngine.VolumeChanged -= OnVolumeChanged;
        _playbackEngine.BitPerfectModeChanged -= OnBitPerfectModeChanged;
        _playbackEngine.BitPerfectStatusChanged -= OnBitPerfectStatusChanged;
    }
}
