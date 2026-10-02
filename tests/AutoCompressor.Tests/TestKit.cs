using AutoCompressor.Core.Models;
using AutoCompressor.Core.Probing;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Tests;

/// <summary>A tiny test runner: no packages, just named checks and an exit code.</summary>
public static class T
{
    public static int Passed, Failed;
    public static readonly List<string> Failures = [];
    private static string _section = "";

    public static void Section(string name)
    {
        _section = name;
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }

    public static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine("  ok    " + name);
        }
        else
        {
            Failed++;
            Failures.Add($"{_section}: {name}{(detail is null ? "" : " -> " + detail)}");
            Console.WriteLine("  FAIL  " + name + (detail is null ? "" : "  -> " + detail));
        }
    }

    public static void Equal<TValue>(string name, TValue expected, TValue actual) =>
        Check(name, EqualityComparer<TValue>.Default.Equals(expected, actual), $"expected [{expected}] but got [{actual}]");

    public static void Info(string text) => Console.WriteLine("        " + text);
}

/// <summary>Generates small synthetic media files with ffmpeg so tests never depend on real content.</summary>
public sealed class MediaFactory(Tools tools, string dir)
{
    public string Dir => dir;

    private void Ffmpeg(params string[] args)
    {
        string[] all = ["-hide_banner", "-loglevel", "error", "-y", "-nostdin", .. args];
        var result = ProcessRunner.Capture(tools.Ffmpeg!, all, TimeSpan.FromMinutes(3));
        if (result.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + result.StdErr);
    }

