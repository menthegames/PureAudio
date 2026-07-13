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

public class ExpandedPanelViewModel : INotifyPropertyChanged
{
    private readonly LibraryService _libraryService;
    private readonly PlaylistService _playlistService;
    private readonly AudioService _audioService;
    private bool _isHiresView = true;
    private int _selectedPlaylistIndex = -1;
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

    public ExpandedPanelViewModel(LibraryService libraryService, PlaylistService playlistService, AudioService audioService)
    {
        _libraryService = libraryService;
        _playlistService = playlistService;
        _audioService = audioService;

        AddHiresSourceCommand = new RelayCommand(_ => AddHiresSource());
        AddMp3SourceCommand = new RelayCommand(_ => AddMp3Source());
        AddToPlaylistCommand = new RelayCommand(_ => AddSelectedToPlaylist());
        PlaylistUpCommand = new RelayCommand(_ => MoveUp());
        PlaylistDownCommand = new RelayCommand(_ => MoveDown());
        PlaylistDeleteCommand = new RelayCommand(_ => DeleteSelected());
        PlaylistClearCommand = new RelayCommand(_ => ClearPlaylist());
        DoubleClickLibraryCommand = new RelayCommand(_ => DoubleClickLibrary());
        DeleteLibrarySourceCommand = new RelayCommand(_ => DeleteLibrarySource());
        RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());

        // Named playlist commands
        SavePlaylistCommand = new RelayCommand(_ => SavePlaylist());
        LoadPlaylistCommand = new RelayCommand(_ => LoadPlaylist());
        RenamePlaylistCommand = new RelayCommand(_ => RenamePlaylist());
        DeletePlaylistCommand = new RelayCommand(_ => DeletePlaylist());
        ImportPlaylistCommand = new RelayCommand(_ => ImportPlaylist());


        // Sync playlist selection with currently playing track
        _audioService.TrackChanged += OnTrackChanged;

