namespace OwlMount.WinUI;

public sealed class MountEntry
{
    public string DriveLetter { get; init; }
    public string Label { get; init; }
    public string Provider { get; init; }
    public string State { get; init; }

    public MountEntry(string driveLetter, string label, string provider, string state)
    {
        DriveLetter = driveLetter;
        Label = label;
        Provider = provider;
        State = state;
    }
}
