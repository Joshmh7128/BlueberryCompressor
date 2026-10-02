using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Profiles;

/// <summary>
/// The stock profiles. Each one spends bits where its kind of content needs them: animation keeps
/// clean lines and gradients, static talk-heavy shows are squeezed hard, concerts keep their sound.
/// </summary>
public static class BuiltInProfiles
{
    public const string Anime = "anime";
    public const string Animation = "animation";
    public const string Sitcom = "sitcom";
    public const string Drama = "drama";
    public const string Cinematic = "cinematic";
    public const string Documentary = "documentary";
    public const string StillCam = "stillcam";
    public const string Sports = "sports";
    public const string Concert = "concert";
    public const string General = "general";
    public const string Compatibility = "compat-h264";
    public const string Av1Archive = "av1-archive";
    public const string Music = "music";
    public const string MusicOpus = "music-opus";
    public const string Speech = "speech";

    public static readonly IReadOnlyDictionary<ContentType, string> DefaultMap = new Dictionary<ContentType, string>
    {
        [ContentType.Anime] = Anime,
        [ContentType.Animation] = Animation,
        [ContentType.Sitcom] = Sitcom,
        [ContentType.Drama] = Drama,
        [ContentType.Cinematic] = Cinematic,
        [ContentType.Documentary] = Documentary,
        [ContentType.StillCam] = StillCam,
        [ContentType.Sports] = Sports,
        [ContentType.Concert] = Concert,
        [ContentType.General] = General,
        [ContentType.Music] = Music,
        [ContentType.Speech] = Speech,
    };

