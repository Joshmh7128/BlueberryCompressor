using AutoCompressor.Core.Models;
using AutoCompressor.Core.Probing;
using AutoCompressor.Core.Profiles;
using AutoCompressor.Core.Queue;
using AutoCompressor.Core.Recognition;
using AutoCompressor.Core.Scanning;
using AutoCompressor.Core.Subtitles;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;
using AutoCompressor.Tests;

// Usage: AutoCompressor.Tests [--online] [--recycle] [--keep] [--only <section substring>]
bool online = args.Contains("--online");
bool recycle = args.Contains("--recycle");
bool keep = args.Contains("--keep");
string? only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault();

string work = Path.Combine(Path.GetTempPath(), "AutoCompressorTests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
AppPaths.OverrideDataDir(Path.Combine(work, "_appdata"));
Console.WriteLine("Working in " + work);

var tools = Tools.Locate(null, null);
var settings = new AppSettings();
var history = new History();
EncoderAvailability encoders = new();
var media = new MediaFactory(tools, work);
var profiles = new ProfileStore();
var services = new JobServices { Settings = () => settings, Tools = () => tools, Encoders = () => encoders, History = history };

bool Run(string section)
{
    if (only is not null && !section.Contains(only, StringComparison.OrdinalIgnoreCase)) return false;
    T.Section(section);
    return true;
}

async Task<QueueJob> RunJob(string path, Profile profile, OutputOptions? output = null, MediaKind? kind = null)
{
    var file = media.Load(path, kind);
    var job = QueueManager.CreateJob(file, profile, output ?? new OutputOptions());
    job.Status = JobStatus.Running;
    try { await new JobProcessor(services).RunAsync(job, new FfmpegRunner(), _ => { }, CancellationToken.None); }
    catch (JobFailedException ex) { job.Status = JobStatus.Failed; job.Message = ex.Message; }
    T.Info($"{Path.GetFileName(path)} -> {job.Status}: {job.Message}");
    if (job.Summary is not null) T.Info(job.Summary);
    foreach (var note in job.Notes) T.Info("note: " + note);
    return job;
}

// =====================================================================================
if (Run("File name parsing"))
{
    void Case(string path, string title, int? season = null, int? episode = null, int? year = null, bool fansub = false, bool dated = false)
    {
        var p = FileNameParser.Parse(path);
        bool ok = p.Title == title && p.Season == season && p.Episode == episode && (year is null || p.Year == year) && p.FansubStyle == fansub && p.IsDateBased == dated;
        T.Check(Path.GetFileName(path), ok, $"title=[{p.Title}] s={p.Season} e={p.Episode} y={p.Year} fansub={p.FansubStyle} dated={p.IsDateBased}");
    }

    Case(@"D:\TV\Seinfeld\Season 4\Seinfeld.S04E11.The.Contest.1080p.WEB-DL.x264-GRP.mkv", "Seinfeld", 4, 11);
    Case(@"D:\Anime\[SubsPlease] Sousou no Frieren - 05 (1080p) [A1B2C3D4].mkv", "Sousou no Frieren", null, 5, fansub: true);
    Case(@"D:\Anime\[Erai-raws] Jujutsu Kaisen 2nd Season - 12 [1080p][Multiple Subtitle].mkv", "Jujutsu Kaisen 2nd Season", null, 12, fansub: true);
    Case(@"D:\Movies\Blade.Runner.2049.2017.2160p.UHD.BluRay.x265-GRP.mkv", "Blade Runner 2049", year: 2017);
    Case(@"D:\Movies\The Matrix (1999)\The Matrix (1999).mkv", "The Matrix", year: 1999);
    Case(@"D:\Movies\1917.2019.1080p.BluRay.mkv", "1917", year: 2019);
    Case(@"D:\TV\Friends\Season 1\S01E03.mkv", "Friends", 1, 3);
    Case(@"D:\TV\The Office (US)\Season 02\The Office (US) - 2x05 - Halloween.avi", "The Office", 2, 5);
    Case(@"D:\TV\The.Daily.Show.2024.03.14.Guest.Name.720p.WEB.h264.mkv", "The Daily Show", year: 2024, dated: true);
    Case(@"D:\TV\Breaking Bad\Breaking Bad Season 3 Episode 7.mp4", "Breaking Bad", 3, 7);
    Case(@"D:\Anime\Cowboy Bebop\Cowboy Bebop - 05.mkv", "Cowboy Bebop", null, 5);
    Case(@"D:\Shows\Planet Earth II\01.mkv", "Planet Earth II", null, 1);
    Case(@"D:\TV\Doctor.Who.2005.S01E01.Rose.mkv", "Doctor Who", 1, 1, year: 2005);
    Case(@"D:\Video\some_home_video.mp4", "some home video");

    T.Equal("normalise drops article and punctuation", "office", FileNameParser.Normalize("The Office"));
    T.Equal("normalise handles ampersand", "willandgrace", FileNameParser.Normalize("Will & Grace"));
    T.Equal("normalise strips accents", "pokemon", FileNameParser.Normalize("Pokémon"));
}

// =====================================================================================
if (Run("Content recognition"))
{
    static StreamInfo Video(int w = 1920, int h = 1080, double fps = 23.976, string codec = "h264") =>
        new() { Index = 0, CodecType = "video", CodecName = codec, Width = w, Height = h, FrameRate = fps, BitDepth = 8 };
    static StreamInfo Audio(int index, string lang = "eng", int ch = 2, string codec = "aac") =>
        new() { Index = index, CodecType = "audio", CodecName = codec, Channels = ch, Language = lang };
    static StreamInfo Sub(int index, string codec, string lang = "eng") =>
        new() { Index = index, CodecType = "subtitle", CodecName = codec, Language = lang };

    Classification Classify(string path, double minutes, MediaKind kind = MediaKind.Video, OnlineMetadata? online = null, params StreamInfo[] streams)
    {
        var file = new MediaFile { Path = path, Kind = kind, Probe = new ProbeResult { DurationSeconds = minutes * 60, Streams = [.. streams] } };
        file.Classification = ContentClassifier.Classify(file, online);
        return file.Classification;
    }

    void Expect(string name, ContentType expected, Classification actual) =>
        T.Check(name, actual.Type == expected, $"got {actual.Type} ({actual.ConfidenceLabel}): {string.Join(" | ", actual.Reasons.Take(3))}");

    Expect("fansub release with Japanese audio and ASS subs is anime", ContentType.Anime,
        Classify(@"D:\Downloads\[SubsPlease] Sousou no Frieren - 05 (1080p) [A1B2C3D4].mkv", 24, streams: [Video(), Audio(1, "jpn"), Sub(2, "ass")]));
    Expect("known sitcom", ContentType.Sitcom,
        Classify(@"D:\TV\Seinfeld\Season 4\Seinfeld.S04E11.The.Contest.mkv", 22, streams: [Video(), Audio(1)]));
    Expect("known drama", ContentType.Drama,
        Classify(@"D:\TV\Breaking Bad\Season 1\Breaking.Bad.S01E01.mkv", 58, streams: [Video(), Audio(1, ch: 6, codec: "ac3")]));
    Expect("known western animation", ContentType.Animation,
        Classify(@"D:\TV\The Simpsons\Season 5\The.Simpsons.S05E01.mkv", 22, streams: [Video(), Audio(1)]));
    Expect("known cinematic series", ContentType.Cinematic,
        Classify(@"D:\TV\Game of Thrones\Season 1\Game.of.Thrones.S01E01.2160p.mkv", 62, streams: [Video(3840, 2160), Audio(1, ch: 8, codec: "truehd")]));
    Expect("folder named Anime decides an unknown show", ContentType.Anime,
        Classify(@"D:\Media\Anime\Some Obscure Show\Some Obscure Show - 03.mkv", 24, streams: [Video(), Audio(1, "jpn")]));
    Expect("folder named Documentaries", ContentType.Documentary,
        Classify(@"D:\Media\Documentaries\Deep Ocean (2019).mkv", 92, streams: [Video(), Audio(1)]));
    Expect("dated daily show is talk/reality", ContentType.StillCam,
        Classify(@"D:\TV\Some.Late.Program.2024.03.14.Guest.720p.mkv", 41, streams: [Video(1280, 720, 29.97), Audio(1)]));
    Expect("lecture folder", ContentType.StillCam,
        Classify(@"D:\Courses\Linear Algebra\Lecture 04 - Eigenvalues.mp4", 51, streams: [Video(1280, 720, 30), Audio(1, ch: 1)]));
    Expect("sports event name", ContentType.Sports,
        Classify(@"D:\Recordings\UFC 300 Main Card 1080p.mkv", 180, streams: [Video(1920, 1080, 59.94), Audio(1)]));
    Expect("concert name", ContentType.Concert,
        Classify(@"D:\Video\Some Band - Live at Wembley 2019.mkv", 110, streams: [Video(), Audio(1, ch: 6, codec: "flac")]));
    // Episode length alone is not enough to risk lowering quality on what might be animation.
    var unknown = Classify(@"D:\TV\Unknown Show\Unknown.Show.S02E04.mkv", 22, streams: [Video(), Audio(1)]);
    T.Check("an unknown show with only its length to go on stays General, flagged low confidence",
        unknown is { Type: ContentType.General, ConfidenceLabel: "Low" }, $"{unknown.Type} {unknown.ConfidenceLabel}");
    Expect("the same unknown show in a Comedy folder is a sitcom", ContentType.Sitcom,
        Classify(@"D:\TV\Comedy\Unknown Show\Unknown.Show.S02E04.mkv", 22, streams: [Video(), Audio(1)]));
    Expect("phone camera clip is general", ContentType.General,
        Classify(@"D:\Phone\VID_20240314_101500.mp4", 1.5, streams: [Video(1920, 1080, 30), Audio(1)]));
    Expect("HDR 4K film leans cinematic", ContentType.Cinematic,
        Classify(@"D:\Movies\Unknown Film (2021).mkv", 130, streams:
        [
            new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 3840, Height = 2160, FrameRate = 23.976, BitDepth = 10, ColorTransfer = "smpte2084" },
            Audio(1, ch: 8, codec: "truehd"),
        ]));

    var tvAnime = new OnlineMetadata { Found = true, Source = "TVMaze", Name = "Obscure", Kind = "Animation", Language = "Japanese", Genres = ["Action", "Anime"], RuntimeMinutes = 24 };
    Expect("online metadata outweighs local guesses", ContentType.Anime,
        Classify(@"D:\TV\Obscure\Obscure.S01E01.mkv", 24, online: tvAnime, streams: [Video(), Audio(1)]));
    T.Equal("online: scripted half-hour comedy is a sitcom", ContentType.Sitcom,
        new OnlineMetadata { Found = true, Kind = "Scripted", Genres = ["Comedy"], RuntimeMinutes = 30, Language = "English" }.ToContentType());
    T.Equal("online: talk show", ContentType.StillCam, new OnlineMetadata { Found = true, Kind = "Talk Show", Genres = ["Comedy"] }.ToContentType());
    T.Equal("online: scripted sci-fi", ContentType.Cinematic,
        new OnlineMetadata { Found = true, Kind = "Scripted", Genres = ["Drama", "Science-Fiction"], RuntimeMinutes = 60 }.ToContentType());
    T.Equal("online: film comedy is not a sitcom", ContentType.Drama, new OnlineMetadata { Found = true, Kind = "Movie", Genres = ["Comedy", "Romance"] }.ToContentType());

    Expect("audiobook by extension", ContentType.Speech,
        Classify(@"D:\Books\Some Novel.m4b", 600, MediaKind.Audio, streams: [Audio(0, ch: 1)]));
    Expect("podcast folder", ContentType.Speech,
        Classify(@"D:\Podcasts\Some Show\Episode 12.mp3", 62, MediaKind.Audio, streams: [Audio(0, codec: "mp3")]));
    Expect("ordinary song", ContentType.Music,
        Classify(@"D:\Music\Artist\Album\03 - Track.flac", 4, MediaKind.Audio, streams: [Audio(0, codec: "flac")]));

    // Folder consensus: two uncertain files follow three confident ones.
    var folder = new List<MediaFile>();
    for (int i = 1; i <= 5; i++)
    {
        var f = new MediaFile { Path = $@"D:\X\Mystery Show\Mystery.Show.S01E0{i}.mkv", Kind = MediaKind.Video };
        f.Classification = new Classification { Type = i <= 3 ? ContentType.Anime : ContentType.General, Confidence = i <= 3 ? 0.7 : 0.1 };
        folder.Add(f);
    }
    var changed = ContentClassifier.ApplyFolderConsensus(folder);
    T.Check("folder consensus pulls uncertain files into line", changed.Count == 2 && folder.All(f => f.Classification.Type == ContentType.Anime));

    T.Check("title matching accepts small differences", OnlineLookup.TitlesMatch("Brooklyn Nine Nine", "Brooklyn Nine-Nine") && OnlineLookup.TitlesMatch("Frieren", "Frieren: Beyond Journey's End"));
    T.Check("title matching rejects unrelated names", !OnlineLookup.TitlesMatch("Holiday Video", "Seinfeld"));
}

