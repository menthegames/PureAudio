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
    private bool _wasapiExclusive;
    private bool _gaplessEnabled;
    private bool _isExpanded;
    private double _windowHeight = 220;
    private bool _isHiresMode;
    private float[] _fftData = new float[48];
    private float[] _fftPeakData = new float[48];
    private string _marqueeText = "";
    private string _marqueeDisplayText = "";
    private int _marqueeOffset;
    private System.Windows.Threading.DispatcherTimer? _marqueeTimer;
    private System.Windows.Threading.DispatcherTimer? _fftTimer;
    private string _statusText = "Stopped";
    private double _progress;
    private double _volumeValue;
    private bool _isSeeking;
    private static readonly Random _rng = new();

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
        _wasapiExclusive = settings.WasapiExclusive;
        _gaplessEnabled = settings.GaplessEnabled;
        _isExpanded = settings.IsExpanded;
        _isHiresMode = settings.IsHiresMode;
        _volumeValue = settings.Volume;

        // Apply non-WASAPI settings immediately
        _audioService.SetGaplessMode(_gaplessEnabled);

        // Subscribe to audio events
        _audioService.TrackChanged += OnTrackChanged;
        _audioService.PlayStateChanged += OnPlayStateChanged;
        _audioService.PositionChanged += OnPositionChanged;
        _audioService.DurationChanged += OnDurationChanged;
        _audioService.BitrateChanged += OnBitrateChanged;
        _audioService.VolumeChanged += OnVolumeChanged;

        // Commands
        PlayPauseCommand = new RelayCommand(_ => PlayPause());
        StopCommand = new RelayCommand(_ => _audioService.Stop());
        NextCommand = new RelayCommand(_ => _audioService.Next());
        PreviousCommand = new RelayCommand(_ => _audioService.Previous());
        ToggleWasapiCommand = new RelayCommand(_ => ToggleWasapi());
        ToggleGaplessCommand = new RelayCommand(_ => ToggleGapless());
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

    public bool WasapiExclusive
    {
        get => _wasapiExclusive;
        set { _wasapiExclusive = value; OnPropertyChanged(); OnPropertyChanged(nameof(WasapiModeText)); }
    }

    public bool GaplessEnabled
    {
        get => _gaplessEnabled;
        set { _gaplessEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(GaplessText)); OnPropertyChanged(nameof(GaplessTextColor)); }
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

    public string WasapiModeText => _wasapiExclusive ? "WASAPI Exclusive" : "WASAPI Shared";
    public string GaplessText => _gaplessEnabled ? "Gapless On" : "Gapless Off";
    public string GaplessTextColor => _gaplessEnabled ? "#FFA500" : "#AAAAAA";
    public string PlayPauseIcon => _isPlaying ? "❚❚" : "►";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    // Play/Pause/Stop indicator colors
    public string PlayIndicatorColor => _isPlaying ? "#FFA500" : "#555555";
    public string PauseIndicatorColor => _audioService.IsPaused ? "#FFA500" : "#555555";
    public string StopIndicatorColor => (!_isPlaying && !_audioService.IsPaused) ? "#FFA500" : "#555555";

    // Hires/MP3 indicator colors
    public string HiresIndicatorColor => _isHiresMode ? "#FFA500" : "#555555";
    public string Mp3IndicatorColor => !_isHiresMode ? "#FFA500" : "#555555";

    public string LibraryModeText => _isHiresMode ? "Lossless" : "Compressed";
    public string AddSourceText => "Add Source";
    public string NoCoverText => "No Image";

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

    public float[] FftData
    {
        get => _fftData;
        set { _fftData = value; OnPropertyChanged(); }
    }

    public float[] FftPeakData
    {
        get => _fftPeakData;
        set { _fftPeakData = value; OnPropertyChanged(); }
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

    public bool IsVolumeActive => _wasapiExclusive;

    public string VolumeTextColor => _wasapiExclusive ? "#FFA500" : "#AAAAAA";

    // Expanded Panel ViewModel
    public ExpandedPanelViewModel ExpandedPanel { get; }

    // Commands
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ToggleWasapiCommand { get; }
    public ICommand ToggleGaplessCommand { get; }
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

    private void ToggleWasapi()
    {
        WasapiExclusive = !WasapiExclusive;
        if (_wasapiExclusive)
        {
            // Set safe starting volume (20%) when switching to Exclusive mode
            VolumeValue = 0.2;
        }
        _audioService.SetWasapiMode(WasapiExclusive);
        OnPropertyChanged(nameof(IsVolumeActive));
        OnPropertyChanged(nameof(VolumeTextColor));
        _settingsService.Update(s => s.WasapiExclusive = _wasapiExclusive);
    }

    private void ToggleGapless()
    {
        GaplessEnabled = !GaplessEnabled;
        _audioService.SetGaplessMode(GaplessEnabled);
        _settingsService.Update(s => s.GaplessEnabled = _gaplessEnabled);
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
    }

    private void OnPlayStateChanged(bool playing)
    {
        IsPlaying = playing;
        StatusText = playing ? "Playing" : (_audioService.IsPaused ? "Paused" : "Stopped");
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(PlayIndicatorColor));
        OnPropertyChanged(nameof(PauseIndicatorColor));
        OnPropertyChanged(nameof(StopIndicatorColor));
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
            var peaks = _fftService.GetPeakData();
            FftData = data;
            FftPeakData = peaks;
        }
        else if (!_isPlaying)
        {
            // Show placeholder bars when stopped (small random-like values for visual appeal)
            var placeholder = new float[48];
            var peakPlaceholder = new float[48];
            for (int i = 0; i < 48; i++)
            {
                placeholder[i] = (float)(_rng.NextDouble() * 0.15);
                peakPlaceholder[i] = (float)(_rng.NextDouble() * 0.2);
            }
            FftData = placeholder;
            FftPeakData = peakPlaceholder;
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
    /// then applies WASAPI Exclusive mode if it was enabled before.
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

        // Step 3: If WASAPI Exclusive was enabled before, try to switch to it
        // with a timeout. If it fails, stay in Shared mode.
        if (_wasapiExclusive)
        {
            LoadingStatusText = "Switching to WASAPI Exclusive...";
            bool success = await TrySwitchToWasapiExclusiveAsync();
            if (success)
            {
                _audioService.SetVolume((float)_volumeValue);
            }
            else
            {
                // Failed to switch — stay in Shared mode, update UI
                _wasapiExclusive = false;
                OnPropertyChanged(nameof(WasapiExclusive));
                OnPropertyChanged(nameof(WasapiModeText));
                OnPropertyChanged(nameof(IsVolumeActive));
                OnPropertyChanged(nameof(VolumeTextColor));
                _settingsService.Update(s => s.WasapiExclusive = false);
                LoadingStatusText = "WASAPI Exclusive unavailable, using Shared mode";
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
            System.Diagnostics.Debug.WriteLine($"Background playlist load error: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Background library scan error: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to switch to WASAPI Exclusive mode with a timeout.
    /// Returns true if successful, false if timeout or error.
    /// </summary>
    private async Task<bool> TrySwitchToWasapiExclusiveAsync()
    {
        var tcs = new TaskCompletionSource<bool>();

        // Run the switch on a background thread to avoid blocking UI
        Thread bgThread = new Thread(() =>
        {
            try
            {
                _audioService.SetWasapiMode(true);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WASAPI Exclusive switch failed: {ex.Message}");
                tcs.TrySetResult(false);
            }
        });
        bgThread.SetApartmentState(ApartmentState.STA); // NAudio may need STA
        bgThread.Start();

        // Wait with timeout (5 seconds)
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (completedTask == tcs.Task)
        {
            return await tcs.Task;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("WASAPI Exclusive switch timed out after 5s");
            // Abort the thread (it will fail gracefully)
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
