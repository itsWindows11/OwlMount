using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwlMount.WinUI.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OwlMount.WinUI.Views;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly MountService _mountService;
    private readonly AppSettingsService _settingsService;
    private readonly LocalLogService _log;
    private Func<Window?> _windowProvider = () => null;
    private bool _saveMountConfigurations;
    private bool _persistMemoryFsOnExit;
    private string _persistMemoryFsPath = string.Empty;
    private bool _isPersistMemoryFsPathEnabled;
    private ElementTheme _selectedTheme;

    public IAsyncRelayCommand BrowseExportPathCommand { get; }
    public IAsyncRelayCommand ClearDiskCacheCommand { get; }
    public IAsyncRelayCommand ClearProjFsResidueCommand { get; }
    public IAsyncRelayCommand ClearAllCacheCommand { get; }

    public IReadOnlyList<ElementTheme> ThemeOptions { get; } = [ElementTheme.Default, ElementTheme.Light, ElementTheme.Dark];

    public bool SaveMountConfigurations
    {
        get => _saveMountConfigurations;
        set
        {
            if (SetProperty(ref _saveMountConfigurations, value))
            {
                _mountService.SetSaveMountPointConfigurations(value);
                _ = _log.InfoAsync($"Save mount configurations set to {value}.");
            }
        }
    }

    public bool PersistMemoryFsOnExit
    {
        get => _persistMemoryFsOnExit;
        set
        {
            if (SetProperty(ref _persistMemoryFsOnExit, value))
            {
                _isPersistMemoryFsPathEnabled = value;
                OnPropertyChanged(nameof(IsPersistMemoryFsPathEnabled));
                _mountService.SetMemoryFileSystemPersistenceOptions(value, _persistMemoryFsPath);
                _ = _log.InfoAsync($"Persist memory filesystem on exit set to {value}.");
            }
        }
    }

    public string PersistMemoryFsPath
    {
        get => _persistMemoryFsPath;
        set
        {
            if (SetProperty(ref _persistMemoryFsPath, value))
            {
                _mountService.SetMemoryFileSystemPersistenceOptions(_persistMemoryFsOnExit, value);
                _ = _log.InfoAsync("Persist memory filesystem path changed.");
            }
        }
    }

    public bool IsPersistMemoryFsPathEnabled
    {
        get => _isPersistMemoryFsPathEnabled;
        set => SetProperty(ref _isPersistMemoryFsPathEnabled, value);
    }

    public ElementTheme SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                _settingsService.SetTheme(value);
                _ = _log.InfoAsync($"Theme changed to {value}.");
            }
        }
    }

    public Uri ProjectUrl { get; } = new("https://github.com/itsWindows11/OwlMount");
    public string AppVersion { get; } = $"OwlMount {typeof(App).Assembly.GetName().Version}";
    public string CopyrightText { get; } = "Copyright \u00a9 2026 itsWindows11 & OwlMount contributors";

    [ObservableProperty] public partial string ClearCacheStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ClearProjFsStatusText { get; set; } = string.Empty;

    public SettingsPageViewModel(MountService mountService, AppSettingsService settingsService, LocalLogService log)
    {
        _mountService = mountService;
        _settingsService = settingsService;
        _log = log;
        BrowseExportPathCommand = new AsyncRelayCommand(BrowseExportPathAsync);
        ClearDiskCacheCommand = new AsyncRelayCommand(ClearDiskCacheAsync);
        ClearProjFsResidueCommand = new AsyncRelayCommand(ClearProjFsResidueAsync);
        ClearAllCacheCommand = new AsyncRelayCommand(ClearAllCacheAsync);

        _selectedTheme = _settingsService.Theme;
        _saveMountConfigurations = _mountService.SaveMountPointConfigurations;
        _persistMemoryFsOnExit = _mountService.PersistMemoryFileSystemOnExit;
        _persistMemoryFsPath = _mountService.MemoryFileSystemPersistPath;
        _isPersistMemoryFsPathEnabled = _persistMemoryFsOnExit;
    }

    public void SetWindowProvider(Func<Window?> windowProvider) => _windowProvider = windowProvider;

    private async Task ClearDiskCacheAsync()
    {
        ClearCacheStatusText = "Clearing…";
        long freed = await _mountService.ClearDiskCacheAsync();
        ClearCacheStatusText = freed > 0
            ? $"Cleared {FormatBytes(freed)}."
            : "Nothing to clear.";
        _ = _log.InfoAsync($"Disk cache cleared: {freed} bytes freed.");
    }

    private async Task ClearProjFsResidueAsync()
    {
        ClearProjFsStatusText = "Clearing…";
        long freed = await _mountService.ClearProjFsResidueAsync();
        ClearProjFsStatusText = freed > 0
            ? $"Cleared {FormatBytes(freed)}."
            : "Nothing to clear.";
        _ = _log.InfoAsync($"ProjFS residue cleared: {freed} bytes freed.");
    }

    private async Task ClearAllCacheAsync()
    {
        ClearCacheStatusText = "Clearing…";
        ClearProjFsStatusText = "Clearing…";

        var cacheData = await Task.WhenAll(
            _mountService.ClearDiskCacheAsync(),
            _mountService.ClearProjFsResidueAsync()
        );

        long cacheFreed = cacheData[0];
        long residueFreed = cacheData[1];

        ClearCacheStatusText = cacheFreed > 0 ? $"Cleared {FormatBytes(cacheFreed)}." : "Nothing to clear.";
        ClearProjFsStatusText = residueFreed > 0 ? $"Cleared {FormatBytes(residueFreed)}." : "Nothing to clear.";
        _ = _log.InfoAsync($"Clear all: cache {cacheFreed} B, residue {residueFreed} B.");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024 * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024               => $"{bytes / 1024.0:F1} KB",
        _                     => $"{bytes} B",
    };

    private async Task BrowseExportPathAsync()
    {
        Window? window = _windowProvider();
        if (window is null)
            return;

        _ = _log.InfoAsync("Browsing for export path.");

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            PersistMemoryFsPath = folder.Path;
            _ = _log.InfoAsync($"Selected export path: {folder.Path}");
        }
    }
}
