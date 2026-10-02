using System.Globalization;
using System.Text.Json;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Probing;

public sealed class ProbeException(string message) : Exception(message);

/// <summary>Reads stream and container details with ffprobe.</summary>
public static class Ffprobe
{
    public static async Task<ProbeResult> ProbeAsync(string ffprobe, string path, CancellationToken ct = default)
    {
        string[] args = ["-v", "error", "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path];
        var result = await ProcessRunner.CaptureAsync(ffprobe, args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            var detail = result.StdErr.Trim().Split('\n').LastOrDefault()?.Trim();
            throw new ProbeException(string.IsNullOrEmpty(detail) ? "ffprobe could not read this file." : detail);
        }
        return Parse(result.StdOut);
    }

    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var probe = new ProbeResult();

        if (root.TryGetProperty("format", out var format))
        {
            probe.FormatName = Str(format, "format_name") ?? "";
            probe.DurationSeconds = Dbl(format, "duration");
            probe.BitRate = Lng(format, "bit_rate");
            if (format.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var tag in tags.EnumerateObject())
                    probe.Tags[tag.Name] = tag.Value.ToString();
        }
        if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            probe.ChapterCount = chapters.GetArrayLength();

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var s in streams.EnumerateArray())
                probe.Streams.Add(ParseStream(s));

        if (probe.DurationSeconds <= 0)
        {
            // Some containers only report duration per stream.
            foreach (var s in root.TryGetProperty("streams", out var ss) ? ss.EnumerateArray() : default)
                probe.DurationSeconds = Math.Max(probe.DurationSeconds, Dbl(s, "duration"));
        }
        return probe;
    }

    private static StreamInfo ParseStream(JsonElement s)
    {
        var info = new StreamInfo
        {
            Index = (int)Lng(s, "index"),
            CodecType = Str(s, "codec_type") ?? "",
            CodecName = Str(s, "codec_name") ?? "",
            Profile = Str(s, "profile"),
            Width = (int)Lng(s, "width"),
            Height = (int)Lng(s, "height"),
            PixFmt = Str(s, "pix_fmt"),
            FieldOrder = Str(s, "field_order"),
            ColorTransfer = Str(s, "color_transfer"),
            ColorPrimaries = Str(s, "color_primaries"),
            ColorSpace = Str(s, "color_space"),
            BitRate = Lng(s, "bit_rate"),
            Channels = (int)Lng(s, "channels"),
            ChannelLayout = Str(s, "channel_layout"),
            SampleRate = (int)Lng(s, "sample_rate"),
        };

        var avg = Str(s, "avg_frame_rate");
        var real = Str(s, "r_frame_rate");
        info.FrameRate = Rational(avg);
        info.FrameRateRational = avg;
        if (info.FrameRate <= 0)
        {
            info.FrameRate = Rational(real);
            info.FrameRateRational = real;
        }

        int bits = (int)Lng(s, "bits_per_raw_sample");
        if (bits <= 0 && info.PixFmt is { } pix)
            bits = pix.Contains("p10") || pix.Contains("p010") ? 10 : pix.Contains("p12") || pix.Contains("p012") ? 12 : 8;
        info.BitDepth = bits;

        if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tags.EnumerateObject())
            {
                var value = tag.Value.ToString();
                if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) info.Language = value;
                else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) info.Title = value;
                else if (tag.Name.Equals("filename", StringComparison.OrdinalIgnoreCase)) info.FileName = value;
                // mkvmerge statistics tags: BPS, BPS-eng...
                else if (IsStatTag(tag.Name))
                {
                    info.StatTags.Add(tag.Name);
                    if (info.BitRate <= 0 && tag.Name.StartsWith("BPS", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps))
                        info.BitRate = bps;
                }
            }
        }

        if (s.TryGetProperty("disposition", out var disp) && disp.ValueKind == JsonValueKind.Object)
        {
            info.IsDefault = Lng(disp, "default") == 1;
            info.IsForced = Lng(disp, "forced") == 1;
            info.IsAttachedPic = Lng(disp, "attached_pic") == 1;
        }

        if (s.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in side.EnumerateArray())
            {
                if ((Str(item, "side_data_type") ?? "").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                {
                    info.HasDolbyVision = true;
                    info.DolbyVisionProfile = (int)Lng(item, "dv_profile");
                }
                if (item.TryGetProperty("rotation", out _))
                    info.Rotation = (int)Math.Round(Dbl(item, "rotation"));
            }
        }
        return info;
    }

    /// <summary>
    /// Read HDR10 mastering metadata from the first video frame, formatted for x265's
    /// master-display / max-cll options. Returns nulls when the stream carries none.
    /// </summary>
    public static async Task<(string? MasterDisplay, string? MaxCll)> ReadHdrMetadataAsync(string ffprobe, string path, CancellationToken ct = default)
    {
        string[] args =
        [
            "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames",
            "-show_entries", "frame=side_data_list", "-print_format", "json", path,
        ];
        ProcessResult result;
        try { result = await ProcessRunner.CaptureAsync(ffprobe, args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
        catch (TimeoutException) { return (null, null); }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut)) return (null, null);
        try { return ParseHdrMetadata(result.StdOut); }
        catch (JsonException) { return (null, null); }
    }

    public static (string? MasterDisplay, string? MaxCll) ParseHdrMetadata(string json)
    {
        string? master = null, cll = null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("frames", out var frames)) return (null, null);
        foreach (var frame in frames.EnumerateArray())
        {
            if (!frame.TryGetProperty("side_data_list", out var side)) continue;
            foreach (var item in side.EnumerateArray())
            {
                var type = Str(item, "side_data_type") ?? "";
                if (type.Contains("Mastering display", StringComparison.OrdinalIgnoreCase))
                {
                    // x265 wants chromaticities in units of 0.00002 and luminance in units of 0.0001 cd/m2.
                    long C(string key) => (long)Math.Round(Rational(Str(item, key)) * 50000);
                    long L(string key) => (long)Math.Round(Rational(Str(item, key)) * 10000);
                    master = $"G({C("green_x")},{C("green_y")})B({C("blue_x")},{C("blue_y")})R({C("red_x")},{C("red_y")})" +
                             $"WP({C("white_point_x")},{C("white_point_y")})L({L("max_luminance")},{L("min_luminance")})";
                }
                else if (type.Contains("Content light level", StringComparison.OrdinalIgnoreCase))
                {
                    cll = $"{Lng(item, "max_content")},{Lng(item, "max_average")}";
                }
            }
        }
        return (master, cll);
    }

    // mkvmerge writes per-stream statistics (BPS, BPS-eng, NUMBER_OF_BYTES-eng...) that ffmpeg copies verbatim.
    private static bool IsStatTag(string name) =>
        name.StartsWith("BPS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NUMBER_OF_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("_STATISTICS_", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("DURATION-", StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    // ffprobe writes many numbers as JSON strings.
    private static long Lng(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : (long)v.GetDouble();
        return long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public static double Rational(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var parts = text.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return 0;
        if (parts.Length == 1) return num;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) || den == 0) return 0;
        return num / den;
    }
}

