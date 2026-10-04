using AutoCompressor.Core.Models;
using AutoCompressor.Core.Scanning;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Queue;

/// <summary>
/// Finishes what "save next to original" started: removes the old file and gives the compressed
/// copy the old file's name ("Movie.compressed.mkv" becomes "Movie.mkv").
/// </summary>
public static class OriginalCleanup
{
    public sealed record Result(int Cleaned, long BytesFreed, List<string> Problems);

    public static Result Run(IEnumerable<HistoryEntry> entries, OutputOptions options, History history)
    {
        int cleaned = 0;
        long freed = 0;
        var problems = new List<string>();

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry.SourcePath);
            try
            {
                var source = new FileInfo(entry.SourcePath);
                var output = new FileInfo(entry.OutputPath);
                if (!source.Exists || !output.Exists) continue;
                if (output.Length == 0) { problems.Add($"{name}: the compressed copy is empty; original kept."); continue; }
                // Only remove the very file the copy was made from.
                if (source.Length != entry.SourceSize || source.LastWriteTimeUtc != entry.SourceModifiedUtc)
                {
                    problems.Add($"{name}: changed since it was compressed; left alone.");
                    continue;
                }

                string directory = source.DirectoryName!;
                string oldCopyBase = Path.GetFileNameWithoutExtension(output.Name);
                string finalPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(source.Name) + output.Extension);
                long sourceSize = source.Length;

                Dispose(source.FullName, options);
                if (File.Exists(finalPath)) finalPath = JobProcessor.Unique(finalPath); // an unrelated file already has that name
                File.Move(output.FullName, finalPath);

                // Subtitle files written or copied beside the compressed copy take the new name too.
                string finalBase = Path.GetFileNameWithoutExtension(finalPath);
                foreach (var sidecar in Directory.EnumerateFiles(output.DirectoryName!, oldCopyBase + ".*").ToList())
                {
                    if (!MediaExtensions.Subtitle.Contains(Path.GetExtension(sidecar))) continue;
                    string target = Path.Combine(directory, finalBase + Path.GetFileName(sidecar)[oldCopyBase.Length..]);
                    if (!File.Exists(target)) File.Move(sidecar, target);
                    else if (new FileInfo(target).Length == new FileInfo(sidecar).Length) File.Delete(sidecar); // our own duplicate
                }

                entry.Mode = OutputMode.Replace;
                entry.OutputPath = finalPath;
                cleaned++;
                freed += sourceSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }
        history.Save();
        return new Result(cleaned, freed, problems);
    }

    private static void Dispose(string path, OutputOptions options)
    {
        switch (options.Disposal)
        {
            case OriginalDisposal.RecycleBin:
                Native.Recycle(path);
                break;
            case OriginalDisposal.BackupFolder:
                if (string.IsNullOrWhiteSpace(options.BackupFolder)) throw new IOException("no backup folder is set");
                // Mirror the full path under the backup folder ("D:\TV\x.mkv" goes to "<backup>\D\TV\x.mkv").
                string relative = Path.GetFullPath(path).Replace(":", "").TrimStart('\\');
                string target = Path.Combine(options.BackupFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, JobProcessor.Unique(target));
                break;
            default:
                File.Delete(path);
                break;
        }
    }
}
