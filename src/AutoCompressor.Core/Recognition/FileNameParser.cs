using System.Text.RegularExpressions;
using AutoCompressor.Core.Models;

namespace AutoCompressor.Core.Recognition;

/// <summary>Pulls a title, season/episode and release hints out of a media file's name and folders.</summary>
public static partial class FileNameParser
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // [Group] Title - 05 (1080p) [ABCD1234]
    [GeneratedRegex(@"^\[(?<group>[^\]]+)\][\s._]*(?<title>.+?)[\s._]+-[\s._]+(?<ep>\d{1,4})(?:v\d)?(?=[\s._]*(?:[\[\(]|$)|[\s._]+(?:END|FINAL)\b)", Opt)]
    private static partial Regex Fansub();

    // [Group] Title [tags]
    [GeneratedRegex(@"^\[(?<group>[^\]]+)\][\s._]*(?<title>[^\[\(]+)", Opt)]
    private static partial Regex LeadingGroup();

    [GeneratedRegex(@"^(?<title>.*?)[\s._\-\[\(]*\bS(?<s>\d{1,2})[\s._\-]?E(?<e>\d{1,3})(?!\d)", Opt)]
    private static partial Regex SxxExx();

    [GeneratedRegex(@"^(?<title>.*?)[\s._\-\[\(]+(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d)", Opt)]
    private static partial Regex NxNN();

    [GeneratedRegex(@"^(?<title>.*?)[\s._\-]*\bSeason[\s._]*(?<s>\d{1,2})[\s._\-]*Episode[\s._]*(?<e>\d{1,3})", Opt)]
    private static partial Regex SeasonEpisodeWords();

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-\(]+(?<y>(?:19|20)\d{2})[\s._\-](?<m>0[1-9]|1[0-2])[\s._\-](?<d>0[1-9]|[12]\d|3[01])(?!\d)", Opt)]
    private static partial Regex DateBased();

    [GeneratedRegex(@"^(?<title>.*?)[\s._\-]*\b(?:Episode|Ep)[\s._]*(?<e>\d{1,4})(?!\d)", Opt)]
    private static partial Regex EpisodeWord();

    // Title - 05
    [GeneratedRegex(@"^(?<title>.+?)\s+-\s+(?<e>\d{2,4})(?:v\d)?(?=\s*(?:[\[\(]|$))", Opt)]
    private static partial Regex AbsoluteDash();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex OnlyNumber();

    [GeneratedRegex(@"(?<![\dA-Za-z])(?<year>(?:19|20)\d{2})(?![\dA-Za-z])")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\[[0-9A-Fa-f]{8}\]")]
    private static partial Regex Crc();

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,12})$")]
    private static partial Regex TrailingGroup();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(2160p|1080[pi]|720p|576[pi]|480[pi]|4k|uhd|blu-?ray|bdrip|brrip|bdremux|web-?dl|webrip|web|hdtv|pdtv|dvdrip|dvd|remux|hdrip|x264|x265|h\.?264|h\.?265|hevc|avc|xvid|divx|av1|aac|ac3|eac3|dts|flac|10bit|10-bit|hdr|hdr10|dv|proper|repack|extended|unrated|remastered|multi|dual[\s._-]?audio|complete|internal|limited)(?![A-Za-z0-9])", Opt)]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"^(?:season|series|saison|staffel|s)[\s._-]*\d{1,2}$|^specials?$|^extras?$|^featurettes?$|^subs?$|^subtitles$|^(?:disc|disk|cd|dvd)[\s._-]*\d+$|^ovas?$|^samples?$|^bonus$|^video_ts$|^bdmv$|^stream$", Opt)]
    private static partial Regex StructuralFolder();

    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    public static ParsedName Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parsed = new ParsedName
        {
            HasCrc = Crc().IsMatch(name),
            ShowFolder = FindShowFolder(path),
        };

        string? rawTitle = null;
        Match m;

        if ((m = Fansub().Match(name)).Success)
        {
            parsed.FansubStyle = true;
            parsed.AbsoluteNumbering = true;
            parsed.ReleaseGroup = m.Groups["group"].Value.Trim();
            parsed.Episode = int.Parse(m.Groups["ep"].Value);
            rawTitle = m.Groups["title"].Value;
            // "[Group] Show S2 - 05" style still counts as fansub naming
        }
        else if ((m = SxxExx().Match(name)).Success || (m = SeasonEpisodeWords().Match(name)).Success || (m = NxNN().Match(name)).Success)
        {
            parsed.Season = int.Parse(m.Groups["s"].Value);
            parsed.Episode = int.Parse(m.Groups["e"].Value);
            rawTitle = m.Groups["title"].Value;
        }
        else if ((m = DateBased().Match(name)).Success)
        {
            parsed.IsDateBased = true;
            parsed.Year = int.Parse(m.Groups["y"].Value);
            rawTitle = m.Groups["title"].Value;
        }
        else if ((m = EpisodeWord().Match(name)).Success)
        {
            parsed.Episode = int.Parse(m.Groups["e"].Value);
            rawTitle = m.Groups["title"].Value;
        }
        else if ((m = AbsoluteDash().Match(name)).Success)
        {
            parsed.Episode = int.Parse(m.Groups["e"].Value);
            parsed.AbsoluteNumbering = true;
            rawTitle = m.Groups["title"].Value;
        }
        else if (OnlyNumber().IsMatch(name.Trim()))
        {
            parsed.Episode = int.Parse(name.Trim());
            rawTitle = "";
        }

        // A leading [Group] tag is how anime fansub releases are named, whatever follows it.
        var lead = LeadingGroup().Match(name);
        if (lead.Success)
        {
            parsed.FansubStyle = true;
            parsed.ReleaseGroup ??= lead.Groups["group"].Value.Trim();
            if (rawTitle is not null && rawTitle.StartsWith('['))
                rawTitle = rawTitle[(rawTitle.IndexOf(']') + 1)..];
        }

        if (rawTitle is null)
        {
            // Film-style name: everything before the last plausible year, or before the first release tag.
            string body = lead.Success ? name[(name.IndexOf(']') + 1)..] : name;
            int maxYear = DateTime.Now.Year + 1;
            var years = YearToken().Matches(body)
                .Where(y => y.Index > 0 && int.Parse(y.Groups["year"].Value) is var v && v >= 1900 && v <= maxYear)
                .ToList();
            if (years.Count > 0)
            {
                var last = years[^1];
                parsed.Year = int.Parse(last.Groups["year"].Value);
                rawTitle = body[..last.Index];
            }
            else
            {
                var tag = ReleaseTag().Match(body);
                rawTitle = tag.Success && tag.Index > 0 ? body[..tag.Index] : body;
            }
        }
        else if (parsed.Year is null)
        {
            // "Show (2010) S01E01": keep the year, drop it from the title
            var year = YearToken().Matches(rawTitle).LastOrDefault(y => y.Index > 0);
            if (year is not null)
            {
                parsed.Year = int.Parse(year.Groups["year"].Value);
                rawTitle = rawTitle[..year.Index];
            }
        }

        if (parsed.ReleaseGroup is null && TrailingGroup().Match(name) is { Success: true } trailing && ReleaseTag().IsMatch(name))
            parsed.ReleaseGroup = trailing.Groups["group"].Value;

        parsed.Title = CleanTitle(rawTitle);
        // "S01E01.mkv", "01.mkv", "Episode 3.mkv": the show's name lives in a parent folder.
        if (parsed.Title.Length < 2)
            parsed.Title = parsed.ShowFolder;
        return parsed;
    }

    /// <summary>The nearest parent folder that names the show or film rather than a season or disc.</summary>
    private static string FindShowFolder(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var folder = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folder)) break; // drive root
            if (!StructuralFolder().IsMatch(folder.Trim()))
            {
                var cleaned = CleanFolderName(folder);
                if (cleaned.Length >= 2) return cleaned;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    private static string CleanFolderName(string folder)
    {
        var text = folder;
        var season = Regex.Match(text, @"[\s._\-\(\[]+(?:S\d{1,2}(?:-S?\d{1,2})?|Season[\s._]*\d{1,2}|Complete)\b.*$", Opt);
        if (season.Success && season.Index > 0) text = text[..season.Index];
        var tag = ReleaseTag().Match(text);
        if (tag.Success && tag.Index > 0) text = text[..tag.Index];
        text = CleanTitle(text);
        var year = Regex.Match(text, @"\s(?:19|20)\d{2}$");
        if (year.Success && year.Index > 0) text = text[..year.Index];
        return text.Trim();
    }

    public static string CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = Bracketed().Replace(raw, " ");
        text = text.Replace('.', ' ').Replace('_', ' ');
        text = Spaces().Replace(text, " ");
        return text.Trim(' ', '-', '[', '(', ']', ')', '+', ',');
    }

    /// <summary>Lower-case letters and digits only, without a leading article: the key for title lookups.</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        Span<char> buffer = stackalloc char[Math.Min(title.Length, 256)];
        int n = 0;
        foreach (var ch in title.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (n == buffer.Length) break;
            if (char.IsLetterOrDigit(ch)) buffer[n++] = char.ToLowerInvariant(ch);
            else if (ch == '&') { if (n + 3 <= buffer.Length) { "and".AsSpan().CopyTo(buffer[n..]); n += 3; } }
        }
        var result = new string(buffer[..n]);
        return result.StartsWith("the", StringComparison.Ordinal) && result.Length > 5 ? result[3..] : result;
    }
}
