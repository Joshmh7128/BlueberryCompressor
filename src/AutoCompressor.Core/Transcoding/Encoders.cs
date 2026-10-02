using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Transcoding;

public enum EncoderVendor { Software, Nvidia, Amd, Intel }

/// <summary>How much a job cares about 10-bit output: HDR cannot do without it, some profiles merely prefer it.</summary>
public enum TenBitNeed { None, Preferred, Required }

public sealed record EncoderDef(string Id, VideoCodec Codec, EncoderVendor Vendor, bool TenBit, string DisplayName)
{
    public bool Hardware => Vendor != EncoderVendor.Software;
}

public static class EncoderCatalog
{
    /// <summary>In order of preference within each codec and kind.</summary>
    public static readonly EncoderDef[] All =
    [
        new("libx264", VideoCodec.H264, EncoderVendor.Software, false, "x264 (software)"),
        new("libx265", VideoCodec.HEVC, EncoderVendor.Software, true, "x265 (software)"),
        new("libsvtav1", VideoCodec.AV1, EncoderVendor.Software, true, "SVT-AV1 (software)"),
        new("libaom-av1", VideoCodec.AV1, EncoderVendor.Software, true, "libaom AV1 (software, slow)"),
        new("h264_nvenc", VideoCodec.H264, EncoderVendor.Nvidia, false, "NVIDIA NVENC H.264"),
        new("hevc_nvenc", VideoCodec.HEVC, EncoderVendor.Nvidia, true, "NVIDIA NVENC HEVC"),
        new("av1_nvenc", VideoCodec.AV1, EncoderVendor.Nvidia, true, "NVIDIA NVENC AV1"),
        new("h264_qsv", VideoCodec.H264, EncoderVendor.Intel, false, "Intel Quick Sync H.264"),
        new("hevc_qsv", VideoCodec.HEVC, EncoderVendor.Intel, true, "Intel Quick Sync HEVC"),
        new("av1_qsv", VideoCodec.AV1, EncoderVendor.Intel, true, "Intel Quick Sync AV1"),
        new("h264_amf", VideoCodec.H264, EncoderVendor.Amd, false, "AMD AMF H.264"),
        new("hevc_amf", VideoCodec.HEVC, EncoderVendor.Amd, true, "AMD AMF HEVC"),
        new("av1_amf", VideoCodec.AV1, EncoderVendor.Amd, true, "AMD AMF AV1"),
    ];

    public static EncoderDef? Find(string id) => All.FirstOrDefault(e => e.Id == id);
}

/// <summary>Which encoders this machine's ffmpeg and hardware can actually run.</summary>
public sealed class EncoderAvailability
{
    public string FfmpegVersion { get; set; } = "";
    public DateTime CheckedUtc { get; set; }
    /// <summary>Encoder id to null when usable, or the reason it is not.</summary>
    public Dictionary<string, string?> Status { get; set; } = new();

    /// <summary>
    /// Encoders this ffmpeg build can actually feed 10-bit frames. A codec supporting 10-bit is not
    /// enough: older builds accept 10-bit input for some GPU encoders and quietly encode 8-bit.
    /// Null in results saved by an earlier version, which are then re-detected.
    /// </summary>
    public HashSet<string>? TenBit { get; set; }

    public bool IsUsable(string id) => Status.TryGetValue(id, out var problem) && problem is null;

    public bool SupportsTenBit(EncoderDef encoder) => encoder.TenBit && TenBit is not null && TenBit.Contains(encoder.Id);

    public IEnumerable<EncoderDef> Usable => EncoderCatalog.All.Where(e => IsUsable(e.Id));

