using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoCompressor.Core.Recognition;

namespace AutoCompressor.Core.Subtitles;

public sealed record SubtitleQuery(string FilePath, string Title, int? Year, int? Season, int? Episode, string Language, string ReleaseName)
{
    public bool IsEpisode => Season.HasValue && Episode.HasValue;
}

public sealed record SubtitleCandidate(string Provider, string Id, string Language, string Release, int Downloads, bool HashMatch, bool HearingImpaired);

public sealed class SubtitleProviderException(string message) : Exception(message);

public interface ISubtitleProvider
{
    string Name { get; }
    Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct);
    /// <summary>Returns the subtitle file's bytes (SubRip text).</summary>
    Task<byte[]?> DownloadAsync(SubtitleCandidate candidate, CancellationToken ct);
}

/// <summary>
/// Gestdown: an open-source, key-less API over the Addic7ed community database. TV episodes only.
/// https://api.gestdown.info
/// </summary>
public sealed class GestdownProvider(HttpClient http) : ISubtitleProvider
{
    private const string Base = "https://api.gestdown.info";
    private readonly Dictionary<string, string?> _showIds = new(StringComparer.Ordinal);

    public string Name => "Gestdown";

    public async Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct)
    {
        var results = new List<SubtitleCandidate>();
        if (!query.IsEpisode) return results;

        string? showId = await FindShowAsync(query.Title, ct).ConfigureAwait(false);
        if (showId is null) return results;

        var url = $"{Base}/subtitles/get/{showId}/{query.Season}/{query.Episode}/{Uri.EscapeDataString(query.Language)}";
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        // 404: no such episode. 423: the service is still refreshing this show. 429: slow down.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Locked or HttpStatusCode.TooManyRequests) return results;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("matchingSubtitles", out var list) || list.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.False) continue;
            string id = item.GetProperty("subtitleId").GetString() ?? "";
            if (id.Length == 0) continue;
            string release = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            if (item.TryGetProperty("qualities", out var q) && q.ValueKind == JsonValueKind.Array)
                release += " " + string.Join(' ', q.EnumerateArray().Select(x => x.ToString()));
            int downloads = item.TryGetProperty("downloadCount", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
            bool hearingImpaired = item.TryGetProperty("hearingImpaired", out var hi) && hi.ValueKind == JsonValueKind.True;
            results.Add(new SubtitleCandidate(Name, id, query.Language, release.Trim(), downloads, false, hearingImpaired));
        }
        return results;
    }

    private async Task<string?> FindShowAsync(string title, CancellationToken ct)
    {
        string key = FileNameParser.Normalize(title);
        if (_showIds.TryGetValue(key, out var known)) return known;

        string? found = null;
        using var response = await http.GetAsync($"{Base}/shows/search/{Uri.EscapeDataString(title)}", ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("shows", out var shows) && shows.ValueKind == JsonValueKind.Array)
            {
                // Prefer an exact name; otherwise accept the first close match.
                foreach (var show in shows.EnumerateArray())
                {
                    string name = show.GetProperty("name").GetString() ?? "";
                    string id = show.GetProperty("id").GetString() ?? "";
                    if (FileNameParser.Normalize(name) == key) { found = id; break; }
                    if (found is null && OnlineLookup.TitlesMatch(title, name)) found = id;
                }
            }
        }
        else if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
        _showIds[key] = found;
        return found;
    }

    public async Task<byte[]?> DownloadAsync(SubtitleCandidate candidate, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Base}/subtitles/download/{candidate.Id}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// OpenSubtitles.com REST API. Needs the user's own free API key, and an account to download.
/// Matches by file hash first, which finds subtitles timed for this exact release.
/// </summary>
public sealed class OpenSubtitlesProvider(HttpClient http, string apiKey, string user, string password) : ISubtitleProvider
{
    private const string UserAgent = "AutoCompressor v1.0";
    private string _host = "api.opensubtitles.com";
    private string? _token;
    private bool _loginTried;

