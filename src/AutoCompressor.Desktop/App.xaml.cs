using System.IO;
using System.Windows;
using System.Windows.Threading;
using AutoCompressor.Core.Util;
using AutoCompressor.Desktop.ViewModels;

namespace AutoCompressor.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Developer switches: --data-dir keeps a run away from the real settings; --scan opens with a
        // folder loaded; --screenshot renders every tab to PNG files and exits (--demo-run also runs the
        // queue first); --self-test runs the scripted UI test and writes its results to a file.
        string? Arg(string name) => e.Args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        if (Arg("--data-dir") is { } dataDir) AppPaths.OverrideDataDir(Path.GetFullPath(dataDir));
        string? screenshotDir = Arg("--screenshot");

        // Two copies would both write the same queue file and fight over the same encodes.
        // (string.GetHashCode differs between runs, so the name is built from a stable hash of the data folder.)
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..16];
        _singleInstance = new Mutex(true, "AutoCompressor-" + id, out bool first);
        if (!first)
        {
            MessageBox.Show("AutoCompressor is already running.", "AutoCompressor", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var services = new AppServices();
        var viewModel = new MainViewModel(services);
        var window = new MainWindow(viewModel)
        {
            ScreenshotDirectory = screenshotDir, StartupScanPath = Arg("--scan"), DemoRun = e.Args.Contains("--demo-run"),
            SelfTestFile = Arg("--self-test"),
        };
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.LogsDir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n"); }
        catch (IOException) { /* nothing more we can do */ }
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to the logs folder.", "AutoCompressor",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
