using System.Windows.Controls;
using AutoCompressor.Desktop.ViewModels;

namespace AutoCompressor.Desktop.Views;

public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        (DataContext as QueueViewModel)?.SetSelection(JobsGrid.SelectedItems.OfType<JobRow>());

    /// <summary>Pick a profile by clicking it in the drop-down, as a mouse would (used by the UI self-test).</summary>
    public async Task ChooseProfile(object profile)
    {
        ProfileCombo.IsDropDownOpen = true;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ProfileCombo.ItemContainerGenerator.ContainerFromItem(profile) is ComboBoxItem item)
        {
            var device = System.Windows.Input.Mouse.PrimaryDevice;
            item.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(device, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
            item.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(device, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
        }
        else ProfileCombo.SelectedItem = profile;
        ProfileCombo.IsDropDownOpen = false;
    }

    /// <summary>Select a row by position (used when capturing screenshots).</summary>
    public void SelectRow(int index)
    {
        if (index < JobsGrid.Items.Count) JobsGrid.SelectedIndex = index;
    }
}
