using System.Windows;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Util;
using AutoCompressor.Desktop.Mvvm;

namespace AutoCompressor.Desktop.ViewModels;

public enum RowState { Ready, Probing, Compressed, HasCopy, Queued, Running, Done, Skipped, Failed, Unreadable }

/// <summary>One line in the library: either a media file or, in folder view, a folder.</summary>
public sealed class LibraryRow : ObservableObject
{
    private bool _isExpanded;
    private RowState _state = RowState.Probing;
    private string _stateDetail = "";
    private double _sizeFraction;
    private Profile? _profile;

    public MediaFile? File { get; }
    public FolderNode? Folder { get; }
    public int Depth { get; set; }

    public LibraryRow(MediaFile file) => File = file;
    public LibraryRow(FolderNode folder) => Folder = folder;

    public bool IsFolder => Folder is not null;
    public bool IsFile => File is not null;
    public string FullPath => File?.Path ?? Folder!.Path;
    public string Name => File?.Name ?? Folder!.Name;
    /// <summary>The folder a file sits in, shown in the flat list where there is no tree to give context.</summary>
    public string Location => File?.Directory ?? "";
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string Glyph => IsFolder ? "" : File!.Kind == MediaKind.Audio ? "" : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) Raise(nameof(ExpanderGlyph)); }
    }
    public string ExpanderGlyph => _isExpanded ? "" : "";

    // ---- size ----
    public long Size => File?.Size ?? Folder!.MediaSize;
    public string SizeText => Format.Bytes(Size);
    /// <summary>How long this row's bar is, relative to the largest thing in view.</summary>
    public double SizeFraction
    {
        get => _sizeFraction;
        set => Set(ref _sizeFraction, value);
    }
    public string SizeTip => IsFolder
        ? $"{Format.Bytes(Folder!.MediaSize)} of media in {Folder.MediaCount:N0} files\n{Format.Bytes(Folder.TotalSize)} including everything else in the folder"
        : $"{Size:N0} bytes";

    // ---- media details ----
    public double DurationSeconds => File?.Probe?.DurationSeconds ?? 0;
    public string DurationText => IsFolder ? "" : Format.Duration(DurationSeconds);
    public long BitRate => File?.Probe?.BitRate ?? 0;
    public string BitrateText => IsFolder ? "" : Format.Bitrate(BitRate);

    public string VideoText
    {
        get
        {
            if (IsFolder) return Folder!.MediaCount == 1 ? "1 file" : $"{Folder.MediaCount:N0} files";
            var v = File!.Probe?.Video;
            if (v is null) return "";
            string codec = v.CodecName switch { "h264" => "H.264", "hevc" => "HEVC", "mpeg4" => "MPEG-4", "mpeg2video" => "MPEG-2", "vc1" => "VC-1", _ => v.CodecName.ToUpperInvariant() };
            // Name the resolution by its long side so widescreen crops and portrait video still read sensibly.
            int longSide = Math.Max(v.DisplayWidth, v.DisplayHeight), shortSide = Math.Min(v.DisplayWidth, v.DisplayHeight);
            string res = longSide >= 3800 ? "4K" : longSide <= 0 ? "" : (longSide >= 1900 ? 1080 : longSide >= 1260 ? 720 : shortSide) + (v.IsInterlaced ? "i" : "p");
            string extras = (v.BitDepth >= 10 ? " 10-bit" : "") + (v.HasDolbyVision ? " DV" : v.IsHdr ? " HDR" : "");
            return $"{codec} {res}{extras}".Trim();
        }
    }

    public string AudioText
    {
        get
        {
            var probe = File?.Probe;
            if (probe is null) return "";
            var tracks = probe.AudioStreams.ToList();
            if (tracks.Count == 0) return "";
            var first = tracks[0];
            string layout = first.Channels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", _ => first.Channels + "ch" };
            string more = tracks.Count > 1 ? $" +{tracks.Count - 1}" : "";
            return $"{first.CodecName.ToUpperInvariant()} {layout}{more}";
        }
    }

    public string SubsText
    {
        get
        {
            var subs = File?.Probe?.SubtitleStreams.ToList();
            if (subs is null || subs.Count == 0) return "";
            var formats = subs.Select(s => s.CodecName switch
            {
                "subrip" => "SRT", "ass" or "ssa" => "ASS", "hdmv_pgs_subtitle" => "PGS", "dvd_subtitle" => "VobSub",
                "mov_text" => "Text", "webvtt" => "VTT", "dvb_subtitle" => "DVB", _ => s.CodecName,
            }).Distinct();
            return $"{subs.Count} · {string.Join(", ", formats)}";
        }
    }

    // ---- recognition and profile ----
    public ContentType Type => File?.Classification.Type ?? ContentType.General;
    public string TypeText => IsFolder ? "" : File!.Probe is null && File.ProbeError is null ? "" : ContentTypes.DisplayName(Type);
    public string ConfidenceText => File is null ? "" : File.Classification.ConfidenceLabel;
    public bool IsGuess => File is { Classification: { IsManual: false, Confidence: < 0.35 } } && File.Probe is not null;
    public string TypeTip => File is null ? "" : $"{File.Classification.ConfidenceLabel} confidence\n" + string.Join("\n", File.Classification.Reasons.Take(6));

    public bool ProfileIsManual { get; set; }
    public Profile? Profile
    {
        get => _profile;
        set { if (Set(ref _profile, value)) Raise(nameof(ProfileName)); }
    }
    public string ProfileName => IsFolder ? "" : _profile?.Name ?? "";

    // ---- status ----
    public RowState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StatusText)); }
    }
    public string StatusText => IsFolder ? "" : _state switch
    {
        RowState.Ready => "",
        RowState.Probing => "Reading…",
        RowState.Compressed => "Compressed",
        RowState.HasCopy => "Has copy",
        RowState.Queued => "Queued",
        RowState.Running => _stateDetail.Length > 0 ? _stateDetail : "Encoding",
        RowState.Done => _stateDetail.Length > 0 ? _stateDetail : "Done",
        RowState.Skipped => "Skipped",
        RowState.Failed => "Failed",
        RowState.Unreadable => "Unreadable",
        _ => "",
    };
    public string StatusTip => _stateDetail.Length > 0 ? _stateDetail : File?.ProbeError ?? "";

    /// <summary>Files in these states are left out when "everything" is queued.</summary>
    public bool IsEligible => IsFile && _state is RowState.Ready or RowState.Skipped or RowState.Failed;

    public void SetState(RowState state, string detail = "")
    {
        _stateDetail = detail;
        if (_state == state) Raise(nameof(StatusText));
        State = state;
        Raise(nameof(StatusTip));
    }

    /// <summary>Re-read everything from the underlying file or folder.</summary>
    public void Refresh() => RaiseAll();
}