// =====================================================================================
if (Run("Profiles and settings"))
{
    T.Check("every content type maps to a built-in profile of the right kind",
        Enum.GetValues<ContentType>().All(t => profiles.ForType(t, settings).Kind == ContentTypes.KindOf(t)));
    var anime = profiles.Find(BuiltInProfiles.Anime)!;
    var still = profiles.Find(BuiltInProfiles.StillCam)!;
    T.Check("animation gets better picture quality than talk shows", anime.Quality < still.Quality && anime.BitDepth == BitDepthMode.Force10Bit && still.MaxHeight == 720);
    T.Check("concert profile keeps audio untouched", profiles.Find(BuiltInProfiles.Concert)!.AudioCodec == AudioCodec.Copy);

    var custom = anime.Clone();
    custom.Id = "custom1"; custom.Name = "My anime"; custom.Quality = 17;
    profiles.Add(custom);
    anime.Quality = 18;
    profiles.Save();
    var reloaded = new ProfileStore();
    T.Check("custom profile survives a reload", reloaded.Find("custom1") is { Quality: 17, BuiltIn: false });
    T.Check("edited built-in survives a reload", reloaded.Find(BuiltInProfiles.Anime) is { Quality: 18, BuiltIn: true });
    T.Check("resetting a built-in restores stock values", reloaded.Reset(BuiltInProfiles.Anime) is { Quality: 19 });
    profiles.Remove(custom);
    profiles.Reset(BuiltInProfiles.Anime);
    profiles.Save();

    var s = new AppSettings { ScheduleEnabled = true, ScheduleStart = "23:00", ScheduleEnd = "07:00" };
    T.Check("overnight schedule window", s.WithinSchedule(new DateTime(2026, 1, 1, 23, 30, 0)) && s.WithinSchedule(new DateTime(2026, 1, 1, 3, 0, 0)) && !s.WithinSchedule(new DateTime(2026, 1, 1, 12, 0, 0)));

    string secret = Native.Protect("hunter2-test");
    T.Check("saved passwords are encrypted and round-trip", secret != "hunter2-test" && secret.Length > 20 && Native.Unprotect(secret) == "hunter2-test");
    T.Equal("language codes normalise", "fr", Languages.ToTwoLetter("fre"));
    T.Equal("language list parsing", "en,ja", string.Join(',', Languages.ParseList("eng, jpn; en")));
}

// =====================================================================================
if (!tools.Available)
{
    Console.WriteLine("\nffmpeg/ffprobe not found; skipping everything that needs them.");
    return Finish();
}
Console.WriteLine($"\nffmpeg {tools.Version} at {tools.Ffmpeg}");

