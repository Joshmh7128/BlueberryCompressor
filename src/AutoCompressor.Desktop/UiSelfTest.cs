using System.IO;
using System.Text;
using System.Windows.Threading;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Queue;
using AutoCompressor.Desktop.ViewModels;

namespace AutoCompressor.Desktop;

/// <summary>
/// A scripted walk through the app's own view models, run with
/// <c>--data-dir X --scan FOLDER --self-test RESULTS.txt</c>. It checks the things unit tests on the
/// core library cannot: that the views' models react correctly to scanning, filtering, overriding,
/// and a real replace-mode queue run. The scanned folder is modified, so point it at a disposable copy.
/// </summary>
public sealed class UiSelfTest(MainViewModel vm, Dispatcher dispatcher, string scanRoot, Views.LibraryView view, Views.QueueView queueView)
{
    private readonly StringBuilder _log = new();
    private int _failed;

    private void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failed++;
        _log.AppendLine($"{(ok ? "ok  " : "FAIL")}  {name}{(ok || detail is null ? "" : "  -> " + detail)}");
    }

    private async Task Settle(int ms = 150)
    {
        await Task.Delay(ms);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    public async Task<int> RunAsync(string resultFile)
    {
        try
        {
            await RunStepsAsync();
        }
        catch (Exception ex)
        {
            _failed++;
            _log.AppendLine("FAIL  unexpected exception: " + ex);
        }
        _log.AppendLine(_failed == 0 ? "ALL PASSED" : $"{_failed} FAILED");
        await File.WriteAllTextAsync(resultFile, _log.ToString());
        return _failed;
    }

    private async Task RunStepsAsync()
    {
        var lib = vm.Library;
        var services = vm.Services;
        lib.Confirm = (_, _) => true;
        LibraryRow Row(string part) => lib.Rows.First(r => r.IsFile && r.Name.Contains(part, StringComparison.OrdinalIgnoreCase));

        // ---- flat list ----
        var rows = lib.Rows.ToList();
        Check("library lists files", rows.Count > 10 && rows.All(r => r.IsFile), rows.Count.ToString());
        Check("files are ordered largest first", rows.Zip(rows.Skip(1)).All(p => p.First.Size >= p.Second.Size));
        Check("every file was read", rows.All(r => r.File!.Probe is not null && r.State != RowState.Probing),
            string.Join(", ", rows.Where(r => r.File!.Probe is null).Select(r => r.Name)));
        Check("the largest file's bar is full and the smallest is short", rows[0].SizeFraction > 0.99 && rows[^1].SizeFraction < 0.2);

        (string Part, ContentType Type)[] expected =
        [
            ("Frieren - 05", ContentType.Anime), ("Mystery Show", ContentType.Anime), ("Seinfeld.S04E11", ContentType.Sitcom),
            ("Breaking.Bad", ContentType.Drama), ("Simpsons", ContentType.Animation), ("Daily.Show", ContentType.StillCam),
            ("Planet.Earth", ContentType.Documentary), ("Lecture 04", ContentType.StillCam), ("UFC 300", ContentType.Sports),
            ("Blade.Runner", ContentType.Cinematic), ("VID_2024", ContentType.General), ("Unknown.Show", ContentType.General),
            ("First Track", ContentType.Music), ("Episode 12", ContentType.Speech),
        ];
        foreach (var (part, type) in expected)
            Check($"recognised {part} as {type}", Row(part).Type == type, $"{Row(part).Type}: {string.Join(" | ", Row(part).File!.Classification.Reasons.Take(3))}");
        Check("each file has a profile of the right kind", rows.All(r => r.Profile is not null && r.Profile.Kind == r.File!.Kind));
        Check("a low-confidence guess is flagged", Row("Unknown.Show").IsGuess && !Row("Seinfeld.S04E11").IsGuess);

        // ---- every profile on every file ----
        int combinations = 0;
        foreach (var row in rows)
        {
            lib.SetSelection([row]);
            foreach (var choice in lib.ProfileChoices.ToList())
            {
                await view.ChooseProfile(choice); // through the real drop-down, as a user would
                await Settle(10);
                combinations++;
                if (row.Profile != choice) Check($"{choice.Name} can be chosen for {row.Name}", false, row.ProfileName);
            }
            foreach (var type in lib.TypeChoices.ToList())
            {
                await view.ChooseType(type);
                await Settle(10);
                if (row.Type != type.Type) Check($"{type.Label} can be chosen for {row.Name}", false, row.TypeText);
            }
            lib.ResetSelectionCommand.Execute(null);
        }
        Check("every profile can be chosen for every file", combinations > rows.Count, combinations.ToString());
        Check("and resetting clears all of those choices", services.Overrides.Count == 0 && Row("Seinfeld.S04E11").ProfileName == "Sitcom", services.Overrides.Count.ToString());

        // ---- filters ----
        lib.Filter = "seinfeld";
        Check("text filter narrows the list", lib.Rows.Count == 2, lib.Rows.Count.ToString());
        lib.Filter = "";
        lib.TypeFilter = lib.TypeFilters.First(t => t.Type == ContentType.Anime);
        Check("type filter narrows the list", lib.Rows.Count == 3 && lib.Rows.All(r => r.Type == ContentType.Anime), lib.Rows.Count.ToString());
        lib.TypeFilter = lib.TypeFilters[0];
        Check("clearing filters restores the list", lib.Rows.Count == rows.Count);

        // ---- folder view ----
        lib.FolderView = true;
        await Settle();
        var root = lib.Rows[0];
        Check("folder view starts with the scanned folder, expanded", root is { IsFolder: true, Depth: 0, IsExpanded: true });
        Check("the root's size is the sum of its files", root.Size == rows.Sum(r => r.Size), $"{root.Size} vs {rows.Sum(r => r.Size)}");
        var level1 = lib.Rows.Where(r => r.Depth == 1).ToList();
        Check("children are ordered largest first", level1.Zip(level1.Skip(1)).All(p => p.First.Size >= p.Second.Size));
        Check("child bars show share of the parent", level1.All(r => Math.Abs(r.SizeFraction - (double)r.Size / root.Size) < 0.0001));
        var tv = lib.Rows.First(r => r.IsFolder && r.Name == "TV");
        int before = lib.Rows.Count;
        lib.ToggleExpanded(tv);
        int tvIndex = lib.Rows.IndexOf(tv);
        Check("expanding a folder inserts its children beneath it", lib.Rows.Count == before + tv.Folder!.Folders.Count + tv.Folder.Files.Count
                                                                     && lib.Rows[tvIndex + 1].Depth == 2);
        lib.ToggleExpanded(tv);
        Check("collapsing removes them again", lib.Rows.Count == before);
        lib.ExpandAllCommand.Execute(null);
        Check("expand all shows every file and folder", lib.Rows.Count(r => r.IsFile) == rows.Count);
        lib.Filter = "frieren";
        Check("filtering the tree keeps only matching branches", lib.Rows.Count(r => r.IsFile) == 2 && lib.Rows.All(r => r.IsFile || r.Folder!.AllFiles().Any(f => f.Name.Contains("Frieren"))),
            string.Join(", ", lib.Rows.Select(r => r.Name)));
        lib.Filter = "";

        // Selecting a folder describes everything inside it.
        lib.SetSelection([tv]);
        Check("selecting a folder summarises its contents", lib.SelectionSubtitle.StartsWith($"{tv.Folder.MediaCount} files") && lib.Reasons.Count >= 3 && lib.PlanSummary.Contains("would be compressed"),
            $"{lib.SelectionSubtitle} / {lib.PlanSummary}");
        lib.FolderView = false;
        await Settle();

        // ---- overrides ----
        var unknown = Row("Unknown.Show");
        lib.SetSelection([unknown]);
        Check("a single selection explains itself", lib.Reasons.Count > 0 && lib.StreamLines.Count >= 2 && lib.PlanSummary.Contains("General"), lib.PlanSummary);
        Check("the plan preview includes the ffmpeg command", lib.PlanCommand.Contains("libx265") && lib.PlanCommand.Contains("-map"));
        lib.SelectedType = lib.TypeChoices.First(t => t.Type == ContentType.Sitcom);
        Check("changing the type changes the profile", unknown.Type == ContentType.Sitcom && unknown.ProfileName == "Sitcom" && unknown.ConfidenceText == "Manual",
            $"{unknown.Type} {unknown.ProfileName} {unknown.ConfidenceText}");
        Check("the choice is remembered", services.Overrides.TryGetValue(unknown.FullPath, out var o) && o.Type == ContentType.Sitcom);
        lib.SelectedProfile = lib.ProfileChoices.First(p => p.Id == "anime");
        Check("a profile can be chosen directly", unknown.ProfileName == "Anime" && unknown.ProfileIsManual);
        lib.ResetSelectionCommand.Execute(null);
        Check("back to automatic undoes both", unknown.Type == ContentType.General && unknown.ProfileName == "General" && !services.Overrides.ContainsKey(unknown.FullPath),
            $"{unknown.Type} {unknown.ProfileName}");

        // Changing the type-to-profile mapping re-assigns files that follow it.
        var mapping = vm.Profiles.Mappings.First(m => m.Type == ContentType.Sports);
        var original = mapping.Selected;
        mapping.Selected = mapping.Choices.First(p => p.Id == "general");
        await Settle();
        Check("remapping a content type updates the library", Row("UFC 300").ProfileName == "General", Row("UFC 300").ProfileName);
        mapping.Selected = original;
        await Settle();
        Check("and can be put back", Row("UFC 300").ProfileName == "Sports");

        // ---- adding, rescanning, removing ----
        int total = rows.Count;
        await lib.AddPathsAsync([Path.Combine(scanRoot, "TV")]);
        Check("adding a folder that is already covered changes nothing", lib.Rows.Count == total, lib.Rows.Count.ToString());

        string looseDir = Path.Combine(Path.GetDirectoryName(scanRoot)!, "Loose");
        Directory.CreateDirectory(looseDir);
        string loose = Path.Combine(looseDir, "holiday clip.mp4");
        File.Copy(Row("VID_2024").FullPath, loose, overwrite: true);
        await lib.AddPathsAsync([loose]);
        Check("a single file can be added on its own", lib.Rows.Count == total + 1 && Row("holiday clip").File!.Probe is not null, lib.Rows.Count.ToString());
        await lib.AddPathsAsync([loose]);
        Check("adding the same file twice does not duplicate it", lib.Rows.Count == total + 1, lib.Rows.Count.ToString());
        lib.FolderView = true;
        Check("the single file gets its own top-level folder entry", lib.Rows.Count(r => r.IsFolder && r.Depth == 0) == 2);
        lib.FolderView = false;

        string csv = Path.Combine(looseDir, "export.csv");
        int exported = lib.ExportCsv(csv);
        var csvLines = File.ReadAllLines(csv);
        Check("the list exports to CSV, largest first", exported == total + 1 && csvLines.Length == total + 2 && csvLines[0].StartsWith("Path,Size") && csvLines[1].Contains("Blade.Runner"),
            $"{exported} rows, {csvLines.Length} lines");
        lib.Notice = "";

        lib.RescanCommand.Execute(null);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && (lib.IsBusy || lib.Rows.Count == 0)) await Task.Delay(100);
        await Settle(300);
        Check("rescanning finds the same files again", lib.Rows.Count == total + 1 && lib.Rows.All(r => r.File!.Probe is not null), lib.Rows.Count.ToString());

        lib.SetSelection([Row("holiday clip")]);
        lib.RemoveSelectedCommand.Execute(null);
        Check("removing from the list leaves the file on disk", lib.Rows.Count == total && File.Exists(loose), lib.Rows.Count.ToString());

        // ---- profiles ----
        var profiles = vm.Profiles;
        profiles.Confirm = (_, _) => true;
        int profileCount = profiles.Profiles.Count;
        profiles.Selected = profiles.Profiles.First(p => p.Id == "sitcom");
        profiles.DuplicateCommand.Execute(null);
        var copy = profiles.Selected!;
        Check("duplicating a profile adds an editable custom copy", profiles.Profiles.Count == profileCount + 1 && copy is { BuiltIn: false, Name: "Sitcom (copy)" } && copy.Id != "sitcom",
            $"{copy.Name} {copy.BuiltIn}");
        profiles.Editing!.Quality = 30;
        profiles.Editing.Name = "Tiny sitcom";
        await Settle(900);
        Check("edits are noticed as unsaved changes", profiles.IsDirty);
        profiles.SaveCommand.Execute(null);
        await Settle();
        var reloaded = new AutoCompressor.Core.Profiles.ProfileStore().Find(copy.Id);
        Check("saving writes the profile to disk", reloaded is { Quality: 30, Name: "Tiny sitcom" } && !profiles.IsDirty, $"{reloaded?.Name} {reloaded?.Quality}");
        lib.SetSelection([Row("Seinfeld.S04E12")]);
        Check("the new profile can be chosen in the library", lib.ProfileChoices.Any(p => p.Id == copy.Id));
        lib.SelectedProfile = lib.ProfileChoices.First(p => p.Id == copy.Id);
        Check("and its settings drive the plan", lib.PlanSummary.Contains("Tiny sitcom") && lib.PlanSummary.Contains("CRF 30"), lib.PlanSummary);
        profiles.Selected = profiles.Profiles.First(p => p.Id == copy.Id);
        profiles.DeleteCommand.Execute(null);
        await Settle();
        Check("deleting a custom profile sends its files back to automatic", profiles.Profiles.Count == profileCount && Row("Seinfeld.S04E12").ProfileName == "Sitcom",
            $"{profiles.Profiles.Count} {Row("Seinfeld.S04E12").ProfileName}");
        profiles.Selected = profiles.Profiles.First(p => p.Id == "anime");
        Check("built-in profiles cannot be deleted", !profiles.DeleteCommand.CanExecute(null) && profiles.ResetCommand.CanExecute(null));

        // ---- settings ----
        var settings = vm.Settings;
        settings.EncoderPolicy = EncoderPolicy.Hardware;
        await Settle();
        lib.SetSelection([Row("Breaking.Bad")]);
        bool hasGpu = services.Encoders.Usable.Any(e => e.Hardware && e.Codec == VideoCodec.HEVC);
        Check("switching to hardware changes the plan when a GPU encoder exists", !hasGpu || !lib.PlanSummary.Contains("libx265"), lib.PlanSummary);
        settings.EncoderPolicy = EncoderPolicy.Software;
        await Settle();
        Check("and switching back restores software", lib.PlanSummary.Contains("libx265"), lib.PlanSummary);
        Check("settings are written to disk as they change",
            AutoCompressor.Core.Util.JsonStore.Load<AppSettings>(AutoCompressor.Core.Util.AppPaths.Settings).EncoderPolicy == EncoderPolicy.Software);
        Check("the encoder list is populated", settings.Encoders.Count(e => e.Usable) >= 2 && settings.HardwareSummary.Contains("HEVC"), settings.HardwareSummary);

        // ---- size estimates ----
        Check("files that would be compressed show an estimated size below their own", Row("Breaking.Bad") is { HasEstimate: true } bb && bb.EstimatedSize < bb.Size && bb.EstimateText.StartsWith('~'),
            Row("Breaking.Bad").EstimateText);
        Check("a file that would be skipped shows no estimate", !Row("Small Indie Film").HasEstimate && Row("Small Indie Film").EstimateText == "");
        Check("the summary totals the estimate", lib.Summary.Contains("after compression"), lib.Summary);
        lib.FolderView = true;
        var rootRow = lib.Rows[0];
        Check("a folder's estimate is the sum over its files", rootRow.HasEstimate && rootRow.EstimatedSize == rootRow.Folder!.AllFiles().Sum(f => f.EstimatedSize ?? f.Size)
                                                               && rootRow.EstimatedSize < rootRow.Size, rootRow.EstimateText);
        lib.FolderView = false;
        var measuredRow = Row("Seinfeld.S04E12");
        lib.SetSelection([measuredRow]);
        long rough = measuredRow.EstimatedSize;
        Check("the plan text includes the estimate", lib.PlanSummary.Contains("Estimated size: ~") && lib.CanMeasure, lib.PlanSummary);
        lib.MeasureCommand.Execute(null);
        var measureUntil = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < measureUntil && !measuredRow.File!.EstimateMeasured && lib.MeasureStatus != "" ) await Task.Delay(100);
        await Settle(300);
        Check("measuring replaces the rough figure with one from test encodes", measuredRow.File!.EstimateMeasured && !measuredRow.EstimateText.StartsWith('~')
                                                                                 && lib.PlanSummary.Contains("Measured size"), $"{measuredRow.EstimateText} / {lib.MeasureStatus} / {lib.PlanSummary}");
        _log.AppendLine($"      Seinfeld.S04E12: {measuredRow.Size:N0} bytes, rough estimate {rough:N0}, measured {measuredRow.EstimatedSize:N0}");
        settings.EncoderPolicy = EncoderPolicy.Hardware;
        await Settle();
        Check("a measurement is dropped when the settings it was made for change", !hasGpu || !measuredRow.File.EstimateMeasured);
        settings.EncoderPolicy = EncoderPolicy.Software;
        await Settle();
        Check("and comes back when they are restored", measuredRow.File.EstimateMeasured);
        var estimates = new[] { "Lecture 04", "Seinfeld.S04E11" }.ToDictionary(n => n, n => Row(n).EstimatedSize);

        // ---- a real queue run in replace mode ----
        string backup = Path.Combine(AutoCompressor.Core.Util.AppPaths.DataDir, "selftest-backup");
        services.Settings.Output.Mode = OutputMode.Replace;
        services.Settings.Output.Disposal = OriginalDisposal.BackupFolder;
        services.Settings.Output.BackupFolder = backup;
        services.SaveSettings();

        var lecture = Row("Lecture 04");            // .mp4, becomes .mkv
        var sitcom = Row("Seinfeld.S04E11");        // .mkv, stays .mkv
        var small = Row("Small Indie Film");        // already efficient: must be skipped untouched
        string lecturePath = lecture.FullPath, sitcomPath = sitcom.FullPath, smallPath = small.FullPath;
        long lectureSize = lecture.Size, sitcomSize = sitcom.Size, smallSize = small.Size;
        long lectureFolderSize = lecture.File!.Folder!.MediaSize, totalBefore = lib.Rows.Sum(r => r.Size);

        lib.SetSelection([lecture, sitcom, small]);
        lib.QueueSelectedCommand.Execute(null);
        Check("three jobs were queued", services.Queue.Jobs.Count == 3 && lecture.State == RowState.Queued, $"{services.Queue.Jobs.Count} {lecture.State}");
        Check("jobs carry the replace setting", services.Queue.Jobs.All(j => j.Output.Mode == OutputMode.Replace));
        lib.QueueSelectedCommand.Execute(null);
        Check("queueing the same files again adds nothing", services.Queue.Jobs.Count == 3);

        vm.Queue.StartCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        bool sawRunning = false;
        while (DateTime.UtcNow < deadline && (services.Queue.IsActive || services.Queue.Jobs.Any(j => !j.IsFinished)))
        {
            sawRunning |= lib.Rows.Any(r => r.State == RowState.Running);
            await Task.Delay(100);
        }
        await Settle(400);

        var jobs = services.Queue.Jobs;
        Check("the queue finished", jobs.All(j => j.IsFinished) && !services.Queue.IsActive, string.Join(", ", jobs.Select(j => $"{j.Status}: {j.Message}")));
        Check("library rows showed the encode in progress", sawRunning);
        Check("the .mp4 was replaced by an .mkv and its row follows it",
            lecture.FullPath.EndsWith("Lecture 04 - Eigenvalues.mkv") && File.Exists(lecture.FullPath) && !File.Exists(lecturePath) && lecture.State == RowState.Done,
            $"{lecture.FullPath} {lecture.State}");
        Check("the .mkv was replaced in place", sitcom.FullPath == sitcomPath && File.Exists(sitcomPath) && new FileInfo(sitcomPath).Length < sitcomSize && sitcom.State == RowState.Done,
            $"{sitcom.State} {new FileInfo(sitcomPath).Length} vs {sitcomSize}");
        _log.AppendLine($"      Lecture 04: estimated {estimates["Lecture 04"]:N0}, actual {lecture.Size:N0}; Seinfeld.S04E11: estimated {estimates["Seinfeld.S04E11"]:N0}, actual {sitcom.Size:N0}");
        Check("finished files no longer show an estimate", !lecture.HasEstimate && !sitcom.HasEstimate);
        Check("row sizes show the new, smaller files", lecture.Size < lectureSize && sitcom.Size < sitcomSize && sitcom.Size == new FileInfo(sitcomPath).Length);
        Check("folder sizes shrank by the same amount", lecture.File.Folder.MediaSize == lectureFolderSize - (lectureSize - lecture.Size),
            $"{lecture.File.Folder.MediaSize} vs {lectureFolderSize - (lectureSize - lecture.Size)}");
        Check("the efficient file was skipped and left untouched", small.State == RowState.Skipped && File.Exists(smallPath) && new FileInfo(smallPath).Length == smallSize,
            $"{small.State}: {small.StatusTip}");
        Check("originals were moved to the backup folder", Directory.Exists(backup) && Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Order().SequenceEqual(new[] { Path.GetFileName(lecturePath), Path.GetFileName(sitcomPath) }.Order()),
            Directory.Exists(backup) ? string.Join(", ", Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Select(Path.GetFileName)) : "no backup folder");
        Check("no temp files were left behind", !Directory.EnumerateFiles(Path.GetDirectoryName(lecture.FullPath)!, "*.ac-tmp*").Any());

        var queueRows = vm.Queue.Rows;
        Check("queue rows report results", queueRows.Count == 3 && queueRows.Count(r => r.Status == JobStatus.Completed && r.SavedText.StartsWith('−')) == 2
                                              && queueRows.Count(r => r.Status == JobStatus.Skipped) == 1,
            string.Join(", ", queueRows.Select(r => $"{r.StatusText}/{r.SavedText}")));
        Check("totals add up", vm.Queue.TotalsText.Contains("2 of 3 done") && vm.Queue.TotalsText.Contains("1 skipped") && vm.Queue.TotalsText.Contains("saved"), vm.Queue.TotalsText);

        await Settle(1500); // let the replaced files be re-read
        lib.SetSelection([sitcom]);
        Check("a replaced file is now known to be compressed and would be skipped", lib.PlanIsSkip && lib.PlanSummary.Contains("Already compressed"), lib.PlanSummary);
        lib.HideCompressed = true;
        Check("hide compressed removes finished files from view", !lib.Rows.Contains(sitcom) && !lib.Rows.Contains(lecture) && lib.Rows.Contains(small));
        lib.HideCompressed = false;

        vm.Queue.ClearFinishedCommand.Execute(null);
        await Settle();
        Check("clearing finished jobs empties the queue", vm.Queue.Rows.Count == 0 && vm.Queue.IsEmpty);

        services.Settings.Output.Mode = OutputMode.NextToOriginal;
        services.SaveSettings();

        // ---- compress beside the original, then swap the copy in ----
        var earth = Row("Planet.Earth");
        string earthPath = earth.FullPath;
        long earthSize = earth.Size;
        lib.SetSelection([earth]);
        lib.QueueSelectedCommand.Execute(null);
        await Settle();
        var queued = vm.Queue.Rows.Single();
        Check("the queue shows where the result will go", queued.OutputModeText == "Beside original" && !queued.ReplacesOriginal && queued.OutputDetail.Contains("original is kept"),
            $"{queued.OutputModeText} / {queued.OutputDetail}");
        // Change the waiting job's profile from the queue, by clicking through every choice.
        vm.SelectedTab = 1;
        await Settle(300);
        queueView.SelectRow(0);
        await Settle();
        Check("a waiting job's profile can be changed from the queue", vm.Queue.CanChangeProfile && vm.Queue.SelectedProfile?.Id == "documentary" && vm.Queue.ProfileChoices.Count > 5,
            $"{vm.Queue.CanChangeProfile} {vm.Queue.SelectedProfile?.Id}");
        foreach (var choice in vm.Queue.ProfileChoices.ToList())
        {
            await queueView.ChooseProfile(choice);
            await Settle(30);
            if (services.Queue.Jobs[0].Profile.Id != choice.Id || queued.ProfileName != choice.Name)
                Check($"queue profile {choice.Name} applied", false, $"{services.Queue.Jobs[0].Profile.Id} / {queued.ProfileName}");
        }
        await queueView.ChooseProfile(vm.Queue.ProfileChoices.First(p => p.Id == "stillcam"));
        await Settle();
        Check("the job, its row and the saved queue all carry the new profile", services.Queue.Jobs[0].Profile.Id == "stillcam" && queued.ProfileName == "Talk / Reality / Lecture"
            && new AutoCompressor.Core.Queue.QueueManager(new JobServices { Settings = () => services.Settings, Tools = () => services.Tools, Encoders = () => services.Encoders, History = services.History })
                .Jobs[0].Profile.Id == "stillcam", queued.ProfileName);

        vm.Queue.StartCommand.Execute(null);
        deadline = DateTime.UtcNow.AddMinutes(5);
        bool lockedWhileRunning = false;
        while (DateTime.UtcNow < deadline && (services.Queue.IsActive || services.Queue.Jobs.Any(j => !j.IsFinished)))
        {
            if (services.Queue.Jobs[0].Status == JobStatus.Running && !vm.Queue.CanChangeProfile) lockedWhileRunning = true;
            await Task.Delay(100);
        }
        await Settle(400);
        Check("the profile is locked while the job runs and once it is done", lockedWhileRunning && !vm.Queue.CanChangeProfile);
        Check("the job was encoded with the profile chosen in the queue", services.Queue.Jobs[0].Summary?.Contains("CRF 28") == true, services.Queue.Jobs[0].Summary);
        vm.SelectedTab = 0;
        string copyPath = services.Queue.Jobs[0].OutputPath ?? "";
        Check("a copy was written beside the original", copyPath.EndsWith(".compressed.mkv") && File.Exists(copyPath) && File.Exists(earthPath), copyPath);
        Check("the queue offers to swap it in", vm.Queue.CleanupCount == 1 && vm.Queue.CleanupCommand.CanExecute(null) && vm.Queue.CleanupLabel.Contains("1 compressed copy"), vm.Queue.CleanupLabel);

        string informed = "";
        vm.Queue.Confirm = (_, _) => true;
        vm.Queue.Inform = (_, text) => informed = text;
        vm.Queue.CleanupCommand.Execute(null);
        deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline && (informed.Length == 0 || lib.IsBusy)) await Task.Delay(100);
        await Settle(800);
        while (DateTime.UtcNow < deadline && lib.IsBusy) await Task.Delay(100);
        Check("the original was removed and the copy now has its name", File.Exists(earthPath) && !File.Exists(copyPath) && new FileInfo(earthPath).Length < earthSize && informed.Contains("1 original"),
            informed);
        Check("nothing is left to swap", vm.Queue.CleanupCount == 0);
        var earthAfter = Row("Planet.Earth");
        Check("the library was rescanned and shows the file as compressed", earthAfter.FullPath == earthPath && earthAfter.State == RowState.Compressed && earthAfter.Size < earthSize
                                                                          && lib.Rows.Count(r => r.Name.Contains("Planet.Earth")) == 1, $"{earthAfter.State} {earthAfter.Size}");
    }
}