/// <summary>Remembers probe results so rescanning a large library does not re-run ffprobe on unchanged files.</summary>
public sealed class ProbeCache
{
    private readonly Dictionary<string, ProbeResult> _entries;
    private readonly object _gate = new();
    private bool _dirty;

    public ProbeCache()
    {
        _entries = new Dictionary<string, ProbeResult>(
            JsonStore.Load<Dictionary<string, ProbeResult>>(AppPaths.ProbeCache), StringComparer.OrdinalIgnoreCase);
        // Tags are looked up case-insensitively; deserialisation gives a case-sensitive dictionary.
        foreach (var entry in _entries.Values)
            entry.Tags = new Dictionary<string, string>(entry.Tags, StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(MediaFile file) => $"{file.Path}|{file.Size}|{file.ModifiedUtc.Ticks}";

    public ProbeResult? Get(MediaFile file)
    {
        lock (_gate) return _entries.TryGetValue(Key(file), out var probe) ? probe : null;
    }

    public void Set(MediaFile file, ProbeResult probe)
    {
        lock (_gate) { _entries[Key(file)] = probe; _dirty = true; }
    }

    public void Save()
    {
        Dictionary<string, ProbeResult> snapshot;
        lock (_gate)
        {
            if (!_dirty) return;
            snapshot = new Dictionary<string, ProbeResult>(_entries);
            _dirty = false;
        }
        JsonStore.Save(AppPaths.ProbeCache, snapshot, indented: false);
    }
}
