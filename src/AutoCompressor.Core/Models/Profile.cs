namespace AutoCompressor.Core.Models;

public enum VideoCodec { H264, HEVC, AV1, Copy }

/// <summary>Auto follows the global setting; the other two pin a profile to one kind of encoder.</summary>
public enum EncoderPreference { Auto, Software, Hardware }

public enum RateControlMode { ConstantQuality, AverageBitrate }
public enum BitDepthMode { Source, Force8Bit, Force10Bit }
public enum DeinterlaceMode { Auto, Off, Always }
public enum DenoiseLevel { Off, Light, Medium, Strong }
public enum AudioCodec { Opus, Aac, Ac3, Eac3, Flac, Mp3, Copy }
public enum SubtitleMode { CopyAll, SelectedLanguages, None }

/// <summary>What to do with a subtitle track the output container cannot hold in its original format.</summary>
public enum SubtitleFallback { Sidecar, Convert, Drop }

/// <summary>For audio-file profiles: what to do when the source is lossless (WAV, FLAC, ALAC...).</summary>
public enum LosslessSourcePolicy { KeepLossless, Transcode, Skip }

/// <summary>
/// Everything needed to turn one source file into one output file. A profile is self-contained:
/// container, video, audio, subtitles and the rules for when not to bother.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New profile";
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }
    public MediaKind Kind { get; set; } = MediaKind.Video;

    /// <summary>Video: mkv or mp4. Audio: opus, m4a, mp3, flac or mka.</summary>
    public string Container { get; set; } = "mkv";

    // ---- video ----
    public VideoCodec Codec { get; set; } = VideoCodec.HEVC;
    public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;
    public RateControlMode RateMode { get; set; } = RateControlMode.ConstantQuality;
    /// <summary>CRF on the software encoder's scale for <see cref="Codec"/>. Lower is better quality.</summary>
    public double Quality { get; set; } = 23;
    /// <summary>Added to <see cref="Quality"/> when a hardware encoder is used; their scales run a few points lower.</summary>
    public double HardwareQualityOffset { get; set; } = 4;
    /// <summary>Target for AverageBitrate mode.</summary>
    public int BitrateKbps { get; set; } = 2500;
    /// <summary>Optional ceiling in either mode. 0 = none.</summary>
    public int MaxBitrateKbps { get; set; }
    /// <summary>veryslow, slower, slow, medium, fast, faster, veryfast. Translated per encoder.</summary>
    public string Speed { get; set; } = "medium";
    /// <summary>none, animation, film, grain.</summary>
    public string Tune { get; set; } = "none";
    public BitDepthMode BitDepth { get; set; } = BitDepthMode.Source;
    /// <summary>Downscale anything taller than this (1080 also caps width at 1920). 0 = keep source size.</summary>
    public int MaxHeight { get; set; }
    /// <summary>Halve or cap frame rates above this. 0 = keep source rate.</summary>
    public int MaxFps { get; set; }
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Auto;
    public DenoiseLevel Denoise { get; set; } = DenoiseLevel.Off;
    /// <summary>Extra codec parameters for the software encoder, e.g. "aq-mode=3:psy-rd=1.0" for x265.</summary>
    public string EncoderParams { get; set; } = "";
    /// <summary>Raw ffmpeg output arguments appended last.</summary>
    public string ExtraArgs { get; set; } = "";

    // ---- audio ----
    public AudioCodec AudioCodec { get; set; } = AudioCodec.Opus;
    public int MonoKbps { get; set; } = 64;
    public int StereoKbps { get; set; } = 128;
    public int SurroundKbps { get; set; } = 320;
    public int Surround71Kbps { get; set; } = 448;
    /// <summary>Downmix tracks with more channels than this. 0 = keep.</summary>
    public int MaxChannels { get; set; }
    /// <summary>Stream-copy lossy tracks that are already at or under the target bitrate instead of re-encoding them.</summary>
    public bool CopyEfficientAudio { get; set; } = true;
    /// <summary>Comma-separated ISO codes to keep, e.g. "eng,jpn". Empty = every track.</summary>
    public string AudioLanguages { get; set; } = "";
    public LosslessSourcePolicy LosslessSource { get; set; } = LosslessSourcePolicy.Transcode;

    // ---- subtitles and extras ----
    public SubtitleMode Subtitles { get; set; } = SubtitleMode.CopyAll;
    public string SubtitleLanguages { get; set; } = "";
    public SubtitleFallback SubtitleFallback { get; set; } = SubtitleFallback.Sidecar;
    public bool KeepChapters { get; set; } = true;
    public bool KeepAttachments { get; set; } = true;
    public bool KeepMetadata { get; set; } = true;

    // ---- skip rules ----
    /// <summary>
    /// Leave a file alone when its video bitrate is already at or below this (at 1080p, scaled with
    /// resolution). Applies in full to HEVC/AV1/VP9 sources and at 60% to older codecs. 0 = never skip.
    /// For audio profiles this is the source bitrate under which a lossy file is left alone.
    /// </summary>
    public int SkipBelowKbps { get; set; } = 1500;

    public Profile Clone()
    {
        var copy = (Profile)MemberwiseClone();
        return copy;
    }

    public override string ToString() => Name;
}
