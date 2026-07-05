using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using PureAudio.Models;

namespace PureAudio.Services;

public class LibraryService
{
    private const string SourcesFileName = "sources.json";

    // Supported audio extensions
    // NOTE: Only formats that can actually be played are included.
    // FLAC and WAV are fully supported with bit-perfect playback.
    // AIFF/AIF are supported via NAudio's AudioFileReader (Shared mode only).
    // DSD (DSF/DFF), APE, and WV are NOT supported — they require special decoders.
    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif"
    };

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".ogg", ".wma", ".opus"
    };

    // Source folders (persisted)
    public List<string> HiresSources { get; private set; } = new();
    public List<string> Mp3Sources { get; private set; } = new();

    // Excluded paths (files and folders removed by user, persisted)
    private readonly HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);

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

        // Skip this folder entirely if it's in the excluded paths
        var normalizedCurrentPath = Path.GetFullPath(currentPath);
        if (_excludedPaths.Contains(normalizedCurrentPath))
            return null;

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

            // Skip if already indexed or excluded by user
            if (!_allFilePaths.Add(normalizedPath)) continue;
            if (_excludedPaths.Contains(normalizedPath)) continue;

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
    /// Rescan only Hires (lossless) sources. Preserves existing files and adds new ones.
    /// </summary>
    public void RescanHires()
    {
        // Collect existing file paths to preserve them
        var existingFiles = new HashSet<string>(_allFilePaths.Where(f =>
            HiresFiles.Any(h => Path.GetFullPath(h.AudioFile.FilePath).Equals(f, StringComparison.OrdinalIgnoreCase))),
            StringComparer.OrdinalIgnoreCase);

        // Clear only Hires trees and files (keep Mp3 intact)
        HiresTree.Clear();
        HiresFiles.Clear();

        // Remove Hires files from the global path set (they'll be re-added during scan)
        _allFilePaths.RemoveWhere(f => existingFiles.Contains(f));

        // Re-scan all Hires sources
        foreach (var source in HiresSources)
        {
            if (Directory.Exists(source))
                ScanFolderToTree(source, HiresTree, LosslessExtensions, HiresFiles);
        }
    }

    /// <summary>
    /// Rescan only Mp3 (compressed) sources. Preserves existing files and adds new ones.
    /// </summary>
    public void RescanMp3()
    {
        // Collect existing file paths to preserve them
        var existingFiles = new HashSet<string>(_allFilePaths.Where(f =>
            Mp3Files.Any(m => Path.GetFullPath(m.AudioFile.FilePath).Equals(f, StringComparison.OrdinalIgnoreCase))),
            StringComparer.OrdinalIgnoreCase);

        // Clear only Mp3 trees and files (keep Hires intact)
        Mp3Tree.Clear();
        Mp3Files.Clear();

        // Remove Mp3 files from the global path set (they'll be re-added during scan)
        _allFilePaths.RemoveWhere(f => existingFiles.Contains(f));

        // Re-scan all Mp3 sources
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

    /// <summary>
    /// Remove a single audio file from the library (both tree and flat list).
    /// The file will be excluded from future rescans.
    /// </summary>
    public void RemoveFile(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);

        // Remove from flat lists
        var hiresItem = HiresFiles.FirstOrDefault(f => Path.GetFullPath(f.AudioFile.FilePath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (hiresItem != null) HiresFiles.Remove(hiresItem);

        var mp3Item = Mp3Files.FirstOrDefault(f => Path.GetFullPath(f.AudioFile.FilePath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (mp3Item != null) Mp3Files.Remove(mp3Item);

        // Remove from tree
        RemoveNodeFromTree(HiresTree, normalized);
        RemoveNodeFromTree(Mp3Tree, normalized);

        // Remove from path set
        _allFilePaths.Remove(normalized);

        // Add to excluded paths so it won't reappear on rescan
        _excludedPaths.Add(normalized);
        SaveSources();
    }

    /// <summary>
    /// Remove a folder (and all its contents) from the library tree.
    /// Does NOT remove the root source — only removes the subtree.
    /// The folder will be excluded from future rescans.
    /// </summary>
    public void RemoveFolder(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath);

        // Collect all file paths under this folder to remove from flat lists and path set
        var filesToRemove = _allFilePaths.Where(f => f.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var f in filesToRemove)
        {
            var hiresItem = HiresFiles.FirstOrDefault(h => Path.GetFullPath(h.AudioFile.FilePath).Equals(f, StringComparison.OrdinalIgnoreCase));
            if (hiresItem != null) HiresFiles.Remove(hiresItem);

            var mp3Item = Mp3Files.FirstOrDefault(m => Path.GetFullPath(m.AudioFile.FilePath).Equals(f, StringComparison.OrdinalIgnoreCase));
            if (mp3Item != null) Mp3Files.Remove(mp3Item);

            _allFilePaths.Remove(f);
        }

        // Remove the folder node from tree
        RemoveNodeFromTree(HiresTree, normalized);
        RemoveNodeFromTree(Mp3Tree, normalized);

        // Add to excluded paths so it won't reappear on rescan
        _excludedPaths.Add(normalized);
        SaveSources();
    }

    /// <summary>
    /// Recursively find and remove a node by its FullPath from the tree.
    /// </summary>
    private bool RemoveNodeFromTree(ObservableCollection<LibraryNode> tree, string normalizedPath)
    {
        for (int i = tree.Count - 1; i >= 0; i--)
        {
            var node = tree[i];
            var nodePath = Path.GetFullPath(node.FullPath);

            if (nodePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                tree.RemoveAt(i);
                return true;
            }

            if (node.IsFolder)
            {
                if (RemoveNodeFromTree(node.Children, normalizedPath))
                {
                    // If folder became empty after removal, remove it too
                    if (node.Children.Count == 0)
                    {
                        tree.RemoveAt(i);
                    }
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Remove a root source folder (either Hires or Mp3) and rebuild the tree.
    /// Also clears any excluded paths that belong to this source.
    /// </summary>
    public void RemoveSource(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath);

        bool removed = false;
        if (HiresSources.RemoveAll(s => Path.GetFullPath(s).Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            removed = true;
        }
        if (Mp3Sources.RemoveAll(s => Path.GetFullPath(s).Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            removed = true;
        }

        if (removed)
        {
            // Remove any excluded paths that belong to this source (no longer needed)
            _excludedPaths.RemoveWhere(p =>
                p.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            // Rebuild everything from scratch
            RescanAll();
            SaveSources();
        }
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
        _excludedPaths.Clear();
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
                Mp3Sources = Mp3Sources,
                ExcludedPaths = _excludedPaths.ToList()
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
                    if (data.ExcludedPaths != null)
                    {
                        foreach (var p in data.ExcludedPaths)
                            _excludedPaths.Add(p);
                    }
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
        public List<string>? ExcludedPaths { get; set; }
    }
}

public class LibraryItem
{
    public AudioFile AudioFile { get; set; } = new();
    public string FolderPath { get; set; } = "";
    public override string ToString() => $"{AudioFile.Artist} - {AudioFile.Title}";
}
