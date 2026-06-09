using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using OwlMount.WinUI.Services;

namespace OwlMount.WinUI;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly MountService _mountService;
    private readonly IAppExitService _exitService;
    private readonly INavigationService _navigation;
    private readonly LocalLogService _log;
    private Func<Task<ProviderOptions?>> _addMountDialog = () => Task.FromResult<ProviderOptions?>(null);
    private Func<MountEntry, Task<ProviderOptions?>> _editMountDialog = _ => Task.FromResult<ProviderOptions?>(null);
    private Func<Task<bool>> _confirmUnmount = () => Task.FromResult(false);
    private Func<Task<bool>> _confirmDisable = () => Task.FromResult(false);
    private Func<string?> _s3SecretProvider;
    private bool _isInitializing = true;
    private bool? _readOnlyBeforeArchive;

    public ObservableCollection<MountEntry> Mounts { get; } = [];
    public ObservableCollection<MountEntry> SelectedMounts { get; } = [];
    public IReadOnlyList<string> Providers { get; } = ["memory", "archive", "local", "kubo-mfs", "kubo-ipfs", "kubo-ipns", "s3", "nfs"];
    public IReadOnlyList<string> ProviderDisplayNames { get; } = ["Memory", "Archive file", "Local folder", "Kubo MFS", "Kubo IPFS", "Kubo IPNS", "Amazon S3", "NFS"];
    public IReadOnlyList<string> Backends { get; } = ["winfsp", "projfs", "dokany"];

    public IAsyncRelayCommand MountCommand { get; }
    public IAsyncRelayCommand AddMountCommand { get; }
    public IAsyncRelayCommand EditSelectedCommand { get; }
    public IAsyncRelayCommand UnmountSelectedCommand { get; }
    public IAsyncRelayCommand DisableSelectedCommand { get; }
    public IAsyncRelayCommand EnableSelectedCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand BrowseSelectedCommand { get; }
    public IRelayCommand ShowSettingsCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand ExitCommand { get; }

    [ObservableProperty] public partial string StatusMessage { get; set; }
    [ObservableProperty] public partial Visibility SelectionActionsVisibility { get; set; } = Visibility.Collapsed;
    [ObservableProperty] public partial int SelectedMountCount { get; set; } = 0;

    public Visibility EditButtonVisibility => SelectedMountCount == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectAllVisibility => (SelectedMountCount > 0 && SelectedMountCount < Mounts.Count) ? Visibility.Collapsed : Visibility.Visible;
    public string SelectAllButtonText => SelectedMountCount == Mounts.Count ? "Unselect all" : "Select all";
    [ObservableProperty] public partial Visibility EnableButtonVisibility { get; set; } = Visibility.Collapsed;
    [ObservableProperty] public partial Visibility DisableButtonVisibility { get; set; } = Visibility.Collapsed;
    [ObservableProperty] public partial Visibility BrowseButtonVisibility { get; set; } = Visibility.Collapsed;
    [ObservableProperty] public partial string SelectedProvider { get; set; }
    [ObservableProperty] public partial string SelectedBackend { get; set; }
    [ObservableProperty] public partial string DriveLetters { get; set; }
    [ObservableProperty] public partial string? Label { get; set; }
    [ObservableProperty] public partial bool ReadOnly { get; set; }
    [ObservableProperty] public partial bool ReadOnlyEnabled { get; set; }
    [ObservableProperty] public partial bool SaveMountConfigurations { get; set; }
    [ObservableProperty] public partial bool PersistMemoryFsOnExit { get; set; }
    [ObservableProperty] public partial string PersistMemoryFsPath { get; set; }
    [ObservableProperty] public partial bool PersistMemoryFsPathEnabled { get; set; }
    [ObservableProperty] public partial string? Path { get; set; }
    [ObservableProperty] public partial string? ArchiveFile { get; set; }
    public string ArchiveFileDisplayText => string.IsNullOrWhiteSpace(ArchiveFile)
        ? "Path: not selected"
        : $"Path: {ArchiveFile}";
    [ObservableProperty] public partial string? ApiUrl { get; set; }
    [ObservableProperty] public partial string? Cid { get; set; }
    [ObservableProperty] public partial string? Ipns { get; set; }
    [ObservableProperty] public partial string? S3Bucket { get; set; }
    [ObservableProperty] public partial string? S3Prefix { get; set; }
    [ObservableProperty] public partial string? S3AccessKey { get; set; }
    [ObservableProperty] public partial string? S3Region { get; set; }
    [ObservableProperty] public partial string? S3Endpoint { get; set; }
    [ObservableProperty] public partial string? NfsHost { get; set; }
    [ObservableProperty] public partial string? NfsExport { get; set; }
    [ObservableProperty] public partial string NfsPath { get; set; }
    [ObservableProperty] public partial Visibility LocalOrArchiveVisibility { get; set; }
    [ObservableProperty] public partial Visibility KuboVisibility { get; set; }
    [ObservableProperty] public partial Visibility S3Visibility { get; set; }
    [ObservableProperty] public partial Visibility NfsVisibility { get; set; }

    public MainWindowViewModel(MountService mountService, IAppExitService exitService, INavigationService navigation, LocalLogService log, Func<string?>? s3SecretProvider = null)
    {
        _mountService = mountService;
        _exitService = exitService;
        _navigation = navigation;
        _log = log;
        _s3SecretProvider = s3SecretProvider ?? (() => null);

        StatusMessage = string.Empty;
        SelectedProvider = "memory";
        SelectedBackend = "winfsp";
        DriveLetters = "M";
        ReadOnlyEnabled = true;
        PersistMemoryFsPath = string.Empty;
        NfsPath = "/";
        LocalOrArchiveVisibility = Visibility.Visible;
        KuboVisibility = Visibility.Collapsed;
        S3Visibility = Visibility.Collapsed;
        NfsVisibility = Visibility.Collapsed;

        SaveMountConfigurations = _mountService.SaveMountPointConfigurations;
        PersistMemoryFsOnExit = _mountService.PersistMemoryFileSystemOnExit;
        PersistMemoryFsPath = _mountService.MemoryFileSystemPersistPath;
        PersistMemoryFsPathEnabled = PersistMemoryFsOnExit;

        MountCommand = new AsyncRelayCommand(MountAsync);
        AddMountCommand = new AsyncRelayCommand(AddMountAsync);
        EditSelectedCommand = new AsyncRelayCommand(EditSelectedAsync);
        UnmountSelectedCommand = new AsyncRelayCommand(UnmountSelectedAsync);
        DisableSelectedCommand = new AsyncRelayCommand(DisableSelectedAsync);
        EnableSelectedCommand = new AsyncRelayCommand(EnableSelectedAsync);
        SelectAllCommand = new RelayCommand(SelectAll);
        BrowseSelectedCommand = new RelayCommand(BrowseSelected);
        ShowSettingsCommand = new RelayCommand(_navigation.ShowSettingsPage);
        RefreshCommand = new RelayCommand(RefreshMountsFromService);
        ExitCommand = new RelayCommand(_exitService.Exit);

        RefreshMountsFromService();
        UpdateProviderPanels();
        _isInitializing = false;
    }

    public void RefreshMountsFromService()
    {
        Mounts.Clear();

        // First add all active mounts
        var activeLookup = new Dictionary<string, ActiveMount>(StringComparer.OrdinalIgnoreCase);
        foreach (ActiveMount m in _mountService.ActiveMounts)
        {
            string letter = m.DriveLetter.TrimEnd(':').ToUpperInvariant();
            activeLookup[letter] = m;
            string state = m.IsReadOnly ? "Running (R/O)" : "Running";
            Mounts.Add(new MountEntry(
                m.DriveLetter,
                m.Label,
                m.Provider,
                GetProviderDisplayName(m.Provider),
                state,
                GetDriveCapacityBytes(m.DriveLetter),
                isEnabled: true));
        }

        // Then add disabled configurations (those in config but not active)
        foreach (ProviderOptions config in _mountService.MountConfigurations)
        {
            string letter = config.Letter.TrimEnd(':').ToUpperInvariant();
            if (!activeLookup.ContainsKey(letter))
            {
                Mounts.Add(new MountEntry(
                    config.Letter.EndsWith(':') ? config.Letter : config.Letter + ":",
                    config.Label ?? string.Empty,
                    config.Provider,
                    GetProviderDisplayName(config.Provider),
                    "Disabled",
                    0,
                    isEnabled: false));
            }
        }

        if (Mounts.Count == 0)
            SetStatus("No active mounts.");

        // Notify property changes for selection-dependent UI
        OnPropertyChanged(nameof(SelectAllVisibility));
        OnPropertyChanged(nameof(SelectAllButtonText));
    }

    public void SetStatus(string message) => StatusMessage = message;

    public void SetS3SecretProvider(Func<string?> provider) => _s3SecretProvider = provider;

    public void SetAddMountDialog(Func<Task<ProviderOptions?>> dialog) => _addMountDialog = dialog;

    public void SetEditMountDialog(Func<MountEntry, Task<ProviderOptions?>> dialog) => _editMountDialog = dialog;

    public void SetConfirmUnmount(Func<Task<bool>> confirmation) => _confirmUnmount = confirmation;

    public void SetConfirmDisable(Func<Task<bool>> confirmation) => _confirmDisable = confirmation;

    public void SetSelectedMounts(IEnumerable<MountEntry> selected)
    {
        SelectedMounts.Clear();
        foreach (MountEntry mount in selected)
            SelectedMounts.Add(mount);

        SelectedMountCount = SelectedMounts.Count;
        SelectionActionsVisibility = SelectedMounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Check if selection is all enabled, all disabled, or mixed
        bool allEnabled = SelectedMounts.Count > 0 && SelectedMounts.All(m => m.IsEnabled);
        bool allDisabled = SelectedMounts.Count > 0 && SelectedMounts.All(m => !m.IsEnabled);
        bool hasAnyDisabled = SelectedMounts.Any(m => !m.IsEnabled);

        EnableButtonVisibility = allDisabled ? Visibility.Visible : Visibility.Collapsed;
        DisableButtonVisibility = allEnabled ? Visibility.Visible : Visibility.Collapsed;
        BrowseButtonVisibility = hasAnyDisabled ? Visibility.Collapsed : Visibility.Visible;

        OnPropertyChanged(nameof(EditButtonVisibility));
        OnPropertyChanged(nameof(SelectAllVisibility));
        OnPropertyChanged(nameof(SelectAllButtonText));
    }

    private void SelectAll()
    {
        // Toggle: if all are selected, unselect all; otherwise select all
        bool allSelected = SelectedMountCount == Mounts.Count;
        foreach (MountEntry mount in Mounts)
            mount.IsSelected = !allSelected;
    }

    private void BrowseSelected()
    {
        foreach (MountEntry mount in SelectedMounts)
        {
            // Skip disabled mounts
            if (!mount.IsEnabled)
                continue;

            string letter = mount.DriveLetter.TrimEnd(':');
            try { System.Diagnostics.Process.Start("explorer.exe", $"{letter}:\\"); }
            catch { /* best-effort */ }
        }
    }

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
        _ = _log.InfoAsync($"Unmounted {selected.Count} mount(s).");
    }

    /// <summary>Disables the provided mounts (unmounts but keeps config).</summary>
    public async Task DisableSelected(IReadOnlyList<MountEntry> selected)
    {
        if (selected.Count == 0)
        {
            SetStatus("Select one or more active mounts first.");
            return;
        }
        
        if (!await _confirmDisable())
            return;

        foreach (MountEntry mount in selected)
            _mountService.Disable(mount.DriveLetter);

        RefreshMountsFromService();
        SetStatus(selected.Count == 1
            ? $"Disabled {selected[0].DriveLetter}."
            : $"Disabled {selected.Count} drive(s).");
        _ = _log.InfoAsync($"Disabled {selected.Count} mount(s).");
        SetSelectedMounts([]);
    }

    /// <summary>Opens the edit dialog for a specific mount entry (used by context menu).</summary>
    public async Task EditMountAsync(MountEntry entry)
    {
        ProviderOptions? updated = await _editMountDialog(entry);
        if (updated is null)
            return;

        if (!await _confirmUnmount())
            return;

        UnmountSelected([entry]);
        _ = _log.InfoAsync($"Editing mount '{entry.DriveLetter}' via context menu.");
        await MountFromOptionsAsync(updated);
    }

    /// <summary>Unmounts a single specific entry after confirmation (used by context menu).</summary>
    public async Task UnmountSingleAsync(MountEntry entry)
    {
        if (!await _confirmUnmount())
            return;

        _ = _log.InfoAsync($"Unmounting '{entry.DriveLetter}' via context menu.");
        UnmountSelected([entry]);
        SetSelectedMounts([]);
    }

    /// <summary>Disables a single specific entry (unmounts but keeps config) via context menu.</summary>
    public async Task DisableSingleAsync(MountEntry entry)
    {
        if (!await _confirmDisable())
            return;

        _ = _log.InfoAsync($"Disabling '{entry.DriveLetter}' via context menu.");
        _mountService.Disable(entry.DriveLetter);
        RefreshMountsFromService();
        SetStatus($"Disabled {entry.DriveLetter}.");
        SetSelectedMounts([]);
    }

    /// <summary>Enables (re-mounts) a single specific disabled entry via context menu.</summary>
    public async Task EnableSingleAsync(MountEntry entry)
    {
        _ = _log.InfoAsync($"Enabling '{entry.DriveLetter}' via context menu.");

        // Find the configuration for this drive
        var config = _mountService.MountConfigurations
            .FirstOrDefault(c => c.Letter.TrimEnd(':').Equals(entry.DriveLetter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            SetStatus($"No configuration found for {entry.DriveLetter}.");
            return;
        }

        await MountFromOptionsAsync(config);
        SetSelectedMounts([]);
    }

    private async Task DisableSelectedAsync()
    {
        if (SelectedMounts.Count == 0)
        {
            SetStatus("Select one or more active mounts first.");
            return;
        }

        if (!await _confirmDisable())
            return;

        var selected = SelectedMounts.ToList();
        foreach (MountEntry mount in selected)
            _mountService.Disable(mount.DriveLetter);

        RefreshMountsFromService();
        SetStatus(selected.Count == 1
            ? $"Disabled {selected[0].DriveLetter}."
            : $"Disabled {selected.Count} drive(s).");
        _ = _log.InfoAsync($"Disabled {selected.Count} mount(s).");
        SetSelectedMounts([]);
    }

    private async Task EnableSelectedAsync()
    {
        if (SelectedMounts.Count == 0)
        {
            SetStatus("Select one or more disabled mounts first.");
            return;
        }

        var selected = SelectedMounts.ToList();
        int successCount = 0;
        var failures = new List<string>();

        foreach (MountEntry mount in selected)
        {
            var config = _mountService.MountConfigurations
                .FirstOrDefault(c => c.Letter.TrimEnd(':').Equals(mount.DriveLetter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase));

            if (config is null)
            {
                failures.Add(mount.DriveLetter);
                continue;
            }

            (bool success, string? error) = await _mountService.MountAsync(config);
            if (success)
                successCount++;
            else
                failures.Add($"{mount.DriveLetter}: {error}");
        }

        RefreshMountsFromService();

        if (failures.Count == 0)
        {
            SetStatus(selected.Count == 1
                ? $"Enabled {selected[0].DriveLetter}."
                : $"Enabled {selected.Count} drive(s).");
        }
        else if (successCount == 0)
        {
            SetStatus($"Failed to enable drive(s): {string.Join(", ", failures)}");
        }
        else
        {
            SetStatus($"Enabled {successCount} drive(s). Failed: {string.Join(", ", failures)}");
        }

        _ = _log.InfoAsync($"Enabled {successCount}/{selected.Count} mount(s).");
        SetSelectedMounts([]);
    }

    private async Task AddMountAsync()
    {
        ProviderOptions? opts = await _addMountDialog();
        if (opts is not null)
        {
            _ = _log.InfoAsync($"Opening add mount dialog produced provider '{opts.Provider}'.");
            await MountFromOptionsAsync(opts);
        }
    }

    private async Task EditSelectedAsync()
    {
        if (SelectedMounts.Count != 1)
        {
            SetStatus("Select exactly one mount to edit.");
            return;
        }

        ProviderOptions? updated = await _editMountDialog(SelectedMounts[0]);
        if (updated is null)
            return;

        if (!await _confirmUnmount())
            return;

        UnmountSelected([SelectedMounts[0]]);
        _ = _log.InfoAsync($"Editing mount '{SelectedMounts[0].DriveLetter}'.");
        await MountFromOptionsAsync(updated);
    }

    private async Task UnmountSelectedAsync()
    {
        if (SelectedMounts.Count == 0)
        {
            SetStatus("Select one or more active mounts first.");
            return;
        }

        if (!await _confirmUnmount())
            return;

        _ = _log.InfoAsync($"Unmounting {SelectedMounts.Count} selected mount(s).");
        UnmountSelected(SelectedMounts.ToList());
        SetSelectedMounts([]);
    }

    private async Task MountAsync()
    {
        var opts = new ProviderOptions
        {
            Provider = SelectedProvider,
            Backend = SelectedBackend,
            Letter = DriveLetters,
            Label = Label,
            ForceReadOnly = ReadOnly,
            Path = Path,
            ArchiveFile = ArchiveFile,
            ApiUrl = ApiUrl,
            Cid = Cid,
            IpnsAddress = Ipns,
            S3Bucket = S3Bucket,
            S3Prefix = S3Prefix,
            S3AccessKey = S3AccessKey,
            S3SecretKey = _s3SecretProvider(),
            S3Region = S3Region,
            S3Endpoint = S3Endpoint,
            NfsHost = NfsHost,
            NfsExport = NfsExport,
            NfsPath = NfsPath,
        };
        await MountFromOptionsAsync(opts);
    }

    public async Task MountFromOptionsAsync(ProviderOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.Provider) || string.IsNullOrWhiteSpace(opts.Backend))
        {
            SetStatus("Provider and backend are required.");
            return;
        }

        IReadOnlyList<string> letters = ParseDriveLetters(opts.Letter);
        if (letters.Count == 0)
        {
            SetStatus("Enter one or more drive letters (e.g. M or M,R,X).");
            return;
        }

        int successCount = 0;
        var failures = new List<string>();

        foreach (string letter in letters)
        {
            var mountOpts = new ProviderOptions
            {
                Provider = opts.Provider,
                Backend = opts.Backend,
                Letter = letter,
                Label = NullIfBlank(opts.Label),
                ForceReadOnly = opts.ForceReadOnly,
                MemorySizeLimitBytes = opts.MemorySizeLimitBytes,
                Path = NullIfBlank(opts.Path),
                ArchiveFile = NullIfBlank(opts.ArchiveFile),
                ApiUrl = NullIfBlank(opts.ApiUrl),
                Cid = NullIfBlank(opts.Cid),
                IpnsAddress = NullIfBlank(opts.IpnsAddress),
                S3Bucket = NullIfBlank(opts.S3Bucket),
                S3Prefix = NullIfBlank(opts.S3Prefix),
                S3AccessKey = NullIfBlank(opts.S3AccessKey),
                S3SecretKey = NullIfBlank(opts.S3SecretKey),
                S3Region = NullIfBlank(opts.S3Region),
                S3Endpoint = NullIfBlank(opts.S3Endpoint),
                NfsHost = NullIfBlank(opts.NfsHost),
                NfsExport = NullIfBlank(opts.NfsExport),
                NfsPath = NullIfBlank(opts.NfsPath) ?? "/",
            };

            var (success, error) = await _mountService.MountAsync(mountOpts);
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

        _ = _log.InfoAsync($"Mount attempt complete. Success={successCount}, Failures={failures.Count}.");
    }

    private static long GetDriveCapacityBytes(string driveLetter)
    {
        try
        {
            string root = $"{driveLetter.TrimEnd(':')}:\\";
            var info = new DriveInfo(root);
            if (!info.IsReady)
                return 0;

            return info.TotalSize;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetDriveCapacityText(string driveLetter)
    {
        try
        {
            string root = $"{driveLetter.TrimEnd(':')}:\\";
            var info = new DriveInfo(root);
            if (!info.IsReady)
                return "Capacity: unavailable";

            return $"Capacity: {FormatBytes(info.TotalSize)} total, {FormatBytes(info.AvailableFreeSpace)} free";
        }
        catch
        {
            return "Capacity: unavailable";
        }
    }

    private static string FormatBytes(long value)
    {
        double size = value;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
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

    partial void OnArchiveFileChanged(string? value)
    {
        OnPropertyChanged(nameof(ArchiveFileDisplayText));
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

    public static string GetProviderDisplayName(string provider) =>
        provider.ToLowerInvariant() switch
        {
            "memory" => "Memory",
            "archive" => "Archive file",
            "local" => "Local folder",
            "kubo-mfs" => "Kubo MFS",
            "kubo-ipfs" => "Kubo IPFS",
            "kubo-ipns" => "Kubo IPNS",
            "s3" => "Amazon S3",
            "nfs" => "NFS",
            _ => provider,
        };

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