    /// <summary>
    /// Pick the encoder for a profile. Hardware is used when asked for and present; otherwise the
    /// software encoder for the codec. Returns null when nothing can produce the codec at all.
    /// </summary>
    public EncoderDef? Resolve(Profile profile, AppSettings settings, bool forceSoftware = false, TenBitNeed need = TenBitNeed.None)
    {
        bool wantHardware = !forceSoftware && profile.Encoder switch
        {
            EncoderPreference.Hardware => true,
            EncoderPreference.Software => false,
            _ => settings.EncoderPolicy == EncoderPolicy.Hardware,
        };

        var candidates = Usable.Where(e => e.Codec == profile.Codec).ToList();
        if (wantHardware)
        {
            var hardware = candidates.Where(e => e.Hardware).ToList();
            EncoderVendor? vendor = settings.HardwareVendor switch
            {
                HardwareVendor.Nvidia => EncoderVendor.Nvidia,
                HardwareVendor.Amd => EncoderVendor.Amd,
                HardwareVendor.Intel => EncoderVendor.Intel,
                _ => null,
            };
            var ofVendor = vendor is null ? hardware : hardware.Where(e => e.Vendor == vendor).ToList();
            var capable = hardware.Where(SupportsTenBit).ToList();

            EncoderDef? chosen = need switch
            {
                // HDR must stay 10-bit: capability beats the vendor preference, and software beats an 8-bit GPU encode.
                TenBitNeed.Required => ofVendor.FirstOrDefault(SupportsTenBit) ?? capable.FirstOrDefault(),
                // A profile that prefers 10-bit takes a capable GPU when the choice is ours, but an explicit vendor choice stands.
                TenBitNeed.Preferred => ofVendor.FirstOrDefault(SupportsTenBit) ?? (vendor is null ? capable.FirstOrDefault() : null)
                                        ?? ofVendor.FirstOrDefault() ?? hardware.FirstOrDefault(),
                _ => ofVendor.FirstOrDefault() ?? hardware.FirstOrDefault(),
            };
            if (chosen is not null) return chosen;
        }
        return candidates.FirstOrDefault(e => !e.Hardware);
    }
}

public static class EncoderDetector
{
    /// <summary>
    /// List the encoders ffmpeg was built with, then actually run each hardware encoder for a few
    /// frames: being compiled in says nothing about whether the GPU and driver are there.
    /// </summary>
    public static async Task<EncoderAvailability> DetectAsync(Tools tools, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var availability = new EncoderAvailability { FfmpegVersion = tools.Version ?? "", CheckedUtc = DateTime.UtcNow, TenBit = [] };
        if (tools.Ffmpeg is null) return availability;

        var listing = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var compiled = listing.StdOut.Split('\n')
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].Length == 6)
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var encoder in EncoderCatalog.All)
        {
            ct.ThrowIfCancellationRequested();
            if (!compiled.Contains(encoder.Id))
            {
                availability.Status[encoder.Id] = "Not included in this ffmpeg build";
                continue;
            }
            if (encoder.TenBit && await AcceptsTenBitAsync(tools, encoder.Id, ct).ConfigureAwait(false))
                availability.TenBit.Add(encoder.Id);
            if (!encoder.Hardware)
            {
                availability.Status[encoder.Id] = null;
                continue;
            }

            progress?.Report($"Testing {encoder.DisplayName}...");
            string[] args =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=0.5",
                "-vf", "format=nv12", "-frames:v", "8", "-c:v", encoder.Id, "-f", "null", "-",
            ];
            try
            {
                var test = await ProcessRunner.CaptureAsync(tools.Ffmpeg, args, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                availability.Status[encoder.Id] = test.ExitCode == 0 ? null : "No supported GPU or driver found";
            }
            catch (TimeoutException)
            {
                availability.Status[encoder.Id] = "Timed out while testing";
            }
        }
        return availability;
    }

    /// <summary>ffmpeg lists the pixel formats each encoder takes; 10-bit ones are named p010 or ...p10le.</summary>
    private static async Task<bool> AcceptsTenBitAsync(Tools tools, string encoderId, CancellationToken ct)
    {
        try
        {
            var help = await ProcessRunner.CaptureAsync(tools.Ffmpeg!, ["-hide_banner", "-h", "encoder=" + encoderId], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var formats = help.StdOut.Split('\n').FirstOrDefault(l => l.Contains("Supported pixel formats", StringComparison.OrdinalIgnoreCase)) ?? "";
            return formats.Contains("p010", StringComparison.Ordinal) || formats.Contains("p10le", StringComparison.Ordinal);
        }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Use the saved result when ffmpeg has not changed, since testing takes a few seconds.</summary>
    public static async Task<EncoderAvailability> LoadOrDetectAsync(Tools tools, bool force, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!force)
        {
            var cached = JsonStore.Load<EncoderAvailability>(AppPaths.EncoderCache);
            if (cached.Status.Count > 0 && cached.TenBit is not null && cached.FfmpegVersion == (tools.Version ?? "") && DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromDays(14))
                return cached;
        }
        var fresh = await DetectAsync(tools, progress, ct).ConfigureAwait(false);
        if (fresh.Status.Count > 0) JsonStore.Save(AppPaths.EncoderCache, fresh);
        return fresh;
    }
}
