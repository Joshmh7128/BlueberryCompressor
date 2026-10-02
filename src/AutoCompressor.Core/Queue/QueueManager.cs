using AutoCompressor.Core.Models;
using AutoCompressor.Core.Transcoding;
using AutoCompressor.Core.Util;

namespace AutoCompressor.Core.Queue;

public enum QueueState { Idle, Running, Paused, WaitingForSchedule, Stopping }

/// <summary>
/// The work list. Jobs are added at any time, survive restarts, and are worked through in order
/// by one or more encoders. Events are raised on worker threads; the UI marshals them itself.
/// </summary>
public sealed class QueueManager
{
    private sealed class Saved
    {
        public List<QueueJob> Jobs { get; set; } = [];
    }

    private readonly List<QueueJob> _jobs;
    private readonly object _gate = new();
    private readonly JobServices _services;
    private readonly JobProcessor _processor;
    private readonly Dictionary<string, (FfmpegRunner Runner, CancellationTokenSource Cancel)> _running = new();

    private CancellationTokenSource? _stop;
    private Task? _loop;
    private bool _paused;
    private bool _finishAfterCurrent;
    private readonly object _saveGate = new();

    /// <summary>The list itself changed: jobs added, removed or reordered.</summary>
    public event Action? ListChanged;
    /// <summary>One job's status or progress changed.</summary>
    public event Action<QueueJob>? JobChanged;
    public event Action? StateChanged;
    /// <summary>A job reached a final state.</summary>
    public event Action<QueueJob>? JobFinished;

    public QueueState State { get; private set; } = QueueState.Idle;

    public QueueManager(JobServices services)
    {
        _services = services;
        _processor = new JobProcessor(services);
        _jobs = JsonStore.Load<Saved>(AppPaths.Queue).Jobs;
        // Anything marked running was interrupted by the app closing; it starts over.
        foreach (var job in _jobs.Where(j => j.Status == JobStatus.Running))
        {
            job.Status = JobStatus.Queued;
            job.Message = "";
        }
    }

    public IReadOnlyList<QueueJob> Jobs
    {
        get { lock (_gate) return [.. _jobs]; }
    }

    public bool IsActive => State != QueueState.Idle;

