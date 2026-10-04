using System.Globalization;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Transcoding;

/// <summary>A subtitle file fetched from an online database, to be added to the output.</summary>
public sealed record ExternalSubtitle(string Path, string Language);

public sealed class EncodeRequest
{
    public required MediaFile File { get; init; }
    public required ProbeResult Probe { get; init; }
    public required Profile Profile { get; init; }
    public required AppSettings Settings { get; init; }
    public required EncoderAvailability Encoders { get; init; }
    public required string OutputPath { get; init; }
    public List<ExternalSubtitle> ExtraSubtitles { get; init; } = [];
    public string? MasterDisplay { get; init; }
    public string? MaxCll { get; init; }
    /// <summary>Set when a hardware encode failed and the job is being retried in software.</summary>
    public bool ForceSoftware { get; init; }
}

/// <summary>A subtitle track that has to live beside the output because the container cannot hold it.</summary>
public sealed record SidecarPlan(int StreamIndex, string Muxer, string Suffix);

public sealed class EncodePlan
{
    public bool Skip { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Args { get; } = [];
    public string Extension { get; set; } = "mkv";
    public EncoderDef? Encoder { get; set; }
    public List<SidecarPlan> Sidecars { get; } = [];
    public List<string> Notes { get; } = [];
    public int VideoStreams { get; set; }
    public int AudioStreams { get; set; }
    public int SubtitleStreams { get; set; }
    public string Summary { get; set; } = "";

    public EncodePlan SkipWith(string reason)
    {
        Skip = true;
        SkipReason = reason;
        return this;
    }

    public string Preview(string ffmpeg = "ffmpeg") => ffmpeg + " " + ProcessRunner.Quote(Args);
}

/// <summary>Turns a probed file and a profile into an ffmpeg command line.</summary>
public static class CommandBuilder
{
    private static string Inv(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string MarkerValue(Profile profile) => $"{MediaFile.MarkerPrefix}1.0 profile={profile.Id}";

    /// <summary>The extension the output will have, which the caller needs before it can name the temp file.</summary>
    public static string ExtensionFor(Profile profile, ProbeResult probe)
    {
        if (profile.Kind == MediaKind.Video) return profile.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "mkv";
        var audio = probe.AudioStreams.FirstOrDefault();
        if (audio is not null && IsLossless(audio) && profile.LosslessSource == LosslessSourcePolicy.KeepLossless) return "flac";
        return profile.Container.ToLowerInvariant() switch
        {
            "m4a" or "mp3" or "flac" or "mka" => profile.Container.ToLowerInvariant(),
            _ => "opus",
        };
    }

    public static EncodePlan Build(EncodeRequest r)
    {
        var plan = new EncodePlan { Extension = ExtensionFor(r.Profile, r.Probe) };
        if (!r.Settings.ReprocessCompressed && r.Probe.CompressorTag is not null)
            return plan.SkipWith("Already compressed by AutoCompressor");
        if (r.Profile.Kind != r.File.Kind)
            return plan.SkipWith($"Profile \"{r.Profile.Name}\" is for {r.Profile.Kind.ToString().ToLowerInvariant()} files");
        return r.File.Kind == MediaKind.Audio ? BuildAudio(r, plan) : BuildVideo(r, plan);
    }

    // ------------------------------------------------------------------ video

    private static EncodePlan BuildVideo(EncodeRequest r, EncodePlan plan)
    {
        var p = r.Profile;
        var probe = r.Probe;
        var video = probe.Video;
        if (video is null) return plan.SkipWith("No video stream found");
        bool mp4 = plan.Extension == "mp4";

        if (video.HasDolbyVision && p.Codec != VideoCodec.Copy)
        {
            // Profile 5 has no HDR10 base layer: without the Dolby Vision data its colours are simply wrong.
            if (video.DolbyVisionProfile == 5)
                return plan.SkipWith("Dolby Vision profile 5 cannot be re-encoded without breaking its colours");
            if (r.Settings.SkipDolbyVision)
                return plan.SkipWith("Dolby Vision would be lost by re-encoding (can be allowed in Settings)");
            plan.Notes.Add("Dolby Vision layer is dropped; the HDR10 base picture is kept.");
        }

        bool hdr = video.IsHdr;
        bool tenBit = hdr || p.BitDepth switch
        {
            BitDepthMode.Force10Bit => true,
            BitDepthMode.Force8Bit => false,
            _ => video.BitDepth >= 10,
        };

        EncoderDef? encoder = null;
        if (p.Codec != VideoCodec.Copy)
        {
            var need = hdr ? TenBitNeed.Required : tenBit ? TenBitNeed.Preferred : TenBitNeed.None;
            encoder = r.Encoders.Resolve(p, r.Settings, r.ForceSoftware, need);
            if (encoder is null) return plan.SkipWith($"No usable {p.Codec} encoder was found in this ffmpeg");
            bool wantedHardware = p.Encoder == EncoderPreference.Hardware || (p.Encoder == EncoderPreference.Auto && r.Settings.EncoderPolicy == EncoderPolicy.Hardware);
            if (wantedHardware && !encoder.Hardware && !r.ForceSoftware)
                plan.Notes.Add(hdr && r.Encoders.Usable.Any(e => e.Hardware && e.Codec == p.Codec)
                    ? $"The GPU encoder here cannot produce 10-bit {p.Codec}, which HDR needs; using {encoder.DisplayName}."
                    : $"No hardware {p.Codec} encoder is available; using {encoder.DisplayName}.");

            if (tenBit && !r.Encoders.SupportsTenBit(encoder))
            {
                if (hdr) return plan.SkipWith(encoder.TenBit
                    ? $"HDR video needs 10-bit output, which {encoder.DisplayName} cannot produce with this ffmpeg"
                    : "HDR video needs a 10-bit codec (HEVC or AV1); this profile uses H.264");
                if (encoder.TenBit)
                    plan.Notes.Add($"{encoder.DisplayName} cannot produce 10-bit with this ffmpeg; encoding 8-bit instead.");
                tenBit = false;
            }
        }
        plan.Encoder = encoder;

        // ---- already small enough? ----
        long videoBitrate = probe.EstimateVideoBitRate();
        if (encoder is not null && p.SkipBelowKbps > 0 && videoBitrate > 0 && video.Width > 0)
        {
            double pixelScale = Math.Pow(video.Width * (double)video.Height / (1920.0 * 1080.0), 0.75);
            bool efficient = video.CodecName is "hevc" or "av1" or "vp9";
            double floor = p.SkipBelowKbps * 1000.0 * pixelScale * (efficient ? 1.0 : 0.6);
            if (videoBitrate <= floor)
                return plan.SkipWith($"Already small: {video.CodecName.ToUpperInvariant()} video at {Format.Bitrate(videoBitrate)} " +
                                     $"(this profile leaves anything under {Format.Bitrate((long)floor)} alone)");
        }

        var args = plan.Args;
        args.AddRange(["-hide_banner", "-y", "-nostdin"]);
        if (r.Settings.HardwareDecode && encoder is not null) args.AddRange(["-hwaccel", "auto"]);
        args.AddRange(["-i", r.File.Path]);
        foreach (var extra in r.ExtraSubtitles)
            args.AddRange(["-sub_charenc", "UTF-8", "-i", extra.Path]);

        // ---- video ----
        args.AddRange(["-map", $"0:{video.Index}"]);
        plan.VideoStreams = 1;
        var summary = new List<string>();

        if (encoder is null)
        {
            args.AddRange(["-c:v", "copy"]);
            summary.Add("video copied");
        }
        else
        {
            var filters = BuildVideoFilters(p, video, summary);
            if (filters.Count > 0) args.AddRange(["-vf", string.Join(',', filters)]);
            AddVideoEncoderArgs(args, encoder, p, video, tenBit, hdr, mp4, r, summary);
            if (hdr && encoder.Id != "libx265")
                plan.Notes.Add("The picture stays HDR, but HDR10 mastering metadata (peak brightness hints) is only carried by the " +
                               "software HEVC encoder with this ffmpeg version.");
            foreach (var tag in video.StatTags)
                args.AddRange(["-metadata:s:v:0", tag + "="]);
        }

        // ---- audio ----
        var audio = SelectByLanguage(probe.AudioStreams.ToList(), p.AudioLanguages, "audio", plan.Notes);
        var audioSummary = new List<string>();
        for (int n = 0; n < audio.Count; n++)
        {
            args.AddRange(["-map", $"0:{audio[n].Index}"]);
            audioSummary.Add(AddAudioArgs(args, n, audio[n], p, mp4 ? "mp4" : "mkv", plan.Notes));
        }
        plan.AudioStreams = audio.Count;
        if (audio.Count > 0) summary.Add("audio: " + string.Join(" + ", audioSummary.Distinct()));

        // ---- subtitles ----
        int subIndex = 0;
        if (p.Subtitles != SubtitleMode.None)
        {
            var subs = probe.SubtitleStreams.ToList();
            if (p.Subtitles == SubtitleMode.SelectedLanguages)
                subs = SelectByLanguage(subs, p.SubtitleLanguages, "subtitle", plan.Notes);

            var sidecarNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subs)
            {
                switch (SubtitleAction(sub, mp4, p.SubtitleFallback))
                {
                    case SubAction.Copy:
                        args.AddRange(["-map", $"0:{sub.Index}", $"-c:s:{subIndex}", "copy"]);
                        subIndex++;
                        break;
                    case SubAction.ToSrt:
                        args.AddRange(["-map", $"0:{sub.Index}", $"-c:s:{subIndex}", "srt"]);
                        plan.Notes.Add($"Subtitle track {sub.Index} ({sub.CodecName}) is converted to SRT: Matroska cannot store it as-is.");
                        subIndex++;
                        break;
                    case SubAction.ToMovText:
                        args.AddRange(["-map", $"0:{sub.Index}", $"-c:s:{subIndex}", "mov_text"]);
                        if (sub.CodecName != "mov_text")
                            plan.Notes.Add($"Subtitle track {sub.Index} ({sub.CodecName}) is converted to MP4 timed text; styling is lost.");
                        subIndex++;
                        break;
                    case SubAction.Sidecar:
                        var (muxer, ext) = SidecarFormat(sub.CodecName);
                        string lang = Languages.ToTwoLetter(sub.Language) is not null ? Languages.ToThreeLetter(sub.Language) : "und";
                        string suffix = $".{lang}{(sub.IsForced ? ".forced" : "")}.{ext}";
                        for (int dup = 2; !sidecarNames.Add(suffix); dup++)
                            suffix = $".{lang}{(sub.IsForced ? ".forced" : "")}.{dup}.{ext}";
                        plan.Sidecars.Add(new SidecarPlan(sub.Index, muxer, suffix));
                        plan.Notes.Add($"Subtitle track {sub.Index} ({sub.CodecName}) is saved beside the file as \"*{suffix}\" in its original format: MP4 cannot hold it.");
                        break;
                    case SubAction.Drop:
                        plan.Notes.Add($"Subtitle track {sub.Index} ({sub.CodecName}) cannot be stored in {plan.Extension.ToUpperInvariant()} and is dropped.");
                        break;
                }
            }
        }

        for (int i = 0; i < r.ExtraSubtitles.Count; i++)
        {
            args.AddRange(["-map", $"{i + 1}:0", $"-c:s:{subIndex}", mp4 ? "mov_text" : "srt"]);
            args.AddRange([$"-metadata:s:s:{subIndex}", "language=" + Languages.ToThreeLetter(r.ExtraSubtitles[i].Language)]);
            args.AddRange([$"-metadata:s:s:{subIndex}", "title=" + Languages.Name(r.ExtraSubtitles[i].Language) + " (downloaded)"]);
            // Never let a downloaded track displace the file's own default.
            args.AddRange([$"-disposition:s:{subIndex}", "0"]);
            subIndex++;
        }
        plan.SubtitleStreams = subIndex;
        if (subIndex > 0) summary.Add(subIndex == 1 ? "1 subtitle track" : $"{subIndex} subtitle tracks");
        if (plan.Sidecars.Count > 0) summary.Add($"{plan.Sidecars.Count} sidecar subtitle file(s)");

        // ---- fonts, chapters, metadata ----
        if (!mp4 && p.KeepAttachments && probe.Attachments.Any())
        {
            args.AddRange(["-map", "0:t?", "-c:t", "copy"]);
            summary.Add("fonts kept");
        }
        args.AddRange(["-map_metadata", p.KeepMetadata ? "0" : "-1"]);
        args.AddRange(["-map_chapters", p.KeepChapters ? "0" : "-1"]);
        AddMarker(args, p, probe, mp4);
        if (mp4) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-max_muxing_queue_size", "4096"]);

        AddExtraArgs(args, p.ExtraArgs);
        // A downloaded subtitle may have been timed for a longer cut; do not let it stretch the file past its video.
        if (r.ExtraSubtitles.Count > 0 && probe.DurationSeconds > 0)
            args.AddRange(["-t", probe.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-f", mp4 ? "mp4" : "matroska", r.OutputPath]);
        plan.Summary = string.Join(" · ", summary);
        return plan;
    }

    private static List<string> BuildVideoFilters(Profile p, StreamInfo video, List<string> summary)
    {
        var filters = new List<string>();

        if (p.Deinterlace == DeinterlaceMode.Always || (p.Deinterlace == DeinterlaceMode.Auto && video.IsInterlaced))
        {
            filters.Add("bwdif=mode=send_frame:parity=auto:deint=all");
            summary.Add("deinterlaced");
        }

        string? denoise = p.Denoise switch
        {
            DenoiseLevel.Light => "hqdn3d=1.5:1.5:4:4",
            DenoiseLevel.Medium => "hqdn3d=3:3:6:6",
            DenoiseLevel.Strong => "hqdn3d=5:5:9:9",
            _ => null,
        };
        if (denoise is not null) filters.Add(denoise);

        if (p.MaxHeight > 0)
        {
            // The limit is a 16:9 box (720 means 1280x720), turned on its side for portrait video.
            int boxH = p.MaxHeight;
            int boxW = (int)Math.Round(p.MaxHeight * 16 / 9.0 / 2) * 2;
            int w = video.DisplayWidth, h = video.DisplayHeight;
            if (h > w) (boxW, boxH) = (boxH, boxW);
            if (w > boxW || h > boxH)
            {
                filters.Add($"scale={boxW}:{boxH}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos");
                summary.Add($"scaled to fit {Math.Max(boxW, boxH)}x{Math.Min(boxW, boxH)}");
            }
        }

        if (p.MaxFps > 0 && video.FrameRate > p.MaxFps * 1.1)
        {
            // Halving (59.94 to 29.97, 50 to 25) drops every other frame evenly; anything else is resampled.
            double half = video.FrameRate / 2;
            string target = half <= p.MaxFps * 1.02 && half >= 20 ? HalfRate(video) : p.MaxFps.ToString(CultureInfo.InvariantCulture);
            filters.Add("fps=" + target);
            summary.Add($"{Inv(Probing.Ffprobe.Rational(target))} fps");
        }
        return filters;
    }

    private static string HalfRate(StreamInfo video)
    {
        var parts = (video.FrameRateRational ?? "").Split('/');
        if (parts.Length == 2 && long.TryParse(parts[0], out var num) && long.TryParse(parts[1], out var den) && den > 0)
            return $"{num}/{den * 2}";
        return Inv(video.FrameRate / 2);
    }

    private static void AddVideoEncoderArgs(List<string> args, EncoderDef encoder, Profile p, StreamInfo video,
        bool tenBit, bool hdr, bool mp4, EncodeRequest r, List<string> summary)
    {
        bool constantQuality = p.RateMode == RateControlMode.ConstantQuality;
        string speed = SpeedNames.Contains(p.Speed) ? p.Speed : "medium";
        int speedIndex = Array.IndexOf(SpeedNames, speed); // 0 = veryslow ... 6 = veryfast
        string tune = (p.Tune ?? "none").ToLowerInvariant();
        string bitrate = p.BitrateKbps + "k";
        var codecParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.EncoderParams) && !encoder.Hardware)
            codecParams.AddRange(p.EncoderParams.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // Hardware encoders use a different quality scale from the software CRF the profile is written in.
        double av1ToH26x = encoder.Codec == VideoCodec.AV1 ? 51.0 / 63.0 : 1.0;
        double hardwareQ = Math.Clamp(Math.Round(p.Quality * av1ToH26x + p.HardwareQualityOffset), 1, 51);
        string qualityLabel;

        args.AddRange(["-c:v", encoder.Id]);
        switch (encoder.Id)
        {
            case "libx264":
                args.AddRange(["-preset", speed]);
                if (tune is "animation" or "film" or "grain") args.AddRange(["-tune", tune]);
                if (constantQuality) args.AddRange(["-crf", Inv(p.Quality)]); else args.AddRange(["-b:v", bitrate]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                if (codecParams.Count > 0) args.AddRange(["-x264-params", string.Join(':', codecParams)]);
                qualityLabel = "CRF " + Inv(p.Quality);
                break;

            case "libx265":
                args.AddRange(["-preset", speed]);
                if (tune is "animation" or "grain") args.AddRange(["-tune", tune]);
                if (constantQuality) args.AddRange(["-crf", Inv(p.Quality)]); else args.AddRange(["-b:v", bitrate]);
                args.AddRange(["-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p"]);
                codecParams.Insert(0, "log-level=error");
                if (hdr)
                {
                    codecParams.AddRange(["hdr-opt=1", "repeat-headers=1",
                        "colorprim=" + (video.ColorPrimaries ?? "bt2020"), "transfer=" + video.ColorTransfer,
                        "colormatrix=" + (video.ColorSpace ?? "bt2020nc")]);
                    if (r.MasterDisplay is not null) codecParams.Add("master-display=" + r.MasterDisplay);
                    if (r.MaxCll is not null) codecParams.Add("max-cll=" + r.MaxCll);
                }
                args.AddRange(["-x265-params", string.Join(':', codecParams)]);
                qualityLabel = "CRF " + Inv(p.Quality);
                break;

            case "libsvtav1":
                args.AddRange(["-preset", new[] { "2", "3", "4", "6", "8", "10", "12" }[speedIndex]]);
                if (constantQuality) args.AddRange(["-crf", Inv(Math.Round(p.Quality))]); else args.AddRange(["-b:v", bitrate]);
                args.AddRange(["-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p"]);
                codecParams.Insert(0, "tune=0");
                if (tune == "grain") codecParams.Add("film-grain=8");
                if (constantQuality && p.MaxBitrateKbps > 0) codecParams.Add("mbr=" + p.MaxBitrateKbps);
                args.AddRange(["-svtav1-params", string.Join(':', codecParams)]);
                qualityLabel = "CRF " + Inv(Math.Round(p.Quality));
                break;

            case "libaom-av1":
                args.AddRange(["-cpu-used", new[] { "2", "3", "4", "5", "6", "7", "8" }[speedIndex], "-row-mt", "1"]);
                // With -crf, libaom treats -b:v as a ceiling; 0 means unconstrained.
                if (constantQuality) args.AddRange(["-crf", Inv(Math.Round(p.Quality)), "-b:v", p.MaxBitrateKbps > 0 ? p.MaxBitrateKbps + "k" : "0"]);
                else args.AddRange(["-b:v", bitrate]);
                args.AddRange(["-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p"]);
                if (codecParams.Count > 0) args.AddRange(["-aom-params", string.Join(':', codecParams)]);
                qualityLabel = "CRF " + Inv(Math.Round(p.Quality));
                break;

            case "h264_nvenc" or "hevc_nvenc" or "av1_nvenc":
                args.AddRange(["-preset", new[] { "p7", "p7", "p6", "p5", "p4", "p3", "p2" }[speedIndex], "-tune", "hq", "-rc", "vbr"]);
                if (constantQuality) args.AddRange(["-cq", Inv(hardwareQ), "-b:v", "0"]); else args.AddRange(["-b:v", bitrate]);
                args.AddRange(["-spatial-aq", "1", "-temporal-aq", "1", "-rc-lookahead", "32"]);
                args.AddRange(["-pix_fmt", tenBit ? "p010le" : "yuv420p"]);
                qualityLabel = "CQ " + Inv(hardwareQ);
                break;

            case "h264_amf" or "hevc_amf" or "av1_amf":
                args.AddRange(["-quality", speedIndex <= 3 ? "quality" : speedIndex == 4 ? "balanced" : "speed"]);
                if (constantQuality)
                {
                    // AV1 quantisers run 0-255 in AMF, four times the H.264/HEVC range.
                    double qp = encoder.Codec == VideoCodec.AV1 ? hardwareQ * 4 : hardwareQ;
                    args.AddRange(["-rc", "cqp", "-qp_i", Inv(qp), "-qp_p", Inv(qp)]);
                    if (encoder.Codec == VideoCodec.H264) args.AddRange(["-qp_b", Inv(Math.Min(qp + 2, 51))]);
                }
                else args.AddRange(["-rc", "vbr_peak", "-b:v", bitrate]);
                args.AddRange(["-pix_fmt", tenBit ? "p010le" : "nv12"]);
                qualityLabel = "QP " + Inv(hardwareQ);
                break;

            default: // Quick Sync
                args.AddRange(["-preset", speed]);
                if (constantQuality) args.AddRange(["-global_quality", Inv(hardwareQ)]); else args.AddRange(["-b:v", bitrate]);
                args.AddRange(["-pix_fmt", tenBit ? "p010le" : "nv12"]);
                qualityLabel = "ICQ " + Inv(hardwareQ);
                break;
        }

        if (!constantQuality)
        {
            int peak = p.MaxBitrateKbps > 0 ? p.MaxBitrateKbps : (int)(p.BitrateKbps * 1.5);
            args.AddRange(["-maxrate", peak + "k", "-bufsize", peak * 2 + "k"]);
            qualityLabel = p.BitrateKbps + " kbps";
        }
        else if (p.MaxBitrateKbps > 0 && encoder.Id is not ("libsvtav1" or "libaom-av1"))
        {
            args.AddRange(["-maxrate", p.MaxBitrateKbps + "k", "-bufsize", p.MaxBitrateKbps * 2 + "k"]);
        }

        if (hdr)
        {
            // Tag the stream so players know it is HDR; without these it is shown washed out.
            args.AddRange(["-color_primaries", video.ColorPrimaries ?? "bt2020", "-color_trc", video.ColorTransfer!,
                "-colorspace", video.ColorSpace ?? "bt2020nc"]);
        }
        if (mp4 && encoder.Codec == VideoCodec.HEVC) args.AddRange(["-tag:v", "hvc1"]); // Apple players insist on this tag

        string codecName = encoder.Codec == VideoCodec.H264 ? "H.264" : encoder.Codec.ToString();
        summary.Insert(0, $"{codecName} ({encoder.Id}) {qualityLabel}, {speed}, {(tenBit ? "10-bit" : "8-bit")}{(hdr ? " HDR" : "")}");
    }

    public static readonly string[] SpeedNames = ["veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast"];
    public static readonly string[] TuneNames = ["none", "animation", "film", "grain"];

    // ------------------------------------------------------------------ audio streams

    private static readonly HashSet<string> OpusLayouts = ["mono", "stereo", "3.0", "quad", "5.0", "5.1", "6.1", "7.1"];

    public static bool IsLossless(StreamInfo s) =>
        s.CodecName is "flac" or "alac" or "ape" or "wavpack" or "tta" or "truehd" or "mlp"
        || s.CodecName.StartsWith("pcm_", StringComparison.Ordinal)
        || (s.CodecName == "dts" && (s.Profile ?? "").Contains("MA", StringComparison.Ordinal));

    private static bool ContainerHolds(string container, string codec) => container switch
    {
        "mp4" or "m4a" => codec is "aac" or "mp3" or "ac3" or "eac3" or "opus" or "alac",
        _ => true,
    };

    private static int BitrateFor(Profile p, int channels) => channels switch
    {
        <= 1 => p.MonoKbps,
        2 => p.StereoKbps,
        <= 6 => p.SurroundKbps,
        _ => p.Surround71Kbps,
    };

    private static string ChannelLabel(int channels) => channels switch
    {
        1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => channels + "ch",
    };

    /// <summary>Add the arguments for output audio stream <paramref name="n"/> and describe what was decided.</summary>
    private static string AddAudioArgs(List<string> args, int n, StreamInfo s, Profile p, string container, List<string> notes)
    {
        int channels = s.Channels > 0 ? s.Channels : 2;
        int target = p.MaxChannels > 0 ? Math.Min(channels, p.MaxChannels) : channels;
        var codec = p.AudioCodec;
        bool copy = codec == AudioCodec.Copy;

        if (!copy && !IsLossless(s) && target == channels && ContainerHolds(container, s.CodecName))
        {
            // Re-encoding a lossy track that is already small costs quality and saves nothing. A track
            // already in the target codec is always left alone; other codecs only if the profile allows it.
            int wanted = BitrateFor(p, target);
            bool small = s.BitRate > 0 ? s.BitRate <= wanted * 1250L : s.CodecName is "aac" or "opus" or "vorbis" or "mp3";
            bool sameCodec = s.CodecName == codec switch
            {
                AudioCodec.Opus => "opus", AudioCodec.Aac => "aac", AudioCodec.Ac3 => "ac3", AudioCodec.Eac3 => "eac3", AudioCodec.Mp3 => "mp3", _ => "",
            };
            copy = small && (sameCodec || p.CopyEfficientAudio);
        }
        if (copy && !ContainerHolds(container, s.CodecName))
        {
            copy = false;
            codec = AudioCodec.Aac;
            notes.Add($"Audio track {s.Index} ({s.CodecName}) cannot be copied into {container.ToUpperInvariant()}; it is encoded to AAC.");
        }
        if (copy)
        {
            args.AddRange([$"-c:a:{n}", "copy"]);
            return $"{s.CodecName} {ChannelLabel(channels)} copied";
        }
        if (codec == AudioCodec.Copy) codec = AudioCodec.Aac;

        // Encoder limits.
        if (codec == AudioCodec.Mp3) target = Math.Min(target, 2);
        if (codec is AudioCodec.Ac3 or AudioCodec.Eac3) target = Math.Min(target, 6);
        target = Math.Min(target, 8);
        int kbps = BitrateFor(p, target);

        switch (codec)
        {
            case AudioCodec.Opus:
                args.AddRange([$"-c:a:{n}", "libopus", $"-b:a:{n}", kbps + "k"]);
                // libopus rejects layouts such as 5.1(side); remap to the equivalent it accepts.
                if (target == channels && !OpusLayouts.Contains(s.ChannelLayout ?? ""))
                {
                    string layout = target switch { 1 => "mono", 2 => "stereo", 3 => "3.0", 4 => "quad", 5 => "5.0", 6 => "5.1", 7 => "6.1", _ => "7.1" };
                    args.AddRange([$"-filter:a:{n}", "aformat=channel_layouts=" + layout]);
                }
                break;
            case AudioCodec.Aac:
                args.AddRange([$"-c:a:{n}", "aac", $"-b:a:{n}", kbps + "k"]);
                break;
            case AudioCodec.Ac3:
                args.AddRange([$"-c:a:{n}", "ac3", $"-b:a:{n}", Math.Min(kbps, 640) + "k"]);
                break;
            case AudioCodec.Eac3:
                args.AddRange([$"-c:a:{n}", "eac3", $"-b:a:{n}", kbps + "k"]);
                break;
            case AudioCodec.Flac:
                args.AddRange([$"-c:a:{n}", "flac", $"-compression_level:a:{n}", "8"]);
                break;
            case AudioCodec.Mp3:
                args.AddRange([$"-c:a:{n}", "libmp3lame", $"-b:a:{n}", kbps + "k"]);
                break;
        }
        if (target != channels) args.AddRange([$"-ac:a:{n}", target.ToString(CultureInfo.InvariantCulture)]);
        foreach (var tag in s.StatTags)
            args.AddRange([$"-metadata:s:a:{n}", tag + "="]);

        string name = codec switch { AudioCodec.Aac => "AAC", AudioCodec.Ac3 => "AC-3", AudioCodec.Eac3 => "E-AC-3", AudioCodec.Flac => "FLAC", AudioCodec.Mp3 => "MP3", _ => "Opus" };
        return codec == AudioCodec.Flac ? $"FLAC {ChannelLabel(target)}" : $"{name} {ChannelLabel(target)} {kbps}k";
    }

    private static List<StreamInfo> SelectByLanguage(List<StreamInfo> streams, string languages, string what, List<string> notes)
    {
        var wanted = Languages.ParseList(languages);
        if (wanted.Count == 0 || streams.Count == 0) return streams;
        // Untagged tracks are kept: dropping the only track because nobody labelled it would be worse.
        var kept = streams.Where(s => Languages.ToTwoLetter(s.Language) is not { } lang || wanted.Contains(lang)).ToList();
        if (kept.Count == 0 && what == "audio")
        {
            notes.Add($"No audio track matches \"{languages}\"; keeping all of them.");
            return streams;
        }
        return kept;
    }

    // ------------------------------------------------------------------ subtitles

    private enum SubAction { Copy, ToSrt, ToMovText, Sidecar, Drop }

    private static bool IsTextSubtitle(string codec) =>
        codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text" or "subviewer" or "microdvd" or "sami" or "realtext" or "stl" or "jacosub" or "mpl2" or "vplayer" or "pjs";

    private static bool IsBitmapSubtitle(string codec) =>
        codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";

    private static SubAction SubtitleAction(StreamInfo sub, bool mp4, SubtitleFallback fallback)
    {
        string codec = sub.CodecName;
        if (!mp4)
        {
            // Matroska stores nearly every subtitle format untouched.
            if (codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle") return SubAction.Copy;
            if (IsTextSubtitle(codec)) return SubAction.ToSrt;
            return SubAction.Drop;
        }

        if (codec == "mov_text") return SubAction.Copy;
        if (fallback == SubtitleFallback.Drop) return SubAction.Drop;
        if (IsTextSubtitle(codec))
        {
            if (fallback == SubtitleFallback.Convert) return SubAction.ToMovText;
            // Only formats with a standalone file form can be written out as they are.
            return codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" ? SubAction.Sidecar : SubAction.ToMovText;
        }
        // Picture subtitles cannot become text; a sidecar is the only way to keep them.
        return IsBitmapSubtitle(codec) && codec != "xsub" ? SubAction.Sidecar : SubAction.Drop;
    }

    private static (string Muxer, string Extension) SidecarFormat(string codec) => codec switch
    {
        "subrip" or "srt" => ("srt", "srt"),
        "ass" or "ssa" => ("ass", "ass"),
        "webvtt" => ("webvtt", "vtt"),
        "hdmv_pgs_subtitle" => ("sup", "sup"),
        _ => ("matroska", "mks"),
    };

    // ------------------------------------------------------------------ audio files

    private static EncodePlan BuildAudio(EncodeRequest r, EncodePlan plan)
    {
        var p = r.Profile;
        var probe = r.Probe;
        var source = probe.AudioStreams.FirstOrDefault();
        if (source is null) return plan.SkipWith("No audio stream found");

        string container = plan.Extension;
        var codec = p.AudioCodec;
        bool lossless = IsLossless(source);
        long bitrate = source.BitRate > 0 ? source.BitRate : probe.BitRate;

        if (source.CodecName.StartsWith("dsd_", StringComparison.Ordinal))
            return plan.SkipWith("DSD audio is left alone");

        if (lossless)
        {
            switch (p.LosslessSource)
            {
                case LosslessSourcePolicy.Skip:
                    return plan.SkipWith("Lossless source; this profile leaves those alone");
                case LosslessSourcePolicy.KeepLossless:
                    if (source.CodecName == "flac") return plan.SkipWith("Already FLAC (lossless); nothing to gain");
                    codec = AudioCodec.Flac;
                    break;
            }
        }
        else
        {
            if (codec == AudioCodec.Flac || container == "flac")
                return plan.SkipWith("Lossy source; converting it to FLAC would only make it bigger");
            if (p.SkipBelowKbps > 0 && bitrate > 0 && bitrate <= p.SkipBelowKbps * 1000L)
                return plan.SkipWith($"Already small: {source.CodecName} at {Format.Bitrate(bitrate)}");
        }

        // Each container has one codec it is meant for.
        var natural = container switch
        {
            "opus" => AudioCodec.Opus, "m4a" => AudioCodec.Aac, "mp3" => AudioCodec.Mp3, "flac" => AudioCodec.Flac, _ => codec,
        };
        if (codec != natural && codec != AudioCodec.Copy)
        {
            plan.Notes.Add($".{container} files hold {natural} audio; using that instead of {codec}.");
            codec = natural;
        }
        if (codec == AudioCodec.Copy && !ContainerHolds(container, source.CodecName)) codec = natural == AudioCodec.Copy ? AudioCodec.Aac : natural;

        var args = plan.Args;
        args.AddRange(["-hide_banner", "-y", "-nostdin", "-i", r.File.Path, "-map", $"0:{source.Index}"]);

        var cover = probe.CoverArt;
        bool keepsCover = container is "m4a" or "mp3" or "flac" or "mka";
        if (cover is not null && keepsCover)
            args.AddRange(["-map", $"0:{cover.Index}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        else if (cover is not null)
            plan.Notes.Add($"Cover art is not carried into .{container} files.");

        // Reuse the per-stream logic with a copy of the profile that pins the decided codec.
        var effective = p.Clone();
        effective.AudioCodec = codec;
        effective.CopyEfficientAudio = false;
        string summary = AddAudioArgs(args, 0, source, effective, container, plan.Notes);
        plan.AudioStreams = 1;

        args.AddRange(["-map_metadata", p.KeepMetadata ? "0" : "-1"]);
        args.AddRange(["-map_chapters", p.KeepChapters ? "0" : "-1"]);
        AddMarker(args, p, probe, mp4: container == "m4a");
        if (container == "m4a") args.AddRange(["-movflags", "+faststart"]);
        if (container == "mp3") args.AddRange(["-id3v2_version", "3"]); // the tag version older players and cars read
        AddExtraArgs(args, p.ExtraArgs);

        string muxer = container switch { "m4a" => "ipod", "mka" => "matroska", "opus" => "opus", _ => container };
        args.AddRange(["-f", muxer, r.OutputPath]);
        plan.Summary = summary + (cover is not null && keepsCover ? " · cover art kept" : "");
        return plan;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Stamp the output so later scans know not to compress it again.</summary>
    private static void AddMarker(List<string> args, Profile p, ProbeResult probe, bool mp4)
    {
        string marker = MarkerValue(p);
        if (mp4)
        {
            // MP4 discards tags it does not know; "keywords" is one it keeps.
            string existing = probe.Tag("keywords") is { Length: > 0 } k && !k.Contains(MediaFile.MarkerPrefix, StringComparison.Ordinal) ? k + "; " : "";
            args.AddRange(["-metadata", "keywords=" + existing + marker]);
        }
        else
        {
            args.AddRange(["-metadata", MediaFile.MarkerTag + "=" + marker]);
        }
    }

    private static void AddExtraArgs(List<string> args, string extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return;
        // Minimal shell-like splitting: spaces separate, double quotes group.
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in extra)
        {
            if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) args.Add(current.ToString());
    }
}