if (Run("Encoder detection") || true)
{
    encoders = await EncoderDetector.DetectAsync(tools);
    if (only is null || "Encoder detection".Contains(only, StringComparison.OrdinalIgnoreCase))
    {
        foreach (var e in EncoderCatalog.All)
            T.Info($"{e.Id,-12} {(encoders.IsUsable(e.Id) ? "usable" + (encoders.SupportsTenBit(e) ? ", 10-bit" : ", 8-bit only") : encoders.Status.GetValueOrDefault(e.Id))}");
        T.Check("x265 is known to do 10-bit and x264 is treated as 8-bit", encoders.SupportsTenBit(EncoderCatalog.Find("libx265")!) && !encoders.SupportsTenBit(EncoderCatalog.Find("libx264")!));
        T.Check("software H.264 and HEVC encoders are usable", encoders.IsUsable("libx264") && encoders.IsUsable("libx265"));
        var hevc = new Profile { Codec = VideoCodec.HEVC };
        T.Equal("software policy resolves to x265", "libx265", encoders.Resolve(hevc, new AppSettings { EncoderPolicy = EncoderPolicy.Software })?.Id);
        var hw = encoders.Resolve(hevc, new AppSettings { EncoderPolicy = EncoderPolicy.Hardware });
        T.Check("hardware policy resolves to a usable encoder", hw is not null && encoders.IsUsable(hw.Id), hw?.Id);
        T.Info("hardware HEVC choice: " + hw?.Id);
        T.Equal("forcing software overrides a hardware profile", "libx265",
            encoders.Resolve(new Profile { Codec = VideoCodec.HEVC, Encoder = EncoderPreference.Hardware }, settings, forceSoftware: true)?.Id);
    }
}

