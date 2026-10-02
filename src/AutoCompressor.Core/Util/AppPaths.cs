namespace AutoCompressor.Core.Util;

/// <summary>Where the app keeps its settings, caches, queue and logs.</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCompressor");

    /// <summary>Point the app at a different data folder (used by tests so they never touch real settings).</summary>
    public static void OverrideDataDir(string dir) => DataDir = dir;

    public static string Settings => File("settings.json");
    public static string Profiles => File("profiles.json");
    public static string Queue => File("queue.json");
    public static string ProbeCache => File("probe-cache.json");
    public static string MetadataCache => File("metadata-cache.json");
    public static string History => File("history.json");
    public static string Overrides => File("overrides.json");
    public static string EncoderCache => File("encoders.json");
    public static string LogsDir => Dir("logs");
    public static string SubtitleDir => Dir("subtitles");

    private static string File(string name)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, name);
    }

    private static string Dir(string name)
    {
        var path = Path.Combine(DataDir, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
