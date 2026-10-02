using System.IO;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Probing;
using AutoCompressor.Core.Profiles;
using AutoCompressor.Core.Queue;
using AutoCompressor.Core.Recognition;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Desktop;

/// <summary>A manual type or profile choice the user made for a file; remembered across rescans.</summary>
public sealed class FileOverride
{
    public ContentType? Type { get; set; }
    public string? ProfileId { get; set; }
}

/// <summary>The long-lived objects every part of the app shares.</summary>
public sealed class AppServices
{
    public AppSettings Settings { get; }
    public Tools Tools { get; private set; }
    public EncoderAvailability Encoders { get; set; } = new();
    public ProfileStore Profiles { get; }
    public ProbeCache ProbeCache { get; }
    public MetadataCache MetadataCache { get; }
    public OnlineLookup Lookup { get; }
    public History History { get; }
    public QueueManager Queue { get; }
    public Dictionary<string, FileOverride> Overrides { get; }

    /// <summary>Raised when something that affects profile assignment or planning changed.</summary>
    public event Action? ConfigurationChanged;

    public AppServices()
    {
        Settings = JsonStore.Load<AppSettings>(AppPaths.Settings);
        Tools = Tools.Locate(Settings.FfmpegPath, Settings.FfprobePath);
        Profiles = new ProfileStore();
        ProbeCache = new ProbeCache();
        MetadataCache = new MetadataCache();
        Lookup = new OnlineLookup(MetadataCache);
        History = new History();
        Overrides = new Dictionary<string, FileOverride>(JsonStore.Load<Dictionary<string, FileOverride>>(AppPaths.Overrides), StringComparer.OrdinalIgnoreCase);
        Queue = new QueueManager(new JobServices
        {
            Settings = () => Settings,
            Tools = () => Tools,
            Encoders = () => Encoders,
            History = History,
        });
        TidyUp();
    }

    /// <summary>Housekeeping at startup, when nothing is running: stray downloaded subtitles and old logs.</summary>
    private static void TidyUp()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.SubtitleDir)) File.Delete(file);
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var log in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log"))
                if (File.GetLastWriteTimeUtc(log) < cutoff && !log.EndsWith("errors.log", StringComparison.OrdinalIgnoreCase)) File.Delete(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not worth bothering anyone about */ }
    }

    public void SaveSettings()
    {
        JsonStore.Save(AppPaths.Settings, Settings);
        ConfigurationChanged?.Invoke();
    }

    public void SaveOverrides() => JsonStore.Save(AppPaths.Overrides, Overrides);

    public void NotifyConfigurationChanged() => ConfigurationChanged?.Invoke();

    public void RelocateTools() => Tools = Tools.Locate(Settings.FfmpegPath, Settings.FfprobePath);

    public Task DetectEncodersAsync(bool force, IProgress<string>? progress = null) =>
        Task.Run(async () =>
        {
            Encoders = await EncoderDetector.LoadOrDetectAsync(Tools, force, progress).ConfigureAwait(false);
            ConfigurationChanged?.Invoke();
        });
}
