using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using PureAudio.Models;

namespace PureAudio.Services;

public class LibraryService
{
    private const string SourcesFileName = "sources.json";

    // Supported audio extensions
    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif", ".dsf", ".dff", ".ape", ".wv"
    };

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".ogg", ".wma", ".opus"
    };

    // Source folders (persisted)
    public List<string> HiresSources { get; private set; } = new();
    public List<string> Mp3Sources { get; private set; } = new();

    // Library trees
    public ObservableCollection<LibraryNode> HiresTree { get; } = new();
    public ObservableCollection<LibraryNode> Mp3Tree { get; } = new();

    // Flat lists for backward compatibility (used by playlist add)
    public ObservableCollection<LibraryItem> HiresFiles { get; } = new();
    public ObservableCollection<LibraryItem> Mp3Files { get; } = new();

    private readonly HashSet<string> _allFilePaths = new(StringComparer.OrdinalIgnoreCase);

    public LibraryService()
    {
        LoadSources();
    }

    /// <summary>
    /// Add a folder as a Hires (lossless) source. Skips duplicates.
    /// </summary>
    public void AddHiresSource(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        var normalized = Path.GetFullPath(folderPath);

        // Check if this exact folder is already a source
        if (HiresSources.Any(s => Path.GetFullPath(s).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        // Check if any file from this folder is already indexed (duplicate check)
        var newFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => LosslessExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (newFiles.Count == 0) return;

        // Check if ALL files are already indexed — if so, skip
        if (newFiles.All(f => _allFilePaths.Contains(Path.GetFullPath(f))))
            return;

        HiresSources.Add(normalized);
        ScanFolderToTree(folderPath, HiresTree, LosslessExtensions, HiresFiles);
        SaveSources();
    }

    /// <summary>
    /// Add a folder as an MP3 (compressed) source. Skips duplicates.
    /// </summary>
    public void AddMp3Source(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        var normalized = Path.GetFullPath(folderPath);

        if (Mp3Sources.Any(s => Path.GetFullPath(s).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        var newFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => CompressedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (newFiles.Count == 0) return;

        if (newFiles.All(f => _allFilePaths.Contains(Path.GetFullPath(f))))
            return;

        Mp3Sources.Add(normalized);
        ScanFolderToTree(folderPath, Mp3Tree, CompressedExtensions, Mp3Files);
        SaveSources();
    }

    /// <summary>
    /// Scan a folder and build a tree of LibraryNodes, also populate the flat list.
    /// </summary>
    private void ScanFolderToTree(string rootPath, ObservableCollection<LibraryNode> tree,
        HashSet<string> allowedExtensions, ObservableCollection<LibraryItem> flatList)
    {
        var rootNode = BuildTreeRecursive(rootPath, rootPath, allowedExtensions, flatList);
        if (rootNode != null)
        {
            rootNode.IsExpanded = true; // Expand root node by default
            tree.Add(rootNode);
        }
    }

    private LibraryNode? BuildTreeRecursive(string rootPath, string currentPath,
        HashSet<string> allowedExtensions, ObservableCollection<LibraryItem> flatList)
    {
        var dirInfo = new DirectoryInfo(currentPath);
        if (!dirInfo.Exists) return null;

        var node = new LibraryNode
        {
            Name = dirInfo.Name,
            FullPath = dirInfo.FullName,
            IsFolder = true
        };

        // Add subdirectories
        foreach (var subDir in dirInfo.GetDirectories())
        {
            var child = BuildTreeRecursive(rootPath, subDir.FullName, allowedExtensions, flatList);
            if (child != null)
            {
                node.Children.Add(child);
            }
        }

        // Add audio files
        foreach (var file in dirInfo.GetFiles())
        {
            if (!allowedExtensions.Contains(file.Extension)) continue;

            var filePath = file.FullName;
            var normalizedPath = Path.GetFullPath(filePath);

            // Skip if already indexed
            if (!_allFilePaths.Add(normalizedPath)) continue;

            var audioFile = MetadataService.ReadMetadata(filePath);
            var fileNode = new LibraryNode
            {
                Name = $"{audioFile.Artist} - {audioFile.Title}",
                FullPath = filePath,
                IsFolder = false,
                AudioFile = audioFile
            };
            node.Children.Add(fileNode);

            // Also add to flat list
            flatList.Add(new LibraryItem
            {
                AudioFile = audioFile,
                FolderPath = rootPath
            });
        }

        // Only return this node if it has children (files or subfolders with files)
        return node.Children.Count > 0 ? node : null;
    }

    /// <summary>
    /// Rescan all sources (called on startup after loading sources).
    /// </summary>
    public void RescanAll()
    {
        HiresTree.Clear();
        Mp3Tree.Clear();
        HiresFiles.Clear();
        Mp3Files.Clear();
        _allFilePaths.Clear();

        foreach (var source in HiresSources)
        {
            if (Directory.Exists(source))
                ScanFolderToTree(source, HiresTree, LosslessExtensions, HiresFiles);
        }

        foreach (var source in Mp3Sources)
        {
            if (Directory.Exists(source))
                ScanFolderToTree(source, Mp3Tree, CompressedExtensions, Mp3Files);
        }
    }

    /// <summary>
    /// Recursively filter a library tree by query string.
    /// Returns a new list of nodes that match (or contain children that match).
    /// Matching is case-insensitive against Title, Artist, Album, and node Name.
    /// </summary>
    public static List<LibraryNode> FilterTree(IEnumerable<LibraryNode> source, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // Return a deep copy of the full tree
            return source.Select(CloneNode).ToList();
        }

        var lowerQuery = query.Trim().ToLowerInvariant();
        var result = new List<LibraryNode>();

        foreach (var node in source)
        {
            var filtered = FilterNode(node, lowerQuery);
            if (filtered != null)
                result.Add(filtered);
        }

        return result;
    }

    private static LibraryNode? FilterNode(LibraryNode node, string lowerQuery)
    {
        if (!node.IsFolder)
        {
            // Leaf node (audio file): check if it matches the query
            if (MatchesQuery(node, lowerQuery))
                return CloneNode(node);
            return null;
        }

        // Folder node: recursively filter children
        var filteredChildren = new List<LibraryNode>();
        foreach (var child in node.Children)
        {
            var filteredChild = FilterNode(child, lowerQuery);
            if (filteredChild != null)
                filteredChildren.Add(filteredChild);
        }

        // Also check if the folder name itself matches
        bool folderMatches = node.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase);

        if (filteredChildren.Count > 0 || folderMatches)
        {
            var clone = CloneNode(node);
            clone.Children = new ObservableCollection<LibraryNode>(filteredChildren);
            // Auto-expand folders when search is active
            clone.IsExpanded = true;
            return clone;
        }

        return null;
    }

    private static bool MatchesQuery(LibraryNode node, string lowerQuery)
    {
        if (node.AudioFile == null)
            return false;

        return
            (!string.IsNullOrEmpty(node.AudioFile.Title) && node.AudioFile.Title.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(node.AudioFile.Artist) && node.AudioFile.Artist.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(node.AudioFile.Album) && node.AudioFile.Album.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(node.Name) && node.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static LibraryNode CloneNode(LibraryNode original)
    {
        var clone = new LibraryNode
        {
            Name = original.Name,
            FullPath = original.FullPath,
            IsFolder = original.IsFolder,
            AudioFile = original.AudioFile,
            IsExpanded = original.IsExpanded,
            Children = new ObservableCollection<LibraryNode>(original.Children.Select(CloneNode))
        };
        return clone;
    }

    public void Clear()
    {
        HiresSources.Clear();
        Mp3Sources.Clear();
        HiresTree.Clear();
        Mp3Tree.Clear();
        HiresFiles.Clear();
        Mp3Files.Clear();
        _allFilePaths.Clear();
        SaveSources();
    }

    // --- JSON Persistence ---

    private string GetSourcesFilePath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PureAudio");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, SourcesFileName);
    }

    private void SaveSources()
    {
        try
        {
            var data = new SourcesData
            {
                HiresSources = HiresSources,
                Mp3Sources = Mp3Sources
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetSourcesFilePath(), json);
        }
        catch
        {
            // Silently fail — persistence is non-critical
        }
    }

    private void LoadSources()
    {
        try
        {
            var path = GetSourcesFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<SourcesData>(json);
                if (data != null)
                {
                    HiresSources = data.HiresSources ?? new List<string>();
                    Mp3Sources = data.Mp3Sources ?? new List<string>();
                }
            }
        }
        catch
        {
            // Start fresh if file is corrupted
            HiresSources = new List<string>();
            Mp3Sources = new List<string>();
        }
    }

    private class SourcesData
    {
        public List<string> HiresSources { get; set; } = new();
        public List<string> Mp3Sources { get; set; } = new();
    }
}

public class LibraryItem
{
    public AudioFile AudioFile { get; set; } = new();
    public string FolderPath { get; set; } = "";
    public override string ToString() => $"{AudioFile.Artist} - {AudioFile.Title}";
}