// =====================================================================================
if (Run("Scanning"))
{
    string root = media.Path("scan", "x")[..^2];
    media.PlainVideo(@"scan\Shows\Show A\Season 1\Show.A.S01E01.mkv", 2);
    media.PlainVideo(@"scan\Shows\Show A\Season 1\Show.A.S01E02.mkv", 3);
    media.PlainVideo(@"scan\Movies\Film (2020).mp4", 2);
    media.Wav(@"scan\Music\song.wav", 2);
    File.WriteAllText(media.Path("scan", "Shows", "Show A", "notes.txt"), new string('x', 5000));
    File.WriteAllText(media.Path("scan", "Empty", "readme.txt"), "nothing here");
    File.WriteAllText(media.Path("scan", "Movies", "Film (2020).ac-tmp.mkv"), "half written");

    var result = LibraryScanner.Scan([root]);
    var top = result.Roots[0];
    T.Equal("finds every media file recursively", 4, result.Files.Count);
    T.Check("ignores its own temp files", result.Files.All(f => !f.Name.Contains(".ac-tmp")));
    T.Equal("root media size is the sum of its files", result.Files.Sum(f => f.Size), top.MediaSize);
    T.Check("total size also counts non-media files", top.TotalSize > top.MediaSize);
    T.Check("folders without media are left out of the tree", top.Folders.All(f => f.Name != "Empty"));
    var shows = top.Folders.First(f => f.Name == "Shows");
    T.Equal("nested folder rolls up its files", 2, shows.MediaCount);
    T.Equal("nested folder size", result.Files.Where(f => f.Path.Contains(@"\Shows\")).Sum(f => f.Size), shows.MediaSize);
    T.Check("files know which folder and root they belong to", result.Files.All(f => f.Folder is not null && f.Root == root));
    T.Check("kinds are told apart", result.Files.Count(f => f.Kind == MediaKind.Audio) == 1);

    var single = LibraryScanner.Scan([result.Files[0].Path]);
    T.Check("a single file can be scanned on its own", single.Files.Count == 1 && single.Roots.Count == 1);

    long before = shows.MediaSize, rootBefore = top.MediaSize;
    result.Files.First(f => f.Path.Contains("S01E01")).Folder!.ApplySizeDelta(-1000);
    T.Check("size changes propagate up the tree", shows.MediaSize == before - 1000 && top.MediaSize == rootBefore - 1000);
}

// =====================================================================================
string animePath = "";
if (Run("Probing"))
{
    animePath = media.AnimeMkv(@"lib\Anime\[SubsPlease] Sousou no Frieren - 05 (720p) [A1B2C3D4].mkv");
    var p = media.Probe(animePath);
    T.Check("video stream details", p.Video is { CodecName: "h264", Width: 1280, Height: 720 } && Math.Abs(p.Video.FrameRate - 23.976) < 0.01);
    T.Equal("audio track count", 2, p.AudioStreams.Count());
    T.Check("audio languages and layouts", p.AudioStreams.First().Language == "jpn" && p.AudioStreams.Last() is { Channels: 6, ChannelLayout: "5.1(side)" });
    T.Check("subtitle formats", p.SubtitleStreams.Select(s => s.CodecName).SequenceEqual(["ass", "subrip"]));
    T.Check("forced flag is read", p.SubtitleStreams.Last().IsForced);
    T.Equal("font attachment found", 1, p.Attachments.Count());
    T.Equal("chapters counted", 2, p.ChapterCount);
    T.Check("duration", Math.Abs(p.DurationSeconds - 4) < 0.3, p.DurationSeconds.ToString());
    T.Check("stale statistics tags are noticed", p.Video!.StatTags.Contains("BPS"));

    var file = media.Load(animePath);
    file.Classification = ContentClassifier.Classify(file);
    T.Equal("the generated anime file is recognised as anime", ContentType.Anime, file.Classification.Type);
    T.Info(string.Join(" | ", file.Classification.Reasons.Take(4)));

    var cache = new ProbeCache();
    cache.Set(file, p);
    cache.Save();
    T.Check("probe cache round-trips", new ProbeCache().Get(file) is { } cached && cached.Streams.Count == p.Streams.Count && cached.Tag("ENCODER") == p.Tag("encoder"));
}

// =====================================================================================
if (Run("Encode: anime MKV keeps subtitles in their original format"))
{
    if (animePath.Length == 0) animePath = media.AnimeMkv(@"lib\Anime\[SubsPlease] Sousou no Frieren - 05 (720p) [A1B2C3D4].mkv");
    var profile = profiles.Find(BuiltInProfiles.Anime)!.Clone();
    profile.Speed = "veryfast";
    var job = await RunJob(animePath, profile);
    T.Equal("job completed", JobStatus.Completed, job.Status);
    if (job.Status == JobStatus.Completed)
    {
        var o = media.Probe(job.OutputPath!);
        T.Check("output sits beside the original with the suffix", job.OutputPath!.EndsWith(".compressed.mkv") && File.Exists(animePath));
        T.Check("video is 10-bit HEVC", o.Video is { CodecName: "hevc", BitDepth: 10 }, $"{o.Video?.CodecName} {o.Video?.BitDepth}");
        T.Check("ASS subtitles are still ASS, SRT still SRT", o.SubtitleStreams.Select(s => s.CodecName).SequenceEqual(["ass", "subrip"]),
            string.Join(",", o.SubtitleStreams.Select(s => s.CodecName)));
        T.Check("subtitle language, title and forced flag survive",
            o.SubtitleStreams.First() is { Language: "eng", Title: "Full Subs" } && o.SubtitleStreams.Last().IsForced);
        T.Equal("embedded font kept", 1, o.Attachments.Count());
        T.Equal("chapters kept", 2, o.ChapterCount);
        T.Equal("both audio tracks kept", 2, o.AudioStreams.Count());
        T.Check("audio languages kept", o.AudioStreams.Select(a => a.Language).SequenceEqual(["jpn", "eng"]));
        T.Check("small AAC track is copied, big AC-3 becomes Opus 5.1",
            o.AudioStreams.First().CodecName == "aac" && o.AudioStreams.Last() is { CodecName: "opus", Channels: 6 },
            string.Join(",", o.AudioStreams.Select(a => $"{a.CodecName}/{a.Channels}")));
        T.Check("output is marked as compressed", o.CompressorTag?.Contains("profile=anime") == true, o.CompressorTag);
        T.Check("stale bitrate tag was cleared", !o.Video!.StatTags.Contains("BPS"));
        T.Check("smaller than the original", job.OutputSize < job.SourceSize, $"{job.SourceSize} -> {job.OutputSize}");
        T.Check("modified time preserved", File.GetLastWriteTimeUtc(job.OutputPath!) == File.GetLastWriteTimeUtc(animePath));

        var again = await RunJob(job.OutputPath!, profile);
        T.Check("an already-compressed file is skipped", again.Status == JobStatus.Skipped && again.Message.Contains("Already compressed"), again.Message);
        var original = await RunJob(animePath, profile);
        T.Check("the original is skipped while its compressed copy exists", original.Status == JobStatus.Skipped && original.Message.Contains("copy already exists"), original.Message);
    }
}

// =====================================================================================
if (Run("Encode: MP4 output and subtitle fallbacks"))
{
    string src = media.AnimeMkv(@"mp4\Show.S01E01.mkv");
    var profile = profiles.Find(BuiltInProfiles.Compatibility)!.Clone();
    profile.Speed = "veryfast";
    var job = await RunJob(src, profile);
    T.Equal("job completed", JobStatus.Completed, job.Status);
    if (job.Status == JobStatus.Completed)
    {
        var o = media.Probe(job.OutputPath!);
        string basePath = job.OutputPath![..^4];
        T.Check("H.264 + AAC in MP4", o.Video?.CodecName == "h264" && o.AudioStreams.All(a => a.CodecName == "aac") && job.OutputPath.EndsWith(".mp4"));
        T.Equal("no subtitle is force-converted into the MP4", 0, o.SubtitleStreams.Count());
        T.Check("ASS written beside the file as .ass", File.Exists(basePath + ".eng.ass"));
        T.Check("forced SRT written beside the file as .srt", File.Exists(basePath + ".eng.forced.srt"));
        if (File.Exists(basePath + ".eng.ass"))
            T.Check("sidecar keeps ASS styling", File.ReadAllText(basePath + ".eng.ass").Contains(@"{\pos(640,600)}"));
        T.Check("MP4 output is marked as compressed", o.CompressorTag is not null, string.Join(",", o.Tags.Keys));
        T.Check("no temp files left behind", !Directory.EnumerateFiles(Path.GetDirectoryName(src)!).Any(f => f.Contains(".ac-tmp")));
    }

    string src2 = media.AnimeMkv(@"mp4b\Show.S01E02.mkv");
    profile.SubtitleFallback = SubtitleFallback.Convert;
    var job2 = await RunJob(src2, profile);
    if (job2.Status == JobStatus.Completed)
        T.Check("Convert fallback embeds both tracks as MP4 timed text",
            media.Probe(job2.OutputPath!).SubtitleStreams.Select(s => s.CodecName).SequenceEqual(["mov_text", "mov_text"]));
    else T.Check("convert-fallback job completed", false, job2.Message);

    string src3 = media.SitcomMp4(@"mp4c\Seinfeld.S04E11.The.Contest.1080p.WEB-DL.mp4");
    var sitcom = profiles.Find(BuiltInProfiles.Sitcom)!.Clone();
    sitcom.Speed = "veryfast";
    var job3 = await RunJob(src3, sitcom);
    if (job3.Status == JobStatus.Completed)
    {
        var o = media.Probe(job3.OutputPath!);
        T.Check("MP4 timed text becomes SRT in MKV (closest equivalent), language kept",
            o.SubtitleStreams.Count() == 1 && o.SubtitleStreams.First() is { CodecName: "subrip", Language: "eng" });
        T.Check("the note explains the conversion", job3.Notes.Any(n => n.Contains("converted to SRT")));
    }
    else T.Check("sitcom job completed", false, job3.Message);
}

// =====================================================================================
if (Run("Encode: talk-show profile reduces resolution and frame rate"))
{
    string src = media.SitcomMp4(@"talk\Some.Late.Program.2024.03.14.1080p.mp4", 4, "60000/1001");
    var profile = profiles.Find(BuiltInProfiles.StillCam)!.Clone();
    profile.Speed = "veryfast";
    var job = await RunJob(src, profile);
    T.Equal("job completed", JobStatus.Completed, job.Status);
    if (job.Status == JobStatus.Completed)
    {
        var o = media.Probe(job.OutputPath!);
        T.Check("scaled to 720p", o.Video is { Width: 1280, Height: 720 }, $"{o.Video?.Width}x{o.Video?.Height}");
        T.Check("59.94 fps halved to 29.97", Math.Abs(o.Video!.FrameRate - 29.97) < 0.02, o.Video.FrameRate.ToString());
        T.Check("8-bit HEVC", o.Video is { CodecName: "hevc", BitDepth: 8 });
        T.Check("duration unchanged by the frame-rate change", Math.Abs(o.DurationSeconds - 4) < 0.3);
    }

    string portrait = media.PlainVideo(@"talk\portrait.mp4", 3, "1080x1920");
    var pj = await RunJob(portrait, profile);
    if (pj.Status == JobStatus.Completed)
        T.Check("portrait video is scaled on its short side", media.Probe(pj.OutputPath!).Video is { Width: 720, Height: 1280 },
            $"{media.Probe(pj.OutputPath!).Video?.Width}x{media.Probe(pj.OutputPath!).Video?.Height}");
    else T.Check("portrait job completed", false, pj.Message);
}

// =====================================================================================
if (Run("Encode: hardware encoder"))
{
    var hwSettings = new AppSettings { EncoderPolicy = EncoderPolicy.Hardware };
    var hw = encoders.Resolve(new Profile { Codec = VideoCodec.HEVC }, hwSettings);
    if (hw is { Hardware: true })
    {
        settings.EncoderPolicy = EncoderPolicy.Hardware;
        string src = media.AnimeMkv(@"hw\[Group] Show - 01 [ABCDEF12].mkv");
        var job = await RunJob(src, profiles.Find(BuiltInProfiles.Anime)!);
        settings.EncoderPolicy = EncoderPolicy.Software;
        T.Equal("job completed", JobStatus.Completed, job.Status);
        T.Check($"used {hw.Id}", job.EncoderId == hw.Id, job.EncoderId);
        if (job.Status == JobStatus.Completed)
        {
            var o = media.Probe(job.OutputPath!);
            T.Check("10-bit HEVC from the GPU with subtitles intact",
                o.Video is { CodecName: "hevc", BitDepth: 10 } && o.SubtitleStreams.Select(s => s.CodecName).SequenceEqual(["ass", "subrip"]));
        }

        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.AV1 })
        {
            var enc = encoders.Resolve(new Profile { Codec = codec }, hwSettings);
            if (enc is not { Hardware: true }) { T.Info($"no hardware {codec} encoder here"); continue; }
            settings.EncoderPolicy = EncoderPolicy.Hardware;
            var j = await RunJob(media.PlainVideo($@"hw\plain-{codec}.mkv", 3), new Profile { Codec = codec, Quality = codec == VideoCodec.AV1 ? 30 : 22, SkipBelowKbps = 0 });
            settings.EncoderPolicy = EncoderPolicy.Software;
            T.Check($"{enc.Id} encode completes", j.Status == JobStatus.Completed && j.EncoderId == enc.Id, j.Message);
        }
    }
    else T.Info("No hardware HEVC encoder on this machine; skipped.");

    // Each GPU vendor's encoder builds its command differently; exercise every one this machine has.
    foreach (var (vendor, id) in new[] { (HardwareVendor.Amd, "hevc_amf"), (HardwareVendor.Intel, "hevc_qsv") })
    {
        if (!encoders.IsUsable(id)) { T.Info($"{id} is not usable here; not exercised"); continue; }
        settings.EncoderPolicy = EncoderPolicy.Hardware;
        settings.HardwareVendor = vendor;
        var j = await RunJob(media.AnimeMkv($@"hw\{id}\[Group] Show - 02 [ABCDEF12].mkv"), profiles.Find(BuiltInProfiles.Anime)!);
        settings.EncoderPolicy = EncoderPolicy.Software;
        settings.HardwareVendor = HardwareVendor.Auto;
        T.Check($"preferring {vendor} uses {id} and completes", j.Status == JobStatus.Completed && j.EncoderId == id, $"{j.Status} {j.EncoderId} {j.Message}");
        if (j.Status == JobStatus.Completed)
        {
            // What the encoder claims and what it writes must agree: no silent drop to 8-bit.
            bool claims = encoders.SupportsTenBit(EncoderCatalog.Find(id)!);
            var outVideo = media.Probe(j.OutputPath!).Video!;
            T.Check($"{id} output bit depth matches what was detected ({(claims ? "10" : "8")}-bit)", outVideo.CodecName == "hevc" && outVideo.BitDepth == (claims ? 10 : 8),
                $"{outVideo.BitDepth}-bit");
            if (!claims) T.Check("the drop to 8-bit is explained", j.Notes.Any(n => n.Contains("cannot produce 10-bit")), string.Join(" | ", j.Notes));
        }

        // HDR must never be handed to an encoder that would flatten it to 8-bit.
        var hdrFile = media.Load(media.HdrMkv($@"hw\{id}\hdr source.mkv"));
        var vendorSettings = new AppSettings { EncoderPolicy = EncoderPolicy.Hardware, HardwareVendor = vendor };
        var hdrPlan = CommandBuilder.Build(new EncodeRequest
        {
            File = hdrFile, Probe = hdrFile.Probe!, Profile = profiles.Find(BuiltInProfiles.Cinematic)!, Settings = vendorSettings, Encoders = encoders, OutputPath = "x.mkv",
        });
        T.Check($"HDR with {vendor} preferred still gets a 10-bit capable encoder", !hdrPlan.Skip && hdrPlan.Encoder is not null && encoders.SupportsTenBit(hdrPlan.Encoder),
            $"{hdrPlan.Encoder?.Id} {hdrPlan.SkipReason}");
        T.Info($"HDR with {vendor} preferred -> {hdrPlan.Encoder?.Id}");
    }

    // A hardware encoder that fails must fall back to software rather than fail the job.
    var broken = new EncoderAvailability { Status = new Dictionary<string, string?>(encoders.Status) { ["hevc_qsv"] = null }, TenBit = encoders.TenBit };
    foreach (var id in new[] { "hevc_nvenc", "hevc_amf" }) broken.Status[id] = "disabled for test";
    if (!encoders.IsUsable("hevc_qsv"))
    {
        var saved = encoders;
        encoders = broken;
        settings.EncoderPolicy = EncoderPolicy.Hardware;
        var job = await RunJob(media.PlainVideo(@"hw\fallback.mkv", 3), new Profile { Codec = VideoCodec.HEVC, Speed = "veryfast", SkipBelowKbps = 0 });
        settings.EncoderPolicy = EncoderPolicy.Software;
        encoders = saved;
        T.Check("falls back to software when the hardware encoder fails", job.Status == JobStatus.Completed && job.EncoderId == "libx265", $"{job.Status} {job.EncoderId} {job.Message}");
        T.Check("the fallback is explained", job.Notes.Any(n => n.Contains("retried with the software encoder")));
    }
}

// =====================================================================================
if (Run("Encode: HDR is preserved"))
{
    string src = media.HdrMkv(@"hdr\Unknown Film (2021).mkv");
    var p = media.Probe(src);
    T.Check("source is detected as HDR 10-bit", p.Video is { IsHdr: true, BitDepth: 10 });
    var (master, cll) = await Ffprobe.ReadHdrMetadataAsync(tools.Ffprobe!, src);
    T.Equal("mastering display metadata is read", "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)", master);
    T.Equal("content light level is read", "1000,400", cll);

    var profile = profiles.Find(BuiltInProfiles.Cinematic)!.Clone();
    profile.Speed = "veryfast"; profile.SkipBelowKbps = 0; profile.BitDepth = BitDepthMode.Force8Bit;
    var job = await RunJob(src, profile, new OutputOptions { MinSavingsPercent = -1000 });
    T.Equal("job completed", JobStatus.Completed, job.Status);
    if (job.Status == JobStatus.Completed)
    {
        var o = media.Probe(job.OutputPath!);
        T.Check("stays 10-bit even though the profile asked for 8-bit", o.Video is { BitDepth: 10 });
        T.Check("HDR colour tags kept", o.Video is { ColorTransfer: "smpte2084", ColorPrimaries: "bt2020", ColorSpace: "bt2020nc" },
            $"{o.Video?.ColorTransfer}/{o.Video?.ColorPrimaries}/{o.Video?.ColorSpace}");
        var (m2, c2) = await Ffprobe.ReadHdrMetadataAsync(tools.Ffprobe!, job.OutputPath!);
        T.Equal("mastering display metadata carried through", master, m2);
        T.Equal("content light level carried through", cll, c2);
    }

    var hdrFile = media.Load(src);
    var refused = CommandBuilder.Build(new EncodeRequest
    {
        File = hdrFile, Probe = hdrFile.Probe!, Profile = profiles.Find(BuiltInProfiles.Compatibility)!, Settings = settings, Encoders = encoders, OutputPath = "x.mp4",
    });
    T.Check("an H.264 profile refuses HDR instead of ruining it", refused.Skip && refused.SkipReason!.Contains("HDR"), refused.SkipReason);

    if (encoders.Resolve(new Profile { Codec = VideoCodec.HEVC }, new AppSettings { EncoderPolicy = EncoderPolicy.Hardware }) is { Hardware: true } hwHdr)
    {
        string src2 = media.HdrMkv(@"hdr-hw\Another Film (2022).mkv");
        settings.EncoderPolicy = EncoderPolicy.Hardware;
        var hwJob = await RunJob(src2, profile, new OutputOptions { MinSavingsPercent = -1000 });
        settings.EncoderPolicy = EncoderPolicy.Software;
        T.Check($"{hwHdr.Id} HDR encode completes", hwJob.Status == JobStatus.Completed && hwJob.EncoderId == hwHdr.Id, hwJob.Message);
        if (hwJob.Status == JobStatus.Completed)
        {
            var o = media.Probe(hwJob.OutputPath!);
            T.Check("hardware HDR output is 10-bit with HDR colour tags", o.Video is { BitDepth: 10, ColorTransfer: "smpte2084", ColorPrimaries: "bt2020" },
                $"{o.Video?.BitDepth} {o.Video?.ColorTransfer}/{o.Video?.ColorPrimaries}");
            var (m3, c3) = await Ffprobe.ReadHdrMetadataAsync(tools.Ffprobe!, hwJob.OutputPath!);
            T.Info($"hardware path mastering metadata: {m3 ?? "(not carried)"} / {c3 ?? "(not carried)"}");
        }
    }
}

// =====================================================================================
if (Run("Skip rules"))
{
    // A tiny, already-efficient HEVC file: testsrc at high CRF is a few hundred kbps.
    string small = media.PlainVideo(@"skip\already-small.mkv", 4, "1280x720", "24", "libx265", "38", "-x265-params log-level=error");
    var p = media.Probe(small);
    T.Info($"source: {p.Video!.CodecName} at {Format.Bitrate(p.EstimateVideoBitRate())}");
    var job = await RunJob(small, profiles.Find(BuiltInProfiles.General)!);
    T.Check("an efficient low-bitrate file is left alone", job.Status == JobStatus.Skipped && job.Message.StartsWith("Already small"), job.Message);

    var noFloor = profiles.Find(BuiltInProfiles.General)!.Clone();
    noFloor.SkipBelowKbps = 0; noFloor.Speed = "veryfast"; noFloor.Quality = 20;
    var job2 = await RunJob(small, noFloor);
    T.Check("a result that is not smaller is discarded and the original kept",
        job2.Status == JobStatus.Skipped && job2.Message.StartsWith("Original kept") && File.Exists(small), job2.Message);
    T.Check("nothing is left beside the original", Directory.GetFiles(Path.GetDirectoryName(small)!).Length == 1,
        string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(small)!).Select(Path.GetFileName)));

    var dv = media.Load(small);
    dv.Probe!.Video!.HasDolbyVision = true; dv.Probe.Video.DolbyVisionProfile = 8;
    var plan = CommandBuilder.Build(new EncodeRequest { File = dv, Probe = dv.Probe, Profile = noFloor, Settings = settings, Encoders = encoders, OutputPath = "x.mkv" });
    T.Check("Dolby Vision is skipped by default", plan.Skip && plan.SkipReason!.Contains("Dolby Vision"));

    var wrongKind = CommandBuilder.Build(new EncodeRequest { File = dv, Probe = dv.Probe, Profile = profiles.Find(BuiltInProfiles.Music)!, Settings = settings, Encoders = encoders, OutputPath = "x.m4a" });
    T.Check("an audio profile is refused for a video file", wrongKind.Skip);
}

