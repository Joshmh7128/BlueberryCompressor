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
public sealed class UiSelfTest(MainViewModel vm, Dispatcher dispatcher, string scanRoot)
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
    }
}
