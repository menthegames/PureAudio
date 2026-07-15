using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PureAudio.Helpers;
using PureAudio.Models;
using PureAudio.Services;

namespace PureAudio.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly AudioService _audioService;
    private readonly PlaylistService _playlistService;
    private readonly LibraryService _libraryService;
    private readonly FftService _fftService;
    private readonly SettingsService _settingsService;
    private readonly StatusDisplayController _statusController;

    private AudioFile? _currentTrack;
    private TimeSpan _elapsed;
    private TimeSpan _totalDuration;
    private int _bitrate;
    private bool _isPlaying;
    private bool _bitPerfectMode;
    private bool _isExpanded;
    private double _windowHeight = 220;
    private bool _isHiresMode;
    private float[] _fftData = new float[48];
    private string _marqueeText = "";
    private string _marqueeDisplayText = "";
    private int _marqueeOffset;
    private System.Windows.Threading.DispatcherTimer? _marqueeTimer;
    private System.Windows.Threading.DispatcherTimer? _fftTimer;
    private string _statusText = "Stopped";
    private double _progress;
    private double _volumeValue;
    private bool _isSeeking;
    private ProgressMode _currentProgressMode;
    // Background loading state
    private bool _isLoadingPlaylist;
    private bool _isLoadingLibrary;
    private string _loadingStatusText = "";
    // Background activity indicator (pulsing dot + toast)
    private bool _isBusy;
    private string _busyStatusText = "";
    private bool _isToastVisible;
    private CancellationTokenSource? _toastCts;

    public MainViewModel(AudioService audioService, PlaylistService playlistService, 
                         LibraryService libraryService, FftService fftService,
                         ExpandedPanelViewModel expandedPanel,
                         SettingsService settingsService)
    {
        _audioService = audioService;
        _playlistService = playlistService;
        _libraryService = libraryService;
        _fftService = fftService;
        _settingsService = settingsService;
        _statusController = new StatusDisplayController(audioService);
        ExpandedPanel = expandedPanel;

        // Wire up the toast callback so ExpandedPanelViewModel can show toasts
        expandedPanel.ShowToastCallback = ShowToast;

        // Load persisted settings (lightweight — just reads JSON)
        var settings = _settingsService.Current;
        _bitPerfectMode = settings.BitPerfectEnabled;
        _isExpanded = settings.IsExpanded;
        _isHiresMode = settings.IsHiresMode;
        _volumeValue = settings.Volume;
        _currentProgressMode = settings.ProgressMode;

        // Subscribe to audio events
        _audioService.TrackChanged += OnTrackChanged;
        _audioService.PlayStateChanged += OnPlayStateChanged;
        _audioService.PositionChanged += OnPositionChanged;
        _audioService.DurationChanged += OnDurationChanged;
        _audioService.BitrateChanged += OnBitrateChanged;
        _audioService.VolumeChanged += OnVolumeChanged;
        _audioService.BitPerfectModeChanged += OnBitPerfectModeChanged;
        _audioService.BitPerfectStatusChanged += OnBitPerfectStatusChanged;

        // Subscribe to playlist changes for CUE segment updates
        _playlistService.Items.CollectionChanged += OnPlaylistCollectionChanged;

        // Commands
        PlayPauseCommand = new RelayCommand(_ => PlayPause());
        StopCommand = new RelayCommand(_ => _audioService.Stop());
        NextCommand = new RelayCommand(_ => _audioService.Next());
        PreviousCommand = new RelayCommand(_ => _audioService.Previous());
        BitPerfectCommand = new RelayCommand(_ => ToggleBitPerfect());
        ToggleExpandedCommand = new RelayCommand(_ => ToggleExpanded());
        ToggleLibraryModeCommand = new RelayCommand(_ => ToggleLibraryMode());
        AddSourceCommand = new RelayCommand(_ => AddSource());
        ShowHelpCommand = new RelayCommand(_ => ShowHelp());
        SeekCommand = new RelayCommand(param => Seek(param));
        ToggleProgressModeCommand = new RelayCommand(_ => ToggleProgressMode());

        // Initialize FFT timer (updates at ~16ms for ~60 FPS — smooth animation)
        _fftTimer = new System.Windows.Threading.DispatcherTimer();
        _fftTimer.Interval = TimeSpan.FromMilliseconds(16);
        _fftTimer.Tick += OnFftTimerTick;
        _fftTimer.Start();

        // Initialize cached DAC info (avoids repeated WASAPI calls on UI updates)
        _audioService.RefreshCachedDacInfo();

        // Set default text
        UpdateMarqueeText();
    }


    // Properties
    public AudioFile? CurrentTrack
    {
        get => _currentTrack;
        set { _currentTrack = value; OnPropertyChanged(); UpdateMarqueeText(); }
    }

    public string ElapsedText => _elapsed.Hours > 0 
        ? _elapsed.ToString(@"h\:mm\:ss") 
        : _elapsed.ToString(@"m\:ss");

    public string TotalDurationText => _totalDuration.Hours > 0 
        ? _totalDuration.ToString(@"h\:mm\:ss") 
        : _totalDuration.ToString(@"m\:ss");

    /// <summary>
    /// Combined time display text.
    /// For CUE tracks in Album mode: shows "track time / album total time".
    /// For CUE tracks in Track mode: shows only "track time" (no total).
    /// For normal tracks: shows "elapsed / total duration" (same as before).
    /// </summary>
    public string CueTimeDisplayText
    {
        get
        {
            if (_audioService.IsCueTrack && _audioService.CurrentCueTrack != null)
            {
                var trackPos = _audioService.CurrentTrackPosition;
                var trackDur = _audioService.CurrentTrackDuration;
                string posText = trackPos.Hours > 0
                    ? trackPos.ToString(@"h\:mm\:ss")
                    : trackPos.ToString(@"m\:ss");

                if (_currentProgressMode == ProgressMode.Track)
                {
                    // Track mode: show only track position time
                    return posText;
                }

                // Album mode: show "track time / track duration"
                string durText = trackDur.Hours > 0
                    ? trackDur.ToString(@"h\:mm\:ss")
                    : trackDur.ToString(@"m\:ss");
                return $"{posText} / {durText}";
            }
            return $"{ElapsedText} / {TotalDurationText}";
        }
    }

    /// <summary>
    /// Current progress display mode: Track (per-track) or Album (full album).
    /// </summary>
    public ProgressMode CurrentProgressMode
    {
        get => _currentProgressMode;
        set
        {
            if (_currentProgressMode != value)
            {
                _currentProgressMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressModeButtonText));
                OnPropertyChanged(nameof(CueSegmentsVisibility));
                OnPropertyChanged(nameof(CueTimeDisplayText));
                _settingsService.Update(s => s.ProgressMode = value);
            }
        }
    }

    /// <summary>
    /// Button text for toggling progress mode.
    /// </summary>
    public string ProgressModeButtonText => _currentProgressMode == ProgressMode.Album ? "Album" : "Track";

    public TimeSpan Elapsed
    {
        get => _elapsed;
        set { _elapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedText)); }
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set { _totalDuration = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalDurationText)); }
    }

    public int BitrateValue
    {
        get => _bitrate;
        set { _bitrate = value; OnPropertyChanged(); }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; OnPropertyChanged(); }
    }

    public bool IsBitPerfectMode
    {
        get => _bitPerfectMode;
        set
        {
            _bitPerfectMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BitPerfectButtonText));
            // Delegate indicator updates to the controller
            RefreshIndicators();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
    }

    public double WindowHeight
    {
        get => _windowHeight;
        set { _windowHeight = value; OnPropertyChanged(); }
    }

    public string ExpandIcon => _isExpanded ? "▲" : "▼";

    public bool IsHiresMode
    {
        get => _isHiresMode;
        set { _isHiresMode = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Button text for Exclusive/Shared mode toggle.
    /// Shows "Exclusive" when Bit Perfect mode is active, "Shared" when inactive.
    /// </summary>
    public string BitPerfectButtonText => _bitPerfectMode ? "Exclusive" : "Shared";

    // ════════════════════════════════════════════════════════════════
    //  Status indicator properties — delegated to StatusDisplayController
    // ════════════════════════════════════════════════════════════════

    public string BitPerfectButtonColor => _statusController.BitPerfectButtonColor;
    public string BitPerfectBorderColor => _statusController.BitPerfectBorderColor;
    public string BitDepthText => _statusController.BitDepthText;
    public string SampleRateText => _statusController.SampleRateText;
    public string BitPerfectInfoText => _statusController.BitPerfectInfoText;
    public bool IsBitPerfectActive => _statusController.IsBitPerfectActive;
    public string BitDepthColor => _statusController.BitDepthColor;
    public string SampleRateColor => _statusController.SampleRateColor;
    public string BitPerfectIndicatorColor => _statusController.BitPerfectIndicatorColor;
    public string PlayIndicatorColor => _statusController.PlayIndicatorColor;
    public string PauseIndicatorColor => _statusController.PauseIndicatorColor;
    public string StopIndicatorColor => _statusController.StopIndicatorColor;
    public string HiresIndicatorColor => _statusController.HiresIndicatorColor;
    public string Mp3IndicatorColor => _statusController.Mp3IndicatorColor;
    public bool IsVolumeActive => _statusController.IsVolumeActive;
    public string VolumeTextColor => _statusController.VolumeTextColor;
    public string SourceFormatText => _statusController.SourceFormatText;
    public string SourceIndicatorColor => _statusController.SourceIndicatorColor;
    public string DacCapabilitiesText => _statusController.DacCapabilitiesText;
    public string BitPerfectStatusText => _statusController.BitPerfectStatusText;
    public string BitPerfectStatusColor => _statusController.BitPerfectStatusColor;
    public string StatusLabelText => _statusController.StatusLabelText;
    public string StatusLabelColor => _statusController.StatusLabelColor;
    public string DeviceMaxSampleRateText => _statusController.DeviceMaxSampleRateText;
    public string DeviceMaxBitDepthText => _statusController.DeviceMaxBitDepthText;
    public string DeviceNameText => _statusController.DeviceNameText;

    public string PlayPauseIcon => _isPlaying ? "❚❚" : "►";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string LibraryModeText => _isHiresMode ? "Lossless" : "Compressed";
    public string AddSourceText => "Add Source";
    public string MarqueeText
    {
        get => _marqueeText;
        set { _marqueeText = value; OnPropertyChanged(); StartMarquee(); }
    }

    public string MarqueeDisplayText
    {
        get => _marqueeDisplayText;
        set { _marqueeDisplayText = value; OnPropertyChanged(); }
    }

    public string CoverPath => _currentTrack?.CoverPath ?? "";

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public bool IsSeeking
    {
        get => _isSeeking;
        set { _isSeeking = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Spectrum data for the segmented-bar spectrum analyzer.
    /// Updated at ~60 FPS from FFT service.
    /// </summary>
    public float[] SpectrumData
    {
        get => _fftData;
        set { _fftData = value; OnPropertyChanged(); }
    }

    // Volume control
    public double VolumeValue
    {
        get => _volumeValue;
        set
        {
            _volumeValue = value;
            OnPropertyChanged();
            _audioService.SetVolume((float)value);
            _settingsService.Update(s => s.Volume = value);
        }
    }

    // Expanded Panel ViewModel
    public ExpandedPanelViewModel ExpandedPanel { get; }

    // ════════════════════════════════════════════════════════════════
    //  CUE Segments for segmented progress bar
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<CueSegment> CueSegments { get; } = new();

    /// <summary>
    /// Visibility of the CUE segments overlay on the progress bar.
    /// Visible only in Album mode AND when CueSegments has items.
    /// In Track mode, segments are always hidden.
    /// </summary>
    public Visibility CueSegmentsVisibility =>
        _currentProgressMode == ProgressMode.Album && CueSegments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Gets the album identifier for a CUE track.
    /// Uses CueFilePath if available, otherwise falls back to the Album name.
    /// </summary>
    private static string GetCueAlbumId(CueTrack cueTrack)
    {
        return !string.IsNullOrEmpty(cueTrack.CueFilePath)
            ? cueTrack.CueFilePath
            : cueTrack.Album;
    }

    /// <summary>
    /// Updates CueSegments based on the currently playing CUE album.
    /// Segments are shown only for the album of the current CUE track.
    /// Non-CUE tracks clear the segments (normal progress bar).
    /// </summary>
    public void UpdateCueSegments()
    {
        var currentCueTrack = _audioService.CurrentCueTrack;

        // If not playing a CUE track, clear segments
        if (currentCueTrack == null)
        {
            CueSegments.Clear();
            return;
        }

        var items = _playlistService.Items;
        if (items.Count == 0)
        {
            CueSegments.Clear();
            return;
        }

        // Determine the current album identifier
        string currentAlbumId = GetCueAlbumId(currentCueTrack);

        // Find all playlist items that belong to this same album
        var albumItems = new List<(PlaylistItem item, CueTrack cueTrack)>();
        foreach (var item in items)
        {
            if (item.CueTrack != null && GetCueAlbumId(item.CueTrack) == currentAlbumId)
            {
                albumItems.Add((item, item.CueTrack));
            }
        }

        if (albumItems.Count == 0)
        {
            CueSegments.Clear();
            return;
        }

        // Sort by TrackNumber (or StartPosition as fallback) to preserve album order
        albumItems.Sort((a, b) =>
        {
            int trackCompare = a.cueTrack.TrackNumber.CompareTo(b.cueTrack.TrackNumber);
            if (trackCompare != 0)
                return trackCompare;
            return a.cueTrack.StartPosition.CompareTo(b.cueTrack.StartPosition);
        });

        // Calculate total duration of the album (sum of all tracks in the album)
        double totalDurationSeconds = 0;
        foreach (var (_, cueTrack) in albumItems)
        {
            totalDurationSeconds += cueTrack.Duration.TotalSeconds;
        }

        if (totalDurationSeconds <= 0)
        {
            CueSegments.Clear();
            return;
        }

        // Build segments — all tracks from the current album are active
        double runningOffset = 0;
        var newSegments = new List<CueSegment>();

        foreach (var (item, cueTrack) in albumItems)
        {
            double durationSeconds = cueTrack.Duration.TotalSeconds;
            string trackId = $"{cueTrack.FilePath}|{cueTrack.StartPosition.Ticks}";

            double startRatio = runningOffset / totalDurationSeconds;
            runningOffset += durationSeconds;
            double endRatio = runningOffset / totalDurationSeconds;

            startRatio = Math.Max(0, Math.Min(1, startRatio));
            endRatio = Math.Max(0, Math.Min(1, endRatio));

            newSegments.Add(new CueSegment
            {
                StartRatio = startRatio,
                EndRatio = endRatio,
                IsActive = true,
                TrackNumber = cueTrack.TrackNumber,
                TrackId = trackId
            });
        }

        // Replace collection content on UI thread
        CueSegments.Clear();
        foreach (var segment in newSegments)
        {
            CueSegments.Add(segment);
        }

        OnPropertyChanged(nameof(CueSegmentsVisibility));
    }

    /// <summary>
    /// Called when the playlist collection changes (items added/removed).
    /// Only rebuilds CUE segments if the changed item belongs to the current album.
    /// </summary>
    private void OnPlaylistCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var currentCueTrack = _audioService.CurrentCueTrack;
        if (currentCueTrack == null)
        {
            // Not playing a CUE track — no segments to update
            return;
        }

        string currentAlbumId = GetCueAlbumId(currentCueTrack);

        // Check if any of the changed items belong to the current album
        bool affectsCurrentAlbum = false;

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is PlaylistItem pi && pi.CueTrack != null &&
                    GetCueAlbumId(pi.CueTrack) == currentAlbumId)
                {
                    affectsCurrentAlbum = true;
                    break;
                }
            }
        }

        if (!affectsCurrentAlbum && e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is PlaylistItem pi && pi.CueTrack != null &&
                    GetCueAlbumId(pi.CueTrack) == currentAlbumId)
                {
                    affectsCurrentAlbum = true;
                    break;
                }
            }
        }

        if (affectsCurrentAlbum)
        {
            UpdateCueSegments();
        }
    }


    // Commands

    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand BitPerfectCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand ToggleLibraryModeCommand { get; }
    public ICommand AddSourceCommand { get; }
    public ICommand ShowHelpCommand { get; }
    public ICommand SeekCommand { get; }
    public ICommand ToggleProgressModeCommand { get; }

    /// <summary>
    /// Refresh all indicator properties by delegating to the StatusDisplayController.
    /// </summary>
    private void RefreshIndicators()
    {
        _statusController.UpdateAllIndicators(
            _bitPerfectMode,
            _isPlaying,
            _audioService.IsPaused,
            _isHiresMode,
            _audioService.CurrentBitPerfectStatus);

        // Notify the UI that all wrapper properties have changed
        OnPropertyChanged(nameof(BitPerfectButtonColor));
        OnPropertyChanged(nameof(BitPerfectBorderColor));
        OnPropertyChanged(nameof(BitDepthText));
        OnPropertyChanged(nameof(SampleRateText));
        OnPropertyChanged(nameof(BitPerfectInfoText));
        OnPropertyChanged(nameof(IsBitPerfectActive));
        OnPropertyChanged(nameof(BitDepthColor));
        OnPropertyChanged(nameof(SampleRateColor));
        OnPropertyChanged(nameof(BitPerfectIndicatorColor));
        OnPropertyChanged(nameof(PlayIndicatorColor));
        OnPropertyChanged(nameof(PauseIndicatorColor));
        OnPropertyChanged(nameof(StopIndicatorColor));
        OnPropertyChanged(nameof(HiresIndicatorColor));
        OnPropertyChanged(nameof(Mp3IndicatorColor));
        OnPropertyChanged(nameof(IsVolumeActive));
        OnPropertyChanged(nameof(VolumeTextColor));
        OnPropertyChanged(nameof(SourceFormatText));
        OnPropertyChanged(nameof(SourceIndicatorColor));
        OnPropertyChanged(nameof(DacCapabilitiesText));
        OnPropertyChanged(nameof(BitPerfectStatusText));
        OnPropertyChanged(nameof(BitPerfectStatusColor));
        OnPropertyChanged(nameof(StatusLabelText));
        OnPropertyChanged(nameof(StatusLabelColor));
        OnPropertyChanged(nameof(DeviceMaxSampleRateText));
        OnPropertyChanged(nameof(DeviceMaxBitDepthText));
        OnPropertyChanged(nameof(DeviceNameText));
    }

    private void PlayPause()
    {
        if (_isPlaying)
        {
            _audioService.Pause();
        }
        else if (_audioService.IsPaused)
        {
            // Resume from where we paused
            _audioService.Resume();
        }
        else
        {
            // If a playlist item is selected in UI, use it instead of CurrentIndex
            int selectedIndex = ExpandedPanel.SelectedPlaylistIndex;
            if (selectedIndex >= 0 && selectedIndex < _playlistService.Items.Count)
            {
                _playlistService.CurrentIndex = selectedIndex;
            }
            _audioService.Play();
        }
    }

    private void ToggleBitPerfect()
    {
        if (_bitPerfectMode)
        {
            // Exiting Bit Perfect mode — restore previous volume
            float savedVolume = _audioService.GetSavedVolume();
            _volumeValue = savedVolume;
            OnPropertyChanged(nameof(VolumeValue));
            _audioService.SetBitPerfectMode(false);
            _settingsService.Update(s => s.BitPerfectEnabled = false);
        }
        else
        {
            // Entering Bit Perfect mode — show warning if not accepted yet
            var settings = _settingsService.Current;
            if (!settings.WarningAccepted)
            {
                // Show warning dialog
                string message = "Для максимального качества звука громкость будет установлена на 100%.\n\n" +
                                 "Пожалуйста, отрегулируйте громкость на вашем усилителе или ЦАП до комфортного уровня перед продолжением.\n\n" +
                                 "В режиме Bit Perfect ползунок громкости отключается для обеспечения чистого, неискажённого аудиосигнала.";

                var dialog = new Views.InputDialog("Bit Perfect Mode", message, "Больше не показывать это предупреждение");

                bool? result = dialog.ShowDialog();
                
                if (result != true)
                {
                    // User cancelled — don't switch to Bit Perfect mode
                    return;
                }

                // Save warning acceptance
                if (dialog.IsCheckBoxChecked)
                {
                    _settingsService.Update(s => s.WarningAccepted = true);
                }
            }

            // Save current volume before entering Bit Perfect mode
            _audioService.SetVolume((float)_volumeValue);
            _audioService.SetBitPerfectMode(true);
            _settingsService.Update(s => s.BitPerfectEnabled = true);

            // Visually set the slider to 100% so the user sees that volume is locked at max
            _volumeValue = 1.0;
            OnPropertyChanged(nameof(VolumeValue));
        }
    }

    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        WindowHeight = _isExpanded ? 620 : 220;
        _settingsService.Update(s => s.IsExpanded = _isExpanded);
    }

    private void ToggleLibraryMode()
    {
        IsHiresMode = !_isHiresMode;
        ExpandedPanel.IsHiresView = _isHiresMode;
        OnPropertyChanged(nameof(LibraryModeText));
        RefreshIndicators();
        _settingsService.Update(s => s.IsHiresMode = _isHiresMode);
    }

    private void ToggleProgressMode()
    {
        CurrentProgressMode = _currentProgressMode == ProgressMode.Album
            ? ProgressMode.Track
            : ProgressMode.Album;
    }

    private void AddSource()
    {
        if (_isHiresMode)
            ExpandedPanel.AddHiresSourceCommand.Execute(null);
        else
            ExpandedPanel.AddMp3SourceCommand.Execute(null);
    }

    /// <summary>
    /// Play a specific playlist item by index (called on double-click).
    /// </summary>
    public void PlayPlaylistItem(int index)
    {
        _playlistService.CurrentIndex = index;
        _audioService.Play();
    }

    private void ShowHelp()
    {
        var helpWindow = new Views.HelpWindow();
        helpWindow.ShowDialog();
    }

    private void Seek(object? parameter)
    {
        if (parameter is double fraction)
        {
            _audioService.Seek(fraction);
        }
    }

    private void OnTrackChanged(AudioFile track, CueTrack? cueTrack)
    {
        Logger.Log($"OnTrackChanged: audioService.CurrentBitDepth = {_audioService.CurrentBitDepth}, audioService.CurrentSampleRate = {_audioService.CurrentSampleRate}, cueTrack={cueTrack?.TrackNumber}");
        CurrentTrack = track;
        StatusText = "Playing";
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(CoverPath));
        RefreshIndicators();

        // For CUE tracks, update duration from the CUE track duration
        if (cueTrack != null)
        {
            TotalDuration = cueTrack.Duration;
        }

        UpdateCueTimeDisplay();

        // Rebuild CUE segments for the new track's album
        UpdateCueSegments();
    }


    private void OnPlayStateChanged(bool playing)
    {
        IsPlaying = playing;
        StatusText = playing ? "Playing" : (_audioService.IsPaused ? "Paused" : "Stopped");
        OnPropertyChanged(nameof(PlayPauseIcon));
        RefreshIndicators();
    }

    /// <summary>
    /// Save all current settings to disk (called on window close).
    /// </summary>
    public void SaveSettings()
    {
        _settingsService.Save();
    }

    public void BeginSeek()
    {
        IsSeeking = true;
    }

    public void EndSeek(double fraction)
    {
        IsSeeking = false;
        _audioService.Seek(fraction);
    }

    private void OnPositionChanged(TimeSpan position)
    {
        Elapsed = position;
        if (!_isSeeking && _totalDuration.TotalSeconds > 0)
        {
            // Во время паузы не обновляем прогресс-бар — используем сохранённое значение,
            // чтобы избежать сброса в 0 при уничтожении аудио-объектов в Exclusive режиме
            if (_audioService.IsPaused)
            {
                Progress = _audioService.PausedProgress;
            }
            else
            {
                Progress = position.TotalSeconds / _totalDuration.TotalSeconds;
            }
        }

        UpdateCueTimeDisplay();
    }

    private void OnDurationChanged(TimeSpan duration)
    {
        TotalDuration = duration;
    }

    private void OnBitrateChanged(int bitrate)
    {
        BitrateValue = bitrate;
    }

    private void OnVolumeChanged(float volume)
    {
        _volumeValue = volume;
        OnPropertyChanged(nameof(VolumeValue));
    }

    private void OnBitPerfectModeChanged(bool enabled)
    {
        // Called when AudioService falls back from Exclusive to Shared mode
        // (e.g., when the audio device doesn't support Exclusive mode).
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            IsBitPerfectMode = enabled;
            _settingsService.Update(s => s.BitPerfectEnabled = enabled);
            
            // Refresh the library tree when switching modes
            // (Exclusive mode may affect how files are displayed)
            ExpandedPanel.ApplyFilter();
        });
    }

    /// <summary>
    /// Called when the Bit Perfect status changes (Off/Perfect/Limited).
    /// Updates the UI indicator colors and text accordingly.
    /// </summary>
    private void OnBitPerfectStatusChanged(BitPerfectStatus status)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshIndicators();
        });
    }

    private void UpdateMarqueeText()
    {
        if (_currentTrack != null)
        {
            MarqueeText = $"{_currentTrack.Artist} - {_currentTrack.Album} - {_currentTrack.Title}";
        }
        else
        {
            MarqueeText = "PureAudio - High Fidelity Music Player";
        }
    }

    /// <summary>
    /// Notify the UI that CueTimeDisplayText has changed.
    /// Called on position changes and track changes.
    /// </summary>
    private void UpdateCueTimeDisplay()
    {
        OnPropertyChanged(nameof(CueTimeDisplayText));
    }

    private void StartMarquee()
    {
        // Stop existing timer
        _marqueeTimer?.Stop();

        // Show the full text initially
        MarqueeDisplayText = _marqueeText;
        _marqueeOffset = 0;

        // Only scroll if text is long enough (more than ~30 chars)
        if (_marqueeText.Length > 30)
        {
            _marqueeTimer = new System.Windows.Threading.DispatcherTimer();
            _marqueeTimer.Interval = TimeSpan.FromMilliseconds(200);
            _marqueeTimer.Tick += (s, e) =>
            {
                _marqueeOffset++;
                if (_marqueeOffset > _marqueeText.Length)
                {
                    _marqueeOffset = 0;
                }
                // Show a window of ~65 characters (to fill 435px width)
                int len = Math.Min(65, _marqueeText.Length);
                int start = Math.Min(_marqueeOffset, _marqueeText.Length - len);
                MarqueeDisplayText = _marqueeText.Substring(start, len);
            };
            _marqueeTimer.Start();
        }
    }

    private void OnFftTimerTick(object? sender, EventArgs e)
    {
        if (_fftService.HasData)
        {
            // Update FFT data at high frequency for smooth animation
            var data = _fftService.GetPlaceholderData();
            SpectrumData = data;
        }
        else if (!_isPlaying)
        {
            // Clear spectrum to zeros when stopped
            if (_fftData.Any(v => v > 0))
            {
                SpectrumData = new float[48];
            }
        }
    }

    // --- Background Loading Status ---

    /// <summary>
    /// Whether the playlist is currently being loaded in the background.
    /// </summary>
    public bool IsLoadingPlaylist
    {
        get => _isLoadingPlaylist;
        set { _isLoadingPlaylist = value; OnPropertyChanged(); UpdateLoadingStatusText(); }
    }

    /// <summary>
    /// Whether the library is currently being scanned in the background.
    /// </summary>
    public bool IsLoadingLibrary
    {
        get => _isLoadingLibrary;
        set { _isLoadingLibrary = value; OnPropertyChanged(); UpdateLoadingStatusText(); }
    }

    /// <summary>
    /// Status text shown in the loading bar (e.g., "Loading playlist...", "Scanning library...").
    /// </summary>
    public string LoadingStatusText
    {
        get => _loadingStatusText;
        set { _loadingStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether any background loading is in progress (for visibility of the status bar).
    /// </summary>
    public bool IsLoading => _isLoadingPlaylist || _isLoadingLibrary;

    // --- Background Activity Indicator (pulsing dot + toast) ---

    /// <summary>
    /// True when any background process is running (library scan, playlist load, etc.).
    /// Controls visibility of the pulsing dot indicator.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            // Auto-show toast when busy starts, auto-hide when busy ends
            if (value)
                IsToastVisible = true;
            else
                IsToastVisible = false;
        }
    }

    /// <summary>
    /// Current status text shown in the toast (e.g. "Loading playlist...", "Scanning library...").
    /// </summary>
    public string BusyStatusText
    {
        get => _busyStatusText;
        set { _busyStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Controls visibility of the toast (for show/hide animation).
    /// The pulsing dot is bound to IsBusy, the toast text is bound to IsToastVisible
    /// so it can fade in/out independently.
    /// </summary>
    public bool IsToastVisible
    {
        get => _isToastVisible;
        set { _isToastVisible = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Show a toast notification with the given text.
    /// Cancels any previous toast timer, so calling this repeatedly
    /// replaces the old toast with the new one.
    /// The toast auto-hides after <paramref name="durationMs"/> milliseconds,
    /// but the pulsing dot (IsBusy) stays on — the caller must explicitly
    /// set <see cref="IsBusy"/> = false when the background operation completes.
    /// </summary>
    public void ShowToast(string text, int durationMs = 3000)
    {
        // Cancel any previous auto-hide timer
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        BusyStatusText = text;
        IsToastVisible = true;
        IsBusy = true;

        // Fire-and-forget: auto-hide the toast text after duration
        // (the pulsing dot stays on until IsBusy is set to false explicitly)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, token);
                // Back on UI thread to update properties
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        IsToastVisible = false;
                        // Do NOT set IsBusy = false here — caller manages it
                    }
                });
            }
            catch (TaskCanceledException)
            {
                // Cancelled — a new toast replaced this one, do nothing
            }
        }, token);
    }

    private void UpdateLoadingStatusText()
    {
        if (_isLoadingPlaylist && _isLoadingLibrary)
        {
            LoadingStatusText = "Loading playlist and scanning library...";
            ShowToast("Loading playlist and scanning library...", 5000);
        }
        else if (_isLoadingPlaylist)
        {
            LoadingStatusText = "Loading playlist...";
            ShowToast("Loading playlist...", 5000);
        }
        else if (_isLoadingLibrary)
        {
            LoadingStatusText = "Scanning library...";
            ShowToast("Scanning library...", 5000);
        }
        else
        {
            LoadingStatusText = "";
        }
    }

    /// <summary>
    /// Called after the window is shown. Loads playlist and library in background,
    /// then applies Bit Perfect mode if it was enabled before.
    /// </summary>
    public async void StartBackgroundLoading()
    {
        // Step 1: Load playlist in background
        IsLoadingPlaylist = true;
        await Task.Run(() => LoadPlaylistInBackground());
        IsLoadingPlaylist = false;

        // Step 2: Load library — try cache first, fall back to full scan
        IsLoadingLibrary = true;
        LoadingStatusText = "Loading library...";
        ShowToast("Loading library...", 5000);
        await Task.Run(() => LoadLibraryInBackground());
        IsLoadingLibrary = false;

        // Step 3: If Bit Perfect mode was enabled before, try to switch to it
        // with a timeout. If it fails, stay in Normal mode.
        if (_bitPerfectMode)
        {
            LoadingStatusText = "Switching to Bit Perfect mode...";
            ShowToast("Switching to Bit Perfect mode...", 5000);
            bool success = await TrySwitchToBitPerfectAsync();
            if (success)
            {
                _audioService.SetVolume((float)_volumeValue);
                // Visually set the slider to 100% in Bit Perfect mode
                _volumeValue = 1.0;
                OnPropertyChanged(nameof(VolumeValue));
            }
            else
            {
                // Failed to switch — stay in Normal mode, update UI
                _bitPerfectMode = false;
                OnPropertyChanged(nameof(IsBitPerfectMode));
                OnPropertyChanged(nameof(BitPerfectButtonText));
                RefreshIndicators();
                _settingsService.Update(s => s.BitPerfectEnabled = false);
                LoadingStatusText = "Bit Perfect mode unavailable, using Normal mode";
            }
        }

        // Clear loading status after a short delay
        await Task.Delay(2000);
        LoadingStatusText = "";
        IsBusy = false;
    }

    /// <summary>
    /// Load saved playlist from JSON on a background thread.
    /// Reads metadata for each file that still exists.
    /// </summary>
    private void LoadPlaylistInBackground()
    {
        try
        {
            var savedPaths = _playlistService.LoadFromJson();
            if (savedPaths.Count > 0)
            {
                foreach (var path in savedPaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        var audioFile = MetadataService.ReadMetadata(path);
                        // Must add on UI thread since ObservableCollection is not thread-safe
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            _playlistService.Add(audioFile);
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Background playlist load error: {ex.Message}");
        }
    }

    /// <summary>
    /// Load library — try cache first, fall back to full scan.
    /// </summary>
    private void LoadLibraryInBackground()
    {
        try
        {
            // Try loading from cache first (fast — no file scanning)
            bool loadedFromCache = _libraryService.TryLoadCache();

            if (!loadedFromCache)
            {
                // Cache miss or invalid — do a full scan
                _libraryService.RescanAll();
            }

            // Refresh the filtered library view on UI thread
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ExpandedPanel.ApplyFilter();
                ExpandedPanel.OnPropertyChanged(nameof(ExpandedPanel.CurrentLibrary));
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Background library load error: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to switch to Bit Perfect mode with a timeout.
    /// Runs the switch on the UI thread (Dispatcher) so that WasapiOut
    /// is created in the correct synchronization context.
    /// Returns true if successful, false if timeout or error.
    /// </summary>
    private async Task<bool> TrySwitchToBitPerfectAsync()
    {
        var tcs = new TaskCompletionSource<bool>();

        // Run the switch on the UI thread via Dispatcher.
        // WasapiOut must be created on a thread with a stable synchronization
        // context (UI thread or a dedicated STA thread). Using the UI thread
        // is the simplest and most reliable approach.
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                _audioService.SetBitPerfectMode(true);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Logger.Log($"Bit Perfect mode switch failed: {ex.Message}");
                tcs.TrySetResult(false);
            }
        });

        // Wait with timeout (5 seconds)
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (completedTask == tcs.Task)
        {
            return await tcs.Task;
        }
        else
        {
            Logger.Log("Bit Perfect mode switch timed out after 5s");
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
