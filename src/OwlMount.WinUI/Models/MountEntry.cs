using CommunityToolkit.Mvvm.ComponentModel;

namespace OwlMount.WinUI;

public partial class MountEntry : ObservableObject
{
    public string DriveLetter { get; init; }
    public string Label { get; init; }
    public string Provider { get; init; }
    public string ProviderDisplay { get; init; }
    public string State { get; init; }
    public long CapacityBytes { get; init; }
    public bool IsEnabled { get; init; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string CapacityDisplay => CapacityBytes > 0 ? FormatBytes(CapacityBytes) : string.Empty;

    public MountEntry(string driveLetter, string label, string provider, string providerDisplay, string state, long capacityBytes, bool isEnabled = true)
    {
        DriveLetter = driveLetter;
        Label = label;
        Provider = provider;
        ProviderDisplay = providerDisplay;
        State = state;
        CapacityBytes = capacityBytes;
        IsEnabled = isEnabled;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"Capacity: {bytes / (1024.0 * 1024 * 1024):F1} GB total",
        >= 1024 * 1024 => $"Capacity: {bytes / (1024.0 * 1024):F1} MB total",
        >= 1024 => $"Capacity: {bytes / 1024.0:F1} KB total",
        _ => $"Capacity: {bytes} B total",
    };
}
