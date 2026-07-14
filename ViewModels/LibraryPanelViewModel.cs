using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PureAudio.Helpers;
using PureAudio.Models;
using PureAudio.Services;
using PureAudio.Views;

namespace PureAudio.ViewModels;

public class LibraryPanelViewModel : INotifyPropertyChanged
{
    private readonly LibraryService _libraryService;
    private readonly PlaylistService _playlistService;
    private bool _isHiresView = true;
    private object? _selectedLibraryItem;
    private bool _isScanning;
    private bool _isRefreshing;
    private System.Windows.Threading.DispatcherTimer? _scanBlinkTimer;
    private bool _scanBlinkVisible;
    private string _scanStatusText = "";

    // Search
    private string _searchText = "";
    private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;
    private ObservableCollection<LibraryNode> _filteredLibrary = new();

    /// <summary>
    /// Callback to show a toast notification via MainViewModel.
    /// Set by MainViewModel after construction.
    /// </summary>
    public Action<string, int>? ShowToastCallback { get; set; }

    public LibraryPanelViewModel(LibraryService libraryService, PlaylistService playlistService)
    {
        _libraryService = libraryService;
        _playlistService = playlistService;

        AddHiresSourceCommand = new RelayCommand(_ => AddHiresSource());
        AddMp3SourceCommand = new RelayCommand(_ => AddMp3Source());
        AddToPlaylistCommand = new RelayCommand(_ => AddSelectedToPlaylist());
        DoubleClickLibraryCommand = new RelayCommand(_ => DoubleClickLibrary());
        DeleteLibrarySourceCommand = new RelayCommand(_ => DeleteLibrarySource());
        RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());

