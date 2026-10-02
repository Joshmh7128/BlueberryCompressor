using AutoCompressor.Core.Models;

namespace AutoCompressor.Core.Scanning;

public static class MediaExtensions
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg",
        ".vob", ".ogv", ".3gp", ".divx", ".asf", ".rm", ".rmvb", ".f4v", ".mxf", ".m2v",
    };

    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif",
        ".ape", ".wv", ".ac3", ".dts", ".mka", ".mp2", ".tta", ".dsf",
    };

    public static readonly HashSet<string> Subtitle = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi",
    };

    /// <summary>Marks a file this app is still writing; such files are never listed.</summary>
    public const string TempMarker = ".ac-tmp";

    public static MediaKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        return null;
    }
}

public sealed record ScanProgress(int FilesSeen, int MediaFound, string CurrentDirectory);

public sealed class ScanResult
{
    public List<FolderNode> Roots { get; } = [];
    public List<MediaFile> Files { get; } = [];
    public List<string> Inaccessible { get; } = [];
}

/// <summary>Walks directories recursively and builds a folder tree with rolled-up sizes.</summary>
public static class LibraryScanner
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "$WINDOWS.~BT", "$WinREAgent",
    };

    /// <summary>
    /// Scan a mix of folders and individual files. Folders are walked recursively; loose files are
    /// grouped under a node for their parent directory.
    /// </summary>
    public static ScanResult Scan(IEnumerable<string> paths, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ScanResult();
        var looseByDir = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        int seen = 0;

        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (Directory.Exists(path))
            {
                var root = ScanTree(path, result, progress, ref seen, ct);
                result.Roots.Add(root);
            }
            else if (File.Exists(path) && MediaExtensions.KindOf(path) is { } kind && !IsTemp(path))
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                if (!looseByDir.TryGetValue(dir, out var node))
                {
                    node = new FolderNode { Path = dir, Name = dir };
                    looseByDir[dir] = node;
                    result.Roots.Add(node);
                }
                var info = new FileInfo(path);
                var file = new MediaFile { Path = path, Size = info.Length, ModifiedUtc = info.LastWriteTimeUtc, Kind = kind, Root = dir, Folder = node };
                node.Files.Add(file);
                node.MediaSize += file.Size;
                node.TotalSize += file.Size;
                node.MediaCount++;
                result.Files.Add(file);
            }
        }
        return result;
    }

    private static FolderNode ScanTree(string rootPath, ScanResult result, IProgress<ScanProgress>? progress, ref int seen, CancellationToken ct)
    {
        string trimmed = rootPath.Length > 3 ? rootPath.TrimEnd('\\', '/') : rootPath;
        var root = new FolderNode { Path = trimmed, Name = trimmed };
        var order = new List<FolderNode>();          // parents always precede their children
        var pending = new Stack<FolderNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = pending.Pop();
            order.Add(node);
            progress?.Report(new ScanProgress(seen, result.Files.Count, node.Path));

            try
            {
                foreach (var entry in new DirectoryInfo(node.Path).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo dir)
                    {
                        // Junctions and symlinks can loop back on themselves; never follow them.
                        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (SkippedDirectories.Contains(dir.Name)) continue;
                        var child = new FolderNode { Path = dir.FullName, Name = dir.Name, Parent = node };
                        node.Folders.Add(child);
                        pending.Push(child);
                    }
                    else if (entry is FileInfo info)
                    {
                        seen++;
                        long length = info.Length;
                        node.TotalSize += length;
                        if (MediaExtensions.KindOf(info.Name) is not { } kind || IsTemp(info.Name)) continue;
                        var file = new MediaFile
                        {
                            Path = info.FullName, Size = length, ModifiedUtc = info.LastWriteTimeUtc,
                            Kind = kind, Root = root.Path, Folder = node,
                        };
                        node.Files.Add(file);
                        node.MediaSize += length;
                        node.MediaCount++;
                        result.Files.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                result.Inaccessible.Add(node.Path);
            }
        }

        // Roll sizes up from the leaves, then drop branches that hold no media at all.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            if (node.Parent is { } parent)
            {
                parent.MediaSize += node.MediaSize;
                parent.TotalSize += node.TotalSize;
                parent.MediaCount += node.MediaCount;
            }
        }
        foreach (var node in order)
            node.Folders.RemoveAll(f => f.MediaCount == 0);

        return root;
    }

    private static bool IsTemp(string path) => path.Contains(MediaExtensions.TempMarker, StringComparison.OrdinalIgnoreCase);
}
