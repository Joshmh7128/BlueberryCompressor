using System.Text.RegularExpressions;
using AutoCompressor.Core.Models;

namespace AutoCompressor.Core.Recognition;

/// <summary>
/// Decides what kind of content a file is by adding up evidence from several places: the folders
/// it lives in, how it is named, what its streams look like, a list of well-known titles and,
/// when enabled, an online database. Every piece of evidence is kept so the UI can explain itself.
/// </summary>
public static partial class ContentClassifier
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly HashSet<string> AnimeGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "SubsPlease", "Erai-raws", "HorribleSubs", "Judas", "EMBER", "ASW", "Commie", "UTW", "Coalgirls", "FFF", "GJM",
        "DameDesuYo", "Doki", "Underwater", "Vivid", "Chihiro", "Tsundere", "CBM", "Reaktor", "Beatrice-Raws",
        "VCB-Studio", "LostYears", "MTBB", "SSA", "Anime Time", "Cleo", "DKB", "Kametsu", "Yameii", "ToonsHub",
        "Varyg", "SallySubs", "Golumpa", "Nii-sama", "Hi10", "Exiled-Destiny", "a.f.k.", "Eclipse", "gg", "Mazui",
        "WhyNot", "Kaleido-subs", "Anime-Koi", "Asenshi", "Chyuu", "Davinci", "PAS", "Tenrai-Sensei", "Trix", "sam",
        "Arid", "smol", "NC-Raws", "Ohys-Raws", "Leopard-Raws", "Moozzi2", "Raws-Maji", "ReinForce", "Sokudo",
        "YuiSubs", "Anime Land", "AnimeRG", "Bonkai77", "iAHD", "Pikari-Teshima", "neoHEVC", "CameEsp", "Breeze",
        "0x539", "New-raws", "MiniMTBB", "Okay-Subs", "Kawaiika-Raws", "Lilith-Raws", "Prof", "BlurayDesuYo",
    };

    // Each rule: a word pattern looked for in the folder path, the type it suggests, and how strongly.
    private static readonly (Regex Pattern, ContentType Type, double Weight, string Label)[] FolderRules =
    [
        (Word(@"animes?|donghua"), ContentType.Anime, 5, "anime"),
        (Word(@"cartoons?|animation|animated|pixar|dreamworks|kids(?:\s+shows)?|children'?s"), ContentType.Animation, 4, "animation"),
        (Word(@"sitcoms?"), ContentType.Sitcom, 5, "sitcom"),
        (Word(@"comedy|comedies"), ContentType.Sitcom, 2.5, "comedy"),
        (Word(@"dramas?|k-?dramas?|soaps?|telenovelas?"), ContentType.Drama, 4, "drama"),
        (Word(@"documentar(?:y|ies)|docus?|nature|wildlife|history channel|natgeo"), ContentType.Documentary, 4, "documentary"),
        (Word(@"talk\s?shows?|late\s?night|reality(?:\s+tv)?|news|game\s?shows?|panel\s?shows?|lectures?|courses?|tutorials?|" +
              @"stand-?\s?up|interviews?|webinars?|conferences?|talks|screen\s?recordings?|recordings|meetings|zoom|" +
              @"podcasts?|classes|training|sermons?|streams|vods?"), ContentType.StillCam, 4, "talk/reality/lecture"),
        (Word(@"sports?|nfl|nba|mlb|nhl|ufc|mma|wwe|aew|f1|formula\s?(?:1|one)|motogp|football|soccer|olympics|" +
              @"premier league|boxing|tennis|cricket|rugby|golf|nascar"), ContentType.Sports, 4, "sports"),
        (Word(@"concerts?|music\s?videos?|live\s?music|mtv"), ContentType.Concert, 4, "concert"),
        (Word(@"action|sci-?fi|science fiction|fantasy|marvel|mcu|dceu|superhero(?:es)?|4k|uhd|imax|remux(?:es)?"), ContentType.Cinematic, 3, "action/sci-fi"),
        (Word(@"home\s?(?:videos?|movies)|camera\s?roll|dcim|phone|gopro|family\s?videos?"), ContentType.General, 4, "home video"),
    ];

    private static readonly (Regex Pattern, ContentType Type, double Weight, string Label)[] NameRules =
    [
        (Word(@"ufc|nfl|nba|mlb|nhl|wwe|aew|motogp|formula\s?(?:1|one)|grand\s?prix|premier\s?league|champions\s?league|" +
              @"super\s?bowl|world\s?cup|wimbledon|olympics|playoffs?|full\s?(?:match|game|fight)"), ContentType.Sports, 3.5, "sports event"),
        (Word(@"live\s?(?:at|in|from)|concert|world\s?tour|unplugged|music\s?video|official\s?video|mtv|glastonbury|coachella"), ContentType.Concert, 3, "concert/music video"),
        (Word(@"documentary|bbc\s?earth|national\s?geographic|nat\s?geo|pbs|nova|frontline|imax\s?documentary|narrated\s?by"), ContentType.Documentary, 3, "documentary"),
        (Word(@"stand-?\s?up|comedy\s?special|podcast|lecture|keynote|webinar|interview|ted\s?talk|tedx|q&a|panel|" +
              @"press\s?conference|tutorial|lesson|course|class|screen\s?recording|zoom|meeting|town\s?hall|debate"), ContentType.StillCam, 3, "talk/lecture"),
        (Word(@"ova|oad|ncop|nced|dual[\s._-]?audio|multi[\s._-]?subs?"), ContentType.Anime, 1.5, "anime release term"),
        (Word(@"imax|remux|2160p|uhd|hdr10?\+?|dolby\s?vision|atmos|truehd"), ContentType.Cinematic, 1, "premium release"),
        (Word(@"vid_?\d{8}|img_?\d{4}|mvi_?\d{4}|gopr\d{4}|gx\d{6}|dji_?\d{4}|pxl_\d{8}|\d{8}_\d{6}"), ContentType.General, 5, "camera file name"),
    ];

    private static Regex Word(string alternation) => new(@"(?<![A-Za-z0-9])(?:" + alternation + @")(?![A-Za-z0-9])", Opt | RegexOptions.Compiled);

    public static Classification Classify(MediaFile file, OnlineMetadata? online = null)
    {
        file.Parsed = FileNameParser.Parse(file.Path);
        return file.Kind == MediaKind.Audio ? ClassifyAudio(file) : ClassifyVideo(file, online);
    }

    private sealed class Tally
    {
        public readonly Dictionary<ContentType, double> Scores = new();
        public readonly List<(double Weight, string Text)> Reasons = [];

        public void Add(ContentType type, double weight, string? reason = null)
        {
            Scores[type] = Scores.GetValueOrDefault(type) + weight;
            if (reason is not null) Reasons.Add((weight, $"{ContentTypes.DisplayName(type)}: {reason}"));
        }

        public Classification Result(ContentType fallback)
        {
            var ranked = Scores.OrderByDescending(s => s.Value).ToList();
            var result = new Classification { Type = fallback };
            if (ranked.Count > 0 && ranked[0].Value >= 1.5)
            {
                double best = ranked[0].Value;
                double second = ranked.Count > 1 ? ranked[1].Value : 0;
                result.Type = ranked[0].Key;
                // Confident when there is a lot of evidence and little of it points elsewhere.
                result.Confidence = Math.Clamp((best - second) / (best + 2.0) + Math.Min(best, 8) / 40.0, 0.05, 0.99);
            }
            else
            {
                result.Confidence = 0.1;
                Reasons.Add((0, "No strong evidence; using the general profile"));
            }
            result.Reasons = Reasons.OrderByDescending(r => r.Weight).Select(r => r.Text).ToList();
            return result;
        }
    }

    private static Classification ClassifyVideo(MediaFile file, OnlineMetadata? online)
    {
        var tally = new Tally();
        var parsed = file.Parsed;
        var probe = file.Probe;
        // The whole path counts: scanning "D:\TV\Sitcoms\Friends" should still see "Sitcoms".
        string folderPath = file.Directory;

        double minutes = (probe?.DurationSeconds ?? 0) / 60.0;
        bool episodic = parsed.IsEpisodic;

        // ---- online database: the strongest single signal ----
        if (online?.ToContentType() is { } onlineType)
        {
            string genres = online.Genres.Count > 0 ? string.Join(", ", online.Genres) : online.Kind;
            tally.Add(onlineType, 8, $"{online.Source} lists \"{online.Name}\" as {online.Kind} ({genres}{(online.Language.Length > 0 ? ", " + online.Language : "")})");
        }

        // ---- well-known titles ----
        var known = KnownTitles.Lookup(parsed.Title) ?? KnownTitles.Lookup(parsed.ShowFolder);
        if (known is { } knownType)
        {
            string matched = KnownTitles.Lookup(parsed.Title) is not null ? parsed.Title : parsed.ShowFolder;
            // One-word titles ("Life", "Avatar") collide with unrelated files too easily to be decisive.
            double weight = FileNameParser.Normalize(matched).Length >= 8 ? 6 : 3.5;
            tally.Add(knownType, weight, $"\"{matched}\" is a known title");
        }

        // ---- folders ----
        foreach (var rule in FolderRules)
        {
            var m = rule.Pattern.Match(folderPath);
            if (!m.Success) continue;
            if (rule.Label == "comedy" && !episodic) continue; // a comedy film is not a sitcom
            tally.Add(rule.Type, rule.Weight, $"folder path contains \"{m.Value}\"");
        }

        // ---- file name ----
        if (parsed.ReleaseGroup is { } group && AnimeGroups.Contains(group))
            tally.Add(ContentType.Anime, 5, $"released by anime group [{group}]");
        else if (parsed.FansubStyle && parsed.HasCrc)
            tally.Add(ContentType.Anime, 4, "fansub naming: [group] prefix and CRC tag");
        else if (parsed.FansubStyle && parsed.AbsoluteNumbering)
            tally.Add(ContentType.Anime, 3, "fansub naming: [group] prefix and absolute episode number");
        else if (parsed.FansubStyle)
            tally.Add(ContentType.Anime, 1.5, "name starts with a [group] tag");

        foreach (var rule in NameRules)
        {
            var m = rule.Pattern.Match(file.Name);
            if (m.Success) tally.Add(rule.Type, rule.Weight, $"file name contains \"{m.Value}\"");
        }
        if (parsed.IsDateBased)
            tally.Add(ContentType.StillCam, 3, "episodes are dated rather than numbered (daily show)");

        // ---- embedded tags ----
        if (probe is not null)
        {
            if (probe.Tag("genre") is { Length: > 0 } genre)
                ScoreGenreTag(tally, genre, episodic, minutes);

            // ---- streams ----
            var video = probe.Video;
            var audio = probe.AudioStreams.ToList();
            var subs = probe.SubtitleStreams.ToList();
            bool japaneseAudio = audio.Any(a => Util.Languages.Same(a.Language, "ja"));
            bool koreanAudio = audio.Any(a => Util.Languages.Same(a.Language, "ko"));
            bool styledSubs = subs.Any(s => s.CodecName is "ass" or "ssa");
            bool fonts = probe.Attachments.Any();

            if (japaneseAudio)
            {
                tally.Add(ContentType.Anime, 2.5, "has a Japanese audio track");
                if (styledSubs) tally.Add(ContentType.Anime, 1.5, "Japanese audio with styled ASS subtitles");
            }
            if (styledSubs && fonts)
                tally.Add(ContentType.Anime, 2, "styled ASS subtitles with embedded fonts");
            else if (styledSubs && !japaneseAudio)
                tally.Add(ContentType.Anime, 1, "styled ASS subtitles");
            if (koreanAudio && minutes is >= 40 and <= 90)
                tally.Add(ContentType.Drama, 1.5, "Korean audio at drama episode length");

            if (minutes > 0)
            {
                if (minutes is >= 17 and <= 27)
                {
                    tally.Add(ContentType.Sitcom, episodic ? 1.4 : 0.7, $"half-hour episode length ({minutes:0} min)");
                    tally.Add(ContentType.Anime, 0.6);
                    tally.Add(ContentType.Animation, 0.6);
                }
                else if (minutes is >= 37 and <= 68)
                {
                    tally.Add(ContentType.Drama, episodic ? 1.4 : 0.7, $"hour-long episode length ({minutes:0} min)");
                    tally.Add(ContentType.Cinematic, 0.4);
                }
                else if (minutes >= 75)
                {
                    tally.Add(ContentType.Drama, 0.8, $"feature length ({minutes:0} min)");
                    tally.Add(ContentType.Cinematic, 0.6);
                }
                else if (minutes < 8)
                {
                    tally.Add(ContentType.General, 1, $"short clip ({minutes:0.#} min)");
                }
            }

            if (video is not null)
            {
                if (video.FrameRate >= 47)
                {
                    tally.Add(ContentType.Sports, 1.2, $"high frame rate ({video.FrameRate:0.##} fps)");
                    tally.Add(ContentType.StillCam, 0.6);
                }
                if (video.IsInterlaced)
                {
                    tally.Add(ContentType.StillCam, 0.5, "interlaced broadcast recording");
                    tally.Add(ContentType.Sports, 0.4);
                }
                if (video.IsHdr || video.HasDolbyVision)
                    tally.Add(ContentType.Cinematic, 1.5, "HDR picture");
                if (video.Height >= 2000 || video.Width >= 3800)
                    tally.Add(ContentType.Cinematic, 0.8, "4K picture");
            }
            if (audio.Any(a => a.CodecName is "truehd" or "dts" && a.Channels >= 6) || audio.Any(a => a.Channels >= 8))
                tally.Add(ContentType.Cinematic, 1, "high-end surround audio");
        }

        return tally.Result(ContentType.General);
    }

    private static void ScoreGenreTag(Tally tally, string genre, bool episodic, double minutes)
    {
        (string Pattern, ContentType Type)[] map =
        [
            ("anime", ContentType.Anime), ("animation|animated|cartoon|kids|children", ContentType.Animation),
            ("documentary|nature|history", ContentType.Documentary), ("sport", ContentType.Sports),
            ("concert|music", ContentType.Concert), ("reality|talk|news|game show|stand-up|standup|educational|podcast", ContentType.StillCam),
            ("action|adventure|sci-fi|science fiction|fantasy|war|western|horror|thriller", ContentType.Cinematic),
            ("drama|romance|crime|mystery", ContentType.Drama),
        ];
        foreach (var (pattern, type) in map)
        {
            if (!Regex.IsMatch(genre, pattern, Opt)) continue;
            tally.Add(type, 4, $"file is tagged with genre \"{genre}\"");
            return;
        }
        if (Regex.IsMatch(genre, "comedy|sitcom", Opt))
        {
            bool halfHour = episodic || minutes is > 0 and <= 35;
            tally.Add(halfHour ? ContentType.Sitcom : ContentType.Drama, 4, $"file is tagged with genre \"{genre}\"");
        }
    }

    private static readonly Regex SpeechFolders = Word(@"audio\s?books?|podcasts?|lectures?|speech(?:es)?|sermons?|interviews?|voice\s?memos?|" +
                                                       @"recordings|radio\s?(?:shows?|dramas?|plays?)|courses?|language\s?learning|spoken(?:\s+word)?|talks");
    private static readonly Regex SpeechGenres = new("audio\\s?book|podcast|speech|spoken|books|comedy|talk|lecture|sermon|radio|news|education", Opt | RegexOptions.Compiled);
    private static readonly Regex MusicFolders = Word(@"music|albums?|discography|soundtracks?|ost|singles|playlists?|flac|mp3s?|lossless|vinyl");

    private static Classification ClassifyAudio(MediaFile file)
    {
        var tally = new Tally();
        var probe = file.Probe;
        double minutes = (probe?.DurationSeconds ?? 0) / 60.0;

        tally.Add(ContentType.Music, 1.5); // most audio files are music

        if (file.Extension == "m4b")
            tally.Add(ContentType.Speech, 6, ".m4b is the audiobook format");
        if (SpeechFolders.Match(file.Directory) is { Success: true } speech)
            tally.Add(ContentType.Speech, 5, $"folder path contains \"{speech.Value}\"");
        if (MusicFolders.Match(file.Directory) is { Success: true } music)
            tally.Add(ContentType.Music, 2.5, $"folder path contains \"{music.Value}\"");
        if (Regex.IsMatch(file.Name, @"(?<![A-Za-z])(podcast|episode|ep\s?\d+|chapter\s?\d+|audiobook|lecture|interview|unabridged)(?![A-Za-z])", Opt))
            tally.Add(ContentType.Speech, 3, "file name looks like a podcast or audiobook");

        if (probe is not null)
        {
            if (probe.Tag("genre") is { Length: > 0 } genre)
            {
                if (SpeechGenres.IsMatch(genre)) tally.Add(ContentType.Speech, 5, $"tagged with genre \"{genre}\"");
                else tally.Add(ContentType.Music, 3, $"tagged with genre \"{genre}\"");
            }
            if (probe.Tag("album") is not null && probe.Tag("track") is not null)
                tally.Add(ContentType.Music, 1, "has album and track tags");
            if (probe.ChapterCount > 1)
                tally.Add(ContentType.Speech, 2.5, "has chapters");

            var audio = probe.AudioStreams.FirstOrDefault();
            if (audio is not null)
            {
                if (audio.Channels == 1 && minutes >= 10)
                    tally.Add(ContentType.Speech, 2.5, "long mono recording");
                if (audio.BitRate is > 0 and <= 72_000 && minutes >= 10)
                    tally.Add(ContentType.Speech, 1.5, $"low bitrate ({audio.BitRate / 1000} kbps)");
                if (audio.CodecName is "flac" or "alac" or "ape" or "wavpack" || audio.CodecName.StartsWith("pcm_", StringComparison.Ordinal))
                    tally.Add(ContentType.Music, 1.5, "lossless audio");
            }
            if (minutes >= 45)
                tally.Add(ContentType.Speech, 1.5, $"very long for a song ({minutes:0} min)");
        }
        return tally.Result(ContentType.Music);
    }

    /// <summary>
    /// Episodes in one folder belong to one show. Where most files in a folder agree, bring the
    /// uncertain ones into line. Returns the files whose type changed.
    /// </summary>
    public static List<MediaFile> ApplyFolderConsensus(IEnumerable<MediaFile> files)
    {
        var changed = new List<MediaFile>();
        foreach (var group in files.Where(f => f.Kind == MediaKind.Video).GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 3) continue;

            var votes = members.Where(f => !f.Classification.IsManual && f.Classification.Confidence >= 0.35)
                .GroupBy(f => f.Classification.Type)
                .Select(g => (Type: g.Key, Count: g.Count(), Weight: g.Sum(f => f.Classification.Confidence)))
                .OrderByDescending(v => v.Weight).ToList();
            if (votes.Count == 0 || votes[0].Type == ContentType.General) continue;
            var winner = votes[0];
            if (winner.Count < members.Count * 0.6) continue;

            foreach (var file in members)
            {
                var c = file.Classification;
                if (c.IsManual || c.Type == winner.Type || c.Confidence >= 0.6) continue;
                c.Reasons.Insert(0, $"{ContentTypes.DisplayName(winner.Type)}: {winner.Count} of {members.Count} files in this folder were recognised as this");
                c.Type = winner.Type;
                c.Confidence = Math.Max(c.Confidence, 0.5);
                changed.Add(file);
            }
        }
        return changed;
    }
}