        // Initialize filtered library with full tree
        ApplyFilter();
    }

    /// <summary>
    /// Current library tree — switches between Hires and MP3 views.
    /// </summary>
    public IList<LibraryNode> CurrentLibrary => _isHiresView ? _libraryService.HiresTree : _libraryService.Mp3Tree;

    public bool IsHiresView
    {
        get => _isHiresView;
        set
        {
            _isHiresView = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentLibrary));
            OnPropertyChanged(nameof(LibraryTitle));
            ApplyFilter();
        }
    }

    public object? SelectedLibraryItem
    {
        get => _selectedLibraryItem;
        set { _selectedLibraryItem = value; OnPropertyChanged(); }
    }

    // Library title text
    public string LibraryTitle => _isHiresView ? "Lossless Audio Library" : "Compressed Audio Library";

    // Scanning indicator
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            _isScanning = value;
            OnPropertyChanged();
            if (value)
                StartScanBlink();
            else
                StopScanBlink();
        }
    }

    public bool ScanBlinkVisible
    {
        get => _scanBlinkVisible;
        set { _scanBlinkVisible = value; OnPropertyChanged(); }
    }

    public string ScanStatusText
    {
        get => _scanStatusText;
        set { _scanStatusText = value; OnPropertyChanged(); }
    }

    private void StartScanBlink()
    {
        StopScanBlink();
        ScanBlinkVisible = true;
        ScanStatusText = "Scanning...";
        _scanBlinkTimer = new System.Windows.Threading.DispatcherTimer();
        _scanBlinkTimer.Interval = TimeSpan.FromMilliseconds(600);
        _scanBlinkTimer.Tick += (s, e) =>
        {
            ScanBlinkVisible = !ScanBlinkVisible;
        };
        _scanBlinkTimer.Start();
    }

    private void StopScanBlink()
    {
        _scanBlinkTimer?.Stop();
        _scanBlinkTimer = null;
        ScanBlinkVisible = false;
        ScanStatusText = "";
    }

    // --- Search ---

    /// <summary>
    /// The filtered library tree shown in the UI (reflects current search query).
    /// </summary>
    public ObservableCollection<LibraryNode> FilteredLibrary
    {
        get => _filteredLibrary;
        set { _filteredLibrary = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Search text entered by the user. Triggers debounced filtering.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            StartSearchDebounce();
        }
    }

    /// <summary>
    /// Placeholder text for the search box.
    /// </summary>
    public string SearchPlaceholderText => "Search library...";

    private void StartSearchDebounce()
    {
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchDebounceTimer.Tick += (s, e) =>
        {
            _searchDebounceTimer?.Stop();
            _searchDebounceTimer = null;
            ApplyFilter();
        };
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// Apply the current search filter to the library tree.
    /// </summary>
    public void ApplyFilter()
    {
        var source = CurrentLibrary;
        var filtered = LibraryService.FilterTree(source, _searchText);
        FilteredLibrary = new ObservableCollection<LibraryNode>(filtered);
    }

    // Button text properties
    public string DeleteLibrarySourceText => "✕";
    public string RefreshLibraryText => "↻";
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { _isRefreshing = value; OnPropertyChanged(); OnPropertyChanged(nameof(RefreshLibraryText)); }
    }

    public ICommand AddHiresSourceCommand { get; }
    public ICommand AddMp3SourceCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
    public ICommand DoubleClickLibraryCommand { get; }
    public ICommand DeleteLibrarySourceCommand { get; }
    public ICommand RefreshLibraryCommand { get; }

    private void AddHiresSource()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "Select a folder with lossless audio files (FLAC, WAV, AIFF, DSD, etc.)";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ShowToastCallback?.Invoke("Scanning lossless library...", 5000);
            IsScanning = true;
            _libraryService.AddHiresSource(dialog.SelectedPath);
            _libraryService.SaveCache();
            IsScanning = false;
            OnPropertyChanged(nameof(CurrentLibrary));
            ApplyFilter();
        }
    }

    private void AddMp3Source()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "Select a folder with compressed audio files (MP3, AAC, OGG, etc.)";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ShowToastCallback?.Invoke("Scanning compressed library...", 5000);
            IsScanning = true;
            _libraryService.AddMp3Source(dialog.SelectedPath);
            _libraryService.SaveCache();
            IsScanning = false;
            OnPropertyChanged(nameof(CurrentLibrary));
            ApplyFilter();
        }
    }

    /// <summary>
    /// Double-click on a library node:
    /// - If it's a folder, add all audio files from that folder to the playlist.
    /// - If it's a file, add just that file.
    /// </summary>
    private void DoubleClickLibrary()
    {
        if (_selectedLibraryItem is LibraryNode node)
        {
            if (node.IsFolder)
            {
                // Add all audio files from this folder recursively
                AddFolderToPlaylist(node);
            }
            else if (node.CueTrack != null && node.AudioFile != null)
            {
                // Add single CUE track with its CueTrack metadata
                _playlistService.Add(node.AudioFile, node.CueTrack);
            }
            else if (node.AudioFile != null)
            {
                // Add single file
                _playlistService.Add(node.AudioFile);
            }
        }
    }

    private void AddFolderToPlaylist(LibraryNode folderNode)
    {
        foreach (var child in folderNode.Children)
        {
            if (child.IsFolder)
            {
                // If this folder is a CUE album group (has CueTrack children),
                // add its children as CUE tracks
                bool isCueAlbum = child.Children.Any(c => c.CueTrack != null);
                if (isCueAlbum)
                {
                    foreach (var cueChild in child.Children)
                    {
                        if (cueChild.AudioFile != null && cueChild.CueTrack != null)
                        {
                            _playlistService.Add(cueChild.AudioFile, cueChild.CueTrack);
                        }
                    }
                }
                else
                {
                    AddFolderToPlaylist(child);
                }
            }
            else if (child.CueTrack != null && child.AudioFile != null)
            {
                // Direct CUE track leaf node (not inside a CUE album folder)
                _playlistService.Add(child.AudioFile, child.CueTrack);
            }
            else if (child.AudioFile != null)
            {
                // Regular audio file
                _playlistService.Add(child.AudioFile);
            }
        }
    }

    private void AddSelectedToPlaylist()
    {
        if (_selectedLibraryItem is LibraryNode node && !node.IsFolder && node.AudioFile != null)
        {
            _playlistService.Add(node.AudioFile);
        }
    }

    /// <summary>
    /// Delete the selected item from the library:
    /// - If it's a file — remove just that file.
    /// - If it's a folder — check if it's a root source or a subfolder.
    ///   Root source: remove the entire source.
    ///   Subfolder: remove just that folder subtree.
    /// </summary>
    private void DeleteLibrarySource()
    {
        if (_selectedLibraryItem is not LibraryNode node) return;
        if (string.IsNullOrEmpty(node.FullPath)) return;

        var normalizedNodePath = Path.GetFullPath(node.FullPath);

        // Build confirmation message
        string message;
        string title;

        if (!node.IsFolder)
        {
            // File deletion
            title = "Remove from Library";
            message = $"Remove \"{node.Name}\" from the library?\n\nThis will remove the file from the library view only. The file will NOT be deleted from your disk.";
        }
        else
        {
            // Check if this folder is a root source
            bool isRootSource = CurrentLibrary.Any(root =>
                Path.GetFullPath(root.FullPath).Equals(normalizedNodePath, StringComparison.OrdinalIgnoreCase));

            if (isRootSource)
            {
                title = "Remove Source Folder";
                message = $"Remove the source folder \"{node.Name}\" and all its contents from the library?\n\nThis will remove the folder from the library view only. The files will NOT be deleted from your disk.";
            }
            else
            {
                title = "Remove Folder from Library";
                message = $"Remove the folder \"{node.Name}\" and all its contents from the library?\n\nThis will remove the folder from the library view only. The files will NOT be deleted from your disk.";
            }
        }

        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // ── File deletion ──
        if (!node.IsFolder)
        {
            _libraryService.RemoveFile(normalizedNodePath);
            OnPropertyChanged(nameof(CurrentLibrary));
            ApplyFilter();
            return;
        }

        // ── Folder deletion ──
        // Check if this folder is a root source (top-level in CurrentLibrary)
        foreach (var root in CurrentLibrary)
        {
            var normalizedRootPath = Path.GetFullPath(root.FullPath);
            if (normalizedRootPath.Equals(normalizedNodePath, StringComparison.OrdinalIgnoreCase))
            {
                // Selected node IS a root source — remove the entire source
                _libraryService.RemoveSource(normalizedRootPath);
                OnPropertyChanged(nameof(CurrentLibrary));
                ApplyFilter();
                return;
            }
        }

        // Not a root source — remove just this folder subtree
        _libraryService.RemoveFolder(normalizedNodePath);
        OnPropertyChanged(nameof(CurrentLibrary));
        ApplyFilter();
    }

    /// <summary>
    /// Refresh the current library (Hires or Mp3) — rescan all sources for new files.
    /// </summary>
    private void RefreshLibrary()
    {
        if (IsRefreshing) return; // Prevent double-trigger

        IsRefreshing = true;
        IsScanning = true;

        ShowToastCallback?.Invoke("Refreshing library...", 5000);

        // Process events so the UI updates before potentially long scan
        System.Windows.Forms.Application.DoEvents();

        if (_isHiresView)
        {
            _libraryService.RescanHires();
        }
        else
        {
            _libraryService.RescanMp3();
        }

        _libraryService.SaveCache();

        IsScanning = false;
        IsRefreshing = false;

        OnPropertyChanged(nameof(CurrentLibrary));
        ApplyFilter();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
