using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PureAudio.Models;
using PureAudio.Services;

namespace PureAudio.ViewModels;

/// <summary>
/// Facade that combines LibraryPanelViewModel and PlaylistPanelViewModel
/// into a single property for easy binding from MainViewModel/MainWindow.
/// </summary>
public class ExpandedPanelViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// Callback to show a toast notification via MainViewModel.
    /// Set by MainViewModel after construction. Delegated to LibraryPanel.
    /// </summary>
    public Action<string, int>? ShowToastCallback
    {
        get => LibraryPanel.ShowToastCallback;
        set => LibraryPanel.ShowToastCallback = value;
    }

    public LibraryPanelViewModel LibraryPanel { get; }
    public PlaylistPanelViewModel PlaylistPanel { get; }

    public ExpandedPanelViewModel(LibraryService libraryService, PlaylistService playlistService, AudioService audioService)
    {
        LibraryPanel = new LibraryPanelViewModel(libraryService, playlistService);
        PlaylistPanel = new PlaylistPanelViewModel(playlistService, audioService);

        // Forward PropertyChanged notifications from sub-ViewModels so that
        // XAML bindings bound to ExpandedPanel facade properties are updated.
        LibraryPanel.PropertyChanged += OnLibraryPanelPropertyChanged;
        PlaylistPanel.PropertyChanged += OnPlaylistPanelPropertyChanged;
    }

    private void OnLibraryPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-raise the property change on this ExpandedPanel so XAML bindings
        // that reference ExpandedPanel.SomeProperty get notified.
        OnPropertyChanged(e.PropertyName);
    }

    private void OnPlaylistPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    // ════════════════════════════════════════════════════════════════
    //  Facade properties — delegate to LibraryPanel
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<LibraryNode> CurrentLibrary => LibraryPanel.CurrentLibrary;
    public bool IsHiresView
    {
        get => LibraryPanel.IsHiresView;
        set => LibraryPanel.IsHiresView = value;
    }
    public object? SelectedLibraryItem
    {
        get => LibraryPanel.SelectedLibraryItem;
        set => LibraryPanel.SelectedLibraryItem = value;
    }
    public string LibraryTitle => LibraryPanel.LibraryTitle;
    public bool IsScanning
    {
        get => LibraryPanel.IsScanning;
        set => LibraryPanel.IsScanning = value;
    }
    public bool ScanBlinkVisible
    {
        get => LibraryPanel.ScanBlinkVisible;
        set => LibraryPanel.ScanBlinkVisible = value;
    }
    public string ScanStatusText
    {
        get => LibraryPanel.ScanStatusText;
        set => LibraryPanel.ScanStatusText = value;
    }
    public ObservableCollection<LibraryNode> FilteredLibrary => LibraryPanel.FilteredLibrary;
    public string SearchText
    {
        get => LibraryPanel.SearchText;
        set => LibraryPanel.SearchText = value;
    }
    public string SearchPlaceholderText => LibraryPanel.SearchPlaceholderText;
    public string DeleteLibrarySourceText => LibraryPanel.DeleteLibrarySourceText;
    public string RefreshLibraryText => LibraryPanel.RefreshLibraryText;
    public bool IsRefreshing
    {
        get => LibraryPanel.IsRefreshing;
        set => LibraryPanel.IsRefreshing = value;
    }

    // Library commands
    public ICommand AddHiresSourceCommand => LibraryPanel.AddHiresSourceCommand;
    public ICommand AddMp3SourceCommand => LibraryPanel.AddMp3SourceCommand;
    public ICommand AddToPlaylistCommand => LibraryPanel.AddToPlaylistCommand;
    public ICommand DoubleClickLibraryCommand => LibraryPanel.DoubleClickLibraryCommand;
    public ICommand DeleteLibrarySourceCommand => LibraryPanel.DeleteLibrarySourceCommand;
    public ICommand RefreshLibraryCommand => LibraryPanel.RefreshLibraryCommand;

    // Library methods
    public void ApplyFilter() => LibraryPanel.ApplyFilter();

    // ════════════════════════════════════════════════════════════════
    //  Facade properties — delegate to PlaylistPanel
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<PlaylistItem> PlaylistItems => PlaylistPanel.PlaylistItems;
    public int SelectedPlaylistIndex
    {
        get => PlaylistPanel.SelectedPlaylistIndex;
        set => PlaylistPanel.SelectedPlaylistIndex = value;
    }
    public string UpText => PlaylistPanel.UpText;
    public string DownText => PlaylistPanel.DownText;
    public string DeleteText => PlaylistPanel.DeleteText;
    public string ClearText => PlaylistPanel.ClearText;
    public string SelectPlaylistText => PlaylistPanel.SelectPlaylistText;
    public string SavePlaylistText => PlaylistPanel.SavePlaylistText;
    public string LoadPlaylistText => PlaylistPanel.LoadPlaylistText;
    public string RenamePlaylistText => PlaylistPanel.RenamePlaylistText;
    public string DeletePlaylistText => PlaylistPanel.DeletePlaylistText;
    public string ImportPlaylistText => PlaylistPanel.ImportPlaylistText;

    // Playlist commands
    public ICommand PlaylistUpCommand => PlaylistPanel.PlaylistUpCommand;
    public ICommand PlaylistDownCommand => PlaylistPanel.PlaylistDownCommand;
    public ICommand PlaylistDeleteCommand => PlaylistPanel.PlaylistDeleteCommand;
    public ICommand PlaylistClearCommand => PlaylistPanel.PlaylistClearCommand;
    public ICommand SavePlaylistCommand => PlaylistPanel.SavePlaylistCommand;
    public ICommand LoadPlaylistCommand => PlaylistPanel.LoadPlaylistCommand;
    public ICommand RenamePlaylistCommand => PlaylistPanel.RenamePlaylistCommand;
    public ICommand DeletePlaylistCommand => PlaylistPanel.DeletePlaylistCommand;
    public ICommand ImportPlaylistCommand => PlaylistPanel.ImportPlaylistCommand;

    // Playlist methods
    public void SaveCurrentPlaylist() => PlaylistPanel.SaveCurrentPlaylist();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
