using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Queue;
using AutoCompressor.Core.Util;
using AutoCompressor.Desktop.Mvvm;

namespace AutoCompressor.Desktop.ViewModels;

public sealed class JobRow(QueueJob job) : ObservableObject
{
    public QueueJob Job { get; } = job;

    public string Name => Path.GetFileName(Job.SourcePath);
    public string Folder => Path.GetDirectoryName(Job.SourcePath) ?? "";
    public string ProfileName => Job.Profile.Name;
    public string TypeText => ContentTypes.DisplayName(Job.ContentType);
    public ContentType Type => Job.ContentType;
    public JobStatus Status => Job.Status;

    public string StatusText => Job.Status switch
    {
        JobStatus.Queued => "Waiting",
        JobStatus.Running => Job.Paused ? "Paused" : Job.Stage.Length > 0 ? Job.Stage : "Running",
        JobStatus.Completed => "Done",
        JobStatus.Skipped => "Skipped",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        _ => "",
    };

    public double Progress => Job.Status == JobStatus.Completed ? 100 : Job.Progress * 100;
    public bool ShowProgress => Job.Status == JobStatus.Running;
    public string ProgressText => Job.Status == JobStatus.Running && Job.Progress > 0 ? $"{Job.Progress * 100:0.0}%" : "";

    public string SpeedText => Job.Status != JobStatus.Running || Job.Fps <= 0 && Job.Speed <= 0 ? ""
        : Job.Kind == MediaKind.Audio ? $"{Job.Speed:0.#}×" : $"{Job.Fps:0} fps · {Job.Speed:0.0#}×";

    public string EtaText => Job.Status == JobStatus.Running && Job.Eta is { } eta && !Job.Paused ? Format.Duration(Math.Max(1, eta.TotalSeconds)) : "";

    public string OutputModeText => Job.Output.Mode switch
    {
        OutputMode.Replace => "Replace",
        OutputMode.OutputFolder => "To folder",
        _ => "Beside original",
    };

    public string SourceSizeText => Format.Bytes(Job.SourceSize);
    public string OutputSizeText => Job.OutputSize > 0 ? Format.Bytes(Job.OutputSize) : "";
    public string SavedText => Job.Saved > 0 && Job.SourceSize > 0 ? $"−{(double)Job.Saved / Job.SourceSize * 100:0}%" : "";

    public string Message => Job.Message;
    public IReadOnlyList<string> Notes
    {
        get
        {
            // The encoder thread may be adding a note at this very moment.
            try { return Job.Notes.ToList(); }
            catch (InvalidOperationException) { return []; }
        }
    }
    public string Summary => Job.Summary ?? "";
    public string Command => Job.Command ?? "";
    public string OutputPath => Job.OutputPath ?? "";
    public string LogPath => Job.LogPath ?? "";

    public void Refresh() => RaiseAll();
}

