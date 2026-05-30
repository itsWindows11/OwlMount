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
    private long _defaultBlockCacheSizeBytes;
    private bool _enableBlockCache;

    public IAsyncRelayCommand BrowseExportPathCommand { get; }
    public IAsyncRelayCommand ClearDiskCacheCommand { get; }
    public IAsyncRelayCommand ClearProjFsResidueCommand { get; }
    public IAsyncRelayCommand ClearAllCacheCommand { get; }
    public IAsyncRelayCommand ExportConfigurationCommand { get; }
    public IAsyncRelayCommand ImportConfigurationCommand { get; }

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

    public long DefaultBlockCacheSizeBytes
    {
        get => _defaultBlockCacheSizeBytes;
        set
        {
            if (SetProperty(ref _defaultBlockCacheSizeBytes, value) && value > 0)
            {
                _settingsService.SetDefaultBlockCacheSize(value);
                _ = _log.InfoAsync($"Default block cache size changed to {FormatBytes(value)}.");
            }
        }
    }

    public bool EnableBlockCache
    {
        get => _enableBlockCache;
        set
        {
            if (SetProperty(ref _enableBlockCache, value))
            {
                _settingsService.SetEnableBlockCache(value);
                _ = _log.InfoAsync($"Block cache {(value ? "enabled" : "disabled")}.");
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
        ExportConfigurationCommand = new AsyncRelayCommand(ExportConfigurationAsync);
        ImportConfigurationCommand = new AsyncRelayCommand(ImportConfigurationAsync);

        _selectedTheme = _settingsService.Theme;
        _defaultBlockCacheSizeBytes = _settingsService.DefaultBlockCacheSizeBytes;
        _enableBlockCache = _settingsService.EnableBlockCache;
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

    private async Task ExportConfigurationAsync()
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                DefaultFileExtension = ".json",
                FileTypeChoices = { { "JSON Configuration", new[] { ".json" } } }
            };

            var window = _windowProvider();
            if (window is not null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            _mountService.ExportConfigurationToFile(file.Path);
            _ = _log.InfoAsync($"Configuration exported to {file.Name}");
        }
        catch (Exception ex)
        {
            _ = _log.ErrorAsync($"Failed to export configuration: {ex.Message}");
        }
    }

    private async Task ImportConfigurationAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                FileTypeFilter = { ".json" }
            };

            var window = _windowProvider();
            if (window is not null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            // Show import options dialog
            var dialog = new ContentDialog
            {
                Title = "Import Options",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "Select what to import:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new CheckBox { Name = "MountPointsCheckBox", Content = "Mount points", IsChecked = true },
                        new CheckBox { Name = "SettingsCheckBox", Content = "Application settings", IsChecked = true },
                    }
                },
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            if (window is not null)
            {
                dialog.XamlRoot = window.Content.XamlRoot;
            }

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            // Get checkbox states
            bool importMountPoints = true;
            bool importSettings = true;

            if (dialog.Content is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is CheckBox cb)
                    {
                        if (cb.Name == "MountPointsCheckBox")
                            importMountPoints = cb.IsChecked ?? false;
                        else if (cb.Name == "SettingsCheckBox")
                            importSettings = cb.IsChecked ?? false;
                    }
                }
            }

            _mountService.ImportConfigurationFromFile(file.Path, importMountPoints: importMountPoints, importSettings: importSettings);
            _ = _log.InfoAsync($"Configuration imported from {file.Name}");
        }
        catch (Exception ex)
        {
            _ = _log.ErrorAsync($"Failed to import configuration: {ex.Message}");
        }
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
