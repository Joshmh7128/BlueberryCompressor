namespace AutoCompressor.Core.Models;

public enum OutputMode
{
    /// <summary>Write "name.suffix.ext" beside the original and leave the original alone.</summary>
    NextToOriginal,
    /// <summary>Put the compressed file where the original was.</summary>
    Replace,
    /// <summary>Write into a separate folder, mirroring the scanned folder structure.</summary>
    OutputFolder,
}

public enum OriginalDisposal { RecycleBin, BackupFolder, DeletePermanently }

/// <summary>What "Auto" means for profiles that do not pin an encoder kind.</summary>
public enum EncoderPolicy { Software, Hardware }

public enum HardwareVendor { Auto, Nvidia, Amd, Intel }

public enum SubtitleDelivery { Embed, Sidecar }

public sealed class OutputOptions
{
    public OutputMode Mode { get; set; } = OutputMode.NextToOriginal;
    public string Suffix { get; set; } = ".compressed";
    public string OutputFolder { get; set; } = "";
    public OriginalDisposal Disposal { get; set; } = OriginalDisposal.RecycleBin;
    public string BackupFolder { get; set; } = "";
    /// <summary>Keep the original when the result is not at least this much smaller.</summary>
    public int MinSavingsPercent { get; set; } = 10;
    public bool PreserveTimestamps { get; set; } = true;

    public OutputOptions Clone() => (OutputOptions)MemberwiseClone();
}

public sealed class SubtitleFetchOptions
{
    public bool Enabled { get; set; }
    /// <summary>Comma-separated two-letter codes, most wanted first.</summary>
    public string Languages { get; set; } = "en";
    public SubtitleDelivery Delivery { get; set; } = SubtitleDelivery.Embed;
    public bool UseGestdown { get; set; } = true;
    public bool UseOpenSubtitles { get; set; }
    public string OpenSubtitlesApiKey { get; set; } = "";
    public string OpenSubtitlesUser { get; set; } = "";
    /// <summary>DPAPI-protected, base64.</summary>
    public string OpenSubtitlesPasswordProtected { get; set; } = "";

    public SubtitleFetchOptions Clone() => (SubtitleFetchOptions)MemberwiseClone();
}

public sealed class AppSettings
{
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";

    public OutputOptions Output { get; set; } = new();

    public EncoderPolicy EncoderPolicy { get; set; } = EncoderPolicy.Software;
    public HardwareVendor HardwareVendor { get; set; } = HardwareVendor.Auto;
    public bool FallbackToSoftware { get; set; } = true;
    public bool HardwareDecode { get; set; }
    public int ParallelJobs { get; set; } = 1;
    public bool LowPriority { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool SkipDolbyVision { get; set; } = true;
    public bool ReprocessCompressed { get; set; }

    /// <summary>Only start new jobs inside this daily window. Running jobs are allowed to finish.</summary>
    public bool ScheduleEnabled { get; set; }
    public string ScheduleStart { get; set; } = "23:00";
    public string ScheduleEnd { get; set; } = "07:00";

    public int ProbeParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Look show titles up on TVMaze (and TMDB when a key is set) to improve recognition.</summary>
    public bool OnlineLookup { get; set; }
    public string TmdbApiKey { get; set; } = "";

    public SubtitleFetchOptions SubtitleFetch { get; set; } = new();

    /// <summary>Which profile each content type gets. Missing entries fall back to the built-in mapping.</summary>
    public Dictionary<ContentType, string> ProfileMap { get; set; } = new();

    public bool WithinSchedule(DateTime now)
    {
        if (!ScheduleEnabled) return true;
        if (!TimeSpan.TryParse(ScheduleStart, out var start) || !TimeSpan.TryParse(ScheduleEnd, out var end)) return true;
        var t = now.TimeOfDay;
        if (start == end) return true;
        return start < end ? t >= start && t < end : t >= start || t < end;
    }
}