    public string Path(params string[] parts)
    {
        string path = System.IO.Path.Combine([dir, .. parts]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return path;
    }

    public string Srt()
    {
        string path = Path("_assets", "subs.srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nHello there.\n\n2\n00:00:02,500 --> 00:00:03,500\n<i>Second line.</i>\n");
        return path;
    }

    public string Ass()
    {
        string path = Path("_assets", "subs.ass");
        File.WriteAllText(path,
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 1280\nPlayResY: 720\n\n[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n\n[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\pos(640,600)}Styled line\n" +
            "Dialogue: 0,0:00:02.50,0:00:03.50,Default,,0,0,0,,{\\i1}Another{\\i0} line\n");
        return path;
    }

    private string Chapters()
    {
        string path = Path("_assets", "chapters.txt");
        File.WriteAllText(path, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=Intro\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=4000\ntitle=Part A\n");
        return path;
    }

    /// <summary>
    /// An anime-style MKV: 720p H.264, Japanese stereo AAC plus English 5.1(side) AC-3, styled ASS and
    /// forced SRT subtitles, an embedded font and chapters. High bitrate so there is something to save.
    /// </summary>
    public string AnimeMkv(string relative, int seconds = 4)
    {
        string path = Path(relative);
        Ffmpeg("-f", "lavfi", "-i", $"testsrc2=s=1280x720:r=24000/1001:d={seconds}", "-f", "lavfi", "-i", $"sine=f=440:d={seconds}",
            "-f", "lavfi", "-i", $"anoisesrc=d={seconds}:c=pink:a=0.1", "-i", Ass(), "-i", Srt(), "-i", Chapters(),
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s", "-map_chapters", "5",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "10", "-c:a:0", "aac", "-b:a:0", "192k", "-ac:a:0", "2",
            "-c:a:1", "ac3", "-b:a:1", "448k", "-ac:a:1", "6", "-c:s:0", "ass", "-c:s:1", "srt",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=eng",
            "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "title=Full Subs", "-metadata:s:s:1", "language=eng", "-disposition:s:1", "forced",
            "-metadata:s:v:0", "BPS=99999999",
            "-attach", @"C:\Windows\Fonts\arial.ttf", "-metadata:s:t", "mimetype=application/x-truetype-font", "-metadata:s:t", "filename=arial.ttf",
            path);
        return path;
    }

    /// <summary>A sitcom-style MP4: 1080p H.264, stereo AAC and MP4 timed-text subtitles.</summary>
    public string SitcomMp4(string relative, int seconds = 4, string rate = "30000/1001")
    {
        string path = Path(relative);
        Ffmpeg("-f", "lavfi", "-i", $"testsrc2=s=1920x1080:r={rate}:d={seconds}", "-f", "lavfi", "-i", $"sine=f=300:d={seconds}", "-i", Srt(),
            "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "10",
            "-c:a", "aac", "-b:a", "256k", "-ac", "2", "-c:s", "mov_text", "-metadata:s:s:0", "language=eng", path);
        return path;
    }

    /// <summary>Plain video with one audio track and nothing else.</summary>
    public string PlainVideo(string relative, int seconds = 4, string size = "1280x720", string rate = "30", string codec = "libx264", string crf = "10", string extra = "")
    {
        string path = Path(relative);
        var args = new List<string>
        {
            "-f", "lavfi", "-i", $"testsrc2=s={size}:r={rate}:d={seconds}", "-f", "lavfi", "-i", $"sine=f=300:d={seconds}",
            "-c:v", codec, "-preset", "ultrafast", "-crf", crf, "-c:a", "aac", "-b:a", "128k", "-ac", "2",
        };
        if (extra.Length > 0) args.AddRange(extra.Split(' '));
        args.Add(path);
        Ffmpeg([.. args]);
        return path;
    }

    /// <summary>A 10-bit HEVC clip tagged as HDR10 with mastering-display metadata.</summary>
    public string HdrMkv(string relative, int seconds = 3)
    {
        string path = Path(relative);
        Ffmpeg("-f", "lavfi", "-i", $"testsrc2=s=1280x720:r=24:d={seconds}", "-f", "lavfi", "-i", $"sine=f=300:d={seconds}",
            "-c:v", "libx265", "-preset", "ultrafast", "-crf", "12", "-pix_fmt", "yuv420p10le",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
            "-x265-params", "log-level=error:hdr-opt=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:" +
                            "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400",
            "-c:a", "aac", "-b:a", "128k", path);
        return path;
    }

    public string Wav(string relative, int seconds = 5, int channels = 2)
    {
        string path = Path(relative);
        Ffmpeg("-f", "lavfi", "-i", $"anoisesrc=d={seconds}:c=pink:a=0.3", "-ac", channels.ToString(), "-ar", "44100", path);
        return path;
    }

    public string Mp3WithCover(string relative, int seconds = 5, string bitrate = "320k")
    {
        string cover = Path("_assets", "cover.jpg");
        Ffmpeg("-f", "lavfi", "-i", "color=c=red:s=300x300:d=1", "-frames:v", "1", cover);
        string path = Path(relative);
        Ffmpeg("-f", "lavfi", "-i", $"anoisesrc=d={seconds}:c=pink:a=0.3", "-i", cover, "-map", "0:a", "-map", "1:v",
            "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", bitrate, "-c:v", "copy", "-disposition:v", "attached_pic",
            "-metadata", "title=Test Song", "-metadata", "artist=Tester", "-metadata", "album=Test Album", "-metadata", "track=1", path);
        return path;
    }

    public MediaFile Load(string path, MediaKind? kind = null)
    {
        var info = new FileInfo(path);
        var file = new MediaFile
        {
            Path = path, Size = info.Length, ModifiedUtc = info.LastWriteTimeUtc,
            Kind = kind ?? AutoCompressor.Core.Scanning.MediaExtensions.KindOf(path) ?? MediaKind.Video,
            Root = System.IO.Path.GetDirectoryName(path),
        };
        file.Probe = Ffprobe.ProbeAsync(tools.Ffprobe!, path).GetAwaiter().GetResult();
        return file;
    }

    public ProbeResult Probe(string path) => Ffprobe.ProbeAsync(tools.Ffprobe!, path).GetAwaiter().GetResult();
}
