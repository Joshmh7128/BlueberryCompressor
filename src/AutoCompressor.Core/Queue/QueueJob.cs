using System.Text.Json.Serialization;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Queue;

public enum JobStatus { Queued, Running, Completed, Skipped, Failed, Cancelled }

/// <summary>
/// One file waiting to be, being, or having been compressed. The profile and output options are
/// copied in when the job is queued, so later edits to settings do not change jobs already waiting.
/// </summary>
public sealed class QueueJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public required string SourcePath { get; set; }
    public string? Root { get; set; }
    public MediaKind Kind { get; set; }
    public ContentType ContentType { get; set; }
    public required Profile Profile { get; set; }
    public required OutputOptions Output { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string Message { get; set; } = "";
    public List<string> Notes { get; set; } = [];
    public string? Summary { get; set; }
    public string? EncoderId { get; set; }
    public string? Command { get; set; }

    public long SourceSize { get; set; }
    public long OutputSize { get; set; }
    public string? OutputPath { get; set; }
    public string? LogPath { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    // Live state while running; not worth persisting.
    [JsonIgnore] public string Stage { get; set; } = "";
    [JsonIgnore] public double Progress { get; set; }
    [JsonIgnore] public double Fps { get; set; }
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public bool Paused { get; set; }
    [JsonIgnore] public TimeSpan? Eta { get; set; }

    [JsonIgnore] public bool IsFinished => Status is JobStatus.Completed or JobStatus.Skipped or JobStatus.Failed or JobStatus.Cancelled;
    [JsonIgnore] public long Saved => Status == JobStatus.Completed && OutputSize > 0 ? SourceSize - OutputSize : 0;
}

public sealed class HistoryEntry
{
    public string SourcePath { get; set; } = "";
    public long SourceSize { get; set; }
    public DateTime SourceModifiedUtc { get; set; }
    public string OutputPath { get; set; } = "";
    public long OutputSize { get; set; }
    public string ProfileId { get; set; } = "";
    public OutputMode Mode { get; set; }
    public DateTime FinishedUtc { get; set; }
}

/// <summary>
/// A record of finished work. Lets a rescan recognise originals that already have a compressed
/// copy beside them, so "queue everything" does not do the same work twice.
/// </summary>
public sealed class History
{
    private sealed class Saved
    {
        public List<HistoryEntry> Entries { get; set; } = [];
    }

    private readonly List<HistoryEntry> _entries;
    private readonly object _gate = new();

    public History() => _entries = JsonStore.Load<Saved>(AppPaths.History).Entries;

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
            JsonStore.Save(AppPaths.History, new Saved { Entries = [.. _entries] });
        }
    }

    /// <summary>The compressed copy made from this exact file, if it is still on disk.</summary>
    public HistoryEntry? FindCopy(string path, long size, DateTime modifiedUtc)
    {
        lock (_gate)
        {
            var entry = _entries.LastOrDefault(e => string.Equals(e.SourcePath, path, StringComparison.OrdinalIgnoreCase)
                                                    && e.SourceSize == size && e.SourceModifiedUtc == modifiedUtc
                                                    && e.Mode != OutputMode.Replace);
            return entry is not null && File.Exists(entry.OutputPath) ? entry : null;
        }
    }

    /// <summary>Originals that still sit beside the compressed copy made from them.</summary>
    public List<HistoryEntry> PendingCleanup()
    {
        lock (_gate)
            return _entries.Where(e => e.Mode == OutputMode.NextToOriginal && File.Exists(e.SourcePath) && File.Exists(e.OutputPath)
                                       && !string.Equals(e.SourcePath, e.OutputPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Write the list out after entries were changed in place.</summary>
    public void Save()
    {
        lock (_gate) JsonStore.Save(AppPaths.History, new Saved { Entries = [.. _entries] });
    }

    public (int Count, long BytesSaved) Totals()
    {
        lock (_gate) return (_entries.Count, _entries.Sum(e => Math.Max(0, e.SourceSize - e.OutputSize)));
    }
}
