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

public class PlaylistPanelViewModel : INotifyPropertyChanged
{
    private readonly PlaylistService _playlistService;
    private readonly AudioService _audioService;
    private int _selectedPlaylistIndex = -1;

    public PlaylistPanelViewModel(PlaylistService playlistService, AudioService audioService)
    {
        _playlistService = playlistService;
        _audioService = audioService;

        PlaylistUpCommand = new RelayCommand(_ => MoveUp());
        PlaylistDownCommand = new RelayCommand(_ => MoveDown());
        PlaylistDeleteCommand = new RelayCommand(_ => DeleteSelected());
        PlaylistClearCommand = new RelayCommand(_ => ClearPlaylist());

        // Named playlist commands
        SavePlaylistCommand = new RelayCommand(_ => SavePlaylist());
        LoadPlaylistCommand = new RelayCommand(_ => LoadPlaylist());
        RenamePlaylistCommand = new RelayCommand(_ => RenamePlaylist());
        DeletePlaylistCommand = new RelayCommand(_ => DeletePlaylist());
        ImportPlaylistCommand = new RelayCommand(_ => ImportPlaylist());

        // Sync playlist selection with currently playing track
        _audioService.TrackChanged += OnTrackChanged;
    }

    public ObservableCollection<PlaylistItem> PlaylistItems => _playlistService.Items;

    public int SelectedPlaylistIndex
    {
        get => _selectedPlaylistIndex;
        set { _selectedPlaylistIndex = value; OnPropertyChanged(); }
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

    // Button text properties
    public string UpText => "Up";
    public string DownText => "Down";
    public string DeleteText => "X";
    public string ClearText => "Clear";

    // Named playlist button texts
    public string SelectPlaylistText => "Sel";
    public string SavePlaylistText => "Save";
    public string LoadPlaylistText => "Load";
    public string RenamePlaylistText => "Rename";
    public string DeletePlaylistText => "Del";
    public string ImportPlaylistText => "Import";

    public ICommand PlaylistUpCommand { get; }
    public ICommand PlaylistDownCommand { get; }
    public ICommand PlaylistDeleteCommand { get; }
    public ICommand PlaylistClearCommand { get; }
    public ICommand SavePlaylistCommand { get; }
    public ICommand LoadPlaylistCommand { get; }
    public ICommand RenamePlaylistCommand { get; }
    public ICommand DeletePlaylistCommand { get; }
    public ICommand ImportPlaylistCommand { get; }

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
