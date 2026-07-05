using System.ComponentModel;
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

    private AudioFile? _currentTrack;
    private TimeSpan _elapsed;
    private TimeSpan _totalDuration;
    private int _bitrate;
    private bool _isPlaying;
    private bool _bitPerfectMode;
    private bool _isExpanded;
    private double _windowHeight = 220;
    private bool _isHiresMode;
    private BitPerfectStatus _bitPerfectStatus = BitPerfectStatus.Off;
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
    // Background loading state
    private bool _isLoadingPlaylist;
    private bool _isLoadingLibrary;
    private string _loadingStatusText = "";

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
        ExpandedPanel = expandedPanel;

        // Load persisted settings (lightweight — just reads JSON)
        var settings = _settingsService.Current;
        _bitPerfectMode = settings.BitPerfectEnabled;
        _isExpanded = settings.IsExpanded;
        _isHiresMode = settings.IsHiresMode;
        _volumeValue = settings.Volume;

        // Subscribe to audio events
        _audioService.TrackChanged += OnTrackChanged;
        _audioService.PlayStateChanged += OnPlayStateChanged;
        _audioService.PositionChanged += OnPositionChanged;
        _audioService.DurationChanged += OnDurationChanged;
        _audioService.BitrateChanged += OnBitrateChanged;
        _audioService.VolumeChanged += OnVolumeChanged;
        _audioService.BitPerfectModeChanged += OnBitPerfectModeChanged;
        _audioService.BitPerfectStatusChanged += OnBitPerfectStatusChanged;

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

        // Initialize FFT timer (updates at ~16ms for ~60 FPS — smooth animation)
        _fftTimer = new System.Windows.Threading.DispatcherTimer();
        _fftTimer.Interval = TimeSpan.FromMilliseconds(16);
        _fftTimer.Tick += OnFftTimerTick;
        _fftTimer.Start();

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
            OnPropertyChanged(nameof(BitPerfectButtonColor));
            OnPropertyChanged(nameof(BitPerfectBorderColor));
            OnPropertyChanged(nameof(IsVolumeActive));
            OnPropertyChanged(nameof(VolumeTextColor));
            OnPropertyChanged(nameof(BitPerfectIndicatorColor));
            OnPropertyChanged(nameof(IsBitPerfectActive));
            OnPropertyChanged(nameof(BitDepthColor));
            OnPropertyChanged(nameof(SampleRateColor));
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
    /// Button text for Bit Perfect toggle.
    /// </summary>
    public string BitPerfectButtonText => "Bit Perfect";

    /// <summary>
    /// Color for the Bit Perfect button text — gold when active, gray when inactive.
    /// </summary>
    public string BitPerfectButtonColor => _bitPerfectMode ? "#C9A84C" : "#555555";

    /// <summary>
    /// Color for the Bit Perfect button border — gold when active, gray when inactive.
    /// </summary>
    public string BitPerfectBorderColor => _bitPerfectMode ? "#C9A84C" : "#4A4A4A";

    /// <summary>
    /// Bit depth text (e.g. "24 bit", "16 bit"). Empty when no track is playing.
    /// </summary>
    public string BitDepthText
    {
        get
        {
            int bd = _audioService.CurrentBitDepth;
            return bd > 0 ? $"{bd} bit" : "";
        }
    }

    /// <summary>
    /// Sample rate text (e.g. "96 kHz", "44.1 kHz"). Empty when no track is playing.
    /// Converts raw Hz to kHz with one decimal place for readability.
    /// </summary>
    public string SampleRateText
    {
        get
        {
            int sr = _audioService.CurrentSampleRate;
            if (sr <= 0) return "";
            double khz = sr / 1000.0;
            // Show one decimal only if it's not a whole number (e.g. 44.1, but 96)
            return khz == (int)khz ? $"{(int)khz} kHz" : $"{khz:F1} kHz";
        }
    }

    /// <summary>
    /// Full info string for tooltip (e.g. "24 bit / 96 kHz").
    /// </summary>
    public string BitPerfectInfoText
    {
        get
        {
            int sr = _audioService.CurrentSampleRate;
            int bd = _audioService.CurrentBitDepth;
            if (sr > 0 && bd > 0)
            {
                double khz = sr / 1000.0;
                string srText = khz == (int)khz ? $"{(int)khz}" : $"{khz:F1}";
                return $"{bd} bit / {srText} kHz";
            }
            return "";
        }
    }

    /// <summary>
    /// Whether bit-perfect indicators should be gold (active).
    /// True when Bit Perfect mode is ON and a track is playing.
    /// </summary>
    public bool IsBitPerfectActive => _bitPerfectMode && _isPlaying && _audioService.CurrentSampleRate > 0;

    /// <summary>
    /// Color for bit depth text — bright gold when bit-perfect active, gray when inactive.
    /// </summary>
    public string BitDepthColor => IsBitPerfectActive ? "#C9A84C" : "#555555";

    /// <summary>
    /// Color for sample rate text — medium gold when bit-perfect active, gray when inactive.
    /// </summary>
    public string SampleRateColor => IsBitPerfectActive ? "#A88A3E" : "#555555";

    /// <summary>
    /// Color for the Bit Perfect indicator badge — gold when active, gray when inactive.
    /// </summary>
    public string BitPerfectIndicatorColor => IsBitPerfectActive ? "#C9A84C" : "#555555";

    public string PlayPauseIcon => _isPlaying ? "❚❚" : "►";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    // Play/Pause/Stop indicator colors — gold accent (#C9A84C) when active
    public string PlayIndicatorColor => _isPlaying ? "#C9A84C" : "#555555";
    public string PauseIndicatorColor => _audioService.IsPaused ? "#C9A84C" : "#555555";
    public string StopIndicatorColor => (!_isPlaying && !_audioService.IsPaused) ? "#C9A84C" : "#555555";

    // Hires/MP3 indicator colors — gold accent when active
    public string HiresIndicatorColor => _isHiresMode ? "#C9A84C" : "#555555";
    public string Mp3IndicatorColor => !_isHiresMode ? "#C9A84C" : "#555555";

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

    /// <summary>
    /// Volume slider is only active in Normal (Shared) mode.
    /// In Bit Perfect mode, volume is locked at 100% (no DSP).
    /// </summary>
    public bool IsVolumeActive => !_bitPerfectMode;

    /// <summary>
    /// Volume label color — gold when volume is adjustable (Normal mode),
    /// gray when locked (Bit Perfect mode).
    /// </summary>
    public string VolumeTextColor => _bitPerfectMode ? "#555555" : "#C9A84C";

    // Expanded Panel ViewModel
    public ExpandedPanelViewModel ExpandedPanel { get; }

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
        OnPropertyChanged(nameof(HiresIndicatorColor));
        OnPropertyChanged(nameof(Mp3IndicatorColor));
        OnPropertyChanged(nameof(LibraryModeText));
        _settingsService.Update(s => s.IsHiresMode = _isHiresMode);
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

    private void OnTrackChanged(AudioFile track)
    {
        CurrentTrack = track;
        StatusText = "Playing";
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(CoverPath));
        OnPropertyChanged(nameof(BitPerfectInfoText));
        OnPropertyChanged(nameof(BitDepthText));
        OnPropertyChanged(nameof(SampleRateText));
        OnPropertyChanged(nameof(IsBitPerfectActive));
        OnPropertyChanged(nameof(BitDepthColor));
        OnPropertyChanged(nameof(SampleRateColor));
        OnPropertyChanged(nameof(BitPerfectIndicatorColor));
        OnPropertyChanged(nameof(BitPerfectButtonColor));
        OnPropertyChanged(nameof(BitPerfectBorderColor));
        OnPropertyChanged(nameof(DeviceMaxSampleRateText));
        OnPropertyChanged(nameof(DeviceMaxBitDepthText));
        OnPropertyChanged(nameof(DeviceNameText));
    }

    private void OnPlayStateChanged(bool playing)
    {
        IsPlaying = playing;
        StatusText = playing ? "Playing" : (_audioService.IsPaused ? "Paused" : "Stopped");
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(PlayIndicatorColor));
        OnPropertyChanged(nameof(PauseIndicatorColor));
        OnPropertyChanged(nameof(StopIndicatorColor));
        OnPropertyChanged(nameof(IsBitPerfectActive));
        OnPropertyChanged(nameof(BitDepthColor));
        OnPropertyChanged(nameof(SampleRateColor));
        OnPropertyChanged(nameof(BitPerfectIndicatorColor));
        OnPropertyChanged(nameof(BitPerfectButtonColor));
        OnPropertyChanged(nameof(BitPerfectBorderColor));
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
            Progress = position.TotalSeconds / _totalDuration.TotalSeconds;
        }
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
        });
    }

    /// <summary>
    /// Called when the Bit Perfect status changes (Off/Perfect/Limited).
    /// Updates the UI indicator colors and text accordingly.
    /// </summary>
    private void OnBitPerfectStatusChanged(BitPerfectStatus status)
    {
        _bitPerfectStatus = status;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(BitPerfectStatusText));
            OnPropertyChanged(nameof(BitPerfectStatusColor));
            OnPropertyChanged(nameof(BitPerfectIndicatorColor));
            OnPropertyChanged(nameof(IsBitPerfectActive));
            OnPropertyChanged(nameof(BitDepthColor));
            OnPropertyChanged(nameof(SampleRateColor));
        });
    }

    /// <summary>
    /// Text description of the current Bit Perfect status.
    /// </summary>
    /// <summary>
    /// Maximum sample rate supported by the audio device (e.g. "192 kHz").
    /// </summary>
    public string DeviceMaxSampleRateText
    {
        get
        {
            int maxSr = _audioService.DeviceCapabilities.MaxSampleRate;
            if (maxSr <= 0) return "";
            double khz = maxSr / 1000.0;
            return khz == (int)khz ? $"{(int)khz} kHz" : $"{khz:F1} kHz";
        }
    }

    /// <summary>
    /// Maximum bit depth supported by the audio device (e.g. "32 bit").
    /// </summary>
    public string DeviceMaxBitDepthText
    {
        get
        {
            int maxBd = _audioService.DeviceCapabilities.MaxBitDepth;
            return maxBd > 0 ? $"{maxBd} bit" : "";
        }
    }

    /// <summary>
    /// Audio device name for display.
    /// </summary>
    public string DeviceNameText
    {
        get
        {
            string name = _audioService.DeviceCapabilities.DeviceName;
            return string.IsNullOrEmpty(name) ? "" : name;
        }
    }

    public string BitPerfectStatusText
    {
        get
        {
            if (!_bitPerfectMode || !_isPlaying)
                return "Bit Perfect: Off";

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => "Bit Perfect: ✓",
                BitPerfectStatus.Limited => "Bit Perfect: Limited",
                _ => "Bit Perfect: Off"
            };
        }
    }

    /// <summary>
    /// Color for the Bit Perfect status indicator.
    /// Green for Perfect, yellow for Limited, gray for Off.
    /// </summary>
    public string BitPerfectStatusColor
    {
        get
        {
            if (!_bitPerfectMode || !_isPlaying)
                return "#555555";

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => "#4CAF50",  // Green
                BitPerfectStatus.Limited => "#FFC107",  // Yellow/Amber
                _ => "#555555"
            };
        }
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

    private void UpdateLoadingStatusText()
    {
        if (_isLoadingPlaylist && _isLoadingLibrary)
            LoadingStatusText = "Loading playlist and scanning library...";
        else if (_isLoadingPlaylist)
            LoadingStatusText = "Loading playlist...";
        else if (_isLoadingLibrary)
            LoadingStatusText = "Scanning library...";
        else
            LoadingStatusText = "";
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

        // Step 2: Scan library in background
        IsLoadingLibrary = true;
        await Task.Run(() => LoadLibraryInBackground());
        IsLoadingLibrary = false;

        // Step 3: If Bit Perfect mode was enabled before, try to switch to it
        // with a timeout. If it fails, stay in Normal mode.
        if (_bitPerfectMode)
        {
            LoadingStatusText = "Switching to Bit Perfect mode...";
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
                OnPropertyChanged(nameof(BitPerfectButtonColor));
                OnPropertyChanged(nameof(BitPerfectBorderColor));
                OnPropertyChanged(nameof(IsVolumeActive));
                OnPropertyChanged(nameof(VolumeTextColor));
                OnPropertyChanged(nameof(BitPerfectIndicatorColor));
                _settingsService.Update(s => s.BitPerfectEnabled = false);
                LoadingStatusText = "Bit Perfect mode unavailable, using Normal mode";
            }
        }

        // Clear loading status after a short delay
        await Task.Delay(2000);
        LoadingStatusText = "";
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
    /// Scan library sources on a background thread.
    /// </summary>
    private void LoadLibraryInBackground()
    {
        try
        {
            _libraryService.RescanAll();
            // Refresh the filtered library view on UI thread
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ExpandedPanel.ApplyFilter();
                ExpandedPanel.OnPropertyChanged(nameof(ExpandedPanel.CurrentLibrary));
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Background library scan error: {ex.Message}");
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
