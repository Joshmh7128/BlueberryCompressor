using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Probing;
using AutoCompressor.Core.Queue;
using AutoCompressor.Core.Recognition;
using AutoCompressor.Core.Scanning;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;
using AutoCompressor.Desktop.Mvvm;

namespace AutoCompressor.Desktop.ViewModels;

public sealed record TypeOption(string Label, ContentType? Type)
{
    public override string ToString() => Label;
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Dispatcher _ui;

    private readonly List<FolderNode> _roots = [];
    private readonly List<MediaFile> _files = [];
    private readonly List<string> _scannedPaths = [];
    private readonly Dictionary<string, LibraryRow> _fileRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FolderNode, LibraryRow> _folderRows = [];
    private readonly Dictionary<MediaFile, OnlineMetadata> _online = [];
    private CancellationTokenSource? _work;
    private HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _folderView;
    private string _filter = "";
    private TypeOption _typeFilter;
    private bool _hideCompressed;
    private bool _isBusy;
    private string _busyText = "";
    private double _busyFraction;
    private string _summary = "Add a folder or some files to get started.";
    private string _notice = "";
    private IReadOnlyList<LibraryRow> _selection = [];
    private bool _updatingSelection;

    /// <summary>Asks the user a yes/no question; supplied by the window.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (_, _) => true;
    public Action<string, string> Alert { get; set; } = (_, _) => { };
    public Action? JobsQueued { get; set; }

    public RangeCollection<LibraryRow> Rows { get; } = [];
    public IReadOnlyList<TypeOption> TypeFilters { get; }