    public string Name => "OpenSubtitles";

    private HttpRequestMessage Request(HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, $"https://{_host}/api/v1/{pathAndQuery}");
        request.Headers.TryAddWithoutValidation("Api-Key", apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        if (_loginTried || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) return;
        _loginTried = true;
        var request = Request(HttpMethod.Post, "login");
        request.Content = JsonContent.Create(new { username = user, password });
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SubtitleProviderException("OpenSubtitles rejected the user name, password or API key.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        _token = doc.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null;
        // VIP accounts are told to use a different host.
        if (doc.RootElement.TryGetProperty("base_url", out var baseUrl) && baseUrl.GetString() is { Length: > 0 } host)
            _host = host;
    }

    public async Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken ct)
    {
        var results = new List<SubtitleCandidate>();
        if (string.IsNullOrWhiteSpace(apiKey)) return results;

        // The API redirects unless parameters are lower-case and in alphabetical order.
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = query.Language.ToLowerInvariant(),
            ["query"] = query.Title.ToLowerInvariant(),
        };
        if (query.IsEpisode)
        {
            parameters["season_number"] = query.Season!.Value.ToString();
            parameters["episode_number"] = query.Episode!.Value.ToString();
        }
        else if (query.Year is { } year)
        {
            parameters["year"] = year.ToString();
        }
        string? hash = MovieHash.Compute(query.FilePath);
        if (hash is not null) parameters["moviehash"] = hash;

        string queryString = string.Join('&', parameters.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value).Replace("%20", "+")));
        using var response = await http.SendAsync(Request(HttpMethod.Get, "subtitles?" + queryString), ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SubtitleProviderException("OpenSubtitles rejected the API key.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return results;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var a)) continue;
            if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1) continue; // skip multi-CD sets
            if (Flag(a, "ai_translated") || Flag(a, "machine_translated") || Flag(a, "foreign_parts_only")) continue;
            string fileId = files[0].GetProperty("file_id").ToString();
            string release = a.TryGetProperty("release", out var rel) ? rel.GetString() ?? "" : "";
            int downloads = a.TryGetProperty("download_count", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : 0;
            results.Add(new SubtitleCandidate(Name, fileId, query.Language, release, downloads, Flag(a, "moviehash_match"), Flag(a, "hearing_impaired")));
        }
        return results;
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public async Task<byte[]?> DownloadAsync(SubtitleCandidate candidate, CancellationToken ct)
    {
        await LoginAsync(ct).ConfigureAwait(false);
        var request = Request(HttpMethod.Post, "download");
        request.Content = JsonContent.Create(new { file_id = long.Parse(candidate.Id), sub_format = "srt" });
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SubtitleProviderException("OpenSubtitles needs a user name and password to download.");
        if (response.StatusCode is HttpStatusCode.NotAcceptable or HttpStatusCode.TooManyRequests)
            throw new SubtitleProviderException("OpenSubtitles download quota reached for today.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("link", out var link) || link.GetString() is not { Length: > 0 } url) return null;
        // The link is a plain temporary file URL; it needs no credentials.
        using var file = await http.GetAsync(url, ct).ConfigureAwait(false);
        return file.IsSuccessStatusCode ? await file.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
    }
}

/// <summary>The OpenSubtitles file hash: file size plus the 64-bit sums of the first and last 64 KiB.</summary>
public static class MovieHash
{
    private const int Chunk = 65536;

    public static string? Compute(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            if (length < Chunk * 2) return null;

            ulong hash = (ulong)length;
            var buffer = new byte[Chunk];
            stream.ReadExactly(buffer);
            hash = Sum(hash, buffer);
            stream.Seek(-Chunk, SeekOrigin.End);
            stream.ReadExactly(buffer);
            hash = Sum(hash, buffer);
            return hash.ToString("x16");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ulong Sum(ulong hash, byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i += 8)
            unchecked { hash += BitConverter.ToUInt64(buffer, i); }
        return hash;
    }
}