    public bool Contains(string sourcePath)
    {
        lock (_gate) return _jobs.Any(j => !j.IsFinished && string.Equals(j.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Add jobs, ignoring files that are already waiting or running. Returns how many were added.</summary>
    public int Add(IEnumerable<QueueJob> jobs)
    {
        int added = 0;
        lock (_gate)
        {
            var waiting = _jobs.Where(j => !j.IsFinished).Select(j => j.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs)
            {
                if (!waiting.Add(job.SourcePath)) continue;
                _jobs.Add(job);
                added++;
            }
        }
        if (added > 0) { Save(); ListChanged?.Invoke(); }
        return added;
    }

    public void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            foreach (var id in set)
                if (_running.TryGetValue(id, out var running)) running.Cancel.Cancel();
            // Running jobs leave the list when their cancellation completes.
            _jobs.RemoveAll(j => set.Contains(j.Id) && j.Status != JobStatus.Running);
            foreach (var job in _jobs.Where(j => set.Contains(j.Id))) job.Message = "Cancelling";
        }
        Save();
        ListChanged?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.IsFinished);
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>Put failed, skipped or cancelled jobs back in line.</summary>
    public void Retry(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            foreach (var job in _jobs.Where(j => set.Contains(j.Id) && j.IsFinished && j.Status != JobStatus.Completed))
            {
                job.Status = JobStatus.Queued;
                job.Message = "";
                job.Notes.Clear();
                job.Progress = 0;
                job.FinishedUtc = null;
            }
        }
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>Move the given jobs one place up or down, keeping their relative order.</summary>
    public void Move(IEnumerable<string> ids, int direction)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            if (direction < 0)
            {
                for (int i = 1; i < _jobs.Count; i++)
                    if (set.Contains(_jobs[i].Id) && !set.Contains(_jobs[i - 1].Id))
                        (_jobs[i - 1], _jobs[i]) = (_jobs[i], _jobs[i - 1]);
            }
            else
            {
                for (int i = _jobs.Count - 2; i >= 0; i--)
                    if (set.Contains(_jobs[i].Id) && !set.Contains(_jobs[i + 1].Id))
                        (_jobs[i + 1], _jobs[i]) = (_jobs[i], _jobs[i + 1]);
            }
        }
        Save();
        ListChanged?.Invoke();
    }

    // ------------------------------------------------------------------ running

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                // Already running: "Start" while paused means resume.
                _finishAfterCurrent = false;
                if (_paused) ResumeLocked();
                return;
            }
            _paused = false;
            _finishAfterCurrent = false;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunLoopAsync(token));
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_loop is null or { IsCompleted: true } || _paused) return;
            _paused = true;
            foreach (var (runner, _) in _running.Values) runner.Pause();
            foreach (var job in _jobs.Where(j => j.Status == JobStatus.Running)) job.Paused = true;
        }
        SetState(QueueState.Paused);
        NotifyRunning();
    }

    public void Resume()
    {
        lock (_gate) ResumeLocked();
        NotifyRunning();
    }

    private void ResumeLocked()
    {
        if (!_paused) return;
        _paused = false;
        foreach (var (runner, _) in _running.Values) runner.Resume();
        foreach (var job in _jobs.Where(j => j.Status == JobStatus.Running)) job.Paused = false;
    }

    /// <summary>Stop now. Jobs in progress are abandoned and go back to waiting.</summary>
    public void Stop()
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            if (_loop is null or { IsCompleted: true }) return;
            stop = _stop;
        }
        SetState(QueueState.Stopping);
        stop?.Cancel();
    }

    /// <summary>Let the jobs in progress finish, then stop.</summary>
    public void StopAfterCurrent()
    {
        lock (_gate)
        {
            if (_loop is null or { IsCompleted: true }) return;
            _finishAfterCurrent = true;
            if (_paused) ResumeLocked();
        }
        SetState(QueueState.Stopping);
    }

    /// <summary>Stop and wait for the encoders to exit; used when the app closes.</summary>
    public async Task ShutdownAsync()
    {
        Task? loop;
        lock (_gate) loop = _loop;
        Stop();
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
            catch (TimeoutException) { /* give up waiting; the processes were already killed */ }
        }
        Save();
    }

    private void NotifyRunning()
    {
        List<QueueJob> running;
        lock (_gate) running = _jobs.Where(j => j.Status == JobStatus.Running).ToList();
        foreach (var job in running) JobChanged?.Invoke(job);
    }

    private void SetState(QueueState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    private async Task RunLoopAsync(CancellationToken stop)
    {
        var active = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                active.RemoveAll(t => t.IsCompleted);
                var settings = _services.Settings();
                bool inSchedule = settings.WithinSchedule(DateTime.Now);

                QueueJob? next = null;
                bool anyQueued;
                lock (_gate)
                {
                    anyQueued = _jobs.Any(j => j.Status == JobStatus.Queued);
                    if (!_paused && !_finishAfterCurrent && inSchedule && active.Count < Math.Clamp(settings.ParallelJobs, 1, 8))
                    {
                        next = _jobs.FirstOrDefault(j => j.Status == JobStatus.Queued);
                        if (next is not null)
                        {
                            next.Status = JobStatus.Running;
                            next.StartedUtc = DateTime.UtcNow;
                            next.Stage = "Starting";
                            next.Message = "";
                            next.Progress = 0;
                            _running[next.Id] = (new FfmpegRunner(), CancellationTokenSource.CreateLinkedTokenSource(stop));
                        }
                    }
                }

                if (next is not null)
                {
                    SetState(QueueState.Running);
                    JobChanged?.Invoke(next);
                    active.Add(RunJobAsync(next, stop));
                    continue;
                }

                if (active.Count == 0 && (!anyQueued || _finishAfterCurrent)) break;

                if (!_finishAfterCurrent)
                    SetState(_paused ? QueueState.Paused : active.Count == 0 && !inSchedule ? QueueState.WaitingForSchedule : QueueState.Running);

                // Wake when a job ends, or once a second to notice new jobs, a resume, or the schedule opening.
                var tick = Task.Delay(1000, CancellationToken.None);
                await Task.WhenAny(active.Append(tick)).ConfigureAwait(false);
            }
        }
        finally
        {
            await Task.WhenAll(active).ConfigureAwait(false);
            lock (_gate) { _paused = false; _finishAfterCurrent = false; }
            Save();
            SetState(QueueState.Idle);
        }
    }

    private async Task RunJobAsync(QueueJob job, CancellationToken stop)
    {
        FfmpegRunner runner;
        CancellationTokenSource cancel;
        lock (_gate) (runner, cancel) = _running[job.Id];
        bool removed = false;

        try
        {
            await _processor.RunAsync(job, runner, OnProgress, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (stop.IsCancellationRequested)
            {
                // The whole queue was stopped: this job simply waits its turn again.
                job.Status = JobStatus.Queued;
                job.Message = "";
            }
            else
            {
                removed = true; // the user removed this one job while it ran
            }
        }
        catch (JobFailedException ex)
        {
            Fail(job, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or HttpRequestException)
        {
            Fail(job, ex.Message);
        }
        finally
        {
            job.Stage = "";
            job.Paused = false;
            job.Eta = null;
            job.Fps = 0;
            job.Speed = 0;
            if (job.Status == JobStatus.Queued) job.Progress = 0;
            lock (_gate)
            {
                _running.Remove(job.Id);
                if (removed) _jobs.Remove(job);
            }
            cancel.Dispose();
        }

        Save();
        if (removed) ListChanged?.Invoke();
        else
        {
            JobChanged?.Invoke(job);
            if (job.IsFinished) JobFinished?.Invoke(job);
        }
    }

    private static void Fail(QueueJob job, string message)
    {
        job.Status = JobStatus.Failed;
        job.Message = message;
        job.FinishedUtc = DateTime.UtcNow;
    }

    private void OnProgress(QueueJob job)
    {
        JobChanged?.Invoke(job);
    }

    public void Save()
    {
        Saved snapshot;
        lock (_gate) snapshot = new Saved { Jobs = [.. _jobs] };
        try
        {
            lock (_saveGate) JsonStore.Save(AppPaths.Queue, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A running job changed while it was being written out; the next save will catch up.
        }
    }

    /// <summary>Build a job for a file, snapshotting the profile and output options in effect right now.</summary>
    public static QueueJob CreateJob(MediaFile file, Profile profile, OutputOptions output) => new()
    {
        SourcePath = file.Path,
        Root = file.Root,
        Kind = file.Kind,
        ContentType = file.Classification.Type,
        Profile = profile.Clone(),
        Output = output.Clone(),
        SourceSize = file.Size,
    };
}
