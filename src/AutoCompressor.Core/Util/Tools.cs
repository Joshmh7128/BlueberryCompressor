using System.Diagnostics;
using System.Text;

namespace AutoCompressor.Core.Util;

/// <summary>Locates ffmpeg and ffprobe.</summary>
public sealed class Tools
{
    public string? Ffmpeg { get; private set; }
    public string? Ffprobe { get; private set; }
    public string? Version { get; private set; }
    public bool Available => Ffmpeg is not null && Ffprobe is not null;

    public static Tools Locate(string? configuredFfmpeg, string? configuredFfprobe)
    {
        var tools = new Tools
        {
            Ffmpeg = Find("ffmpeg.exe", configuredFfmpeg, null),
        };
        // ffprobe almost always sits beside ffmpeg
        tools.Ffprobe = Find("ffprobe.exe", configuredFfprobe, Path.GetDirectoryName(tools.Ffmpeg));
        if (tools.Ffmpeg is not null)
        {
            try
            {
                var result = ProcessRunner.Capture(tools.Ffmpeg, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10));
                var first = result.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? "";
                tools.Version = first.StartsWith("ffmpeg version ", StringComparison.Ordinal)
                    ? first["ffmpeg version ".Length..].Split(' ')[0]
                    : null;
            }
            catch (Exception) { tools.Ffmpeg = null; }
        }
        return tools;
    }

    private static string? Find(string exe, string? configured, string? hintDir)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var candidates = new List<string>();
        if (hintDir is not null) candidates.Add(Path.Combine(hintDir, exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", exe));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), exe)); }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", exe));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", exe));
        candidates.Add(Path.Combine(@"C:\ffmpeg\bin", exe));

        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>Run a short-lived process and collect its output.</summary>
    public static async Task<ProcessResult> CaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = StartInfo(exe, args) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {timeout.TotalSeconds:0}s.");
        }
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public static ProcessResult Capture(string exe, IEnumerable<string> args, TimeSpan timeout) =>
        CaptureAsync(exe, args, timeout).GetAwaiter().GetResult();

    /// <summary>Render an argument list the way a user would type it, for display and logs.</summary>
    public static string Quote(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '(' or ')' or '[' or ']' or ';' or '\'')
            ? '"' + a.Replace("\"", "\\\"") + '"'
            : a));
}