public sealed class QueueViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, JobRow> _rowsById = [];
    private IReadOnlyList<JobRow> _selection = [];
    private JobRow? _current;

    public RangeCollection<JobRow> Rows { get; } = [];

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand FinishCurrentCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand ShowOutputCommand { get; }
    public ICommand OpenLogCommand { get; }

    public Func<string, string, bool> Confirm { get; set; } = (_, _) => true;

    public QueueViewModel(AppServices services)
    {
        _s = services;
        _ui = Dispatcher.CurrentDispatcher;
        var queue = _s.Queue;

        StartCommand = new RelayCommand(queue.Start, () => queue.State == QueueState.Idle && Rows.Any(r => r.Status == JobStatus.Queued));
        PauseCommand = new RelayCommand(TogglePause, () => queue.State is QueueState.Running or QueueState.Paused or QueueState.WaitingForSchedule);
        StopCommand = new RelayCommand(() =>
        {
            if (Confirm("Stop the queue?", "The file being encoded right now will be abandoned and start over next time. Finished files are kept."))
                queue.Stop();
        }, () => queue.IsActive);
        FinishCurrentCommand = new RelayCommand(queue.StopAfterCurrent, () => queue.State is QueueState.Running or QueueState.Paused);
        RemoveCommand = new RelayCommand(() => queue.Remove(_selection.Select(r => r.Job.Id)), () => _selection.Count > 0);
        MoveUpCommand = new RelayCommand(() => queue.Move(_selection.Select(r => r.Job.Id), -1), () => _selection.Count > 0);
        MoveDownCommand = new RelayCommand(() => queue.Move(_selection.Select(r => r.Job.Id), +1), () => _selection.Count > 0);
        RetryCommand = new RelayCommand(() => queue.Retry(_selection.Select(r => r.Job.Id)),
            () => _selection.Any(r => r.Job.IsFinished && r.Status != JobStatus.Completed));
        ClearFinishedCommand = new RelayCommand(queue.ClearFinished, () => Rows.Any(r => r.Job.IsFinished));
        ShowOutputCommand = new RelayCommand(ShowOutput, () => _current is not null);
        OpenLogCommand = new RelayCommand(OpenLog, () => _current is { LogPath.Length: > 0 });

        queue.ListChanged += () => _ui.InvokeAsync(SyncRows);
        queue.JobChanged += job => _ui.InvokeAsync(() => OnJobChanged(job), DispatcherPriority.Background);
        queue.JobFinished += _ => _ui.InvokeAsync(UpdateTotals);
        queue.StateChanged += () => _ui.InvokeAsync(() =>
        {
            UpdateTotals();
            CommandManager.InvalidateRequerySuggested();
        });
        SyncRows();
    }

    private void SyncRows()
    {
        var jobs = _s.Queue.Jobs;
        var alive = jobs.Select(j => j.Id).ToHashSet();
        foreach (var gone in _rowsById.Keys.Where(id => !alive.Contains(id)).ToList()) _rowsById.Remove(gone);

        var rows = new List<JobRow>(jobs.Count);
        foreach (var job in jobs)
        {
            if (!_rowsById.TryGetValue(job.Id, out var row)) _rowsById[job.Id] = row = new JobRow(job);
            else row.Refresh();
            rows.Add(row);
        }
        if (!rows.SequenceEqual(Rows)) Rows.ReplaceAll(rows);
        UpdateTotals();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnJobChanged(QueueJob job)
    {
        if (_rowsById.TryGetValue(job.Id, out var row)) row.Refresh();
        UpdateTotals();
    }

    public void SetSelection(IEnumerable<JobRow> rows)
    {
        _selection = rows.ToList();
        Current = _selection.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The job whose details are shown under the list.</summary>
    public JobRow? Current
    {
        get => _current;
        private set { if (Set(ref _current, value)) Raise(nameof(HasCurrent)); }
    }
    public bool HasCurrent => _current is not null;

    // ------------------------------------------------------------------ totals

    public string StateText { get; private set; } = "Idle";
    public string TotalsText { get; private set; } = "The queue is empty.";
    public double OverallProgress { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsEmpty => Rows.Count == 0;
    public int WaitingCount { get; private set; }
    public string TabHeader => WaitingCount > 0 ? $"Queue ({WaitingCount:N0})" : "Queue";
    public string PauseLabel => _s.Queue.State == QueueState.Paused ? "Resume" : "Pause";

    private void UpdateTotals()
    {
        var jobs = _s.Queue.Jobs;
        var settings = _s.Settings;
        int waiting = jobs.Count(j => j.Status == JobStatus.Queued);
        int running = jobs.Count(j => j.Status == JobStatus.Running);
        int done = jobs.Count(j => j.Status == JobStatus.Completed);
        int skipped = jobs.Count(j => j.Status == JobStatus.Skipped);
        int failed = jobs.Count(j => j.Status == JobStatus.Failed);
        long saved = jobs.Sum(j => j.Saved);

        StateText = _s.Queue.State switch
        {
            QueueState.Running => running > 1 ? $"Encoding {running} files" : "Encoding",
            QueueState.Paused => "Paused",
            QueueState.WaitingForSchedule => $"Waiting for the scheduled window ({settings.ScheduleStart}–{settings.ScheduleEnd})",
            QueueState.Stopping => "Stopping…",
            _ => waiting > 0 ? "Ready to start" : "Idle",
        };

        if (jobs.Count == 0) TotalsText = "The queue is empty. Add files from the Library tab.";
        else
        {
            var parts = new List<string> { $"{done:N0} of {jobs.Count:N0} done" };
            if (waiting > 0) parts.Add($"{waiting:N0} waiting");
            if (skipped > 0) parts.Add($"{skipped:N0} skipped");
            if (failed > 0) parts.Add($"{failed:N0} failed");
            if (saved > 0) parts.Add($"{Format.Bytes(saved)} saved");
            TotalsText = string.Join(" · ", parts);
        }

        // Weight progress by file size: a 40 GB film is not the same amount of work as a 200 MB episode.
        double totalBytes = jobs.Sum(j => (double)Math.Max(j.SourceSize, 1));
        double doneBytes = jobs.Sum(j => j.IsFinished ? Math.Max(j.SourceSize, 1) : j.Status == JobStatus.Running ? j.SourceSize * j.Progress : 0);
        OverallProgress = totalBytes > 0 ? doneBytes / totalBytes * 100 : 0;
        IsRunning = _s.Queue.IsActive;
        WaitingCount = waiting + running;

        foreach (var name in new[] { nameof(StateText), nameof(TotalsText), nameof(OverallProgress), nameof(IsRunning), nameof(IsEmpty), nameof(WaitingCount), nameof(TabHeader), nameof(PauseLabel) })
            Raise(name);
    }

    public void TogglePause()
    {
        if (_s.Queue.State == QueueState.Paused) _s.Queue.Resume();
        else _s.Queue.Pause();
    }

    private void ShowOutput()
    {
        if (_current is null) return;
        string target = File.Exists(_current.OutputPath) ? _current.OutputPath : _current.Job.SourcePath;
        try
        {
            if (File.Exists(target)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{target}\"" });
            else if (Directory.Exists(_current.Folder)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _current.Folder } });
        }
        catch (System.ComponentModel.Win32Exception) { /* Explorer unavailable */ }
    }

    private void OpenLog()
    {
        if (_current is null || !File.Exists(_current.LogPath)) return;
        try { Process.Start(new ProcessStartInfo(_current.LogPath) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no handler for .log */ }
    }
}
