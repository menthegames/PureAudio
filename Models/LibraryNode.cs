using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PureAudio.Models;

/// <summary>
/// Represents a node in the library tree — either a folder or a file.
/// </summary>
public class LibraryNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFolder { get; set; }
    public AudioFile? AudioFile { get; set; }
    
    /// <summary>
    /// If this node represents a CUE sheet track, contains the CUE track metadata
    /// (start/end positions within the physical audio file).
    /// </summary>
    public CueTrack? CueTrack { get; set; }
    
    public ObservableCollection<LibraryNode> Children { get; set; } = new();


    /// <summary>
    /// Whether the node is expanded in the TreeView.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
