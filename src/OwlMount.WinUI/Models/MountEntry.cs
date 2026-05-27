using CommunityToolkit.Mvvm.ComponentModel;

namespace OwlMount.WinUI;

public partial class MountEntry : ObservableObject
{
    public string DriveLetter { get; init; }
    public string Label { get; init; }
    public string Provider { get; init; }
    public string ProviderDisplay { get; init; }
    public string State { get; init; }
    public string Capacity { get; init; }
    public bool IsEnabled { get; init; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public MountEntry(string driveLetter, string label, string provider, string providerDisplay, string state, string capacity, bool isEnabled = true)
    {
        DriveLetter = driveLetter;
        Label = label;
        Provider = provider;
        ProviderDisplay = providerDisplay;
        State = state;
        Capacity = capacity;
        IsEnabled = isEnabled;
    }
}
