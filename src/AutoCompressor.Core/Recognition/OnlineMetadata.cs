using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Recognition;

/// <summary>What an online database says about a title. Stored in the cache, including misses.</summary>
public sealed class OnlineMetadata
{
    public bool Found { get; set; }
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>TVMaze show type: Scripted, Animation, Reality, Talk Show, Documentary, News, Sports...</summary>
    public string Kind { get; set; } = "";
    public string Language { get; set; } = "";
    public List<string> Genres { get; set; } = [];
    public int RuntimeMinutes { get; set; }
    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Translate database genres into one of our content types.</summary>
    public ContentType? ToContentType()
    {
        if (!Found) return null;
        bool Has(string genre) => Genres.Any(g => g.Equals(genre, StringComparison.OrdinalIgnoreCase));
        bool japanese = Language.Equals("Japanese", StringComparison.OrdinalIgnoreCase) || Language.Equals("ja", StringComparison.OrdinalIgnoreCase);

        if (Has("Anime") || ((Kind == "Animation" || Has("Animation")) && japanese)) return ContentType.Anime;
        if (Kind == "Animation" || Has("Animation")) return ContentType.Animation;
        if (Kind is "Reality" or "Talk Show" or "News" or "Game Show" or "Panel Show" or "Variety" or "Award Show") return ContentType.StillCam;
        if (Kind == "Sports" || Has("Sports")) return ContentType.Sports;
        if (Kind == "Documentary" || Has("Documentary") || Has("Nature")) return ContentType.Documentary;
        if (Has("Music") && Kind != "Scripted") return ContentType.Concert;

        bool spectacle = Has("Action") || Has("Adventure") || Has("Science-Fiction") || Has("Science Fiction") || Has("Sci-Fi")
                         || Has("Fantasy") || Has("War") || Has("Western") || Has("Horror") || Has("Supernatural");
        // A half-hour scripted comedy is a sitcom whether it is multi-camera or not.
        if (Has("Comedy") && !spectacle && RuntimeMinutes is > 0 and <= 35) return ContentType.Sitcom;
        if (spectacle) return ContentType.Cinematic;
        return ContentType.Drama;
    }
}

public sealed class MetadataCache
{
    private readonly Dictionary<string, OnlineMetadata> _entries;
    private readonly object _gate = new();
    private bool _dirty;

    public MetadataCache() => _entries = JsonStore.Load<Dictionary<string, OnlineMetadata>>(AppPaths.MetadataCache);

    public OnlineMetadata? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            // Retry misses after a week: new shows get added.
            if (!entry.Found && DateTime.UtcNow - entry.FetchedUtc > TimeSpan.FromDays(7)) return null;
            return entry;
        }
    }

    public void Set(string key, OnlineMetadata value)
    {
        lock (_gate) { _entries[key] = value; _dirty = true; }
    }

    public void Save()
    {
        Dictionary<string, OnlineMetadata> snapshot;
        lock (_gate)
        {
            if (!_dirty) return;
            snapshot = new Dictionary<string, OnlineMetadata>(_entries);
            _dirty = false;
        }
        JsonStore.Save(AppPaths.MetadataCache, snapshot, indented: false);
    }
}

/// <summary>
/// Looks titles up on TVMaze (series, no key needed) and TMDB (films, needs the user's key).
/// Only the parsed title is sent; results are cached so each show is asked about once.
/// </summary>
public sealed class OnlineLookup : IDisposable
{
    private readonly HttpClient _http;
    private readonly MetadataCache _cache;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public OnlineLookup(MetadataCache cache, HttpMessageHandler? handler = null)
    {
        _cache = cache;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCompressor/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<OnlineMetadata?> LookupAsync(ParsedName parsed, bool looksLikeFilm, string? tmdbKey, CancellationToken ct = default)
    {
        var title = parsed.Title;
        if (string.IsNullOrWhiteSpace(title) || title.Length < 2) return null;

        bool film = looksLikeFilm && !parsed.IsEpisodic;
        if (film && string.IsNullOrWhiteSpace(tmdbKey)) return null; // TVMaze lists series only

        string key = (film ? "film:" : "tv:") + FileNameParser.Normalize(title) + (film && parsed.Year is { } y ? ":" + y : "");
        if (_cache.Get(key) is { } cached) return cached.Found ? cached : null;

        OnlineMetadata? result;
        try
        {
            result = film
                ? await QueryTmdbAsync(title, parsed.Year, tmdbKey!, ct).ConfigureAwait(false)
                : await QueryTvMazeAsync(title, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return null; // offline or the service is down: not worth caching
        }

        // The services return their best guess even for nonsense; only trust a result whose name matches.
        if (result is not null && !TitlesMatch(title, result.Name)) result = null;
        _cache.Set(key, result ?? new OnlineMetadata { Found = false });
        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // TVMaze allows 20 calls per 10 seconds; stay well under it.
            var wait = TimeSpan.FromMilliseconds(550) - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;
            return response;
        }
        finally { _throttle.Release(); }
    }

    private async Task<OnlineMetadata?> QueryTvMazeAsync(string title, CancellationToken ct)
    {
        var url = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(title);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var meta = new OnlineMetadata
            {
                Found = true,
                Source = "TVMaze",
                Name = Text(root, "name"),
                Kind = Text(root, "type"),
                Language = Text(root, "language"),
                RuntimeMinutes = Number(root, "averageRuntime") is > 0 and var avg ? avg : Number(root, "runtime"),
            };
            if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                meta.Genres = genres.EnumerateArray().Select(g => g.ToString()).ToList();
            return meta;
        }
        throw new HttpRequestException("TVMaze rate limit");
    }

    private static readonly Dictionary<int, string> TmdbGenres = new()
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime", [99] = "Documentary",
        [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History", [27] = "Horror", [10402] = "Music",
        [9648] = "Mystery", [10749] = "Romance", [878] = "Science Fiction", [10770] = "TV Movie", [53] = "Thriller",
        [10752] = "War", [37] = "Western",
    };

    private async Task<OnlineMetadata?> QueryTmdbAsync(string title, int? year, string apiKey, CancellationToken ct)
    {
        var url = "https://api.themoviedb.org/3/search/movie?include_adult=false&query=" + Uri.EscapeDataString(title);
        if (year is { } y) url += "&year=" + y;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // TMDB issues two credentials: a long "read access token" (sent as a bearer header) and a short v3 key.
        apiKey = apiKey.Trim();
        if (apiKey.Length > 40) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else request.RequestUri = new Uri(url + "&api_key=" + Uri.EscapeDataString(apiKey));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("TMDB rejected the API key.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var first = results[0];
        var meta = new OnlineMetadata
        {
            Found = true,
            Source = "TMDB",
            Name = Text(first, "title"),
            Kind = "Movie",
            Language = Text(first, "original_language"),
        };
        if (first.TryGetProperty("genre_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            meta.Genres = ids.EnumerateArray().Select(i => TmdbGenres.GetValueOrDefault(i.GetInt32(), "")).Where(g => g.Length > 0).ToList();
        return meta;
    }

    /// <summary>Accept a result when the names are equal, one contains the other, or they differ by a few characters.</summary>
    public static bool TitlesMatch(string wanted, string found)
    {
        var a = FileNameParser.Normalize(wanted);
        var b = FileNameParser.Normalize(found);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length >= 5 && longer.Contains(shorter, StringComparison.Ordinal)) return true;
        int distance = Levenshtein(a, b);
        return distance <= Math.Max(1, longer.Length / 5);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
