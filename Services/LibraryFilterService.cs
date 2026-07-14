using System.Collections.ObjectModel;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Handles filtering of the library tree based on a search query.
/// Extracted from LibraryService to follow Single Responsibility Principle.
/// </summary>
public static class LibraryFilterService
{
    /// <summary>
    /// Filter a library tree (list of root nodes) by the given query.
    /// Returns a new tree containing only nodes that match the query
    /// (or have children that match).
    /// </summary>
    public static List<LibraryNode> FilterTree(IList<LibraryNode> source, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return source.ToList();

        var result = new List<LibraryNode>();
        foreach (var node in source)
        {
            var filtered = FilterNode(node, query);
            if (filtered != null)
                result.Add(filtered);
        }
        return result;
    }

    /// <summary>
    /// Recursively filter a single node. Returns null if the node and all its children
    /// do not match the query.
    /// </summary>
    private static LibraryNode? FilterNode(LibraryNode node, string query)
    {
        // Check if this node matches the query
        if (MatchesQuery(node, query))
        {
            // If it's a folder, include all children (they match by parent)
            if (node.IsFolder)
            {
                return new LibraryNode
                {
                    Name = node.Name,
                    FullPath = node.FullPath,
                    IsFolder = true,
                    Children = new ObservableCollection<LibraryNode>(node.Children),
                    AudioFile = node.AudioFile,
                    CueTrack = node.CueTrack
                };
            }
            return CloneNode(node);
        }

        if (node.Children.Count > 0)
        {
            // Check children recursively
            var matchingChildren = new List<LibraryNode>();
            foreach (var child in node.Children)
            {
                var filteredChild = FilterNode(child, query);
                if (filteredChild != null)
                    matchingChildren.Add(filteredChild);
            }

            if (matchingChildren.Count > 0)
            {
                // Create a folder node with only matching children
                return new LibraryNode
                {
                    Name = node.Name,
                    FullPath = node.FullPath,
                    IsFolder = true,
                    Children = new ObservableCollection<LibraryNode>(matchingChildren),
                    AudioFile = node.AudioFile,
                    CueTrack = node.CueTrack
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Check if a node matches the search query.
    /// Matches against Name, FullPath, and audio file metadata.
    /// </summary>
    private static bool MatchesQuery(LibraryNode node, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var q = query.Trim().ToLowerInvariant();

        // Search by name
        if (node.Name.ToLowerInvariant().Contains(q))
            return true;

        // Search by full path
        if (node.FullPath.ToLowerInvariant().Contains(q))
            return true;

        // Search by audio file metadata
        if (node.AudioFile != null)
        {
            if (!string.IsNullOrEmpty(node.AudioFile.Title) &&
                node.AudioFile.Title.ToLowerInvariant().Contains(q))
                return true;

            if (!string.IsNullOrEmpty(node.AudioFile.Artist) &&
                node.AudioFile.Artist.ToLowerInvariant().Contains(q))
                return true;

            if (!string.IsNullOrEmpty(node.AudioFile.Album) &&
                node.AudioFile.Album.ToLowerInvariant().Contains(q))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Create a shallow clone of a LibraryNode (without children).
    /// </summary>
    private static LibraryNode CloneNode(LibraryNode node)
    {
        return new LibraryNode
        {
            Name = node.Name,
            FullPath = node.FullPath,
            IsFolder = node.IsFolder,
            Children = node.Children,
            AudioFile = node.AudioFile,
            CueTrack = node.CueTrack
        };
    }
}
