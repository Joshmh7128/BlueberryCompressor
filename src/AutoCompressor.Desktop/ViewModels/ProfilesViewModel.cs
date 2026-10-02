using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Profiles;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;
using AutoCompressor.Desktop.Mvvm;

namespace AutoCompressor.Desktop.ViewModels;

/// <summary>One line of the "which profile does each kind of content get" table.</summary>
public sealed class MappingRow(ContentType type, AppServices services) : ObservableObject
{
    public ContentType Type => type;
    public string TypeName => ContentTypes.DisplayName(type);
    public IReadOnlyList<Profile> Choices => services.Profiles.ForKind(ContentTypes.KindOf(type)).ToList();

    public Profile Selected
    {
        get => services.Profiles.ForType(type, services.Settings);
        set
        {
            if (value is null || value.Id == Selected.Id) return;
            if (value.Id == BuiltInProfiles.DefaultMap[type]) services.Settings.ProfileMap.Remove(type);
            else services.Settings.ProfileMap[type] = value.Id;
            services.SaveSettings();
            Raise();
        }
    }

    public void Refresh()
    {
        Raise(nameof(Choices));
        Raise(nameof(Selected));
    }
}

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class ProfilesViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _dirtyTimer;
    private Profile? _selected;
    private Profile? _editing;
    private string _savedJson = "";
    private bool _isDirty;

    public ObservableCollection<Profile> Profiles { get; } = [];
    public IReadOnlyList<MappingRow> Mappings { get; }

    public ICommand NewCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }

    public Func<string, string, bool> Confirm { get; set; } = (_, _) => true;

    // Choices for the editor's drop-downs.
    public IReadOnlyList<Choice<string>> VideoContainers { get; } = [new("mkv", "MKV (keeps every subtitle format)"), new("mp4", "MP4 (most compatible)")];
    public IReadOnlyList<Choice<string>> AudioContainers { get; } =
        [new("m4a", "M4A (AAC)"), new("opus", "Opus"), new("mp3", "MP3"), new("flac", "FLAC (lossless)"), new("mka", "MKA (any codec)")];
    public IReadOnlyList<Choice<VideoCodec>> Codecs { get; } =
        [new(VideoCodec.HEVC, "HEVC / H.265"), new(VideoCodec.H264, "H.264"), new(VideoCodec.AV1, "AV1"), new(VideoCodec.Copy, "Copy (don't re-encode video)")];
    public IReadOnlyList<Choice<EncoderPreference>> EncoderPreferences { get; } =
    [
        new(EncoderPreference.Auto, "Follow the global setting"), new(EncoderPreference.Software, "Always software (CPU)"),
        new(EncoderPreference.Hardware, "Always hardware (GPU)"),
    ];
    public IReadOnlyList<Choice<RateControlMode>> RateModes { get; } =
        [new(RateControlMode.ConstantQuality, "Constant quality"), new(RateControlMode.AverageBitrate, "Average bitrate")];
    public IReadOnlyList<string> Speeds => CommandBuilder.SpeedNames;
    public IReadOnlyList<string> Tunes => CommandBuilder.TuneNames;
    public IReadOnlyList<Choice<BitDepthMode>> BitDepths { get; } =
        [new(BitDepthMode.Source, "Same as source"), new(BitDepthMode.Force8Bit, "8-bit"), new(BitDepthMode.Force10Bit, "10-bit (less banding)")];
    public IReadOnlyList<Choice<int>> Heights { get; } =
        [new(0, "Keep source size"), new(2160, "2160p (4K)"), new(1440, "1440p"), new(1080, "1080p"), new(720, "720p"), new(576, "576p"), new(480, "480p")];
    public IReadOnlyList<Choice<int>> FrameRates { get; } = [new(0, "Keep source rate"), new(60, "60 fps"), new(30, "30 fps"), new(24, "24 fps")];
    public IReadOnlyList<Choice<DeinterlaceMode>> DeinterlaceModes { get; } =
        [new(DeinterlaceMode.Auto, "When the source is interlaced"), new(DeinterlaceMode.Off, "Never"), new(DeinterlaceMode.Always, "Always")];
    public IReadOnlyList<Choice<DenoiseLevel>> DenoiseLevels { get; } =
        [new(DenoiseLevel.Off, "Off"), new(DenoiseLevel.Light, "Light"), new(DenoiseLevel.Medium, "Medium"), new(DenoiseLevel.Strong, "Strong")];
    public IReadOnlyList<Choice<AudioCodec>> AudioCodecs { get; } =
    [
        new(AudioCodec.Opus, "Opus (most efficient)"), new(AudioCodec.Aac, "AAC (most compatible)"), new(AudioCodec.Eac3, "E-AC-3 (Dolby Digital Plus)"),
        new(AudioCodec.Ac3, "AC-3 (Dolby Digital)"), new(AudioCodec.Mp3, "MP3"), new(AudioCodec.Flac, "FLAC (lossless)"), new(AudioCodec.Copy, "Copy (keep original audio)"),
    ];
    public IReadOnlyList<Choice<int>> ChannelLimits { get; } = [new(0, "Keep all channels"), new(6, "5.1 at most"), new(2, "Stereo"), new(1, "Mono")];
    public IReadOnlyList<Choice<SubtitleMode>> SubtitleModes { get; } =
        [new(SubtitleMode.CopyAll, "Keep all, in their original format"), new(SubtitleMode.SelectedLanguages, "Keep selected languages"), new(SubtitleMode.None, "Remove subtitles")];
    public IReadOnlyList<Choice<SubtitleFallback>> SubtitleFallbacks { get; } =
    [
        new(SubtitleFallback.Sidecar, "Save beside the file in the original format"), new(SubtitleFallback.Convert, "Convert to a format the container holds"),
        new(SubtitleFallback.Drop, "Drop the track"),
    ];
    public IReadOnlyList<Choice<LosslessSourcePolicy>> LosslessPolicies { get; } =
    [
        new(LosslessSourcePolicy.KeepLossless, "Keep lossless (convert to FLAC)"), new(LosslessSourcePolicy.Transcode, "Compress with this profile's codec"),
        new(LosslessSourcePolicy.Skip, "Leave untouched"),
    ];

    public ProfilesViewModel(AppServices services)
    {
        _s = services;
        Mappings = ContentTypes.Video.Concat(ContentTypes.Audio).Select(t => new MappingRow(t, services)).ToList();

        NewCommand = new RelayCommand(() => AddCopyOf(_s.Profiles.Find(BuiltInProfiles.General)!, "New profile"));
        DuplicateCommand = new RelayCommand(() => AddCopyOf(_editing!, _editing!.Name + " (copy)"), () => _editing is not null);
        DeleteCommand = new RelayCommand(Delete, () => _selected is { BuiltIn: false });
        ResetCommand = new RelayCommand(Reset, () => _selected is { BuiltIn: true });
        SaveCommand = new RelayCommand(Save, () => _isDirty);
        RevertCommand = new RelayCommand(() => Select(_selected), () => _isDirty);

        Reload(BuiltInProfiles.Anime);

        // The profile being edited is a plain object, so changes are noticed by comparing snapshots.
        _dirtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dirtyTimer.Tick += (_, _) => IsDirty = _editing is not null && Snapshot(_editing) != _savedJson;
        _dirtyTimer.Start();
    }

    private static string Snapshot(Profile p) => JsonSerializer.Serialize(p, JsonStore.Options);

    private void Reload(string? selectId)
    {
        Profiles.Clear();
        foreach (var p in _s.Profiles.All) Profiles.Add(p);
        Select(Profiles.FirstOrDefault(p => p.Id == selectId) ?? Profiles.FirstOrDefault());
        foreach (var m in Mappings) m.Refresh();
    }

    /// <summary>The profile highlighted in the list.</summary>
    public Profile? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(value, _selected) || value is null) return;
            if (_isDirty) Save(); // moving on keeps your edits rather than silently dropping them
            Select(Profiles.FirstOrDefault(p => p.Id == value.Id) ?? value);
        }
    }

    private void Select(Profile? profile)
    {
        _selected = profile;
        _editing = profile?.Clone();
        _savedJson = _editing is null ? "" : Snapshot(_editing);
        IsDirty = false;
        Raise(nameof(Selected));
        Raise(nameof(Editing));
        Raise(nameof(IsVideo));
        Raise(nameof(IsAudio));
        Raise(nameof(KindText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>A working copy; nothing changes for real until it is saved.</summary>
    public Profile? Editing => _editing;
    public bool IsVideo => _editing?.Kind == MediaKind.Video;
    public bool IsAudio => _editing?.Kind == MediaKind.Audio;
    public string KindText => _editing is null ? "" : (_editing.BuiltIn ? "Built-in" : "Custom") + (_editing.Kind == MediaKind.Audio ? " audio profile" : " video profile");

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (Set(ref _isDirty, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public void Save()
    {
        if (_editing is null) return;
        if (string.IsNullOrWhiteSpace(_editing.Name)) _editing.Name = "Unnamed profile";
        _s.Profiles.Replace(_editing.Clone());
        _s.Profiles.Save();
        string id = _editing.Id;
        Reload(id);
        _s.NotifyConfigurationChanged();
    }

    private void AddCopyOf(Profile source, string name)
    {
        if (_isDirty) Save();
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = name;
        copy.BuiltIn = false;
        _s.Profiles.Add(copy);
        _s.Profiles.Save();
        Reload(copy.Id);
        _s.NotifyConfigurationChanged();
    }

    private void Delete()
    {
        if (_selected is not { BuiltIn: false } profile) return;
        if (!Confirm("Delete profile?", $"Delete \"{profile.Name}\"? Files that were assigned to it go back to their automatic profile.")) return;
        _s.Profiles.Remove(profile);
        foreach (var key in _s.Settings.ProfileMap.Where(kv => kv.Value == profile.Id).Select(kv => kv.Key).ToList())
            _s.Settings.ProfileMap.Remove(key);
        _s.Profiles.Save();
        Reload(null);
        _s.SaveSettings();
    }

    private void Reset()
    {
        if (_selected is not { BuiltIn: true } profile) return;
        if (!Confirm("Reset profile?", $"Put \"{profile.Name}\" back to its original settings?")) return;
        _s.Profiles.Reset(profile.Id);
        _s.Profiles.Save();
        Reload(profile.Id);
        _s.NotifyConfigurationChanged();
    }
}
