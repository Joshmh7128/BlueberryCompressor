using System.Text.Json.Serialization;

namespace AutoCompressor.Core.Models;

public enum MediaKind { Video, Audio }

/// <summary>What a file is, as far as the compressor cares: it decides which profile is used.</summary>
public enum ContentType
{
    General,      // video we could not place anywhere else
    Anime,
    Animation,    // western / non-Japanese animation
    Sitcom,
    Drama,        // drama series and most feature films
    Cinematic,    // action, sci-fi, fantasy: picture and sound both matter
    Documentary,
    StillCam,     // talk shows, reality, news, lectures, stand-up: static, simply shot
    Sports,
    Concert,      // concerts and music videos: sound matters most
    Music,        // audio file
    Speech,       // audio file: podcast, audiobook, lecture
}

public static class ContentTypes
{
    public static readonly ContentType[] Video =
    [
        ContentType.Anime, ContentType.Animation, ContentType.Sitcom, ContentType.Drama, ContentType.Cinematic,
        ContentType.Documentary, ContentType.StillCam, ContentType.Sports, ContentType.Concert, ContentType.General,
    ];

    public static readonly ContentType[] Audio = [ContentType.Music, ContentType.Speech];

    public static MediaKind KindOf(ContentType type) =>
        type is ContentType.Music or ContentType.Speech ? MediaKind.Audio : MediaKind.Video;

    public static string DisplayName(ContentType type) => type switch
    {
        ContentType.General => "General",
        ContentType.Anime => "Anime",
        ContentType.Animation => "Animation",
        ContentType.Sitcom => "Sitcom",
        ContentType.Drama => "Drama / Film",
        ContentType.Cinematic => "Cinematic",
        ContentType.Documentary => "Documentary",
        ContentType.StillCam => "Talk / Reality",
        ContentType.Sports => "Sports",
        ContentType.Concert => "Concert",
        ContentType.Music => "Music",
        ContentType.Speech => "Speech",
        _ => type.ToString(),
    };
}