    public static List<Profile> Create() =>
    [
        new Profile
        {
            Id = Anime, Name = "Anime", BuiltIn = true,
            Description = "Picture first. 10-bit HEVC to keep gradients free of banding and line art sharp; every audio " +
                          "track and styled subtitle (with fonts) is kept.",
            Codec = VideoCodec.HEVC, Quality = 19, Speed = "slow", Tune = "animation", BitDepth = BitDepthMode.Force10Bit,
            EncoderParams = "aq-mode=3",
            StereoKbps = 128, SurroundKbps = 256, Surround71Kbps = 384, SkipBelowKbps = 1800,
        },
        new Profile
        {
            Id = Animation, Name = "Animation", BuiltIn = true,
            Description = "Western animation and CG features. 10-bit HEVC tuned for flat colour and hard edges.",
            Codec = VideoCodec.HEVC, Quality = 21, Speed = "slow", Tune = "animation", BitDepth = BitDepthMode.Force10Bit,
            StereoKbps = 112, SurroundKbps = 256, Surround71Kbps = 384, SkipBelowKbps = 1500,
        },
        new Profile
        {
            Id = Sitcom, Name = "Sitcom", BuiltIn = true,
            Description = "Studio sets, static cameras, dialogue. Picture quality is lowered noticeably; speech stays clear " +
                          "and surround is folded down to stereo.",
            Codec = VideoCodec.HEVC, Quality = 26, Speed = "medium", MaxHeight = 1080, MaxFps = 30,
            StereoKbps = 96, MonoKbps = 56, MaxChannels = 2, SkipBelowKbps = 900,
        },
        new Profile
        {
            Id = Drama, Name = "Drama / Film", BuiltIn = true,
            Description = "Balanced picture and sound for dramas and most feature films.",
            Codec = VideoCodec.HEVC, Quality = 23, Speed = "medium", MaxHeight = 1080,
            StereoKbps = 128, SurroundKbps = 320, Surround71Kbps = 448, SkipBelowKbps = 1500,
        },
        new Profile
        {
            Id = Cinematic, Name = "Cinematic", BuiltIn = true,
            Description = "Action, sci-fi and fantasy: fast motion, dark scenes and effects need bits, and so does the " +
                          "soundtrack. Keeps 4K, HDR and full surround.",
            Codec = VideoCodec.HEVC, Quality = 20, Speed = "slow",
            StereoKbps = 160, SurroundKbps = 384, Surround71Kbps = 512, SkipBelowKbps = 2500,
        },
        new Profile
        {
            Id = Documentary, Name = "Documentary", BuiltIn = true,
            Description = "Nature and documentary footage: detailed, textured pictures with narration. Picture is favoured.",
            Codec = VideoCodec.HEVC, Quality = 22, Speed = "medium",
            StereoKbps = 112, SurroundKbps = 256, Surround71Kbps = 384, SkipBelowKbps = 1800,
        },
        new Profile
        {
            Id = StillCam, Name = "Talk / Reality / Lecture", BuiltIn = true,
            Description = "Talk shows, reality, news, stand-up, lectures and screen recordings. The picture barely changes, " +
                          "so it is reduced the most: 720p, 30 fps, low quality target. Speech is kept intelligible.",
            Codec = VideoCodec.HEVC, Quality = 28, Speed = "medium", MaxHeight = 720, MaxFps = 30, BitDepth = BitDepthMode.Force8Bit,
            StereoKbps = 80, MonoKbps = 48, MaxChannels = 2, SkipBelowKbps = 600,
        },
        new Profile
        {
            Id = Sports, Name = "Sports", BuiltIn = true,
            Description = "Motion first: the original frame rate is kept (50/60 fps stays smooth) and broadcasts are deinterlaced.",
            Codec = VideoCodec.HEVC, Quality = 23, Speed = "medium", MaxHeight = 1080,
            StereoKbps = 112, SurroundKbps = 256, SkipBelowKbps = 2500,
        },
        new Profile
        {
            Id = Concert, Name = "Concert / Music video", BuiltIn = true,
            Description = "Sound first: audio tracks are copied untouched. Picture gets a moderate quality target.",
            Codec = VideoCodec.HEVC, Quality = 23, Speed = "medium", MaxHeight = 1080,
            AudioCodec = AudioCodec.Copy, SkipBelowKbps = 1500,
        },
        new Profile
        {
            Id = General, Name = "General", BuiltIn = true,
            Description = "A safe middle setting for anything that was not recognised.",
            Codec = VideoCodec.HEVC, Quality = 24, Speed = "medium",
            StereoKbps = 128, SurroundKbps = 320, Surround71Kbps = 448, SkipBelowKbps = 1500,
        },
        new Profile
        {
            Id = Compatibility, Name = "Compatibility (H.264 MP4)", BuiltIn = true,
            Description = "Plays on almost anything: H.264 video and AAC audio in MP4. Larger files than the HEVC profiles. " +
                          "MP4 cannot hold ASS or image subtitles, so those are written beside the file in their original format.",
            Container = "mp4", Codec = VideoCodec.H264, Quality = 21, Speed = "medium", BitDepth = BitDepthMode.Force8Bit, MaxHeight = 1080,
            AudioCodec = AudioCodec.Aac, StereoKbps = 160, SurroundKbps = 384, MaxChannels = 6, MonoKbps = 96, CopyEfficientAudio = false,
            SubtitleFallback = SubtitleFallback.Sidecar, KeepAttachments = false, SkipBelowKbps = 2000,
        },
        new Profile
        {
            Id = Av1Archive, Name = "AV1 (smallest files)", BuiltIn = true,
            Description = "AV1 gives the smallest files at a given quality but encodes slowly in software and needs a recent " +
                          "device to play. Works best with a hardware AV1 encoder.",
            Codec = VideoCodec.AV1, Quality = 30, Speed = "medium", BitDepth = BitDepthMode.Force10Bit,
            StereoKbps = 112, SurroundKbps = 256, Surround71Kbps = 384, SkipBelowKbps = 1200,
        },
        new Profile
        {
            Id = Music, Name = "Music (AAC)", BuiltIn = true, Kind = MediaKind.Audio,
            Description = "AAC in .m4a keeps cover art and plays everywhere. Lossless sources stay lossless (converted to FLAC); " +
                          "files already at a modest bitrate are left alone.",
            Container = "m4a", AudioCodec = AudioCodec.Aac, StereoKbps = 192, MonoKbps = 96, MaxChannels = 2,
            LosslessSource = LosslessSourcePolicy.KeepLossless, SkipBelowKbps = 224, CopyEfficientAudio = false,
        },
        new Profile
        {
            Id = MusicOpus, Name = "Music (Opus, smallest)", BuiltIn = true, Kind = MediaKind.Audio,
            Description = "Opus is the most efficient lossy codec, also for lossless sources. Cover art is not carried into " +
                          ".opus files.",
            Container = "opus", AudioCodec = AudioCodec.Opus, StereoKbps = 128, MonoKbps = 72, MaxChannels = 2,
            LosslessSource = LosslessSourcePolicy.Transcode, SkipBelowKbps = 170, CopyEfficientAudio = false,
        },
        new Profile
        {
            Id = Speech, Name = "Speech / Podcast / Audiobook", BuiltIn = true, Kind = MediaKind.Audio,
            Description = "Voice needs very little: mono Opus at 40 kbps is clear and tiny. Chapters are kept.",
            Container = "opus", AudioCodec = AudioCodec.Opus, MonoKbps = 40, StereoKbps = 40, MaxChannels = 1,
            LosslessSource = LosslessSourcePolicy.Transcode, SkipBelowKbps = 56, CopyEfficientAudio = false,
        },
    ];
}

