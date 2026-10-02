using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCompressor.Core.Util;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>Load a JSON file, returning a fresh instance when it is missing or unreadable.</summary>
    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A corrupt file should not stop the app from starting; keep it for inspection.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
        }
        return new T();
    }

    /// <summary>Write via a temp file so a crash mid-save cannot leave a truncated file behind.</summary>
    public static void Save<T>(string path, T value, bool indented = true)
    {
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, value, indented ? Options : Compact);
        File.Move(tmp, path, overwrite: true);
    }
}