        // Initialize filtered library with full tree
        ApplyFilter();
    }

    private void OnTrackChanged(AudioFile track, CueTrack? cueTrack)
    {
        // Find the index of the currently playing track in the playlist and select it
        for (int i = 0; i < _playlistService.Items.Count; i++)
        {
            var item = _playlistService.Items[i];
            
            // For CUE tracks, match by both file path AND track number/start position
            if (cueTrack != null && item.CueTrack != null)
            {
                if (item.CueTrack.FilePath == cueTrack.FilePath &&
                    item.CueTrack.TrackNumber == cueTrack.TrackNumber &&
                    item.CueTrack.StartPosition == cueTrack.StartPosition)
                {
                    SelectedPlaylistIndex = i;
                    return;
                }
            }
            else if (item.AudioFile.FilePath == track.FilePath && item.CueTrack == null)
            {
                SelectedPlaylistIndex = i;
                return;
            }
        }
    }

    /// <summary>
    /// Current library tree — switches between Hires and MP3 views.
    /// </summary>
    public ObservableCollection<LibraryNode> CurrentLibrary => _isHiresView ? _libraryService.HiresTree : _libraryService.Mp3Tree;

    public ObservableCollection<PlaylistItem> PlaylistItems => _playlistService.Items;

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

    public int SelectedPlaylistIndex
    {
        get => _selectedPlaylistIndex;
        set { _selectedPlaylistIndex = value; OnPropertyChanged(); }
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
    public string UpText => "Up";
    public string DownText => "Down";
    public string DeleteText => "X";
    public string ClearText => "Clear";
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
    public ICommand PlaylistUpCommand { get; }
    public ICommand PlaylistDownCommand { get; }
    public ICommand PlaylistDeleteCommand { get; }
    public ICommand PlaylistClearCommand { get; }
    public ICommand DoubleClickLibraryCommand { get; }
    public ICommand DeleteLibrarySourceCommand { get; }
    public ICommand RefreshLibraryCommand { get; }

    // Named playlist commands
    public ICommand SavePlaylistCommand { get; }
    public ICommand LoadPlaylistCommand { get; }
    public ICommand RenamePlaylistCommand { get; }
    public ICommand DeletePlaylistCommand { get; }
    public ICommand ImportPlaylistCommand { get; }

    // Button text for named playlist controls
    public string SelectPlaylistText => "Sel";
    public string SavePlaylistText => "Save";
    public string LoadPlaylistText => "Load";
    public string RenamePlaylistText => "Rename";
    public string DeletePlaylistText => "Del";
    public string ImportPlaylistText => "Import";


    private void AddHiresSource()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "Select a folder with lossless audio files (FLAC, WAV, AIFF, DSD, etc.)";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
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
            OnPropertyChanged(nameof(PlaylistItems));
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
            OnPropertyChanged(nameof(PlaylistItems));
        }
    }

    private void MoveUp()
    {
        if (_selectedPlaylistIndex >= 0)
        {
            _playlistService.MoveUp(_selectedPlaylistIndex);
            SelectedPlaylistIndex = _selectedPlaylistIndex - 1;
            OnPropertyChanged(nameof(PlaylistItems));
        }
    }

    private void MoveDown()
    {
        if (_selectedPlaylistIndex >= 0)
        {
            _playlistService.MoveDown(_selectedPlaylistIndex);
            SelectedPlaylistIndex = _selectedPlaylistIndex + 1;
            OnPropertyChanged(nameof(PlaylistItems));
        }
    }

    private void DeleteSelected()
    {
        if (_selectedPlaylistIndex >= 0)
        {
            _playlistService.RemoveAt(_selectedPlaylistIndex);
            SelectedPlaylistIndex = -1;
            OnPropertyChanged(nameof(PlaylistItems));
        }
    }

    private void ClearPlaylist()
    {
        _playlistService.Clear();
        _audioService.Stop();
        SelectedPlaylistIndex = -1;
        OnPropertyChanged(nameof(PlaylistItems));
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

    /// <summary>
    /// Save the current playlist to JSON (called on window close).
    /// </summary>
    public void SaveCurrentPlaylist()
    {
        _playlistService.SaveToJson();
    }

    // ════════════════════════════════════════════════════════════════
    //  Named Playlist Operations
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save the current playlist as a named playlist.
    /// Always shows a dialog to enter/confirm the name, so the user can
    /// save a new playlist even if a named playlist is currently loaded.
    /// </summary>
    private void SavePlaylist()
    {
        try
        {
            var initialName = _playlistService.CurrentPlaylistName ?? "";
            var dialog = new InputDialog(initialText: initialName, title: "Save playlist as:");
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.InputText))
            {
                _playlistService.SaveCurrentAs(dialog.InputText);
                OnPropertyChanged(nameof(PlaylistItems));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SavePlaylist error: {ex}");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Show a dialog to select and load a saved playlist.
    /// </summary>
    private void LoadPlaylist()
    {
        try
        {
            var names = _playlistService.GetPlaylistNames();
            if (names.Count == 0)
            {
                MessageBox.Show("No saved playlists found.", "Load Playlist",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SelectPlaylistDialog(names);
            if (dialog.ShowDialog() == true && dialog.SelectedPlaylistName != null)
            {
                _playlistService.LoadPlaylist(dialog.SelectedPlaylistName);
                OnPropertyChanged(nameof(PlaylistItems));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadPlaylist error: {ex}");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Rename the currently loaded playlist.
    /// </summary>
    private void RenamePlaylist()
    {
        try
        {
            var currentName = _playlistService.CurrentPlaylistName;
            if (currentName == null) return;

            var dialog = new InputDialog(initialText: currentName, title: "Rename playlist:");
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.InputText) && dialog.InputText != currentName)
            {
                _playlistService.RenamePlaylist(currentName, dialog.InputText);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RenamePlaylist error: {ex}");
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Delete the currently loaded playlist with confirmation.
    /// </summary>
    private void DeletePlaylist()
    {
        try
        {
            var currentName = _playlistService.CurrentPlaylistName;
            if (currentName == null) return;

            var result = MessageBox.Show(
                $"Delete playlist \"{currentName}\"?",
                "Delete Playlist",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _playlistService.DeletePlaylist(currentName);
                OnPropertyChanged(nameof(PlaylistItems));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DeletePlaylist error: {ex}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Import External Playlists (m3u / pls)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Import an external playlist file (.m3u, .m3u8, .pls) and add its tracks
    /// to the current playlist.
    /// </summary>
    private void ImportPlaylist()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Playlist",
                Filter = "Playlist files (*.m3u;*.m3u8;*.pls)|*.m3u;*.m3u8;*.pls|All files (*.*)|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true) return;

            var filePath = dialog.FileName;
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var importedFiles = new List<string>();

            if (extension == ".pls")
            {
                importedFiles = ParsePls(filePath);
            }
            else // .m3u or .m3u8
            {
                importedFiles = ParseM3u(filePath);
            }

            if (importedFiles.Count == 0)
            {
                MessageBox.Show("No valid audio files found in the playlist.",
                    "Import Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Add found files to the playlist
            int addedCount = 0;
            foreach (var path in importedFiles)
            {
                if (File.Exists(path))
                {
                    var audioFile = MetadataService.ReadMetadata(path);
                    _playlistService.Add(audioFile);
                    addedCount++;
                }
            }

            OnPropertyChanged(nameof(PlaylistItems));

            MessageBox.Show($"Imported {addedCount} of {importedFiles.Count} tracks.",
                "Import Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ImportPlaylist error: {ex}");
            MessageBox.Show($"Error importing playlist: {ex.Message}",
                "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Parse an M3U/M3U8 file. Returns a list of file paths.
    /// Ignores empty lines and lines starting with '#'.
    /// </summary>
    private static List<string> ParseM3u(string filePath)
    {
        var files = new List<string>();
        var baseDir = Path.GetDirectoryName(filePath) ?? "";

        foreach (var line in File.ReadAllLines(filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            // Resolve relative paths against the playlist file's directory
            var fullPath = Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.GetFullPath(Path.Combine(baseDir, trimmed));

            files.Add(fullPath);
        }

        return files;
    }

    /// <summary>
    /// Parse a PLS file. Returns a list of file paths.
    /// Extracts paths from File1=..., File2=..., etc. entries.
    /// </summary>
    private static List<string> ParsePls(string filePath)
    {
        var files = new List<string>();
        var baseDir = Path.GetDirectoryName(filePath) ?? "";

        foreach (var line in File.ReadAllLines(filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Match lines like "File1=C:\path\to\file.flac" or "File1=relative\path.mp3"
            if (trimmed.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex < 0) continue;

                var value = trimmed.Substring(eqIndex + 1).Trim();
                if (string.IsNullOrEmpty(value)) continue;

                var fullPath = Path.IsPathRooted(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(baseDir, value));

                files.Add(fullPath);
            }
        }

        return files;
    }


    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


