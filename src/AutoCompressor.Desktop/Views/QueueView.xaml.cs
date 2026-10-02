using System.Windows.Controls;
using AutoCompressor.Desktop.ViewModels;

namespace AutoCompressor.Desktop.Views;

public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        (DataContext as QueueViewModel)?.SetSelection(JobsGrid.SelectedItems.OfType<JobRow>());

    /// <summary>Select a row by position (used when capturing screenshots).</summary>
    public void SelectRow(int index)
    {
        if (index < JobsGrid.Items.Count) JobsGrid.SelectedIndex = index;
    }
}
