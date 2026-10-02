using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoCompressor.Desktop.ViewModels;

namespace AutoCompressor.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closing;

    /// <summary>When set, every tab is rendered to a PNG in this folder and the app exits.</summary>
    public string? ScreenshotDirectory { get; init; }
    public string? StartupScanPath { get; init; }
    /// <summary>With screenshots: also queue everything that was scanned and run the queue to completion.</summary>
    public bool DemoRun { get; init; }
    /// <summary>When set, the scripted UI self-test runs, writes its results here, and the app exits.</summary>
    public string? SelfTestFile { get; init; }

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        bool Ask(string title, string text) =>
            MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.Library.Confirm = Ask;
        _vm.Queue.Confirm = Ask;
        _vm.Profiles.Confirm = Ask;
        _vm.Library.Alert = (title, text) => MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Finding out which GPU encoders work takes a few seconds the first time; it is remembered afterwards.
        var detect = _vm.Settings.DetectAsync(force: false);
        if (StartupScanPath is not null) await _vm.Library.AddPathsAsync([StartupScanPath]);
        if (SelfTestFile is not null)
        {
            await detect;
            int failed = await new UiSelfTest(_vm, Dispatcher, StartupScanPath ?? "").RunAsync(SelfTestFile);
            _closing = true;
            await _vm.Services.Queue.ShutdownAsync();
            Application.Current.Shutdown(failed == 0 ? 0 : 1);
            return;
        }
        if (ScreenshotDirectory is not null)
        {
            await detect;
            await CaptureScreenshotsAsync(ScreenshotDirectory);
            _closing = true;
            Close();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _vm.SelectedTab = 0;
        await _vm.Library.AddPathsAsync(paths);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;

        var queue = _vm.Services.Queue;
        if (queue.IsActive)
        {
            var answer = MessageBox.Show(this,
                "Files are still being compressed.\n\nIf you exit now, the file being encoded is abandoned and starts over next time. " +
                "Everything else stays in the queue.\n\nExit anyway?",
                "Exit AutoCompressor?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Hold the window open until the encoders have really stopped and their temp files are gone.
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        if (_vm.Profiles.IsDirty) _vm.Profiles.Save();
        await queue.ShutdownAsync();
        _vm.Library.SaveCaches();
        Close();
    }

    // ------------------------------------------------------------------ screenshots (for documentation and testing)

    private async Task CaptureScreenshotsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        async Task Settle(int ms = 350)
        {
            await Task.Delay(ms);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        await Settle(600);
        _vm.SelectedTab = 0;
        LibraryView.SelectRow(0);
        await Settle();
        Capture(Path.Combine(directory, "1-library-files.png"));

        _vm.Library.FolderView = true;
        await Settle();
        LibraryView.SelectRow(1);
        await Settle();
        Capture(Path.Combine(directory, "2-library-folders.png"));

        if (DemoRun)
        {
            // Drive the real thing end to end: queue everything, start, and capture it mid-run and when done.
            var queue = _vm.Services.Queue;
            _vm.Library.FolderView = false;
            _vm.Library.QueueAllCommand.Execute(null);
            _vm.SelectedTab = 1;
            await Settle();
            _vm.Queue.StartCommand.Execute(null);
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline && !queue.Jobs.Any(j => j.Status == AutoCompressor.Core.Queue.JobStatus.Running && j.Progress > 0.15)) await Task.Delay(150);
            QueueView.SelectRow(queue.Jobs.Count(j => j.IsFinished));
            await Settle(200);
            Capture(Path.Combine(directory, "3-queue-running.png"));

            deadline = DateTime.UtcNow.AddMinutes(15);
            while (DateTime.UtcNow < deadline && queue.IsActive) await Task.Delay(300);
            QueueView.SelectRow(0);
            await Settle();
            Capture(Path.Combine(directory, "3-queue-done.png"));

            _vm.SelectedTab = 0;
            await Settle();
            LibraryView.SelectRow(0);
            await Settle();
            Capture(Path.Combine(directory, "6-library-after.png"));
        }

        _vm.SelectedTab = 1;
        await Settle();
        QueueView.SelectRow(0);
        await Settle();
        Capture(Path.Combine(directory, "3-queue.png"));

        _vm.SelectedTab = 2;
        await Settle();
        Capture(Path.Combine(directory, "4-profiles.png"));

        _vm.SelectedTab = 3;
        await Settle();
        Capture(Path.Combine(directory, "5-settings.png"));
    }

    private void Capture(string path)
    {
        double scale = 1.0;
        int width = (int)(Root.ActualWidth * scale), height = (int)(Root.ActualHeight * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