// =====================================================================================
if (Run("Replace mode"))
{
    string backup = media.Path("replace-backup", "x")[..^2];
    string src = media.PlainVideo(@"replace\TV\Show\Show.S01E01.avi", 4, "1280x720", "30", "mpeg4", "1", "-q:v 2");
    string sidecar = Path.ChangeExtension(src, ".en.srt");
    File.Copy(media.Srt(), sidecar);
    long originalSize = new FileInfo(src).Length;
    var profile = profiles.Find(BuiltInProfiles.General)!.Clone();
    profile.Speed = "veryfast"; profile.SkipBelowKbps = 0;

    var file = media.Load(src);
    file.Root = media.Path("replace", "x")[..^2];
    var job = QueueManager.CreateJob(file, profile, new OutputOptions { Mode = OutputMode.Replace, Disposal = OriginalDisposal.BackupFolder, BackupFolder = backup });
    await new JobProcessor(services).RunAsync(job, new FfmpegRunner(), _ => { }, CancellationToken.None);
    T.Info($"{job.Status}: {job.Message}");
    T.Equal("job completed", JobStatus.Completed, job.Status);
    string expected = Path.ChangeExtension(src, ".mkv");
    T.Check("the compressed file takes the original's name with the new extension", job.OutputPath == expected && File.Exists(expected), job.OutputPath);
    T.Check("the original is gone from its folder", !File.Exists(src));
    string backedUp = Path.Combine(backup, "replace", "TV", "Show", "Show.S01E01.avi");
    T.Check("the original was moved to the backup folder, mirroring its path", File.Exists(backedUp) && new FileInfo(backedUp).Length == originalSize, backedUp);
    T.Check("the existing sidecar subtitle still matches the new file name", File.Exists(sidecar));

    // Same extension: the output must end up at exactly the original path.
    string mkv = media.PlainVideo(@"replace\TV\Show\Show.S01E02.mkv", 4);
    var job2 = QueueManager.CreateJob(media.Load(mkv), profile, new OutputOptions { Mode = OutputMode.Replace, Disposal = OriginalDisposal.DeletePermanently });
    long before = new FileInfo(mkv).Length;
    await new JobProcessor(services).RunAsync(job2, new FfmpegRunner(), _ => { }, CancellationToken.None);
    T.Check("in-place replace keeps the same path and shrinks the file", job2.Status == JobStatus.Completed && job2.OutputPath == mkv && new FileInfo(mkv).Length < before, job2.Message);
    T.Check("the replaced file is recognised as compressed", media.Probe(mkv).CompressorTag is not null);

    // An unrelated file already has the target name: nothing may be overwritten.
    string avi = media.PlainVideo(@"replace\clash\Movie.avi", 3, "1280x720", "30", "mpeg4", "1", "-q:v 2");
    string unrelated = Path.ChangeExtension(avi, ".mkv");
    File.WriteAllText(unrelated, "an unrelated file that must survive");
    var job3 = QueueManager.CreateJob(media.Load(avi), profile, new OutputOptions { Mode = OutputMode.Replace, Disposal = OriginalDisposal.DeletePermanently });
    await new JobProcessor(services).RunAsync(job3, new FfmpegRunner(), _ => { }, CancellationToken.None);
    T.Check("an unrelated file with the target name is never overwritten",
        job3.Status == JobStatus.Completed && File.ReadAllText(unrelated).StartsWith("an unrelated") && job3.OutputPath!.EndsWith("Movie (2).mkv"), job3.OutputPath);

    // The original is locked: the work is kept beside it rather than lost.
    string locked = media.PlainVideo(@"replace\locked\Clip.mkv", 3);
    var job4 = QueueManager.CreateJob(media.Load(locked), profile, new OutputOptions { Mode = OutputMode.Replace, Disposal = OriginalDisposal.DeletePermanently });
    using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        await new JobProcessor(services).RunAsync(job4, new FfmpegRunner(), _ => { }, CancellationToken.None);
    T.Check("when the original cannot be removed, both files are kept",
        job4.Status == JobStatus.Completed && File.Exists(locked) && job4.OutputPath!.EndsWith("Clip.compressed.mkv") && File.Exists(job4.OutputPath), job4.OutputPath);

    if (recycle)
    {
        string bin = media.PlainVideo(@"replace\recycle\autocompressor-recycle-test.mkv", 3);
        var job5 = QueueManager.CreateJob(media.Load(bin), profile, new OutputOptions { Mode = OutputMode.Replace, Disposal = OriginalDisposal.RecycleBin });
        long binBefore = new FileInfo(bin).Length;
        await new JobProcessor(services).RunAsync(job5, new FfmpegRunner(), _ => { }, CancellationToken.None);
        T.Check("Recycle Bin disposal completes and the new file is in place", job5.Status == JobStatus.Completed && File.Exists(bin) && new FileInfo(bin).Length < binBefore, job5.Message);
    }
    else T.Info("Recycle Bin disposal not exercised (pass --recycle).");
}

