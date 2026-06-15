using CommunityToolkit.Mvvm.ComponentModel;

namespace OwlMount.WinUI;

public partial class MountEntry(
    string driveLetter,
    string label,
    string provider,
    string providerDisplay,
    string state,
    long capacityBytes,
    long? freeBytes,
    bool isEnabled = true) : ObservableObject
{
    public string DriveLetter { get; init; } = driveLetter;
    public string Label { get; init; } = label;
    public string Provider { get; init; } = provider;
    public string ProviderDisplay { get; init; } = providerDisplay;
    public string State { get; init; } = state;
    public bool IsEnabled { get; init; } = isEnabled;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty] public partial long CapacityBytes { get; set; } = capacityBytes;

    [ObservableProperty] public partial long FreeBytes { get; set; } = freeBytes ?? 0;

    public string CapacityDisplay => CapacityBytes > 0
        ? FreeBytes > 0
            ? $"Capacity: {FormatBytes(CapacityBytes)} total, {FormatBytes(FreeBytes)} free"
            : $"Capacity: {FormatBytes(CapacityBytes)} total"
        : string.Empty;

    public void UpdateCapacity(long capacityBytes, long? freeBytes)
    {
        CapacityBytes = capacityBytes;
        FreeBytes = freeBytes ?? 0;
        OnPropertyChanged(nameof(CapacityDisplay));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}
