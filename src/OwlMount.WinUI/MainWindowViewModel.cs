using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using OwlMount.WinUI.Services;

namespace OwlMount.WinUI;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly MountService _mountService;
    private readonly Action _exitAction;
    private Func<string?> _s3SecretProvider;
    private bool _isInitializing = true;
    private bool? _readOnlyBeforeArchive;

    public ObservableCollection<MountEntry> Mounts { get; } = [];
    public IReadOnlyList<string> Providers { get; } = ["memory", "archive", "local", "kubo-mfs", "kubo-ipfs", "kubo-ipns", "s3", "nfs"];
    public IReadOnlyList<string> Backends { get; } = ["winfsp", "projfs"];

    public IAsyncRelayCommand MountCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand ExitCommand { get; }

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string selectedProvider = "memory";
    [ObservableProperty] private string selectedBackend = "winfsp";
    [ObservableProperty] private string driveLetters = "M";
    [ObservableProperty] private string? label;
    [ObservableProperty] private bool readOnly;
    [ObservableProperty] private bool readOnlyEnabled = true;
    [ObservableProperty] private bool saveMountConfigurations;
    [ObservableProperty] private bool persistMemoryFsOnExit;
    [ObservableProperty] private string persistMemoryFsPath = string.Empty;
    [ObservableProperty] private bool persistMemoryFsPathEnabled;
    [ObservableProperty] private string? path;
    [ObservableProperty] private string? archiveFile;
    [ObservableProperty] private string? apiUrl;
    [ObservableProperty] private string? cid;
    [ObservableProperty] private string? ipns;
    [ObservableProperty] private string? s3Bucket;
    [ObservableProperty] private string? s3Prefix;
    [ObservableProperty] private string? s3AccessKey;
    [ObservableProperty] private string? s3Region;
    [ObservableProperty] private string? s3Endpoint;
    [ObservableProperty] private string? nfsHost;
    [ObservableProperty] private string? nfsExport;
    [ObservableProperty] private string nfsPath = "/";
    [ObservableProperty] private Visibility localOrArchiveVisibility = Visibility.Visible;
    [ObservableProperty] private Visibility kuboVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility s3Visibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility nfsVisibility = Visibility.Collapsed;

    public MainWindowViewModel(MountService mountService, Action exitAction, Func<string?>? s3SecretProvider = null)
    {
        _mountService = mountService;
        _exitAction = exitAction;
        _s3SecretProvider = s3SecretProvider ?? (() => null);

        SaveMountConfigurations = _mountService.SaveMountPointConfigurations;
        PersistMemoryFsOnExit = _mountService.PersistMemoryFileSystemOnExit;
        PersistMemoryFsPath = _mountService.MemoryFileSystemPersistPath;
        PersistMemoryFsPathEnabled = PersistMemoryFsOnExit;

        MountCommand = new AsyncRelayCommand(MountAsync);
        RefreshCommand = new RelayCommand(RefreshMountsFromService);
        ExitCommand = new RelayCommand(_exitAction);

        RefreshMountsFromService();
        UpdateProviderPanels();
        _isInitializing = false;
    }

    public void RefreshMountsFromService()
    {
        Mounts.Clear();
        foreach (ActiveMount m in _mountService.ActiveMounts)
        {
            string state = m.IsReadOnly ? "Running (R/O)" : "Running";
            Mounts.Add(new MountEntry(m.DriveLetter, m.Label, m.Provider, state));
        }

        if (Mounts.Count == 0)
            SetStatus("No active mounts.");
    }

    public void SetStatus(string message) => StatusMessage = message;

    public void SetS3SecretProvider(Func<string?> provider) => _s3SecretProvider = provider;

    public void UnmountSelected(IReadOnlyList<MountEntry> selected)
    {
        if (selected.Count == 0)
        {
            SetStatus("Select one or more active mounts first.");
            return;
        }

        foreach (MountEntry mount in selected)
            _mountService.Unmount(mount.DriveLetter);

        RefreshMountsFromService();
        SetStatus(selected.Count == 1
            ? $"Unmounted {selected[0].DriveLetter}."
            : $"Unmounted {selected.Count} drive(s).");
    }

    private async Task MountAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProvider) || string.IsNullOrWhiteSpace(SelectedBackend))
        {
            SetStatus("Provider and backend are required.");
            return;
        }

        IReadOnlyList<string> letters = ParseDriveLetters(DriveLetters);
        if (letters.Count == 0)
        {
            SetStatus("Enter one or more drive letters (e.g. M or M,R,X).");
            return;
        }

        int successCount = 0;
        var failures = new List<string>();

        foreach (string letter in letters)
        {
            var opts = new ProviderOptions
            {
                Provider = SelectedProvider,
                Backend = SelectedBackend,
                Letter = letter,
                Label = NullIfBlank(Label),
                ForceReadOnly = ReadOnly,
                Path = NullIfBlank(Path),
                ArchiveFile = NullIfBlank(ArchiveFile),
                ApiUrl = NullIfBlank(ApiUrl),
                Cid = NullIfBlank(Cid),
                IpnsAddress = NullIfBlank(Ipns),
                S3Bucket = NullIfBlank(S3Bucket),
                S3Prefix = NullIfBlank(S3Prefix),
                S3AccessKey = NullIfBlank(S3AccessKey),
                S3SecretKey = NullIfBlank(_s3SecretProvider()),
                S3Region = NullIfBlank(S3Region),
                S3Endpoint = NullIfBlank(S3Endpoint),
                NfsHost = NullIfBlank(NfsHost),
                NfsExport = NullIfBlank(NfsExport),
                NfsPath = NullIfBlank(NfsPath) ?? "/",
            };

            var (success, error) = await _mountService.MountAsync(opts);
            if (success)
                successCount++;
            else
                failures.Add($"{letter}: {error ?? "Unknown error"}");
        }

        RefreshMountsFromService();

        if (failures.Count == 0)
            SetStatus($"Mounted {successCount} drive(s): {string.Join(", ", letters.Select(l => l + ":"))}.");
        else if (successCount == 0)
            SetStatus($"All mounts failed: {string.Join(" | ", failures)}");
        else
            SetStatus($"Mounted {successCount} drive(s); failures: {string.Join(" | ", failures)}");
    }

    partial void OnSelectedProviderChanged(string value) => UpdateProviderPanels();

    partial void OnSaveMountConfigurationsChanged(bool value)
    {
        _mountService.SetSaveMountPointConfigurations(value);
        if (_isInitializing)
            return;

        SetStatus(value
            ? "Mount point configurations will be saved."
            : "Mount point configurations will remain in-memory only.");
    }

    partial void OnPersistMemoryFsOnExitChanged(bool value)
    {
        PersistMemoryFsPathEnabled = value;
        _mountService.SetMemoryFileSystemPersistenceOptions(value, PersistMemoryFsPath);
        if (_isInitializing)
            return;

        SetStatus(value
            ? $"In-memory filesystem files will be exported on exit to: {_mountService.MemoryFileSystemPersistPath}"
            : "In-memory filesystem export on exit is disabled.");
    }

    partial void OnPersistMemoryFsPathChanged(string value)
    {
        _mountService.SetMemoryFileSystemPersistenceOptions(PersistMemoryFsOnExit, value);
    }

    private void UpdateProviderPanels()
    {
        string provider = SelectedProvider;

        LocalOrArchiveVisibility = provider is "local" or "archive" or "kubo-mfs"
            ? Visibility.Visible : Visibility.Collapsed;
        KuboVisibility = provider is "kubo-mfs" or "kubo-ipfs" or "kubo-ipns"
            ? Visibility.Visible : Visibility.Collapsed;
        S3Visibility = provider == "s3"
            ? Visibility.Visible : Visibility.Collapsed;
        NfsVisibility = provider == "nfs"
            ? Visibility.Visible : Visibility.Collapsed;

        if (provider == "archive")
        {
            if (_readOnlyBeforeArchive is null)
                _readOnlyBeforeArchive = ReadOnly;

            ReadOnly = true;
            ReadOnlyEnabled = false;
            return;
        }

        ReadOnlyEnabled = true;
        if (_readOnlyBeforeArchive is bool previousValue)
        {
            ReadOnly = previousValue;
            _readOnlyBeforeArchive = null;
        }
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> ParseDriveLetters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd(':').ToUpperInvariant())
            .Where(s => s.Length == 1 && char.IsLetter(s[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