// =====================================================================================
if (Run("Output folder mode"))
{
    string outDir = media.Path("outfolder-target", "x")[..^2];
    string src = media.PlainVideo(@"outfolder\Library\Show\Season 1\Show.S01E01.mkv", 3);
    File.Copy(media.Srt(), Path.ChangeExtension(src, ".en.srt"));
    var profile = profiles.Find(BuiltInProfiles.General)!.Clone();
    profile.Speed = "veryfast"; profile.SkipBelowKbps = 0;
    var file = media.Load(src);
    file.Root = media.Path("outfolder", "Library", "x")[..^2];
    var job = QueueManager.CreateJob(file, profile, new OutputOptions { Mode = OutputMode.OutputFolder, OutputFolder = outDir });
    await new JobProcessor(services).RunAsync(job, new FfmpegRunner(), _ => { }, CancellationToken.None);
    string expected = Path.Combine(outDir, "Library", "Show", "Season 1", "Show.S01E01.mkv");
    T.Check("output mirrors the scanned folder structure", job.Status == JobStatus.Completed && job.OutputPath == expected && File.Exists(src), $"{job.Status} {job.OutputPath} {job.Message}");
    T.Check("sidecar subtitles are copied along", File.Exists(Path.ChangeExtension(expected, ".en.srt")));
}

