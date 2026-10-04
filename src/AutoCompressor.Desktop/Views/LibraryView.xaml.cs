using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCompressor.Core.Scanning;
using AutoCompressor.Desktop.ViewModels;
using Microsoft.Win32;

namespace AutoCompressor.Desktop.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as LibraryViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
            ResetSorting();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.FolderView)) ResetSorting();
    }

    /// <summary>
    /// The tree is ordered by the view model; a leftover column sort would scramble it. In the flat
    /// list the rows arrive largest first, so show that on the Size header.
    /// </summary>
    private void ResetSorting()
    {
        RowsGrid.Items.SortDescriptions.Clear();
        foreach (var column in RowsGrid.Columns) column.SortDirection = null;
        if (_vm is { FolderView: false }) SizeColumn.SortDirection = ListSortDirection.Descending;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose folders to scan (subfolders are included)", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && _vm is not null)
            await _vm.AddPathsAsync(dialog.FolderNames);
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        string Pattern(IEnumerable<string> extensions) => string.Join(';', extensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "Choose video or audio files",
            Multiselect = true,
            Filter = $"Media files|{Pattern(MediaExtensions.Video.Concat(MediaExtensions.Audio))}|Video|{Pattern(MediaExtensions.Video)}|Audio|{Pattern(MediaExtensions.Audio)}|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && _vm is not null)
            await _vm.AddPathsAsync(dialog.FileNames);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not { HasFiles: true }) return;
        var dialog = new SaveFileDialog { Title = "Export the file list", Filter = "CSV spreadsheet|*.csv", FileName = "media-files.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { _vm.ExportCsv(dialog.FileName); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), "The list could not be saved:\n\n" + ex.Message, "Export list", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm?.SetSelection(RowsGrid.SelectedItems.OfType<LibraryRow>());

    private void Expander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryRow row) _vm?.ToggleExpanded(row);
        e.Handled = true;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows react; double-clicking a header (to sort) or the scrollbar must not toggle anything.
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(RowsGrid, source) is not DataGridRow) return;
        if (RowsGrid.SelectedItem is LibraryRow { IsFolder: true } row && _vm is { FolderView: true }) _vm.ToggleExpanded(row);
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is not { FolderView: true } || RowsGrid.SelectedItem is not LibraryRow { IsFolder: true } row) return;
        // Arrow keys open and close folders, as in Explorer's tree.
        if ((e.Key == Key.Right && !row.IsExpanded) || (e.Key == Key.Left && row.IsExpanded))
        {
            _vm.ToggleExpanded(row);
            e.Handled = true;
        }
    }

    private void DismissNotice_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Notice = "";
    }

    /// <summary>Pick from the detail panel's drop-downs the way a user would (used by the UI self-test).</summary>
    public Task ChooseProfile(object profile) => ClickChoice(ProfileCombo, profile);
    public Task ChooseType(object type) => ClickChoice(TypeCombo, type);

    private async Task ClickChoice(ComboBox combo, object choice)
    {
        // Open the list and click the entry: the same route a mouse click takes.
        combo.IsDropDownOpen = true;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (combo.ItemContainerGenerator.ContainerFromItem(choice) is ComboBoxItem item)
        {
            var device = Mouse.PrimaryDevice;
            item.RaiseEvent(new MouseButtonEventArgs(device, Environment.TickCount, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
            item.RaiseEvent(new MouseButtonEventArgs(device, Environment.TickCount, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
        }
        else combo.SelectedItem = choice;
        combo.IsDropDownOpen = false;
    }

    /// <summary>Select a row by position (used when capturing screenshots).</summary>
    public void SelectRow(int index)
    {
        if (index < RowsGrid.Items.Count) RowsGrid.SelectedIndex = index;
    }
}
