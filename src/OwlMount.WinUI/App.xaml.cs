using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OwlMount.WinUI.Services;

namespace OwlMount.WinUI;

public partial class App : Application
{
    public IServiceProvider Services { get; }
    public MountService MountService => Services.GetRequiredService<MountService>();
    public AppSettingsService AppSettings => Services.GetRequiredService<AppSettingsService>();
    public IAppExitService AppExitService => Services.GetRequiredService<IAppExitService>();
    public IAppTrayService AppTrayService => Services.GetRequiredService<IAppTrayService>();

    // ── Private state ─────────────────────────────────────────────────────────

    private MainWindow? _window;

    // ── Application lifecycle ─────────────────────────────────────────────────

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddSingleton<MountService>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<LocalLogService>();
        services.AddSingleton<AppExitService>();
        services.AddSingleton<IAppExitService>(sp => sp.GetRequiredService<AppExitService>());
        services.AddSingleton<IAppTrayService, AppTrayService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<MountService>(),
            sp.GetRequiredService<IAppExitService>(),
            sp.GetRequiredService<INavigationService>(),
            sp.GetRequiredService<LocalLogService>()));
        services.AddSingleton(sp => new Views.SettingsPageViewModel(
            sp.GetRequiredService<MountService>(),
            sp.GetRequiredService<AppSettingsService>(),
            sp.GetRequiredService<LocalLogService>()));
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppExitService.SetExitAction(ExitApp);
        MountService.MountsChanged += OnMountsChanged;
        _ = Services.GetRequiredService<LocalLogService>().InfoAsync("Application launched.");

        _window = Services.GetRequiredService<MainWindow>();
        _window.AppWindow.Closing += OnWindowClosing;
        _window.Activate();

        AppTrayService.Initialize(ShowWindow, ShowSettings, ExitApp);
        AppTrayService.Start();
        _ = RestoreConfiguredMountsAsync();
    }

    // Closing the window hides it to the tray instead of exiting.
    private static void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
    }

    // ── Cross-thread helpers ──────────────────────────────────────────────────

    private void OnMountsChanged(object? sender, EventArgs e)
    {
        AppTrayService.NotifyMountsChanged();
        _ = Services.GetRequiredService<LocalLogService>().InfoAsync("Mounts changed.");

        // Refresh the main window's mount list on the WinUI dispatcher.
        _window?.DispatcherQueue.TryEnqueue(() => _window.RefreshMountsFromService());
    }

    private void ShowWindow()
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.AppWindow.Show();
            _window.Activate();
            BringToForeground(_window);
        });
    }

    private void ShowSettings()
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.AppWindow.Show();
            _window.Activate();
            BringToForeground(_window);
            Services.GetRequiredService<INavigationService>().ShowSettingsPage();
        });
    }

    private static void BringToForeground(MainWindow window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private async Task RestoreConfiguredMountsAsync()
    {
        var (successCount, failures) = await MountService.RestoreConfiguredMountsAsync();
        if (_window is null)
            return;

        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (successCount > 0 && failures.Count == 0)
            {
                _window.SetExternalStatus($"Restored {successCount} saved mount point configuration(s).");
                _ = Services.GetRequiredService<LocalLogService>().InfoAsync($"Restored {successCount} saved mount point configuration(s).");
            }
            else if (successCount > 0 && failures.Count > 0)
            {
                _window.SetExternalStatus(
                    $"Restored {successCount} saved mount point configuration(s); failures: {string.Join(" | ", failures)}");
                _ = Services.GetRequiredService<LocalLogService>().WarnAsync($"Restored {successCount} saved mount point configuration(s) with failures: {string.Join(" | ", failures)}");
            }
            else if (failures.Count > 0)
            {
                _window.SetExternalStatus($"Failed to restore saved mount point configurations: {string.Join(" | ", failures)}");
                _ = Services.GetRequiredService<LocalLogService>().ErrorAsync($"Failed to restore saved mount point configurations: {string.Join(" | ", failures)}");
            }
        });
    }

    /// <summary>
    /// Unmounts all drives, cleans up the tray icon, and terminates the process.
    /// Safe to call from any thread.
    /// </summary>
    internal void ExitApp()
    {
        IReadOnlyList<string> exportFailures = [];
        try
        {
            exportFailures = MountService.PersistMemoryFileSystemsAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort export
        }

        MountService.UnmountAll();
        MountService.Dispose();

        AppTrayService.Dispose();
        _ = Services.GetRequiredService<LocalLogService>().InfoAsync("Application exiting.");

        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window?.AppWindow.Closing -= OnWindowClosing;
            if (exportFailures.Count > 0)
                _window?.SetExternalStatus($"In-memory filesystem export failures: {string.Join(" | ", exportFailures)}");
            Current.Exit();
        });
    }
}
