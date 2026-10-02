using AutoCompressor.Desktop.Mvvm;

namespace AutoCompressor.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private int _selectedTab;

    public AppServices Services { get; }
    public LibraryViewModel Library { get; }
    public QueueViewModel Queue { get; }
    public ProfilesViewModel Profiles { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(AppServices services)
    {
        Services = services;
        Library = new LibraryViewModel(services);
        Queue = new QueueViewModel(services);
        Profiles = new ProfilesViewModel(services);
        Settings = new SettingsViewModel(services);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            // Leaving the Profiles tab keeps any edits in progress.
            if (_selectedTab == 2 && value != 2 && Profiles.IsDirty) Profiles.Save();
            if (Set(ref _selectedTab, value) && value == 3) Settings.RefreshHistory();
        }
    }
}