// =====================================================================================
if (Run("Audio files"))
{
    string wav = media.Wav(@"audio\Music\Artist\Album\01 - Track.wav");
    var music = profiles.Find(BuiltInProfiles.Music)!;
    var job = await RunJob(wav, music);
    T.Check("WAV becomes FLAC under the default music profile (still lossless)",
        job.Status == JobStatus.Completed && job.OutputPath!.EndsWith(".flac") && media.Probe(job.OutputPath).AudioStreams.First().CodecName == "flac", job.Message);

    string mp3 = media.Mp3WithCover(@"audio\Music\Artist\Album\02 - Track.mp3");
    var job2 = await RunJob(mp3, music);
    T.Equal("320k MP3 is compressed", JobStatus.Completed, job2.Status);
    if (job2.Status == JobStatus.Completed)
    {
        var o = media.Probe(job2.OutputPath!);
        T.Check("AAC in .m4a", job2.OutputPath!.EndsWith(".m4a") && o.AudioStreams.First().CodecName == "aac");
        T.Check("cover art kept", o.CoverArt is not null);
        T.Check("tags kept", o.Tag("title") == "Test Song" && o.Tag("artist") == "Tester" && o.Tag("album") == "Test Album");
        T.Check("marked as compressed", o.CompressorTag is not null);
    }

    string lowMp3 = media.Mp3WithCover(@"audio\Music\Artist\Album\03 - Low.mp3", 5, "128k");
    var job3 = await RunJob(lowMp3, music);
    T.Check("a 128k MP3 is left alone", job3.Status == JobStatus.Skipped && job3.Message.StartsWith("Already small"), job3.Message);

    string speechWav = media.Wav(@"audio\Podcasts\Show\Episode 12.wav", 6);
    var job4 = await RunJob(speechWav, profiles.Find(BuiltInProfiles.Speech)!);
    T.Equal("speech job completed", JobStatus.Completed, job4.Status);
    if (job4.Status == JobStatus.Completed)
    {
        var o = media.Probe(job4.OutputPath!);
        T.Check("speech becomes mono Opus", o.AudioStreams.First() is { CodecName: "opus", Channels: 1 } && job4.OutputPath!.EndsWith(".opus"));
        T.Check("and is far smaller", job4.OutputSize < job4.SourceSize / 10, $"{job4.SourceSize} -> {job4.OutputSize}");
    }

    var opusJob = await RunJob(media.Mp3WithCover(@"audio\Music\Other\song.mp3"), profiles.Find(BuiltInProfiles.MusicOpus)!);
    T.Check("Opus music profile works and notes the lost cover art", opusJob.Status == JobStatus.Completed && opusJob.Notes.Any(n => n.Contains("Cover art")), opusJob.Message);
}

// =====================================================================================
if (Run("Queue"))
{
    var qSettings = settings;
    var queue = new QueueManager(services);
    var profile = profiles.Find(BuiltInProfiles.General)!.Clone();
    profile.Speed = "veryfast"; profile.SkipBelowKbps = 0;
    var files = Enumerable.Range(1, 3).Select(i => media.Load(media.PlainVideo($@"queue\clip{i}.mkv", 3))).ToList();
    int added = queue.Add(files.Select(f => QueueManager.CreateJob(f, profile, new OutputOptions())));
    T.Equal("three jobs added", 3, added);
    T.Equal("the same files are not queued twice", 0, queue.Add(files.Select(f => QueueManager.CreateJob(f, profile, new OutputOptions()))));

    var order = queue.Jobs.Select(j => j.Id).ToList();
    queue.Move([order[2]], -1);
    T.Check("jobs can be reordered", queue.Jobs[1].Id == order[2]);
    queue.Move([order[2]], +1);

    var finished = new List<string>();
    bool sawProgress = false;
    queue.JobFinished += j => { lock (finished) finished.Add(j.Id); };
    queue.JobChanged += j => { if (j.Progress is > 0 and < 1) sawProgress = true; };
    var idle = new TaskCompletionSource();
    queue.StateChanged += () => { if (queue.State == QueueState.Idle) idle.TrySetResult(); };
    queue.Start();
    await Task.WhenAny(idle.Task, Task.Delay(TimeSpan.FromMinutes(3)));
    T.Check("the queue runs to completion and returns to idle", queue.State == QueueState.Idle && queue.Jobs.All(j => j.Status == JobStatus.Completed),
        string.Join(", ", queue.Jobs.Select(j => $"{j.Status}:{j.Message}")));
    T.Check("jobs finish in queue order", finished.SequenceEqual(order), string.Join(",", finished));
    T.Check("progress is reported while encoding", sawProgress);
    T.Check("each job has a log file", queue.Jobs.All(j => j.LogPath is not null && File.Exists(j.LogPath)));

    var restored = new QueueManager(services);
    T.Check("the queue is restored from disk", restored.Jobs.Count == 3 && restored.Jobs.All(j => j.Status == JobStatus.Completed && j.Profile.Name == profile.Name));
    restored.ClearFinished();
    T.Equal("finished jobs can be cleared", 0, restored.Jobs.Count);

    // Pause, resume and stop on a longer encode.
    var slow = profile.Clone();
    slow.Speed = "veryslow";
    var longFile = media.Load(media.PlainVideo(@"queue\long.mkv", 40, "1920x1080"));
    var q2 = new QueueManager(services);
    q2.Add([QueueManager.CreateJob(longFile, slow, new OutputOptions())]);
    var job = q2.Jobs[0];
    q2.Start();
    await WaitFor(() => job.Progress > 0.02, 60);
    q2.Pause();
    await Task.Delay(1500);
    double atPause = job.Progress;
    await Task.Delay(2500);
    T.Check("pausing freezes the encoder", q2.State == QueueState.Paused && Math.Abs(job.Progress - atPause) < 0.0001, $"{atPause} -> {job.Progress}");
    q2.Resume();
    await WaitFor(() => job.Progress > atPause + 0.01, 60);
    T.Check("resuming continues from where it stopped", job.Progress > atPause);
    string tempFile = Path.Combine(Path.GetDirectoryName(longFile.Path)!, "long.ac-tmp.mkv");
    T.Check("the encode writes to a temp file", File.Exists(tempFile));
    q2.Stop();
    await WaitFor(() => q2.State == QueueState.Idle, 30);
    T.Check("stopping returns the job to the queue", job.Status == JobStatus.Queued && q2.State == QueueState.Idle, $"{job.Status} {q2.State}");
    T.Check("stopping removes the half-written file and keeps the original", !File.Exists(tempFile) && File.Exists(longFile.Path));

    // Removing a running job cancels just that job.
    q2.Start();
    await WaitFor(() => job.Progress > 0.01, 60);
    q2.Remove([job.Id]);
    await WaitFor(() => q2.State == QueueState.Idle, 30);
    T.Check("removing a running job cancels and drops it", q2.Jobs.Count == 0 && !File.Exists(tempFile) && File.Exists(longFile.Path));

    // Outside the schedule window nothing starts.
    settings.ScheduleEnabled = true;
    settings.ScheduleStart = DateTime.Now.AddHours(2).ToString("HH:mm");
    settings.ScheduleEnd = DateTime.Now.AddHours(3).ToString("HH:mm");
    var q3 = new QueueManager(services);
    q3.ClearFinished();
    q3.Add([QueueManager.CreateJob(files[0], profile, new OutputOptions { Suffix = ".again" })]);
    q3.Start();
    await Task.Delay(2500);
    T.Check("outside the schedule window jobs wait", q3.State == QueueState.WaitingForSchedule && q3.Jobs[0].Status == JobStatus.Queued, $"{q3.State} {q3.Jobs[0].Status}");
    q3.Stop();
    await WaitFor(() => q3.State == QueueState.Idle, 10);
    settings.ScheduleEnabled = false;

    // Two jobs at once.
    settings.ParallelJobs = 2;
    var q4 = new QueueManager(services);
    q4.ClearFinished();
    q4.Remove(q4.Jobs.Select(j => j.Id));
    var pair = Enumerable.Range(1, 2).Select(i => media.Load(media.PlainVideo($@"queue\par{i}.mkv", 12, "1920x1080"))).ToList();
    q4.Add(pair.Select(f => QueueManager.CreateJob(f, slow, new OutputOptions())));
    int maxRunning = 0;
    q4.JobChanged += _ => maxRunning = Math.Max(maxRunning, q4.Jobs.Count(j => j.Status == JobStatus.Running));
    q4.Start();
    await WaitFor(() => maxRunning >= 2, 60);
    T.Check("two jobs run side by side when allowed", maxRunning == 2, maxRunning.ToString());
    q4.Stop();
    await WaitFor(() => q4.State == QueueState.Idle, 30);
    settings.ParallelJobs = 1;
}