public sealed class StreamInfo
{
    public int Index { get; set; }
    public string CodecType { get; set; } = "";
    public string CodecName { get; set; } = "";
    public string? Profile { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string? FrameRateRational { get; set; }
    public string? PixFmt { get; set; }
    public string? FieldOrder { get; set; }
    public int BitDepth { get; set; }
    public string? ColorTransfer { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? ColorSpace { get; set; }
    public bool HasDolbyVision { get; set; }
    public int DolbyVisionProfile { get; set; }
    /// <summary>Display rotation in degrees from the container (phone videos); ffmpeg applies it when decoding.</summary>
    public int Rotation { get; set; }

    public long BitRate { get; set; }
    public int Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public int SampleRate { get; set; }

    public string? Language { get; set; }
    public string? Title { get; set; }
    public string? FileName { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsAttachedPic { get; set; }
    /// <summary>Names of mkvmerge statistics tags (BPS, NUMBER_OF_BYTES...) that go stale once the stream is re-encoded.</summary>
    public List<string> StatTags { get; set; } = [];

    [JsonIgnore] private bool Sideways => Math.Abs(Rotation) % 180 == 90;
    /// <summary>Frame size as displayed, after rotation.</summary>
    [JsonIgnore] public int DisplayWidth => Sideways ? Height : Width;
    [JsonIgnore] public int DisplayHeight => Sideways ? Width : Height;

    [JsonIgnore] public bool IsVideo => CodecType == "video" && !IsAttachedPic;
    [JsonIgnore] public bool IsAudio => CodecType == "audio";
    [JsonIgnore] public bool IsSubtitle => CodecType == "subtitle";
    [JsonIgnore] public bool IsAttachment => CodecType == "attachment";
    [JsonIgnore] public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";
    [JsonIgnore] public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
}

public sealed class ProbeResult
{
    public double DurationSeconds { get; set; }
    public long BitRate { get; set; }
    public string FormatName { get; set; } = "";
    public int ChapterCount { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StreamInfo> Streams { get; set; } = [];

    [JsonIgnore] public StreamInfo? Video => Streams.FirstOrDefault(s => s.IsVideo);
    [JsonIgnore] public IEnumerable<StreamInfo> AudioStreams => Streams.Where(s => s.IsAudio);
    [JsonIgnore] public IEnumerable<StreamInfo> SubtitleStreams => Streams.Where(s => s.IsSubtitle);
    [JsonIgnore] public IEnumerable<StreamInfo> Attachments => Streams.Where(s => s.IsAttachment);
    [JsonIgnore] public StreamInfo? CoverArt => Streams.FirstOrDefault(s => s.CodecType == "video" && s.IsAttachedPic);

    public string? Tag(string key) => Tags.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// The marker this app writes into files it produced. Matroska, Ogg, FLAC and MP3 take a tag of
    /// our own; MP4 only keeps tags it knows, so there the marker rides in "keywords".
    /// </summary>
    [JsonIgnore]
    public string? CompressorTag =>
        Tag(MediaFile.MarkerTag) ?? (Tag("keywords") is { } k && k.Contains(MediaFile.MarkerPrefix, StringComparison.Ordinal) ? k : null);

    /// <summary>
    /// Best estimate of the video stream's bitrate. Matroska rarely stores per-stream bitrates,
    /// so fall back to the container bitrate minus what the audio is known (or assumed) to use.
    /// </summary>
    public long EstimateVideoBitRate()
    {
        var video = Video;
        if (video is null) return 0;
        if (video.BitRate > 0) return video.BitRate;
        if (BitRate <= 0) return 0;
        long audio = AudioStreams.Sum(a => a.BitRate > 0 ? a.BitRate : a.Channels * 64_000L);
        return Math.Max(BitRate - audio, BitRate / 2);
    }
}

public sealed class ParsedName
{
    public string Title { get; set; } = "";
    public string ShowFolder { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsDateBased { get; set; }
    public string? ReleaseGroup { get; set; }
    public bool FansubStyle { get; set; }
    public bool HasCrc { get; set; }
    public bool AbsoluteNumbering { get; set; }

    [JsonIgnore] public bool IsEpisodic => Season.HasValue || Episode.HasValue || IsDateBased;
}

public sealed class Classification
{
    public ContentType Type { get; set; }
    /// <summary>0..1. Below 0.35 is a guess.</summary>
    public double Confidence { get; set; }
    public List<string> Reasons { get; set; } = [];
    public bool IsManual { get; set; }

    public string ConfidenceLabel => IsManual ? "Manual" : Confidence >= 0.6 ? "High" : Confidence >= 0.35 ? "Medium" : "Low";
}

public sealed class MediaFile
{
    public const string MarkerTag = "AUTOCOMPRESSOR";
    public const string MarkerPrefix = "AutoCompressor/";

    public required string Path { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public MediaKind Kind { get; set; }
    /// <summary>The scan root this file was found under; used to mirror folders in output-folder mode.</summary>
    public string? Root { get; set; }

    public ProbeResult? Probe { get; set; }
    public string? ProbeError { get; set; }
    public ParsedName Parsed { get; set; } = new();
    public Classification Classification { get; set; } = new();
    public FolderNode? Folder { get; set; }
    /// <summary>Predicted size after compression with the assigned profile; null when it would be skipped.</summary>
    public long? EstimatedSize { get; set; }
    /// <summary>True when the estimate comes from test-encoding samples rather than the rule of thumb.</summary>
    public bool EstimateMeasured { get; set; }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
    public bool AlreadyCompressed => Probe?.CompressorTag is not null;
}

/// <summary>A directory in the scanned tree, with sizes rolled up from everything beneath it.</summary>
public sealed class FolderNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public FolderNode? Parent { get; set; }
    public List<FolderNode> Folders { get; } = [];
    public List<MediaFile> Files { get; } = [];

    /// <summary>Bytes of media files in this folder and all subfolders.</summary>
    public long MediaSize { get; set; }
    /// <summary>Bytes of every file in this folder and all subfolders, media or not.</summary>
    public long TotalSize { get; set; }
    public int MediaCount { get; set; }

    public IEnumerable<MediaFile> AllFiles()
    {
        foreach (var f in Files) yield return f;
        foreach (var d in Folders)
            foreach (var f in d.AllFiles())
                yield return f;
    }

    /// <summary>Apply a size change of one media file to this folder and every ancestor.</summary>
    public void ApplySizeDelta(long delta)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            n.MediaSize += delta;
            n.TotalSize += delta;
        }
    }
}
