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
    public string CopyrightText { get; } = "Copyright (c) 2026 itsWindows11 & OwlMount contributors";
    public string AboutDescription { get; } = $"OwlMount {typeof(App).Assembly.GetName().Version} - Copyright (c) 2026 itsWindows11 & OwlMount contributors";

    public SettingsPageViewModel(MountService mountService, AppSettingsService settingsService, LocalLogService log)
    {
        _mountService = mountService;
        _settingsService = settingsService;
        _log = log;
        BrowseExportPathCommand = new AsyncRelayCommand(BrowseExportPathAsync);

        _selectedTheme = _settingsService.Theme;
        _saveMountConfigurations = _mountService.SaveMountPointConfigurations;
        _persistMemoryFsOnExit = _mountService.PersistMemoryFileSystemOnExit;
        _persistMemoryFsPath = _mountService.MemoryFileSystemPersistPath;
        _isPersistMemoryFsPathEnabled = _persistMemoryFsOnExit;
    }

    public void SetWindowProvider(Func<Window?> windowProvider) => _windowProvider = windowProvider;

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