// =====================================================================================
if (Run("Subtitle handling (offline)"))
{
    string src = media.PlainVideo(@"subs\Seinfeld\Season 4\Seinfeld.S04E11.The.Contest.720p.WEB-DL.mkv", 4);
    var file = media.Load(src);
    file.Parsed = FileNameParser.Parse(src);
    var options = new SubtitleFetchOptions { Enabled = true, Languages = "en, fr" };
    T.Check("a file with no subtitles is missing every wanted language", SubtitleFetcher.MissingLanguages(file, file.Probe!, options).SequenceEqual(["en", "fr"]));
    File.Copy(media.Srt(), Path.ChangeExtension(src, ".en.srt"));
    T.Check("a sidecar file counts as present", SubtitleFetcher.MissingLanguages(file, file.Probe!, options).SequenceEqual(["fr"]));
    File.Delete(Path.ChangeExtension(src, ".en.srt"));

    var withSubs = media.Load(media.AnimeMkv(@"subs\with-subs.mkv"));
    T.Check("embedded subtitles count as present", SubtitleFetcher.MissingLanguages(withSubs, withSubs.Probe!, options).SequenceEqual(["fr"]));

    var exact = new SubtitleCandidate("x", "1", "en", "Some.Other.Release", 5, true, false);
    var popular = new SubtitleCandidate("x", "2", "en", "720p WEB-DL", 90000, false, false);
    var mismatch = new SubtitleCandidate("x", "3", "en", "DVDRip", 500, false, false);
    T.Check("an exact file-hash match always wins", SubtitleFetcher.Score(exact, file.Name) > SubtitleFetcher.Score(popular, file.Name));
    T.Check("a matching release beats a mismatched one", SubtitleFetcher.Score(popular, file.Name) > SubtitleFetcher.Score(mismatch, file.Name));

    T.Check("valid SubRip is accepted", SubtitleFetcher.DecodeSubRip(File.ReadAllBytes(media.Srt())) is not null);
    T.Check("an HTML error page is rejected", SubtitleFetcher.DecodeSubRip("<html><body>Too many requests</body></html>"u8.ToArray()) is null);
    byte[] latin = [.. "1\r\n00:00:01,000 --> 00:00:02,000\r\nCaf"u8, 0xE9, .. "\r\n"u8];
    T.Check("Windows-1252 text is converted", SubtitleFetcher.DecodeSubRip(latin)?.Contains("Café") == true);

    string hashFile = media.Path("subs", "hash.bin");
    var bytes = new byte[200_000];
    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
    File.WriteAllBytes(hashFile, bytes);
    ulong expected = (ulong)bytes.Length;
    for (int i = 0; i < 65536; i += 8) expected += BitConverter.ToUInt64(bytes, i);
    for (int i = bytes.Length - 65536; i < bytes.Length; i += 8) expected += BitConverter.ToUInt64(bytes, i);
    T.Equal("file hash matches the published algorithm", expected.ToString("x16"), MovieHash.Compute(hashFile));

    // A downloaded subtitle is embedded as an extra, non-default track with its language set.
    string extra = media.Path("subs", "downloaded.fr.srt");
    File.Copy(media.Srt(), extra, true);
    var profile = profiles.Find(BuiltInProfiles.General)!.Clone();
    profile.SkipBelowKbps = 0; profile.Speed = "veryfast";
    string outPath = media.Path("subs", "embedded.mkv");
    var plan = CommandBuilder.Build(new EncodeRequest
    {
        File = withSubs, Probe = withSubs.Probe!, Profile = profile, Settings = settings, Encoders = encoders, OutputPath = outPath,
        ExtraSubtitles = [new ExternalSubtitle(extra, "fr")],
    });
    var run = await new FfmpegRunner().RunAsync(tools.Ffmpeg!, plan.Args, 4, null, null, false, CancellationToken.None);
    T.Check("encode with an added subtitle succeeds", run.Success, run.ErrorSummary);
    if (run.Success)
    {
        var o = media.Probe(outPath);
        T.Check("the downloaded track is appended after the originals with its language",
            o.SubtitleStreams.Count() == 3 && o.SubtitleStreams.Last() is { CodecName: "subrip", Language: "fre", IsDefault: false },
            string.Join(",", o.SubtitleStreams.Select(s => $"{s.CodecName}/{s.Language}/{s.IsDefault}")));
    }
}

// =====================================================================================
if (online && Run("Online services (live)"))
{
    using var lookup = new OnlineLookup(new MetadataCache());
    var seinfeld = await lookup.LookupAsync(new ParsedName { Title = "Seinfeld", Season = 4, Episode = 11 }, false, null);
    T.Check("TVMaze knows Seinfeld as a sitcom", seinfeld?.ToContentType() == ContentType.Sitcom, $"{seinfeld?.Kind} {string.Join(",", seinfeld?.Genres ?? [])}");
    var titan = await lookup.LookupAsync(new ParsedName { Title = "Attack on Titan", Season = 1, Episode = 1 }, false, null);
    T.Check("TVMaze knows Attack on Titan as anime", titan?.ToContentType() == ContentType.Anime, $"{titan?.Kind} {titan?.Language}");
    var nothing = await lookup.LookupAsync(new ParsedName { Title = "zzzzqqqq not a show 12345", Season = 1, Episode = 1 }, false, null);
    T.Check("an unknown title returns nothing rather than a wrong match", nothing is null);
    T.Check("films are not looked up without a TMDB key", await lookup.LookupAsync(new ParsedName { Title = "Inception", Year = 2010 }, true, null) is null);

    string src = media.PlainVideo(@"online\Seinfeld\Season 4\Seinfeld.S04E11.The.Contest.720p.WEB-DL.mkv", 4);
    settings.SubtitleFetch = new SubtitleFetchOptions { Enabled = true, Languages = "en", UseGestdown = true };
    var profile = profiles.Find(BuiltInProfiles.Sitcom)!.Clone();
    profile.SkipBelowKbps = 0; profile.Speed = "veryfast";
    var job = await RunJob(src, profile);
    settings.SubtitleFetch = new SubtitleFetchOptions();
    T.Equal("job completed", JobStatus.Completed, job.Status);
    if (job.Status == JobStatus.Completed)
    {
        var o = media.Probe(job.OutputPath!);
        T.Check("subtitles found on Gestdown were embedded as an English SRT track",
            o.SubtitleStreams.Count() == 1 && o.SubtitleStreams.First() is { CodecName: "subrip", Language: "eng" },
            $"{o.SubtitleStreams.Count()} tracks; notes: {string.Join(" | ", job.Notes)}");
    }

    using var noKey = new SubtitleFetcher(new SubtitleFetchOptions { UseGestdown = false, UseOpenSubtitles = true, OpenSubtitlesApiKey = "" });
    T.Equal("OpenSubtitles is not used without an API key", 0, noKey.Providers.Count);
    using var http = new HttpClient();
    var badKey = new OpenSubtitlesProvider(http, "invalid-key-for-test", "", "");
    try
    {
        await badKey.SearchAsync(new SubtitleQuery(src, "Seinfeld", null, 4, 11, "en", Path.GetFileName(src)), CancellationToken.None);
        T.Check("OpenSubtitles rejects a bad API key with a clear message", false, "no error was raised");
    }
    catch (SubtitleProviderException ex) { T.Check("OpenSubtitles rejects a bad API key with a clear message", ex.Message.Contains("API key"), ex.Message); }
}

return Finish();

// -------------------------------------------------------------------------------------
static async Task WaitFor(Func<bool> condition, int seconds)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
}

int Finish()
{
    Console.WriteLine();
    Console.WriteLine($"{T.Passed} passed, {T.Failed} failed");
    foreach (var failure in T.Failures) Console.WriteLine("  FAILED  " + failure);
    if (!keep)
    {
        try { Directory.Delete(work, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("Could not clean up " + work); }
    }
    else Console.WriteLine("Kept " + work);
    return T.Failed == 0 ? 0 : 1;
}