    public ICommand QueueSelectedCommand { get; }
    public ICommand QueueAllCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ResetSelectionCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }

    public LibraryViewModel(AppServices services)
    {
        _s = services;
        _ui = Dispatcher.CurrentDispatcher;
        TypeFilters = [new TypeOption("All types", null), .. Enum.GetValues<ContentType>().Select(t => new TypeOption(ContentTypes.DisplayName(t), t))];
        _typeFilter = TypeFilters[0];

        QueueSelectedCommand = new RelayCommand(() => QueueFiles(SelectedFiles(), "selected"), () => _selection.Count > 0);
        QueueAllCommand = new RelayCommand(() => QueueFiles(Rows.Where(r => r.IsFile).Select(r => r.File!).Concat(
            _folderView ? Rows.Where(r => r.IsFolder && r.Depth == 0).SelectMany(r => r.Folder!.AllFiles()) : []).Distinct().Where(Matches), "listed"), () => _files.Count > 0);
        RescanCommand = new RelayCommand(async () => await RescanAsync(), () => !_isBusy && _scannedPaths.Count > 0);
        ClearCommand = new RelayCommand(Clear, () => !_isBusy && _files.Count > 0);
        CancelCommand = new RelayCommand(() => _work?.Cancel(), () => _isBusy);
        ResetSelectionCommand = new RelayCommand(ResetSelectionToAutomatic, () => _selection.Count > 0);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => !_isBusy && _selection.Count > 0);
        ShowInExplorerCommand = new RelayCommand(ShowInExplorer, () => _selection.Count > 0);
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true), () => _folderView);
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false), () => _folderView);

        _s.ConfigurationChanged += () => _ui.InvokeAsync(OnConfigurationChanged);
        _s.Queue.ListChanged += () => _ui.InvokeAsync(SyncQueueStates);
        _s.Queue.JobChanged += job => _ui.InvokeAsync(() => OnJobChanged(job));
        _s.Queue.JobFinished += job => _ui.InvokeAsync(() => OnJobFinished(job));
        SyncQueueStates(); // jobs restored from the last session
    }

    // ------------------------------------------------------------------ view state

    public bool FolderView
    {
        get => _folderView;
        set { if (Set(ref _folderView, value)) { Raise(nameof(FileView)); RebuildRows(); } }
    }
    public bool FileView
    {
        get => !_folderView;
        set => FolderView = !value;
    }

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) RebuildRows(); }
    }

    public TypeOption TypeFilter
    {
        get => _typeFilter;
        set { if (Set(ref _typeFilter, value ?? TypeFilters[0])) RebuildRows(); }
    }

    public bool HideCompressed
    {
        get => _hideCompressed;
        set { if (Set(ref _hideCompressed, value)) RebuildRows(); }
    }

    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public double BusyFraction { get => _busyFraction; private set => Set(ref _busyFraction, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    /// <summary>A short message about the last action, shown under the toolbar.</summary>
    public string Notice { get => _notice; set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => _notice.Length > 0;
    public bool IsEmpty => _files.Count == 0 && !_isBusy;
    public bool ToolsMissing => !_s.Tools.Available;

    // Quick settings surfaced on the library toolbar.
    public IReadOnlyList<KeyValuePair<OutputMode, string>> OutputModes { get; } =
    [
        new(OutputMode.NextToOriginal, "Save next to original"),
        new(OutputMode.Replace, "Replace original"),
        new(OutputMode.OutputFolder, "Save to output folder"),
    ];
    public OutputMode OutputMode
    {
        get => _s.Settings.Output.Mode;
        set { if (_s.Settings.Output.Mode != value) { _s.Settings.Output.Mode = value; _s.SaveSettings(); Raise(); } }
    }
    public IReadOnlyList<KeyValuePair<EncoderPolicy, string>> EncoderPolicies { get; } =
    [
        new(EncoderPolicy.Software, "Software (smallest files)"),
        new(EncoderPolicy.Hardware, "Hardware / GPU (fastest)"),
    ];
    public EncoderPolicy EncoderPolicy
    {
        get => _s.Settings.EncoderPolicy;
        set { if (_s.Settings.EncoderPolicy != value) { _s.Settings.EncoderPolicy = value; _s.SaveSettings(); Raise(); } }
    }

    // ------------------------------------------------------------------ scanning

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
        if (list.Count == 0) return;
        if (_isBusy)
        {
            Notice = "Still working on the last scan. Add more once it has finished, or cancel it first.";
            return;
        }
        foreach (var path in list)
            if (!_scannedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _scannedPaths.Add(path);
        await ScanAsync(list);
    }

    private async Task RescanAsync()
    {
        var paths = _scannedPaths.ToList();
        ClearLibrary();
        await ScanAsync(paths);
    }

    private void Clear()
    {
        _scannedPaths.Clear();
        ClearLibrary();
        RebuildRows();
        Notice = "";
    }

    private void ClearLibrary()
    {
        _roots.Clear();
        _files.Clear();
        _fileRows.Clear();
        _folderRows.Clear();
        _online.Clear();
    }

    private async Task ScanAsync(List<string> paths)
    {
        var cts = _work = new CancellationTokenSource();
        IsBusy = true;
        BusyFraction = 0;
        BusyText = "Scanning folders…";
        Notice = "";
        Raise(nameof(IsEmpty));
        try
        {
            var progress = new Progress<ScanProgress>(p => BusyText = $"Scanning… {p.MediaFound:N0} media files found, {p.FilesSeen:N0} files checked");
            var result = await Task.Run(() => LibraryScanner.Scan(paths, progress, cts.Token), cts.Token);
            var added = Merge(result);
            RebuildRows();
            if (result.Inaccessible.Count > 0)
                Notice = $"{result.Inaccessible.Count:N0} folder(s) could not be opened and were skipped.";
            await ProbeAsync(added, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Notice = "Cancelled. Files that were not read yet can still be queued; they are read again when their turn comes.";
            foreach (var row in _fileRows.Values.Where(r => r.State == RowState.Probing)) ApplyAfterProbe(row.File!);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            Raise(nameof(IsEmpty));
            Raise(nameof(ToolsMissing));
            UpdateSummary();
            UpdateSelectionDetails();
        }
    }

    /// <summary>Fold a scan result into the library, replacing anything it overlaps.</summary>
    private List<MediaFile> Merge(ScanResult result)
    {
        var added = new List<MediaFile>();
        foreach (var root in result.Roots)
        {
            bool isLoose = root.Folders.Count == 0 && root.Files.Count > 0 && !_scannedPaths.Contains(root.Path, StringComparer.OrdinalIgnoreCase);
            if (isLoose)
            {
                // Individually added files join the existing node for their folder, if there is one.
                var existing = _roots.FirstOrDefault(r => string.Equals(r.Path, root.Path, StringComparison.OrdinalIgnoreCase));
                foreach (var file in root.Files.Where(f => !_fileRows.ContainsKey(f.Path)))
                {
                    var target = existing ?? root;
                    if (existing is not null)
                    {
                        file.Folder = existing;
                        existing.Files.Add(file);
                        existing.MediaCount++;
                        existing.ApplySizeDelta(file.Size);
                    }
                    Register(file, added);
                }
                if (existing is null)
                {
                    root.Files.RemoveAll(f => !added.Contains(f));
                    if (root.Files.Count == 0) continue;
                    root.MediaSize = root.TotalSize = root.Files.Sum(f => f.Size);
                    root.MediaCount = root.Files.Count;
                    _roots.Add(root);
                }
                continue;
            }

            // A folder scan replaces any earlier roots it contains, and is ignored if already covered.
            if (_roots.Any(r => IsUnder(root.Path, r.Path) && r.Folders.Count + r.Files.Count > 0 && !IsLooseRoot(r))) continue;
            foreach (var old in _roots.Where(r => IsUnder(r.Path, root.Path)).ToList()) RemoveRoot(old);
            _roots.Add(root);
            foreach (var file in root.AllFiles()) Register(file, added);
        }
        return added;
    }

    private bool IsLooseRoot(FolderNode root) => !_scannedPaths.Contains(root.Path, StringComparer.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string parent)
    {
        string p = parent.TrimEnd('\\', '/');
        return path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void Register(MediaFile file, List<MediaFile> added)
    {
        if (_fileRows.TryGetValue(file.Path, out var existing))
        {
            _files.Remove(existing.File!);
            existing.File!.Folder?.Files.Remove(existing.File);
        }
        _files.Add(file);
        _fileRows[file.Path] = new LibraryRow(file);
        added.Add(file);
    }

    private void RemoveRoot(FolderNode root)
    {
        _roots.Remove(root);
        foreach (var file in root.AllFiles())
        {
            _files.Remove(file);
            _fileRows.Remove(file.Path);
            _online.Remove(file);
        }
    }

    private async Task ProbeAsync(List<MediaFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var tools = _s.Tools;
        if (!tools.Available)
        {
            foreach (var file in files) { file.Classification = ContentClassifier.Classify(file); ApplyAfterProbe(file); }
            Notice = "ffmpeg was not found, so files cannot be inspected or compressed. Set its location in Settings.";
            return;
        }

        int done = 0, total = files.Count;
        BusyText = $"Reading files… 0 of {total:N0}";
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_s.Settings.ProbeParallelism, 1, 16), CancellationToken = ct };
        // Largest first: those are the files the user cares about, and the list is sorted that way.
        await Parallel.ForEachAsync(files.OrderByDescending(f => f.Size), options, async (file, token) =>
        {
            var cached = _s.ProbeCache.Get(file);
            if (cached is not null) file.Probe = cached;
            else
            {
                try
                {
                    file.Probe = await Ffprobe.ProbeAsync(tools.Ffprobe!, file.Path, token).ConfigureAwait(false);
                    _s.ProbeCache.Set(file, file.Probe);
                }
                catch (ProbeException ex) { file.ProbeError = ex.Message; }
                catch (TimeoutException) { file.ProbeError = "Timed out while reading the file."; }
            }
            file.Classification = ContentClassifier.Classify(file);
            int n = Interlocked.Increment(ref done);
            _ = _ui.InvokeAsync(() =>
            {
                ApplyAfterProbe(file);
                if (n % 5 == 0 || n == total)
                {
                    BusyText = $"Reading files… {n:N0} of {total:N0}";
                    BusyFraction = (double)n / total;
                }
            }, DispatcherPriority.Background);
        });
        await _ui.InvokeAsync(() => { }, DispatcherPriority.Background); // let the row updates land

        foreach (var file in ContentClassifier.ApplyFolderConsensus(_files.Where(f => !_s.Overrides.ContainsKey(f.Path))))
            ApplyAfterProbe(file);

        if (_s.Settings.OnlineLookup) await LookupOnlineAsync(files, ct);

        await Task.Run(() => { _s.ProbeCache.Save(); _s.MetadataCache.Save(); }, CancellationToken.None);
        RebuildRows();
    }

    private async Task LookupOnlineAsync(List<MediaFile> files, CancellationToken ct)
    {
        // One question per show, not per episode.
        var groups = files.Where(f => f.Kind == MediaKind.Video && f.Parsed.Title.Length >= 2)
            .Select(f => (File: f, Film: !f.Parsed.IsEpisodic && (f.Probe?.DurationSeconds ?? 0) >= 3600))
            .Where(x => x.File.Parsed.IsEpisodic || x.Film)
            .GroupBy(x => (Key: FileNameParser.Normalize(x.File.Parsed.Title), x.Film, Year: x.Film ? x.File.Parsed.Year : null))
            .ToList();
        int done = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            BusyText = $"Looking up titles online… {++done:N0} of {groups.Count:N0}";
            BusyFraction = (double)done / groups.Count;
            OnlineMetadata? meta;
            try { meta = await _s.Lookup.LookupAsync(group.First().File.Parsed, group.Key.Film, _s.Settings.TmdbApiKey, ct); }
            catch (HttpRequestException ex) { Notice = "Online lookup stopped: " + ex.Message; break; }
            if (meta is null) continue;
            foreach (var (file, _) in group)
            {
                _online[file] = meta;
                if (_s.Overrides.TryGetValue(file.Path, out var o) && o.Type is not null) continue;
                file.Classification = ContentClassifier.Classify(file, meta);
                ApplyAfterProbe(file);
            }
        }
    }

    /// <summary>Bring a row up to date once its file has been probed and classified.</summary>
    private void ApplyAfterProbe(MediaFile file)
    {
        if (!_fileRows.TryGetValue(file.Path, out var row)) return;

        if (_s.Overrides.TryGetValue(file.Path, out var o) && o.Type is { } manual && ContentTypes.KindOf(manual) == file.Kind)
            file.Classification = new Classification { Type = manual, Confidence = 1, IsManual = true, Reasons = ["Set by you"] };
        AssignProfile(row);

        if (file.ProbeError is not null) row.SetState(RowState.Unreadable, file.ProbeError);
        else if (_queuedPaths.Contains(file.Path)) row.SetState(RowState.Queued);
        else if (file.AlreadyCompressed) row.SetState(RowState.Compressed, "Made by AutoCompressor; it will not be compressed again.");
        else if (_s.History.FindCopy(file.Path, file.Size, file.ModifiedUtc) is { } copy)
            row.SetState(RowState.HasCopy, "Compressed copy: " + copy.OutputPath);
        else if (row.State is RowState.Probing or RowState.Queued) row.SetState(RowState.Ready);
        row.Refresh();
    }

    private void AssignProfile(LibraryRow row)
    {
        var file = row.File!;
        if (_s.Overrides.TryGetValue(file.Path, out var o) && _s.Profiles.Find(o.ProfileId) is { } chosen && chosen.Kind == file.Kind)
        {
            row.ProfileIsManual = true;
            row.Profile = chosen;
        }
        else
        {
            row.ProfileIsManual = false;
            row.Profile = _s.Profiles.ForType(file.Classification.Type, _s.Settings);
        }
    }

    private void OnConfigurationChanged()
    {
        foreach (var row in _fileRows.Values) AssignProfile(row);
        Raise(nameof(OutputMode));
        Raise(nameof(EncoderPolicy));
        Raise(nameof(ToolsMissing));
        Raise(nameof(ProfileChoices));
        UpdateSelectionDetails();
    }

    // ------------------------------------------------------------------ rows

    private bool Matches(MediaFile file)
    {
        if (_typeFilter.Type is { } type && file.Classification.Type != type) return false;
        if (_filter.Length > 0 && !file.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase)) return false;
        if (_hideCompressed && _fileRows.TryGetValue(file.Path, out var row) && row.State is RowState.Compressed or RowState.HasCopy or RowState.Done) return false;
        return true;
    }

    private bool Filtering => _typeFilter.Type is not null || _filter.Length > 0 || _hideCompressed;

    public void RebuildRows()
    {
        var visible = new List<LibraryRow>();
        if (!_folderView)
        {
            var files = _files.Where(Matches).OrderByDescending(f => f.Size).ToList();
            long max = files.Count > 0 ? files[0].Size : 0;
            foreach (var file in files)
            {
                var row = _fileRows[file.Path];
                row.Depth = 0;
                row.SizeFraction = max > 0 ? (double)file.Size / max : 0;
                visible.Add(row);
            }
        }
        else
        {
            HashSet<FolderNode>? withMatches = null;
            if (Filtering)
            {
                withMatches = [];
                foreach (var file in _files.Where(Matches))
                    for (var n = file.Folder; n is not null && withMatches.Add(n); n = n.Parent) { }
            }
            long biggest = _roots.Count > 0 ? _roots.Max(r => r.MediaSize) : 0;
            foreach (var root in _roots.OrderByDescending(r => r.MediaSize))
            {
                if (withMatches is not null && !withMatches.Contains(root)) continue;
                var row = FolderRow(root, expandedByDefault: true);
                row.Depth = 0;
                row.SizeFraction = biggest > 0 ? (double)root.MediaSize / biggest : 0;
                visible.Add(row);
                if (row.IsExpanded) AddChildren(root, 1, visible, withMatches);
            }
        }
        Rows.ReplaceAll(visible);
        UpdateSummary();
    }

    private LibraryRow FolderRow(FolderNode node, bool expandedByDefault = false)
    {
        if (!_folderRows.TryGetValue(node, out var row))
        {
            row = new LibraryRow(node) { IsExpanded = expandedByDefault };
            row.SetState(RowState.Ready);
            _folderRows[node] = row;
        }
        return row;
    }

    /// <summary>Folders and files together, largest first, each with a bar showing its share of the parent.</summary>
    private void AddChildren(FolderNode parent, int depth, List<LibraryRow> into, HashSet<FolderNode>? withMatches)
    {
        var children = new List<(long Size, FolderNode? Folder, MediaFile? File)>();
        foreach (var folder in parent.Folders)
            if (withMatches is null || withMatches.Contains(folder)) children.Add((folder.MediaSize, folder, null));
        foreach (var file in parent.Files)
            if (withMatches is null || Matches(file)) children.Add((file.Size, null, file));

        foreach (var (size, folder, file) in children.OrderByDescending(c => c.Size))
        {
            var row = folder is not null ? FolderRow(folder) : _fileRows[file!.Path];
            row.Depth = depth;
            row.SizeFraction = parent.MediaSize > 0 ? (double)size / parent.MediaSize : 0;
            into.Add(row);
            if (folder is not null && row.IsExpanded) AddChildren(folder, depth + 1, into, withMatches);
        }
    }

    public void ToggleExpanded(LibraryRow row)
    {
        if (!row.IsFolder) return;
        int index = Rows.IndexOf(row);
        if (index < 0) return;
        if (row.IsExpanded)
        {
            int count = 0;
            while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > row.Depth) count++;
            row.IsExpanded = false;
            Rows.RemoveRange(index + 1, count);
        }
        else
        {
            row.IsExpanded = true;
            HashSet<FolderNode>? withMatches = null;
            if (Filtering)
            {
                withMatches = [];
                foreach (var file in row.Folder!.AllFiles().Where(Matches))
                    for (var n = file.Folder; n is not null && withMatches.Add(n); n = n.Parent) { }
            }
            var children = new List<LibraryRow>();
            AddChildren(row.Folder!, row.Depth + 1, children, withMatches);
            Rows.InsertRange(index + 1, children);
        }
    }

    private void SetAllExpanded(bool expanded)
    {
        void Walk(FolderNode node, int depth)
        {
            // Collapsing keeps the top level open so the list never goes blank.
            FolderRow(node).IsExpanded = expanded || depth == 0;
            foreach (var child in node.Folders) Walk(child, depth + 1);
        }
        foreach (var root in _roots) Walk(root, 0);
        RebuildRows();
    }

    private void UpdateSummary()
    {
        if (_files.Count == 0)
        {
            Summary = _isBusy ? "" : "Add a folder or some files to get started.";
            return;
        }
        long total = _files.Sum(f => f.Size);
        int shown = _folderView ? _files.Count(Matches) : Rows.Count;
        string text = $"{_files.Count:N0} files · {Format.Bytes(total)}";
        if (Filtering) text += $" · {shown:N0} shown ({Format.Bytes(_files.Where(Matches).Sum(f => f.Size))})";
        int compressed = _fileRows.Values.Count(r => r.State is RowState.Compressed or RowState.HasCopy or RowState.Done);
        if (compressed > 0) text += $" · {compressed:N0} already compressed";
        Summary = text;
    }

    // ------------------------------------------------------------------ selection

    public void SetSelection(IEnumerable<LibraryRow> rows)
    {
        _selection = rows.ToList();
        UpdateSelectionDetails();
        CommandManager.InvalidateRequerySuggested();
    }

    private List<MediaFile> SelectedFiles() =>
        _selection.SelectMany(r => r.IsFolder ? r.Folder!.AllFiles() : [r.File!]).Distinct().ToList();

    private MediaKind SelectionKind => SelectedFiles().FirstOrDefault()?.Kind ?? MediaKind.Video;

    public bool HasSelection => _selection.Count > 0;
    public string SelectionTitle { get; private set; } = "Nothing selected";
    public string SelectionSubtitle { get; private set; } = "Select files or folders to see how they will be handled.";
    public IReadOnlyList<TypeOption> TypeChoices { get; private set; } = [];
    public IReadOnlyList<Profile> ProfileChoices => _s.Profiles.ForKind(SelectionKind).ToList();
    public IReadOnlyList<string> Reasons { get; private set; } = [];
    public string ReasonsCaption { get; private set; } = "WHY THIS TYPE";
    public IReadOnlyList<string> StreamLines { get; private set; } = [];
    public string PlanSummary { get; private set; } = "";
    public IReadOnlyList<string> PlanNotes { get; private set; } = [];
    public string PlanCommand { get; private set; } = "";
    public bool PlanIsSkip { get; private set; }

    public TypeOption? SelectedType
    {
        get
        {
            var types = SelectedFiles().Select(f => f.Classification.Type).Distinct().Take(2).ToList();
            return types.Count == 1 ? TypeChoices.FirstOrDefault(t => t.Type == types[0]) : null;
        }
        set
        {
            if (_updatingSelection || value?.Type is not { } type) return;
            foreach (var file in SelectedFiles().Where(f => f.Kind == ContentTypes.KindOf(type)))
            {
                file.Classification = new Classification { Type = type, Confidence = 1, IsManual = true, Reasons = ["Set by you"] };
                Override(file.Path).Type = type;
                if (_fileRows.TryGetValue(file.Path, out var row)) { AssignProfile(row); row.Refresh(); }
            }
            _s.SaveOverrides();
            UpdateSelectionDetails();
        }
    }

    public Profile? SelectedProfile
    {
        get
        {
            var profiles = SelectedFiles().Select(f => _fileRows.TryGetValue(f.Path, out var r) ? r.Profile : null).Distinct().Take(2).ToList();
            return profiles.Count == 1 ? profiles[0] : null;
        }
        set
        {
            if (_updatingSelection || value is null) return;
            foreach (var file in SelectedFiles().Where(f => f.Kind == value.Kind))
            {
                Override(file.Path).ProfileId = value.Id;
                if (_fileRows.TryGetValue(file.Path, out var row)) { AssignProfile(row); row.Refresh(); }
            }
            _s.SaveOverrides();
            UpdateSelectionDetails();
        }
    }

    private FileOverride Override(string path)
    {
        if (!_s.Overrides.TryGetValue(path, out var o)) _s.Overrides[path] = o = new FileOverride();
        return o;
    }

    private void ResetSelectionToAutomatic()
    {
        foreach (var file in SelectedFiles())
        {
            if (!_s.Overrides.Remove(file.Path)) continue;
            file.Classification = ContentClassifier.Classify(file, _online.GetValueOrDefault(file));
            ApplyAfterProbe(file);
        }
        _s.SaveOverrides();
        UpdateSelectionDetails();
    }

    private void UpdateSelectionDetails()
    {
        _updatingSelection = true;
        try
        {
            var files = SelectedFiles();
            var kind = SelectionKind;
            TypeChoices = (kind == MediaKind.Audio ? ContentTypes.Audio : ContentTypes.Video)
                .Select(t => new TypeOption(ContentTypes.DisplayName(t), t)).ToList();

            var first = _selection.FirstOrDefault();
            if (first is null)
            {
                SelectionTitle = "Nothing selected";
                SelectionSubtitle = "Select files or folders to see how they will be handled.";
                Reasons = []; StreamLines = []; PlanSummary = ""; PlanNotes = []; PlanCommand = ""; PlanIsSkip = false;
            }
            else
            {
                SelectionTitle = _selection.Count == 1 ? first.Name : $"{_selection.Count:N0} items selected";
                SelectionSubtitle = files.Count == 1 && first.IsFile
                    ? first.File!.Directory
                    : $"{files.Count:N0} files · {Format.Bytes(files.Sum(f => f.Size))}";
                if (files.Count == 1) DescribeFile(files[0]);
                else DescribeMany(files);
            }
        }
        finally { _updatingSelection = false; }

        // Lists before the values chosen from them, or the combo boxes briefly lose their selection.
        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(SelectionTitle), nameof(SelectionSubtitle), nameof(TypeChoices), nameof(ProfileChoices),
                     nameof(SelectedType), nameof(SelectedProfile), nameof(Reasons), nameof(ReasonsCaption), nameof(StreamLines), nameof(PlanSummary),
                     nameof(PlanNotes), nameof(PlanCommand), nameof(PlanIsSkip),
                 })
            Raise(name);
    }

    /// <summary>Summarise a folder or a multi-selection: what kinds of content it holds and what queueing it would do.</summary>
    private void DescribeMany(List<MediaFile> files)
    {
        StreamLines = []; PlanCommand = ""; PlanIsSkip = false;
        ReasonsCaption = "WHAT IS SELECTED";
        Reasons = files.GroupBy(f => f.Classification.Type)
            .Select(g => (Type: g.Key, Count: g.Count(), Size: g.Sum(f => f.Size)))
            .OrderByDescending(g => g.Size)
            .Select(g => $"{ContentTypes.DisplayName(g.Type)} · {g.Count:N0} file{(g.Count == 1 ? "" : "s")} · {Format.Bytes(g.Size)}")
            .ToList();

        int compress = 0, unread = 0, leftOut = 0;
        long compressBytes = 0;
        var skipReasons = new Dictionary<string, int>();
        foreach (var file in files.Take(5000))
        {
            if (!_fileRows.TryGetValue(file.Path, out var row)) continue;
            if (row.State is RowState.Compressed or RowState.HasCopy or RowState.Done && !_s.Settings.ReprocessCompressed) { leftOut++; continue; }
            if (file.Probe is null || row.Profile is null) { unread++; continue; }
            var plan = CommandBuilder.Build(new EncodeRequest
            {
                File = file, Probe = file.Probe, Profile = row.Profile, Settings = _s.Settings, Encoders = _s.Encoders, OutputPath = file.Path,
            });
            if (!plan.Skip) { compress++; compressBytes += file.Size; continue; }
            // "Already small: HEVC at 1.2 Mbps (…)" and friends: group by the part before the detail.
            string reason = plan.SkipReason!.Split(':', '(')[0].Trim();
            skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
        }

        PlanSummary = compress > 0
            ? $"{compress:N0} file{(compress == 1 ? "" : "s")} ({Format.Bytes(compressBytes)}) would be compressed."
            : "Nothing here would be compressed.";
        var notes = new List<string>();
        if (leftOut > 0) notes.Add($"{leftOut:N0} already compressed: left out.");
        foreach (var (reason, count) in skipReasons.OrderByDescending(kv => kv.Value)) notes.Add($"{count:N0} skipped: {reason}.");
        if (unread > 0) notes.Add($"{unread:N0} not read yet.");
        if (files.Count > 5000) notes.Add($"Only the first 5,000 of {files.Count:N0} files were checked for this summary.");
        var profiles = files.Select(f => _fileRows.TryGetValue(f.Path, out var r) ? r.Profile?.Name : null).Where(n => n is not null).Distinct().ToList();
        if (profiles.Count > 0) notes.Add("Profiles: " + string.Join(", ", profiles) + ".");
        PlanNotes = notes;
    }

    private void DescribeFile(MediaFile? file)
    {
        Reasons = []; StreamLines = []; PlanSummary = ""; PlanNotes = []; PlanCommand = ""; PlanIsSkip = false;
        ReasonsCaption = "WHY THIS TYPE";
        if (file is null) return;

        var c = file.Classification;
        Reasons = [$"{ContentTypes.DisplayName(c.Type)} · {c.ConfidenceLabel.ToLowerInvariant()} confidence", .. c.Reasons.Take(7)];

        var probe = file.Probe;
        if (probe is null)
        {
            PlanSummary = file.ProbeError ?? "Not read yet.";
            PlanIsSkip = file.ProbeError is not null;
            return;
        }

        var lines = new List<string>();
        foreach (var s in probe.Streams)
        {
            string lang = string.IsNullOrEmpty(s.Language) || s.Language == "und" ? "" : $" [{s.Language}]";
            string title = string.IsNullOrEmpty(s.Title) ? "" : $" “{s.Title}”";
            if (s.IsVideo)
                lines.Add($"Video: {s.CodecName} {s.Width}×{s.Height} {s.FrameRate:0.###} fps{(s.BitDepth >= 10 ? $" {s.BitDepth}-bit" : "")}" +
                          $"{(s.IsHdr ? " HDR" : "")}{(s.IsInterlaced ? " interlaced" : "")}{(probe.EstimateVideoBitRate() > 0 ? " · ~" + Format.Bitrate(probe.EstimateVideoBitRate()) : "")}");
            else if (s.IsAudio)
                lines.Add($"Audio: {s.CodecName} {s.ChannelLayout ?? s.Channels + "ch"}{(s.BitRate > 0 ? " · " + Format.Bitrate(s.BitRate) : "")}{lang}{title}");
            else if (s.IsSubtitle)
                lines.Add($"Subtitle: {s.CodecName}{lang}{(s.IsForced ? " forced" : "")}{title}");
            else if (s.IsAttachment)
                lines.Add($"Attachment: {s.FileName ?? s.CodecName}");
        }
        if (probe.ChapterCount > 0) lines.Add($"{probe.ChapterCount} chapters");
        StreamLines = lines;

        if (!_fileRows.TryGetValue(file.Path, out var row) || row.Profile is null) return;
        string extension = CommandBuilder.ExtensionFor(row.Profile, probe);
        var plan = CommandBuilder.Build(new EncodeRequest
        {
            File = file, Probe = probe, Profile = row.Profile, Settings = _s.Settings, Encoders = _s.Encoders,
            OutputPath = Path.Combine(file.Directory, Path.GetFileNameWithoutExtension(file.Path) + "." + extension),
        });
        PlanIsSkip = plan.Skip;
        PlanSummary = plan.Skip ? "Will be skipped: " + plan.SkipReason : $"{row.Profile.Name}{(row.ProfileIsManual ? " (chosen by you)" : "")} → .{extension}\n{plan.Summary}";
        PlanNotes = plan.Notes;
        PlanCommand = plan.Skip ? "" : plan.Preview();
    }

    private void RemoveSelected()
    {
        // Removes from the list only; nothing on disk is touched.
        foreach (var row in _selection.ToList())
        {
            if (row.IsFolder)
            {
                var node = row.Folder!;
                foreach (var file in node.AllFiles().ToList()) { _files.Remove(file); _fileRows.Remove(file.Path); }
                if (node.Parent is { } parent)
                {
                    parent.Folders.Remove(node);
                    for (var n = parent; n is not null; n = n.Parent) { n.MediaSize -= node.MediaSize; n.TotalSize -= node.TotalSize; n.MediaCount -= node.MediaCount; }
                }
                else
                {
                    _roots.Remove(node);
                    _scannedPaths.RemoveAll(p => p.TrimEnd('\\').Equals(node.Path, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (_fileRows.Remove(row.File!.Path))
            {
                var file = row.File;
                _files.Remove(file);
                if (file.Folder is { } folder)
                {
                    folder.Files.Remove(file);
                    for (var n = folder; n is not null; n = n.Parent) { n.MediaSize -= file.Size; n.TotalSize -= file.Size; n.MediaCount--; }
                }
            }
        }
        _roots.RemoveAll(r => r.MediaCount <= 0);
        RebuildRows();
        Raise(nameof(IsEmpty));
    }

    private void ShowInExplorer()
    {
        var row = _selection.FirstOrDefault();
        if (row is null) return;
        try
        {
            if (row.IsFolder) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { row.FullPath } });
            else Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{row.FullPath}\"" });
        }
        catch (System.ComponentModel.Win32Exception) { /* Explorer unavailable */ }
    }

    // ------------------------------------------------------------------ queueing

    private void QueueFiles(IEnumerable<MediaFile> files, string what)
    {
        var settings = _s.Settings;
        var output = settings.Output;
        if (!_s.Tools.Available)
        {
            Alert("ffmpeg not found", "AutoCompressor needs ffmpeg and ffprobe to compress files. Set their location in Settings.");
            return;
        }
        if (output.Mode == OutputMode.OutputFolder && string.IsNullOrWhiteSpace(output.OutputFolder))
        {
            Alert("No output folder", "\"Save to output folder\" is selected but no folder has been chosen. Pick one in Settings.");
            return;
        }
        if (output.Mode == OutputMode.Replace && output.Disposal == OriginalDisposal.BackupFolder && string.IsNullOrWhiteSpace(output.BackupFolder))
        {
            Alert("No backup folder", "Originals are set to move to a backup folder, but no folder has been chosen. Pick one in Settings.");
            return;
        }

        var all = files.ToList();
        var eligible = new List<LibraryRow>();
        int alreadyDone = 0, unreadable = 0, queued = 0;
        foreach (var file in all)
        {
            if (!_fileRows.TryGetValue(file.Path, out var row)) continue;
            switch (row.State)
            {
                case RowState.Compressed or RowState.HasCopy or RowState.Done when !settings.ReprocessCompressed: alreadyDone++; break;
                case RowState.Unreadable: unreadable++; break;
                case RowState.Queued or RowState.Running: queued++; break;
                default: eligible.Add(row); break;
            }
        }
        if (eligible.Count == 0)
        {
            Notice = all.Count == 0 ? "Nothing to queue." : $"Nothing to queue: {Describe(alreadyDone, unreadable, queued)}.";
            return;
        }

        if (output.Mode == OutputMode.Replace)
        {
            string fate = output.Disposal switch
            {
                OriginalDisposal.RecycleBin => "moved to the Recycle Bin",
                OriginalDisposal.BackupFolder => $"moved to {output.BackupFolder}",
                _ => "PERMANENTLY DELETED",
            };
            string extra = output.Disposal == OriginalDisposal.DeletePermanently
                ? "\n\nThis cannot be undone. Compression is lossy: the originals cannot be recreated from the compressed files."
                : "";
            if (!Confirm("Replace originals?",
                    $"{eligible.Count:N0} file(s) will be compressed. After each one is encoded and verified, the original is {fate} " +
                    $"and the compressed file takes its place.{extra}\n\nContinue?"))
                return;
        }

        var jobs = eligible.Select(row =>
        {
            var profile = row.Profile ?? _s.Profiles.ForType(row.File!.Classification.Type, settings);
            return QueueManager.CreateJob(row.File!, profile, output);
        }).ToList();
        int added = _s.Queue.Add(jobs);
        foreach (var row in eligible) row.SetState(RowState.Queued);

        string left = Describe(alreadyDone, unreadable, queued);
        Notice = $"Added {added:N0} {what} file(s) to the queue" + (left.Length > 0 ? $"; left out {left}." : ".");
        UpdateSummary();
        JobsQueued?.Invoke();
    }

    private static string Describe(int alreadyDone, int unreadable, int queued)
    {
        var parts = new List<string>();
        if (alreadyDone > 0) parts.Add($"{alreadyDone:N0} already compressed");
        if (queued > 0) parts.Add($"{queued:N0} already in the queue");
        if (unreadable > 0) parts.Add($"{unreadable:N0} unreadable");
        return string.Join(", ", parts);
    }

    private void SyncQueueStates()
    {
        var waiting = _s.Queue.Jobs.Where(j => !j.IsFinished).Select(j => j.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _queuedPaths = waiting;
        foreach (var row in _fileRows.Values)
        {
            bool inQueue = waiting.Contains(row.File!.Path);
            if (inQueue && row.State == RowState.Ready) row.SetState(RowState.Queued);
            else if (!inQueue && row.State is RowState.Queued or RowState.Running) row.SetState(RowState.Ready);
        }
    }

    private void OnJobChanged(QueueJob job)
    {
        if (!_fileRows.TryGetValue(job.SourcePath, out var row)) return;
        if (job.Status == JobStatus.Running)
            row.SetState(RowState.Running, job.Paused ? "Paused" : job.Progress > 0 ? $"Encoding {job.Progress * 100:0}%" : job.Stage);
        else if (job.Status == JobStatus.Queued)
            row.SetState(RowState.Queued);
    }

    private void OnJobFinished(QueueJob job)
    {
        if (!_fileRows.TryGetValue(job.SourcePath, out var row)) return;
        var file = row.File!;
        switch (job.Status)
        {
            case JobStatus.Completed:
                double smaller = job.SourceSize > 0 ? 1.0 - (double)job.OutputSize / job.SourceSize : 0;
                bool replaced = job.Output.Mode == OutputMode.Replace && job.OutputPath is not null
                                && (string.Equals(job.OutputPath, job.SourcePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(job.SourcePath));
                if (replaced && job.OutputPath is not null)
                {
                    // The original was replaced: this row now describes the new, smaller file.
                    long delta = job.OutputSize - file.Size;
                    _fileRows.Remove(file.Path);
                    file.Path = job.OutputPath;
                    file.Size = job.OutputSize;
                    file.ModifiedUtc = File.GetLastWriteTimeUtc(job.OutputPath);
                    file.Probe = null;
                    _fileRows[file.Path] = row;
                    file.Folder?.ApplySizeDelta(delta);
                    for (var n = file.Folder; n is not null; n = n.Parent)
                        if (_folderRows.TryGetValue(n, out var folderRow)) folderRow.Refresh();
                    _ = ReprobeAsync(file);
                }
                row.SetState(RowState.Done, $"Done · −{smaller * 100:0}%");
                break;
            case JobStatus.Skipped:
                row.SetState(RowState.Skipped, job.Message);
                break;
            case JobStatus.Failed:
                row.SetState(RowState.Failed, job.Message);
                break;
            default:
                row.SetState(RowState.Ready);
                break;
        }
        row.Refresh();
        UpdateSummary();
    }

    private async Task ReprobeAsync(MediaFile file)
    {
        if (!_s.Tools.Available) return;
        try
        {
            file.Probe = await Ffprobe.ProbeAsync(_s.Tools.Ffprobe!, file.Path);
            _s.ProbeCache.Set(file, file.Probe);
        }
        catch (Exception ex) when (ex is ProbeException or TimeoutException) { return; }
        if (_fileRows.TryGetValue(file.Path, out var row)) row.Refresh();
    }

    /// <summary>Write the listed files (respecting the current filters) to a CSV file, largest first.</summary>
    public int ExportCsv(string path)
    {
        static string Q(string? text) => '"' + (text ?? "").Replace("\"", "\"\"") + '"';
        var files = _files.Where(Matches).OrderByDescending(f => f.Size).ToList();
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("Path,Size (bytes),Size,Length,Video,Audio,Subtitles,Bitrate (kbps),Type,Confidence,Profile,Status");
        foreach (var file in files)
        {
            var row = _fileRows[file.Path];
            writer.WriteLine(string.Join(',', Q(file.Path), file.Size, Q(row.SizeText), Q(row.DurationText), Q(row.VideoText), Q(row.AudioText),
                Q(row.SubsText), row.BitRate / 1000, Q(row.TypeText), Q(row.ConfidenceText), Q(row.ProfileName), Q(row.StatusText)));
        }
        Notice = $"Exported {files.Count:N0} files to {path}";
        return files.Count;
    }

    public bool HasFiles => _files.Count > 0;

    public void SaveCaches()
    {
        _s.ProbeCache.Save();
        _s.MetadataCache.Save();
    }
}
