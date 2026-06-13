using CommunityToolkit.Mvvm.ComponentModel;

namespace OwlMount.WinUI;

public partial class MountEntry : ObservableObject
{
    public string DriveLetter { get; init; }
    public string Label { get; init; }
    public string Provider { get; init; }
    public string ProviderDisplay { get; init; }
    public string State { get; init; }
    public bool IsEnabled { get; init; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty]
    private long _capacityBytes;

    [ObservableProperty]
    private long? _freeBytes;

    public string CapacityDisplay => CapacityBytes > 0
        ? FreeBytes.HasValue
            ? $"Capacity: {FormatBytes(CapacityBytes)} total, {FormatBytes(FreeBytes.Value)} free"
            : $"Capacity: {FormatBytes(CapacityBytes)} total"
        : string.Empty;

    public MountEntry(
        string driveLetter,
        string label,
        string provider,
        string providerDisplay,
        string state,
        long capacityBytes,
        long? freeBytes,
        bool isEnabled = true)
    {
        DriveLetter = driveLetter;
        Label = label;
        Provider = provider;
        ProviderDisplay = providerDisplay;
        State = state;
        IsEnabled = isEnabled;
        CapacityBytes = capacityBytes;
        FreeBytes = freeBytes;
    }

    public void UpdateCapacity(long capacityBytes, long? freeBytes)
    {
        bool changed = SetProperty(ref _capacityBytes, capacityBytes);
        changed |= SetProperty(ref _freeBytes, freeBytes);
        if (changed)
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
