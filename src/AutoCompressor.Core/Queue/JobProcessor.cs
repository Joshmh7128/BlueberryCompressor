using AutoCompressor.Core.Recognition;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Probing;
using AutoCompressor.Core.Scanning;
using AutoCompressor.Core.Subtitles;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Queue;

/// <summary>Everything a job needs from the running application.</summary>
public sealed class JobServices
{
    public required Func<AppSettings> Settings { get; init; }
    public required Func<Tools> Tools { get; init; }
    public required Func<EncoderAvailability> Encoders { get; init; }
    public required History History { get; init; }
}

public sealed class JobFailedException(string message) : Exception(message);

/// <summary>
/// Takes one job from source file to finished output: probe, plan, fetch subtitles, encode,
/// verify, and only then touch the original.
/// </summary>
public sealed class JobProcessor(JobServices services)
{
    public async Task RunAsync(QueueJob job, FfmpegRunner runner, Action<QueueJob> changed, CancellationToken ct)
    {
        var settings = services.Settings();
        var tools = services.Tools();
        if (!tools.Available) throw new JobFailedException("ffmpeg was not found. Set its location in Settings.");

        void Stage(string stage)
        {
            job.Stage = stage;
            changed(job);
        }

        var sourceInfo = new FileInfo(job.SourcePath);
        if (!sourceInfo.Exists) throw new JobFailedException("The source file no longer exists.");
        job.SourceSize = sourceInfo.Length;
        var sourceModified = sourceInfo.LastWriteTimeUtc;

        if (job.Output.Mode != OutputMode.Replace && services.History.FindCopy(job.SourcePath, job.SourceSize, sourceModified) is { } copy)
        {
            Finish(job, JobStatus.Skipped, $"A compressed copy already exists: {Path.GetFileName(copy.OutputPath)}");
            return;
        }

        // ---- probe ----
        Stage("Reading file");
        ProbeResult probe;
        try { probe = await Ffprobe.ProbeAsync(tools.Ffprobe!, job.SourcePath, ct).ConfigureAwait(false); }
        catch (ProbeException ex) { throw new JobFailedException("Could not read the file: " + ex.Message); }

        var file = new MediaFile
        {
            Path = job.SourcePath, Size = job.SourceSize, ModifiedUtc = sourceModified, Kind = job.Kind, Root = job.Root, Probe = probe,
        };
        file.Parsed = FileNameParser.Parse(file.Path);

        // ---- where the output goes ----
        string extension = CommandBuilder.ExtensionFor(job.Profile, probe);
        string baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        string targetDir = TargetDirectory(job);
        string tempPath = Path.Combine(targetDir, $"{baseName}{MediaExtensions.TempMarker}.{extension}");
        var tempFiles = new List<string> { tempPath };

        var request = new EncodeRequest
        {
            File = file, Probe = probe, Profile = job.Profile, Settings = settings, Encoders = services.Encoders(), OutputPath = tempPath,
        };
        var plan = CommandBuilder.Build(request);
        if (plan.Skip)
        {
            Finish(job, JobStatus.Skipped, plan.SkipReason!);
            return;
        }

        Directory.CreateDirectory(targetDir);
        EnsureFreeSpace(targetDir, job.SourceSize);
        job.LogPath = Path.Combine(AppPaths.LogsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{job.Id}.log");

        var downloaded = new List<ExternalSubtitle>();
        try
        {
            // ---- subtitles from online databases ----
            var fetch = settings.SubtitleFetch;
            if (fetch.Enabled && job.Kind == MediaKind.Video && job.Profile.Subtitles != SubtitleMode.None
                && SubtitleFetcher.MissingLanguages(file, probe, fetch).Count > 0)
            {
                Stage("Looking for subtitles");
                using var fetcher = new SubtitleFetcher(fetch);
                downloaded = await fetcher.FetchAsync(file, probe, fetch, note => job.Notes.Add(note), ct).ConfigureAwait(false);
            }
            List<ExternalSubtitle> embedded = settings.SubtitleFetch.Delivery == SubtitleDelivery.Embed ? [.. downloaded] : [];

            // HDR10 mastering data has to be read from the first frame and handed to the encoder explicitly.
            string? masterDisplay = null, maxCll = null;
            if (probe.Video is { IsHdr: true })
                (masterDisplay, maxCll) = await Ffprobe.ReadHdrMetadataAsync(tools.Ffprobe!, job.SourcePath, ct).ConfigureAwait(false);

            EncodeRequest Request(bool forceSoftware) => new()
            {
                File = file, Probe = probe, Profile = job.Profile, Settings = settings, Encoders = services.Encoders(), OutputPath = tempPath,
                ExtraSubtitles = embedded, MasterDisplay = masterDisplay, MaxCll = maxCll, ForceSoftware = forceSoftware,
            };

            // ---- encode ----
            plan = CommandBuilder.Build(Request(false));
            if (plan.Skip)
            {
                Finish(job, JobStatus.Skipped, plan.SkipReason!);
                return;
            }
            var result = await EncodeAsync(job, plan, runner, tools, probe, settings, "Encoding", changed, ct).ConfigureAwait(false);

            if (!result.Success && !result.Cancelled && plan.Encoder is { Hardware: true } && settings.FallbackToSoftware)
            {
                job.Notes.Add($"{plan.Encoder.DisplayName} failed ({result.ErrorSummary}); retried with the software encoder.");
                plan = CommandBuilder.Build(Request(true));
                if (!plan.Skip)
                    result = await EncodeAsync(job, plan, runner, tools, probe, settings, "Encoding (software fallback)", changed, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (!result.Success) throw new JobFailedException("Encoding failed: " + result.ErrorSummary);

            // ---- subtitles the container could not hold, written out in their original format ----
            var sidecars = new List<(string Temp, string Suffix)>();
            foreach (var sidecar in plan.Sidecars)
            {
                Stage("Saving subtitles");
                string sidecarTemp = Path.Combine(targetDir, $"{baseName}{MediaExtensions.TempMarker}{sidecar.Suffix}");
                tempFiles.Add(sidecarTemp);
                string[] args = ["-hide_banner", "-y", "-nostdin", "-loglevel", "error", "-i", job.SourcePath,
                    "-map", $"0:{sidecar.StreamIndex}", "-c", "copy", "-f", sidecar.Muxer, sidecarTemp];
                var extract = await ProcessRunner.CaptureAsync(tools.Ffmpeg!, args, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                if (extract.ExitCode != 0 || !File.Exists(sidecarTemp))
                    throw new JobFailedException($"Could not save subtitle track {sidecar.StreamIndex}: {extract.StdErr.Trim().Split('\n').LastOrDefault()}");
                sidecars.Add((sidecarTemp, sidecar.Suffix));
            }
            if (settings.SubtitleFetch.Delivery == SubtitleDelivery.Sidecar)
                foreach (var sub in downloaded)
                    sidecars.Add((sub.Path, $".{sub.Language}.srt"));

            // ---- verify before anything irreversible ----
            Stage("Verifying");
            await VerifyAsync(tools, job.SourcePath, probe, plan, tempPath, ct).ConfigureAwait(false);

            long outputSize = new FileInfo(tempPath).Length;
            double savings = job.SourceSize > 0 ? 1.0 - (double)outputSize / job.SourceSize : 0;
            if (savings * 100 < job.Output.MinSavingsPercent)
            {
                DeleteQuietly(tempFiles);
                string outcome = outputSize >= job.SourceSize
                    ? $"the result would be {Format.Bytes(outputSize)}, no smaller than the original"
                    : $"the result would be only {savings * 100:0.#}% smaller (minimum is {job.Output.MinSavingsPercent}%)";
                Finish(job, JobStatus.Skipped, $"Original kept: {outcome}.");
                return;
            }

            // ---- put the output in place ----
            Stage("Finishing");
            ct.ThrowIfCancellationRequested();
            // From here on the encoded files are the only copy that matters once the original is gone,
            // so they are no longer treated as disposable: if a step fails they stay where they are.
            tempFiles.Clear();
            downloaded.Clear();
            string finalPath;
            try
            {
                finalPath = Finalize(job, tempPath, targetDir, baseName, extension, sourceModified);
                string sidecarBase = Path.Combine(Path.GetDirectoryName(finalPath)!, Path.GetFileNameWithoutExtension(finalPath));
                foreach (var (temp, suffix) in sidecars)
                    File.Move(temp, Unique(sidecarBase + suffix));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new JobFailedException($"The file was compressed but could not be moved into place ({ex.Message}). " +
                                             $"The compressed file is still at \"{tempPath}\".");
            }
            string finalBase = Path.Combine(Path.GetDirectoryName(finalPath)!, Path.GetFileNameWithoutExtension(finalPath));
            if (job.Output.Mode != OutputMode.Replace)
                CopyExistingSidecars(job.SourcePath, finalBase);

            if (settings.SubtitleFetch.Delivery == SubtitleDelivery.Embed)
                DeleteQuietly(embedded.Select(e => e.Path));

            job.OutputPath = finalPath;
            job.OutputSize = outputSize;
            services.History.Add(new HistoryEntry
            {
                SourcePath = job.SourcePath, SourceSize = job.SourceSize, SourceModifiedUtc = sourceModified, OutputPath = finalPath,
                OutputSize = outputSize, ProfileId = job.Profile.Id, Mode = job.Output.Mode, FinishedUtc = DateTime.UtcNow,
            });
            Finish(job, JobStatus.Completed, $"Saved {Format.Bytes(job.SourceSize - outputSize)} ({savings * 100:0}% smaller)");
        }
        finally
        {
            // Never leave half-written files beside the user's media. (Both lists are emptied once finishing starts.)
            DeleteQuietly(tempFiles);
            DeleteQuietly(downloaded.Select(d => d.Path));
        }
    }

    private static async Task<FfmpegResult> EncodeAsync(QueueJob job, EncodePlan plan, FfmpegRunner runner, Tools tools, ProbeResult probe,
        AppSettings settings, string stage, Action<QueueJob> changed, CancellationToken ct)
    {
        job.Stage = stage;
        job.Summary = plan.Summary;
        job.EncoderId = plan.Encoder?.Id;
        job.Command = plan.Preview();
        foreach (var note in plan.Notes.Where(n => !job.Notes.Contains(n))) job.Notes.Add(note);
        job.Progress = 0;
        changed(job);

        var started = DateTime.UtcNow;
        return await runner.RunAsync(tools.Ffmpeg!, plan.Args, probe.DurationSeconds, progress =>
        {
            job.Progress = progress.Fraction;
            job.Fps = progress.Fps;
            job.Speed = progress.Speed;
            job.Paused = runner.IsPaused;
            var elapsed = DateTime.UtcNow - started;
            job.Eta = progress.Fraction > 0.01 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - progress.Fraction) / progress.Fraction) : null;
            if (settings.KeepAwake) Native.PokeAwake();
            changed(job);
        }, job.LogPath, settings.LowPriority, ct).ConfigureAwait(false);
    }

    /// <summary>Make sure the output is a complete, readable file with every stream we meant it to have.</summary>
    private static async Task VerifyAsync(Tools tools, string sourcePath, ProbeResult source, EncodePlan plan, string outputPath, CancellationToken ct)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new JobFailedException("Encoding produced no output file.");

        ProbeResult output;
        try { output = await Ffprobe.ProbeAsync(tools.Ffprobe!, outputPath, ct).ConfigureAwait(false); }
        catch (ProbeException ex) { throw new JobFailedException("The encoded file could not be read back: " + ex.Message); }

        if (source.DurationSeconds > 0)
        {
            double tolerance = Math.Max(2.0, source.DurationSeconds * 0.02);
            bool mismatch = Math.Abs(output.DurationSeconds - source.DurationSeconds) > tolerance;
            if (mismatch && source.AudioStreams.Any())
            {
                // Container durations can be wrong (an MP3 without a length header is only estimated).
                // Decode the source audio to find out how long it really is before calling it a failure.
                double measured = await MeasureDurationAsync(tools, sourcePath, ct).ConfigureAwait(false);
                if (measured > 0) mismatch = Math.Abs(output.DurationSeconds - measured) > Math.Max(2.0, measured * 0.02);
            }
            if (mismatch)
                throw new JobFailedException($"The encoded file is {Format.Duration(output.DurationSeconds)} long but the original is " +
                                             $"{Format.Duration(source.DurationSeconds)}; original left untouched.");
        }
        int video = output.Streams.Count(s => s.IsVideo);
        int audio = output.AudioStreams.Count();
        int subtitles = output.SubtitleStreams.Count();
        if (video != plan.VideoStreams || audio != plan.AudioStreams || subtitles != plan.SubtitleStreams)
            throw new JobFailedException($"The encoded file has {video} video, {audio} audio and {subtitles} subtitle streams; expected " +
                                         $"{plan.VideoStreams}, {plan.AudioStreams} and {plan.SubtitleStreams}. Original left untouched.");
    }

    private static async Task<double> MeasureDurationAsync(Tools tools, string path, CancellationToken ct)
    {
        string[] args = ["-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-nostats", "-i", path, "-map", "0:a:0", "-f", "null", "-"];
        try
        {
            var result = await ProcessRunner.CaptureAsync(tools.Ffmpeg!, args, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            var last = result.StdOut.Split('\n').LastOrDefault(l => l.StartsWith("out_time_us=", StringComparison.Ordinal));
            return last is not null && long.TryParse(last["out_time_us=".Length..].Trim(), out var us) ? us / 1_000_000.0 : 0;
        }
        catch (TimeoutException) { return 0; }
    }

    private static string TargetDirectory(QueueJob job)
    {
        string sourceDir = Path.GetDirectoryName(job.SourcePath)!;
        if (job.Output.Mode != OutputMode.OutputFolder) return sourceDir;
        if (string.IsNullOrWhiteSpace(job.Output.OutputFolder))
            throw new JobFailedException("No output folder is set.");
        return Path.Combine(job.Output.OutputFolder, RelativeToRoot(sourceDir, job.Root));
    }

    /// <summary>The part of a directory below the scan root, including the root's own name, so folder structure is mirrored.</summary>
    private static string RelativeToRoot(string directory, string? root)
    {
        if (string.IsNullOrEmpty(root) || !directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
        string rootName = Path.GetFileName(root.TrimEnd('\\', '/'));
        string below = directory[root.Length..].TrimStart('\\', '/');
        return Path.Combine(rootName, below);
    }

    private static string Finalize(QueueJob job, string tempPath, string targetDir, string baseName, string extension, DateTime sourceModified)
    {
        var output = job.Output;
        string finalPath;

        if (output.Mode == OutputMode.Replace)
        {
            finalPath = Path.Combine(targetDir, baseName + "." + extension);
            bool sameName = string.Equals(finalPath, job.SourcePath, StringComparison.OrdinalIgnoreCase);
            // "Movie.avi" becoming "Movie.mkv" must not clobber an unrelated "Movie.mkv" that is already there.
            if (!sameName && File.Exists(finalPath)) finalPath = Unique(finalPath);

            try
            {
                DisposeOriginal(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original is still there (open in a player, read-only...). Keep the work: save beside it.
                finalPath = Unique(Path.Combine(targetDir, baseName + CleanSuffix(output.Suffix) + "." + extension));
                job.Notes.Add($"The original could not be removed ({ex.Message}); the compressed file was saved beside it instead.");
            }
        }
        else if (output.Mode == OutputMode.NextToOriginal)
        {
            finalPath = Unique(Path.Combine(targetDir, baseName + CleanSuffix(output.Suffix) + "." + extension));
        }
        else
        {
            finalPath = Unique(Path.Combine(targetDir, baseName + "." + extension));
        }

        File.Move(tempPath, finalPath);
        if (output.PreserveTimestamps)
        {
            try { File.SetLastWriteTimeUtc(finalPath, sourceModified); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cosmetic */ }
        }
        return finalPath;
    }

    private static void DisposeOriginal(QueueJob job)
    {
        switch (job.Output.Disposal)
        {
            case OriginalDisposal.RecycleBin:
                Native.Recycle(job.SourcePath);
                break;
            case OriginalDisposal.BackupFolder:
                if (string.IsNullOrWhiteSpace(job.Output.BackupFolder))
                    throw new IOException("no backup folder is set");
                string dir = Path.Combine(job.Output.BackupFolder, RelativeToRoot(Path.GetDirectoryName(job.SourcePath)!, job.Root));
                Directory.CreateDirectory(dir);
                File.Move(job.SourcePath, Unique(Path.Combine(dir, Path.GetFileName(job.SourcePath))));
                break;
            case OriginalDisposal.DeletePermanently:
                File.Delete(job.SourcePath);
                break;
        }
    }

    /// <summary>Subtitle files that sit beside the source ("Movie.en.srt") follow the output under its new name.</summary>
    private static void CopyExistingSidecars(string sourcePath, string finalBase)
    {
        string dir = Path.GetDirectoryName(sourcePath)!;
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(dir, baseName + ".*"))
            {
                if (!MediaExtensions.Subtitle.Contains(Path.GetExtension(sidecar))) continue;
                string rest = Path.GetFileName(sidecar)[baseName.Length..]; // ".en.srt"
                string target = finalBase + rest;
                if (!File.Exists(target)) File.Copy(sidecar, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sidecars are a nicety */ }
    }

    private static string CleanSuffix(string suffix)
    {
        suffix = string.Concat((suffix ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
        return suffix.Length == 0 ? ".compressed" : suffix;
    }

    /// <summary>Never overwrite: "name.mkv" becomes "name (2).mkv" when taken.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void EnsureFreeSpace(string directory, long sourceSize)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return; // network share: cannot tell
            long free = new DriveInfo(root).AvailableFreeSpace;
            // The output is normally far smaller than the source, but it can never need more than this.
            if (free < sourceSize + 64 * 1024 * 1024)
                throw new JobFailedException($"Not enough free space on {root} ({Format.Bytes(free)} free, up to {Format.Bytes(sourceSize)} needed).");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { /* unknown drive type */ }
    }

    private static void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var path in paths.ToList())
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again next run */ }
        }
    }

    private static void Finish(QueueJob job, JobStatus status, string message)
    {
        job.Status = status;
        job.Message = message;
        job.Stage = "";
        job.Progress = status == JobStatus.Completed ? 1 : 0;
        job.Eta = null;
        job.FinishedUtc = DateTime.UtcNow;
    }
}
