using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;
using AutoCompressor.Desktop.Mvvm;

namespace AutoCompressor.Desktop.ViewModels;

public sealed record EncoderLine(string Name, string Status, bool Usable);

/// <summary>Settings are saved the moment they change; there is no Apply button.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Dispatcher _ui;
    private AppSettings S => _s.Settings;
    private string _detectStatus = "";
    private bool _detecting;

    public ICommand RedetectCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand AutoLocateCommand { get; }

    public SettingsViewModel(AppServices services)
    {
        _s = services;
        _ui = Dispatcher.CurrentDispatcher;
        RedetectCommand = new RelayCommand(async () => await DetectAsync(force: true), () => !_detecting && _s.Tools.Available);
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { AppPaths.DataDir } }); }
            catch (System.ComponentModel.Win32Exception) { }
        });
        AutoLocateCommand = new RelayCommand(() => { FfmpegPath = ""; });
        _s.ConfigurationChanged += () => _ui.InvokeAsync(() =>
        {
            Raise(nameof(OutputMode));
            Raise(nameof(EncoderPolicy));
            Raise(nameof(IsReplace));
            Raise(nameof(IsOutputFolder));
            Raise(nameof(Encoders));
            Raise(nameof(HardwareSummary));
        });
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        _s.SaveSettings();
        Raise(name);
    }

    // ---- tools ----
    public string FfmpegPath
    {
        get => S.FfmpegPath;
        set
        {
            S.FfmpegPath = value?.Trim() ?? "";
            S.FfprobePath = "";
            _s.RelocateTools();
            Changed();
            Raise(nameof(ToolsStatus));
            Raise(nameof(ToolsFound));
            _ = DetectAsync(force: true);
        }
    }
    public bool ToolsFound => _s.Tools.Available;
    public string ToolsStatus => _s.Tools.Available
        ? $"Using ffmpeg {_s.Tools.Version}\n{_s.Tools.Ffmpeg}"
        : "ffmpeg and ffprobe were not found. Install ffmpeg (for example with: winget install Gyan.FFmpeg) or choose ffmpeg.exe here.";

    // ---- output ----
    public IReadOnlyList<Choice<OutputMode>> OutputModes { get; } =
    [
        new(OutputMode.NextToOriginal, "Save next to the original (keeps both)"),
        new(OutputMode.Replace, "Replace the original"),
        new(OutputMode.OutputFolder, "Save to a separate folder (mirrors the folder structure)"),
    ];
    public OutputMode OutputMode
    {
        get => S.Output.Mode;
        set { S.Output.Mode = value; Changed(); Raise(nameof(IsReplace)); Raise(nameof(IsNextTo)); Raise(nameof(IsOutputFolder)); }
    }
    public bool IsReplace => S.Output.Mode == OutputMode.Replace;
    public bool IsNextTo => S.Output.Mode == OutputMode.NextToOriginal;
    public bool IsOutputFolder => S.Output.Mode == OutputMode.OutputFolder;

    public string Suffix { get => S.Output.Suffix; set { S.Output.Suffix = value ?? ""; Changed(); Raise(nameof(SuffixExample)); } }
    public string SuffixExample => $"Example: Movie.mkv → Movie{(string.IsNullOrWhiteSpace(S.Output.Suffix) ? ".compressed" : S.Output.Suffix)}.mkv";
    public string OutputFolder { get => S.Output.OutputFolder; set { S.Output.OutputFolder = value ?? ""; Changed(); } }

    public IReadOnlyList<Choice<OriginalDisposal>> Disposals { get; } =
    [
        new(OriginalDisposal.RecycleBin, "Move it to the Recycle Bin"),
        new(OriginalDisposal.BackupFolder, "Move it to a backup folder"),
        new(OriginalDisposal.DeletePermanently, "Delete it permanently"),
    ];
    public OriginalDisposal Disposal
    {
        get => S.Output.Disposal;
        set { S.Output.Disposal = value; Changed(); Raise(nameof(IsBackupFolder)); Raise(nameof(DisposalNote)); }
    }
    public bool IsBackupFolder => S.Output.Disposal == OriginalDisposal.BackupFolder;
    public string DisposalNote => S.Output.Disposal switch
    {
        OriginalDisposal.RecycleBin => "Windows will ask before destroying a file that is too large for the Recycle Bin, or on a drive that has none (network shares).",
        OriginalDisposal.BackupFolder => "A backup folder on the same drive makes the move instant. You delete the backups yourself once you are happy.",
        _ => "There is no way back. Compression is lossy, so the original quality is gone for good.",
    };
    public string BackupFolder { get => S.Output.BackupFolder; set { S.Output.BackupFolder = value ?? ""; Changed(); } }
    public int MinSavingsPercent { get => S.Output.MinSavingsPercent; set { S.Output.MinSavingsPercent = Math.Clamp(value, 0, 95); Changed(); } }
    public bool PreserveTimestamps { get => S.Output.PreserveTimestamps; set { S.Output.PreserveTimestamps = value; Changed(); } }

    // ---- encoding ----
    public IReadOnlyList<Choice<EncoderPolicy>> EncoderPolicies { get; } =
    [
        new(EncoderPolicy.Software, "Software (CPU): smallest files, slower"),
        new(EncoderPolicy.Hardware, "Hardware (GPU): much faster, somewhat larger files"),
    ];
    public EncoderPolicy EncoderPolicy { get => S.EncoderPolicy; set { S.EncoderPolicy = value; Changed(); Raise(nameof(HardwareSummary)); } }
    public IReadOnlyList<Choice<HardwareVendor>> Vendors { get; } =
    [
        new(HardwareVendor.Auto, "Automatic"), new(HardwareVendor.Nvidia, "NVIDIA (NVENC)"),
        new(HardwareVendor.Intel, "Intel (Quick Sync)"), new(HardwareVendor.Amd, "AMD (AMF)"),
    ];
    public HardwareVendor HardwareVendor { get => S.HardwareVendor; set { S.HardwareVendor = value; Changed(); Raise(nameof(HardwareSummary)); } }
    public bool FallbackToSoftware { get => S.FallbackToSoftware; set { S.FallbackToSoftware = value; Changed(); } }
    public bool HardwareDecode { get => S.HardwareDecode; set { S.HardwareDecode = value; Changed(); } }
    public int ParallelJobs { get => S.ParallelJobs; set { S.ParallelJobs = Math.Clamp(value, 1, 8); Changed(); } }
    public IReadOnlyList<int> ParallelChoices { get; } = [1, 2, 3, 4];
    public bool LowPriority { get => S.LowPriority; set { S.LowPriority = value; Changed(); } }
    public bool KeepAwake { get => S.KeepAwake; set { S.KeepAwake = value; Changed(); } }
    public bool SkipDolbyVision { get => S.SkipDolbyVision; set { S.SkipDolbyVision = value; Changed(); } }
    public bool ReprocessCompressed { get => S.ReprocessCompressed; set { S.ReprocessCompressed = value; Changed(); } }

    public IReadOnlyList<EncoderLine> Encoders => EncoderCatalog.All
        .Select(e => new EncoderLine(e.DisplayName,
            !_s.Encoders.IsUsable(e.Id) ? _s.Encoders.Status.GetValueOrDefault(e.Id) ?? "Not checked yet"
            : e.Codec == VideoCodec.H264 || _s.Encoders.SupportsTenBit(e) ? "Available" : "Available (8-bit only with this ffmpeg)",
            _s.Encoders.IsUsable(e.Id)))
        .ToList();

    public string HardwareSummary
    {
        get
        {
            if (_s.Encoders.Status.Count == 0) return "Encoders have not been checked yet.";
            string For(VideoCodec codec) => _s.Encoders.Resolve(new Profile { Codec = codec }, S)?.DisplayName ?? "none available";
            return $"With the current settings: HEVC → {For(VideoCodec.HEVC)} · H.264 → {For(VideoCodec.H264)} · AV1 → {For(VideoCodec.AV1)}";
        }
    }
    public string DetectStatus { get => _detectStatus; private set => Set(ref _detectStatus, value); }

    public async Task DetectAsync(bool force)
    {
        if (_detecting || !_s.Tools.Available) return;
        _detecting = true;
        DetectStatus = "Checking encoders…";
        try
        {
            var progress = new Progress<string>(text => DetectStatus = text);
            await _s.DetectEncodersAsync(force, progress);
            DetectStatus = "";
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DetectStatus = "Could not check encoders: " + ex.Message;
        }
        finally
        {
            _detecting = false;
            Raise(nameof(Encoders));
            Raise(nameof(HardwareSummary));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---- schedule ----
    public bool ScheduleEnabled { get => S.ScheduleEnabled; set { S.ScheduleEnabled = value; Changed(); } }
    public string ScheduleStart { get => S.ScheduleStart; set { if (TimeSpan.TryParse(value, out _)) { S.ScheduleStart = value; Changed(); } } }
    public string ScheduleEnd { get => S.ScheduleEnd; set { if (TimeSpan.TryParse(value, out _)) { S.ScheduleEnd = value; Changed(); } } }

    // ---- recognition ----
    public bool OnlineLookup { get => S.OnlineLookup; set { S.OnlineLookup = value; Changed(); } }
    public string TmdbApiKey { get => S.TmdbApiKey; set { S.TmdbApiKey = value?.Trim() ?? ""; Changed(); } }

    // ---- subtitles ----
    public bool SubtitleFetch { get => S.SubtitleFetch.Enabled; set { S.SubtitleFetch.Enabled = value; Changed(); } }
    public string SubtitleLanguages { get => S.SubtitleFetch.Languages; set { S.SubtitleFetch.Languages = value ?? ""; Changed(); Raise(nameof(SubtitleLanguagesRead)); } }
    public string SubtitleLanguagesRead
    {
        get
        {
            var codes = Languages.ParseList(S.SubtitleFetch.Languages);
            return codes.Count == 0 ? "No recognised language codes. Use codes such as en, es, fr, de, ja." : "Will look for: " + string.Join(", ", codes.Select(Languages.Name));
        }
    }
    public IReadOnlyList<Choice<SubtitleDelivery>> Deliveries { get; } =
        [new(SubtitleDelivery.Embed, "Add them to the compressed file as a track"), new(SubtitleDelivery.Sidecar, "Save them beside the file as .srt")];
    public SubtitleDelivery SubtitleDelivery { get => S.SubtitleFetch.Delivery; set { S.SubtitleFetch.Delivery = value; Changed(); } }
    public bool UseGestdown { get => S.SubtitleFetch.UseGestdown; set { S.SubtitleFetch.UseGestdown = value; Changed(); } }
    public bool UseOpenSubtitles { get => S.SubtitleFetch.UseOpenSubtitles; set { S.SubtitleFetch.UseOpenSubtitles = value; Changed(); } }
    public string OpenSubtitlesApiKey { get => S.SubtitleFetch.OpenSubtitlesApiKey; set { S.SubtitleFetch.OpenSubtitlesApiKey = value?.Trim() ?? ""; Changed(); } }
    public string OpenSubtitlesUser { get => S.SubtitleFetch.OpenSubtitlesUser; set { S.SubtitleFetch.OpenSubtitlesUser = value?.Trim() ?? ""; Changed(); } }
    public bool HasOpenSubtitlesPassword => S.SubtitleFetch.OpenSubtitlesPasswordProtected.Length > 0;

    /// <summary>The password is encrypted for this Windows account before it is written to disk.</summary>
    public void SetOpenSubtitlesPassword(string plain)
    {
        S.SubtitleFetch.OpenSubtitlesPasswordProtected = Native.Protect(plain);
        _s.SaveSettings();
        Raise(nameof(HasOpenSubtitlesPassword));
    }

    public string DataFolder => AppPaths.DataDir;
    public string HistorySummary
    {
        get
        {
            var (count, saved) = _s.History.Totals();
            return count == 0 ? "Nothing compressed yet." : $"{count:N0} files compressed so far, {Format.Bytes(saved)} saved in total.";
        }
    }
    public void RefreshHistory() => Raise(nameof(HistorySummary));
}
