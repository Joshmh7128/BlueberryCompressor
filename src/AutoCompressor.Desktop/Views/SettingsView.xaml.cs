using System.Windows;
using System.Windows.Controls;
using AutoCompressor.Desktop.ViewModels;
using Microsoft.Win32;

namespace AutoCompressor.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && PickFolder("Choose the folder for compressed files") is { } folder) Vm.OutputFolder = folder;
    }

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && PickFolder("Choose the folder originals are moved to") is { } folder) Vm.BackupFolder = folder;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && Vm is not null) Vm.FfmpegPath = dialog.FileName;
    }

    private void Password_LostFocus(object sender, RoutedEventArgs e)
    {
        // Only a typed password replaces the saved one; an empty box means "no change".
        if (PasswordBox.Password.Length == 0 || Vm is null) return;
        Vm.SetOpenSubtitlesPassword(PasswordBox.Password);
        PasswordBox.Clear();
    }
}
