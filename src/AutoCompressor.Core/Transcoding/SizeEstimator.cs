using System.Globalization;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Transcoding;

/// <summary>
/// Predicts how big a file will be after compression. <see cref="Estimate"/> is an instant rule of
/// thumb for whole libraries; <see cref="MeasureAsync"/> encodes short samples with the real command
/// and is far more trustworthy for one file.
/// </summary>
public static class SizeEstimator
{
    /// <summary>How demanding each kind of content is at a given quality target, relative to ordinary live action.</summary>
    private static double ContentFactor(ContentType type) => type switch
    {
        ContentType.Anime => 0.7,
        ContentType.Animation => 0.65,
        ContentType.Sitcom => 0.75,
        ContentType.StillCam => 0.5,
        ContentType.Documentary => 1.1,
        ContentType.Cinematic => 1.25,
        ContentType.Concert => 1.2,
        ContentType.Sports => 1.6,
        _ => 1.0,
    };

    /// <summary>A rough size in bytes, or null when the plan is a skip or the file's length is unknown.</summary>
    public static long? Estimate(MediaFile file, ProbeResult probe, Profile profile, EncodePlan plan)
    {
        if (plan.Skip || probe.DurationSeconds <= 0) return null;

        double audioBits = AudioBitRate(probe, plan);
        if (file.Kind == MediaKind.Audio || probe.Video is null)
        {
            // FLAC typically lands around 60% of uncompressed PCM.
            if (plan.Extension == "flac") return (long)(file.Size * 0.6);
            return Math.Min(file.Size, (long)(audioBits * probe.DurationSeconds / 8 * 1.01));
        }

        var video = probe.Video;
        long sourceVideoBits = probe.EstimateVideoBitRate();
        double videoBits;
        if (plan.Encoder is null) videoBits = sourceVideoBits;
        else if (profile.RateMode == RateControlMode.AverageBitrate) videoBits = profile.BitrateKbps * 1000.0;
        else
        {
            // Bitrate at 1080p24 for the profile's quality value; it roughly halves every six CRF steps.
            double reference = profile.Codec switch
            {
                VideoCodec.H264 => 3000 * Math.Pow(2, (23 - profile.Quality) / 6),
                VideoCodec.AV1 => 1500 * Math.Pow(2, (30 - profile.Quality) / 7),
                _ => 1800 * Math.Pow(2, (23 - profile.Quality) / 6),
            };

            double w = video.DisplayWidth, h = video.DisplayHeight;
            if (profile.MaxHeight > 0)
            {
                double boxH = profile.MaxHeight, boxW = profile.MaxHeight * 16 / 9.0;
                if (h > w) (boxW, boxH) = (boxH, boxW);
                double shrink = Math.Min(1, Math.Min(boxW / Math.Max(w, 1), boxH / Math.Max(h, 1)));
                w *= shrink;
                h *= shrink;
            }
            double fps = video.FrameRate > 0 ? video.FrameRate : 24;
            if (profile.MaxFps > 0 && fps > profile.MaxFps * 1.1) fps = fps / 2 <= profile.MaxFps * 1.02 ? fps / 2 : profile.MaxFps;
            fps = Math.Clamp(fps, 10, 60);

            videoBits = reference * 1000
                        * Math.Pow(Math.Max(w * h, 1) / (1920.0 * 1080.0), 0.75)
                        * Math.Sqrt(fps / 24.0)
                        * ContentFactor(file.Classification.Type)
                        * (plan.Encoder.Hardware ? 1.25 : 1.0);
            if (profile.MaxBitrateKbps > 0) videoBits = Math.Min(videoBits, profile.MaxBitrateKbps * 1000.0);
            // A quality-targeted encode of an already lean source ends up near the source, not above it.
            if (sourceVideoBits > 0) videoBits = Math.Min(videoBits, sourceVideoBits * 0.95);
        }

        long estimate = (long)((videoBits + audioBits) * probe.DurationSeconds / 8 * 1.005);
        return Math.Min(estimate, file.Size);
    }

    /// <summary>Total audio bitrate the plan will produce, read back from its arguments.</summary>
    private static double AudioBitRate(ProbeResult probe, EncodePlan plan)
    {
        var args = plan.Args;
        var mapped = new List<StreamInfo>();
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] != "-map" || !args[i + 1].StartsWith("0:", StringComparison.Ordinal)) continue;
            if (int.TryParse(args[i + 1].AsSpan(2), out int index) && probe.Streams.FirstOrDefault(s => s.Index == index) is { IsAudio: true } stream)
                mapped.Add(stream);
        }

        double total = 0;
        for (int n = 0; n < mapped.Count; n++)
        {
            var source = mapped[n];
            long sourceBits = source.BitRate > 0 ? source.BitRate : Math.Max(source.Channels, 1) * 64_000L;
            int codec = args.IndexOf($"-c:a:{n}");
            int rate = args.IndexOf($"-b:a:{n}");
            if (codec >= 0 && args[codec + 1] == "flac") total += sourceBits * 0.6;
            else if (rate >= 0 && double.TryParse(args[rate + 1].TrimEnd('k'), NumberStyles.Float, CultureInfo.InvariantCulture, out double kbps)) total += kbps * 1000;
            else total += sourceBits; // copied
        }
        return total;
    }

    public sealed record Measurement(long Bytes, int Samples, double SampledSeconds);

    /// <summary>
    /// Encode a few short stretches of the file with the plan's exact settings and scale the result
    /// up to the full length. Takes seconds to a couple of minutes depending on the encoder.
    /// </summary>
    public static async Task<Measurement?> MeasureAsync(Tools tools, ProbeResult probe, EncodePlan plan, CancellationToken ct = default)
    {
        if (plan.Skip || tools.Ffmpeg is null || probe.DurationSeconds <= 0) return null;
        double duration = probe.DurationSeconds;
        const double length = 6;
        double[] starts = duration <= length * 4 ? [0] : [duration * 0.15, duration * 0.5, duration * 0.8];
        double sampleLength = Math.Min(length, duration);

        // The sample must not carry fixed-size extras (fonts) that would be multiplied up with it.
        var template = new List<string>(plan.Args);
        int fonts = template.IndexOf("0:t?");
        if (fonts >= 1 && fonts + 2 < template.Count) template.RemoveRange(fonts - 1, 4);
        int input = template.IndexOf("-i");
        if (input < 0) return null;

        string temp = Path.Combine(AppPaths.DataDir, $"sample-{Guid.NewGuid():N}.{plan.Extension}");
        long bytes = 0;
        int done = 0;
        try
        {
            foreach (double start in starts)
            {
                ct.ThrowIfCancellationRequested();
                var args = new List<string>(template);
                args.InsertRange(input, ["-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", sampleLength.ToString("0.###", CultureInfo.InvariantCulture)]);
                args[^1] = temp;
                args.InsertRange(0, ["-loglevel", "error"]);
                var result = await ProcessRunner.CaptureAsync(tools.Ffmpeg, args, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(temp)) continue;
                bytes += new FileInfo(temp).Length;
                done++;
            }
        }
        catch (TimeoutException) { /* use whatever samples finished */ }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
        if (done == 0) return null;
        double sampled = done * sampleLength;
        return new Measurement((long)(bytes * duration / sampled), done, sampled);
    }
}
