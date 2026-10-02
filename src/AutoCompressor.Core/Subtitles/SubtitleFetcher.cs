using System.Text;
using System.Text.RegularExpressions;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Scanning;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Subtitles;

/// <summary>Finds subtitles a file is missing, picks the best match and saves it as UTF-8 SubRip.</summary>
public sealed class SubtitleFetcher : IDisposable
{
    private readonly HttpClient _http;
    private readonly List<ISubtitleProvider> _providers = [];

    public SubtitleFetcher(SubtitleFetchOptions options, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCompressor/1.0");

        // OpenSubtitles first: it can match on the file's hash, which gets timing right.
        if (options.UseOpenSubtitles && !string.IsNullOrWhiteSpace(options.OpenSubtitlesApiKey))
            _providers.Add(new OpenSubtitlesProvider(_http, options.OpenSubtitlesApiKey.Trim(), options.OpenSubtitlesUser.Trim(),
                Native.Unprotect(options.OpenSubtitlesPasswordProtected)));
        if (options.UseGestdown)
            _providers.Add(new GestdownProvider(_http));
    }

    public IReadOnlyList<ISubtitleProvider> Providers => _providers;

    /// <summary>The languages from the wish list that the file has neither embedded nor beside it.</summary>
    public static List<string> MissingLanguages(MediaFile file, ProbeResult probe, SubtitleFetchOptions options)
    {
        var wanted = Languages.ParseList(options.Languages);
        if (wanted.Count == 0) return wanted;

        var present = probe.SubtitleStreams.Select(s => Languages.ToTwoLetter(s.Language)).Where(l => l is not null).ToHashSet();
        bool untaggedEmbedded = probe.SubtitleStreams.Any(s => Languages.ToTwoLetter(s.Language) is null);

        string baseName = Path.GetFileNameWithoutExtension(file.Path);
        bool untaggedSidecar = false;
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(file.Directory, "*").Where(p => MediaExtensions.Subtitle.Contains(Path.GetExtension(p))))
            {
                string name = Path.GetFileNameWithoutExtension(sidecar);
                if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                // "Movie.en.srt", "Movie.eng.forced.srt", or just "Movie.srt"
                var parts = name[baseName.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);
                var lang = parts.Select(Languages.ToTwoLetter).FirstOrDefault(l => l is not null);
                if (lang is not null) present.Add(lang);
                else untaggedSidecar = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* treat as no sidecars */ }

        var missing = wanted.Where(l => !present.Contains(l)).ToList();
        // An unlabelled subtitle is most likely in the first language the user asked for.
        if ((untaggedEmbedded || untaggedSidecar) && missing.Count > 0 && missing[0] == wanted[0]) missing.RemoveAt(0);
        return missing;
    }

    public async Task<List<ExternalSubtitle>> FetchAsync(MediaFile file, ProbeResult probe, SubtitleFetchOptions options,
        Action<string>? log, CancellationToken ct)
    {
        var fetched = new List<ExternalSubtitle>();
        if (_providers.Count == 0 || file.Kind != MediaKind.Video) return fetched;
        var parsed = file.Parsed;
        if (string.IsNullOrWhiteSpace(parsed.Title)) return fetched;

        foreach (var language in MissingLanguages(file, probe, options))
        {
            ct.ThrowIfCancellationRequested();
            var query = new SubtitleQuery(file.Path, parsed.Title, parsed.Year, parsed.Season, parsed.Episode, language, file.Name);

            foreach (var provider in _providers)
            {
                try
                {
                    var candidates = await provider.SearchAsync(query, ct).ConfigureAwait(false);
                    var best = candidates.OrderByDescending(c => Score(c, file.Name)).FirstOrDefault();
                    if (best is null) continue;

                    var bytes = await provider.DownloadAsync(best, ct).ConfigureAwait(false);
                    var text = bytes is null ? null : DecodeSubRip(bytes);
                    if (text is null) continue;

                    string path = Path.Combine(AppPaths.SubtitleDir, $"{Guid.NewGuid():N}.{language}.srt");
                    await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    fetched.Add(new ExternalSubtitle(path, language));
                    log?.Invoke($"Found {Languages.Name(language)} subtitles on {provider.Name}" +
                                (best.HashMatch ? " (exact file match)" : best.Release.Length > 0 ? $" (release: {best.Release})" : ""));
                    break;
                }
                catch (SubtitleProviderException ex)
                {
                    log?.Invoke($"{provider.Name}: {ex.Message}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    ct.ThrowIfCancellationRequested();
                    log?.Invoke($"{provider.Name}: could not be reached ({ex.Message})");
                }
            }
        }
        return fetched;
    }

    /// <summary>Hash matches win outright; after that, prefer the release that shares the most tags with the file.</summary>
    public static double Score(SubtitleCandidate candidate, string fileName)
    {
        double score = candidate.HashMatch ? 1000 : 0;
        var fileTokens = Tokens(fileName);
        var releaseTokens = Tokens(candidate.Release);
        if (releaseTokens.Count > 0)
            score += 100.0 * releaseTokens.Count(fileTokens.Contains) / releaseTokens.Count;
        score += Math.Log10(candidate.Downloads + 1) * 10;
        if (candidate.HearingImpaired) score -= 5;
        return score;
    }

    private static HashSet<string> Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Where(t => t.Length >= 2).ToHashSet();

    /// <summary>Validate a download as SubRip and return it as text, or null when it is something else.</summary>
    public static string? DecodeSubRip(byte[] bytes)
    {
        if (bytes.Length < 10) return null;
        string text;
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes is [0xFF, 0xFE, ..]) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes is [0xFE, 0xFF, ..]) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                // Older subtitle files are usually Windows-1252.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(1252).GetString(bytes);
            }
        }
        return Regex.IsMatch(text, @"\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}") ? text : null;
    }

    public void Dispose() => _http.Dispose();
}