/// <summary>Built-in profiles plus the user's own, with the user's edits to built-ins layered on top.</summary>
public sealed class ProfileStore
{
    private sealed class Saved
    {
        public List<Profile> Profiles { get; set; } = [];
    }

    private readonly List<Profile> _profiles = [];

    public IReadOnlyList<Profile> All => _profiles;

    public ProfileStore()
    {
        var saved = JsonStore.Load<Saved>(AppPaths.Profiles).Profiles;
        foreach (var builtIn in BuiltInProfiles.Create())
        {
            // An edited built-in is saved under its id and replaces the stock version.
            var edited = saved.FirstOrDefault(p => p.Id == builtIn.Id);
            if (edited is not null) { edited.BuiltIn = true; _profiles.Add(edited); }
            else _profiles.Add(builtIn);
        }
        var builtInIds = _profiles.Select(p => p.Id).ToHashSet();
        foreach (var custom in saved.Where(p => !builtInIds.Contains(p.Id)))
        {
            custom.BuiltIn = false;
            _profiles.Add(custom);
        }
    }

    public Profile? Find(string? id) => id is null ? null : _profiles.FirstOrDefault(p => p.Id == id);

    public IEnumerable<Profile> ForKind(MediaKind kind) => _profiles.Where(p => p.Kind == kind);

    /// <summary>The profile a content type should use: the user's mapping first, then the stock mapping.</summary>
    public Profile ForType(ContentType type, AppSettings settings)
    {
        var kind = ContentTypes.KindOf(type);
        if (settings.ProfileMap.TryGetValue(type, out var id) && Find(id) is { } mapped && mapped.Kind == kind) return mapped;
        return Find(BuiltInProfiles.DefaultMap[type]) ?? ForKind(kind).First();
    }

    public void Add(Profile profile)
    {
        profile.BuiltIn = false;
        _profiles.Add(profile);
    }

    public bool Remove(Profile profile) => !profile.BuiltIn && _profiles.Remove(profile);

    /// <summary>Put a built-in profile back to its stock settings.</summary>
    public Profile? Reset(string id)
    {
        var stock = BuiltInProfiles.Create().FirstOrDefault(p => p.Id == id);
        int index = _profiles.FindIndex(p => p.Id == id);
        if (stock is null || index < 0) return null;
        _profiles[index] = stock;
        return stock;
    }

    public void Replace(Profile updated)
    {
        int index = _profiles.FindIndex(p => p.Id == updated.Id);
        if (index >= 0) _profiles[index] = updated;
    }

    public void Save()
    {
        // Only store what differs from stock: custom profiles and edited built-ins.
        var stock = BuiltInProfiles.Create().ToDictionary(p => p.Id);
        var toSave = _profiles.Where(p => !stock.TryGetValue(p.Id, out var original) || !Same(p, original)).ToList();
        JsonStore.Save(AppPaths.Profiles, new Saved { Profiles = toSave });
    }

    private static bool Same(Profile a, Profile b) =>
        System.Text.Json.JsonSerializer.Serialize(a, JsonStore.Options) == System.Text.Json.JsonSerializer.Serialize(b, JsonStore.Options);
}
