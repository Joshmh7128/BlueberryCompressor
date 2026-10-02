using System.Diagnostics;
using System.Globalization;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Transcoding;

public sealed record EncodeProgress(double Fraction, double Fps, double Speed, long OutputBytes, TimeSpan Position);

public sealed class FfmpegResult
{
    public int ExitCode { get; init; }
    public bool Cancelled { get; init; }
    public List<string> LogTail { get; init; } = [];
    public bool Success => ExitCode == 0 && !Cancelled;

    /// <summary>The most useful line of ffmpeg's error output for showing to the user.</summary>
    public string ErrorSummary
    {
        get
        {
            var lines = LogTail.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var explicitError = lines.LastOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                                                         || l.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                                                         || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                                         || l.Contains("not supported", StringComparison.OrdinalIgnoreCase));
            return (explicitError ?? lines.LastOrDefault() ?? $"ffmpeg exited with code {ExitCode}").Trim();
        }
    }
}

/// <summary>Runs one ffmpeg process, reporting progress and supporting pause and cancel.</summary>
public sealed class FfmpegRunner
{
    private Process? _process;
    private readonly object _gate = new();
    private bool _paused;
    private bool _pauseRequested;

    /// <summary>True once a pause has been asked for, even if no encoder process has started yet.</summary>
    public bool IsPaused => _pauseRequested;

    public async Task<FfmpegResult> RunAsync(string ffmpeg, IReadOnlyList<string> args, double durationSeconds,
        Action<EncodeProgress>? onProgress, string? logFile, bool lowPriority, CancellationToken ct)
    {
        // "-progress pipe:1" makes ffmpeg print machine-readable key=value blocks on stdout.
        var fullArgs = new List<string> { "-progress", "pipe:1", "-nostats", "-loglevel", "warning" };
        fullArgs.AddRange(args);

        var tail = new Queue<string>();
        StreamWriter? log = null;
        if (logFile is not null)
        {
            log = new StreamWriter(logFile, append: true) { AutoFlush = true };
            await log.WriteLineAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Path.GetFileName(ffmpeg)} {ProcessRunner.Quote(fullArgs)}").ConfigureAwait(false);
        }

        using var process = new Process { StartInfo = ProcessRunner.StartInfo(ffmpeg, fullArgs) };
        bool cancelled = false;
        try
        {
            process.Start();
            lock (_gate)
            {
                _process = process;
                // A pause requested before the encoder started takes effect now.
                _paused = _pauseRequested && Native.Suspend(process);
            }
            if (lowPriority)
            {
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already exited */ }
            }

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    lock (tail)
                    {
                        tail.Enqueue(line);
                        while (tail.Count > 60) tail.Dequeue();
                    }
                    if (log is not null) await log.WriteLineAsync(line).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            var stdoutTask = Task.Run(async () =>
            {
                double fps = 0, speed = 0;
                long size = 0;
                TimeSpan position = TimeSpan.Zero;
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq], value = line[(eq + 1)..].Trim();
                    switch (key)
                    {
                        case "fps":
                            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
                            break;
                        case "speed":
                            double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                            break;
                        case "total_size":
                            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                            break;
                        case "out_time_us" or "out_time_ms": // both are microseconds, despite the second one's name
                            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                                position = TimeSpan.FromMicroseconds(us);
                            break;
                        case "progress":
                            double fraction = durationSeconds > 0 ? Math.Clamp(position.TotalSeconds / durationSeconds, 0, 1) : 0;
                            if (value == "end") fraction = 1;
                            onProgress?.Invoke(new EncodeProgress(fraction, fps, speed, size, position));
                            break;
                    }
                }
            }, CancellationToken.None);

            await using (ct.Register(() =>
                         {
                             cancelled = true;
                             // A suspended process cannot exit; wake it before killing it.
                             if (_paused) Native.Resume(process);
                             try { process.Kill(entireProcessTree: true); }
                             catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                         }))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);

            List<string> logTail;
            lock (tail) logTail = [.. tail];
            return new FfmpegResult { ExitCode = process.ExitCode, Cancelled = cancelled, LogTail = logTail };
        }
        finally
        {
            lock (_gate) { _process = null; _paused = false; }
            if (log is not null) await log.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _pauseRequested = true;
            if (_process is not null && !_paused && Native.Suspend(_process)) _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _pauseRequested = false;
            if (_process is not null && _paused && Native.Resume(_process)) _paused = false;
        }
    }
}
