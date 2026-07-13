using System.IO;
using System.Text.Json;
using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// Responsible for caching the library tree and file metadata to disk.
/// Separated from LibraryService to follow Single Responsibility Principle.
/// </summary>
public class LibraryCacheService
{
    private const string CacheFileName = "library_cache.json";

    /// <summary>
    /// Get the full path to the cache file.
    /// </summary>
    private string GetCacheFilePath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PureAudio");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, CacheFileName);
    }

    /// <summary>
    /// Save the current library tree and file metadata to a cache file.
    /// Called after RescanAll() and after adding a new source.
    /// </summary>
    public void SaveCache(LibraryService library)
    {
        try
        {
            var cache = new LibraryCacheData
            {
                Version = "1.1",
                LastUpdated = DateTime.UtcNow,
                Files = new List<CachedFileEntry>(),
                Tree = new List<CachedTreeNode>()
            };

            // Build flat file list from all files
            var fileDict = new Dictionary<string, AudioFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in library.HiresFiles)
            {
                var key = Path.GetFullPath(item.AudioFile.FilePath);
                if (!fileDict.ContainsKey(key))
                    fileDict[key] = item.AudioFile;
            }
            foreach (var item in library.Mp3Files)
            {
                var key = Path.GetFullPath(item.AudioFile.FilePath);
                if (!fileDict.ContainsKey(key))
                    fileDict[key] = item.AudioFile;
            }

            foreach (var kvp in fileDict)
            {
                cache.Files.Add(new CachedFileEntry
                {
                    FilePath = kvp.Key,
                    Artist = kvp.Value.Artist,
                    Title = kvp.Value.Title,
                    Album = kvp.Value.Album,
                    DurationSeconds = kvp.Value.Duration.TotalSeconds,
                    SampleRate = kvp.Value.SampleRate,
                    BitsPerSample = kvp.Value.BitsPerSample,
                    Bitrate = kvp.Value.Bitrate,
                    CoverPath = kvp.Value.CoverPath
                });
            }

            // Convert tree structure
            foreach (var node in library.HiresTree)
                cache.Tree.Add(ConvertTreeToCache(node));
            foreach (var node in library.Mp3Tree)
                cache.Tree.Add(ConvertTreeToCache(node));

            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetCacheFilePath(), json);
        }
        catch
        {
            // Silently fail — cache is non-critical
        }
    }

    /// <summary>
    /// Try to load the library from cache. Returns true if cache was loaded successfully.
    /// </summary>
    public bool TryLoadCache(LibraryService library)
    {
        try
        {
            var path = GetCacheFilePath();
            if (!File.Exists(path))
                return false;

            // Check if sources.json is newer than cache — if so, cache is stale
            var sourcesPath = library.GetSourcesFilePath();
            if (File.Exists(sourcesPath))
            {
                var sourcesTime = File.GetLastWriteTimeUtc(sourcesPath);
                var cacheTime = File.GetLastWriteTimeUtc(path);
                if (sourcesTime > cacheTime)
                    return false; // sources changed, cache is invalid
            }

            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<LibraryCacheData>(json);
            if (cache == null || cache.Files == null || cache.Tree == null)
                return false;

            // Check cache version — if version mismatch, invalidate cache
            const string expectedVersion = "1.1";
            if (cache.Version != expectedVersion)
            {
                Logger.Log($"Cache version mismatch: expected {expectedVersion}, got {cache.Version}. Rebuilding cache.");
                return false;
            }

            // Check if any .cue file referenced in the cache has been modified
            // since the cache was saved. If so, cache is stale.
            var cueFilesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectCueFilePaths(cache.Tree, cueFilesToCheck);
            foreach (var cueFilePath in cueFilesToCheck)
            {
                if (File.Exists(cueFilePath))
                {
                    var cueFileTime = File.GetLastWriteTimeUtc(cueFilePath);
                    if (cueFileTime > cache.LastUpdated)
                    {
                        Logger.Log($"CUE file modified since cache: {cueFilePath}, cache invalidated");
                        return false;
                    }
                }
            }

            // Build a lookup dictionary for cached file entries
            var fileLookup = new Dictionary<string, CachedFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in cache.Files)
            {
                var key = Path.GetFullPath(f.FilePath);
                if (!fileLookup.ContainsKey(key))
                    fileLookup[key] = f;
            }

            // Rebuild trees from cached tree structure
            library.HiresTree.Clear();
            library.Mp3Tree.Clear();
            library.HiresFiles.Clear();
            library.Mp3Files.Clear();
            library.ClearAllFilePaths();

            // We need to know which tree is Hires vs Mp3.
            // Strategy: check if the root folder path matches any HiresSource or Mp3Source.
            foreach (var cachedNode in cache.Tree)
            {
                var rootPath = Path.GetFullPath(cachedNode.FullPath);
                bool isHires = library.HiresSources.Any(s =>
                    Path.GetFullPath(s).Equals(rootPath, StringComparison.OrdinalIgnoreCase));
                bool isMp3 = library.Mp3Sources.Any(s =>
                    Path.GetFullPath(s).Equals(rootPath, StringComparison.OrdinalIgnoreCase));

                var targetTree = isHires ? library.HiresTree : (isMp3 ? library.Mp3Tree : null);
                if (targetTree == null)
                    continue;

                var restored = BuildTreeFromCache(cachedNode, fileLookup, rootPath, library);
                if (restored != null)
                {
                    restored.IsExpanded = true;
                    targetTree.Add(restored);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convert a CachedTreeNode (and its children) back into LibraryNode tree,
    /// and populate the flat file list.
    /// </summary>
    private LibraryNode? BuildTreeFromCache(CachedTreeNode cached, Dictionary<string, CachedFileEntry> fileLookup, string rootPath, LibraryService library)
    {
        if (cached.IsFolder)
        {
            var node = new LibraryNode
            {
                Name = cached.Name,
                FullPath = cached.FullPath,
                IsFolder = true
            };

            foreach (var child in cached.Children)
            {
                var restored = BuildTreeFromCache(child, fileLookup, rootPath, library);
                if (restored != null)
                    node.Children.Add(restored);
            }

            return node.Children.Count > 0 ? node : null;
        }
        else
        {
            // File node
            if (cached.FilePath == null)
                return null;

            var normalizedPath = Path.GetFullPath(cached.FilePath);

            // Check if this is a CUE track node
            if (cached.CueTrack != null)
            {
                var ct = cached.CueTrack;
                var cueTrack = new CueTrack
                {
                    FilePath = ct.FilePath,
                    Artist = ct.Artist,
                    Title = ct.Title,
                    Album = ct.Album,
                    TrackNumber = ct.TrackNumber,
                    StartPosition = TimeSpan.FromSeconds(ct.StartPositionSeconds),
                    EndPosition = TimeSpan.FromSeconds(ct.EndPositionSeconds),
                    CueFilePath = ct.CueFilePath
                };

                // Use a unique key for CUE tracks to avoid collisions
                var cueKey = $"{normalizedPath}::cue::{ct.TrackNumber}";
                if (!library.AddFilePath(cueKey))
                    return null; // already added this CUE track

                var audioFile = new AudioFile
                {
                    FilePath = cueTrack.FilePath,
                    Artist = cueTrack.Artist,
                    Title = cueTrack.Title,
                    Album = cueTrack.Album,
                    Duration = cueTrack.Duration
                };

                var fileNode = new LibraryNode
                {
                    Name = $"{cueTrack.Artist} - {cueTrack.Title}",
                    FullPath = cueTrack.FilePath,
                    IsFolder = false,
                    AudioFile = audioFile,
                    CueTrack = cueTrack
                };

                // Add to flat list
                var flatList = library.HiresSources.Any(s =>
                    normalizedPath.StartsWith(Path.GetFullPath(s), StringComparison.OrdinalIgnoreCase))
                    ? library.HiresFiles : library.Mp3Files;
                flatList.Add(new LibraryItem
                {
                    AudioFile = audioFile,
                    FolderPath = rootPath
                });

                return fileNode;
            }

            // Regular file node — use file path as dedup key
            if (!library.AddFilePath(normalizedPath))
                return null; // already added

            CachedFileEntry? entry = null;
            if (!fileLookup.TryGetValue(normalizedPath, out entry))
                return null;

            var audioFile2 = new AudioFile
            {
                FilePath = normalizedPath,
                Artist = entry.Artist,
                Title = entry.Title,
                Album = entry.Album,
                Duration = TimeSpan.FromSeconds(entry.DurationSeconds),
                SampleRate = entry.SampleRate,
                BitsPerSample = entry.BitsPerSample,
                Bitrate = entry.Bitrate,
                CoverPath = entry.CoverPath
            };

            var fileNode2 = new LibraryNode
            {
                Name = $"{audioFile2.Artist} - {audioFile2.Title}",
                FullPath = normalizedPath,
                IsFolder = false,
                AudioFile = audioFile2
            };

            // Add to flat list
            var flatList2 = library.HiresSources.Any(s =>
                normalizedPath.StartsWith(Path.GetFullPath(s), StringComparison.OrdinalIgnoreCase))
                ? library.HiresFiles : library.Mp3Files;
            flatList2.Add(new LibraryItem
            {
                AudioFile = audioFile2,
                FolderPath = rootPath
            });

            return fileNode2;
        }
    }

    /// <summary>
    /// Recursively collect all .cue file paths from a cached tree structure.
    /// Used to check if any CUE file has been modified since cache was saved.
    /// </summary>
    private static void CollectCueFilePaths(List<CachedTreeNode> nodes, HashSet<string> cuePaths)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                // Check if this folder node represents a CUE album (FullPath ends with .cue)
                if (node.FullPath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                {
                    cuePaths.Add(Path.GetFullPath(node.FullPath));
                }
                CollectCueFilePaths(node.Children, cuePaths);
            }
            else if (node.CueTrack != null && !string.IsNullOrEmpty(node.CueTrack.CueFilePath))
            {
                cuePaths.Add(Path.GetFullPath(node.CueTrack.CueFilePath));
            }
        }
    }

    /// <summary>
    /// Convert a LibraryNode tree into a CachedTreeNode tree (serializable).
    /// </summary>
    private static CachedTreeNode ConvertTreeToCache(LibraryNode node)
    {
        var cached = new CachedTreeNode
        {
            Name = node.Name,
            FullPath = node.FullPath,
            IsFolder = node.IsFolder,
            FilePath = node.IsFolder ? null : node.FullPath
        };

        // Serialize CueTrack if present
        if (node.CueTrack != null)
        {
            cached.CueTrack = new CachedCueTrack
            {
                FilePath = node.CueTrack.FilePath,
                Artist = node.CueTrack.Artist,
                Title = node.CueTrack.Title,
                Album = node.CueTrack.Album,
                TrackNumber = node.CueTrack.TrackNumber,
                StartPositionSeconds = node.CueTrack.StartPosition.TotalSeconds,
                EndPositionSeconds = node.CueTrack.EndPosition.TotalSeconds,
                CueFilePath = node.CueTrack.CueFilePath
            };
        }

        foreach (var child in node.Children)
        {
            cached.Children.Add(ConvertTreeToCache(child));
        }

        return cached;
    }
}
