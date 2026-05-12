using System.Drawing;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OwlMount.WinUI.Services;
using WinForms = System.Windows.Forms;

namespace OwlMount.WinUI;

public partial class App : Application
{
    // ── Shared singleton ──────────────────────────────────────────────────────

    /// <summary>
    /// In-process mount manager. Created once for the lifetime of the application.
    /// </summary>
    public static MountService MountService { get; } = new MountService();

    // ── Private state ─────────────────────────────────────────────────────────

    private MainWindow? _window;

    /// <summary>
    /// Invisible WinForms form that hosts the Win32 message pump required by
    /// <see cref="WinForms.NotifyIcon"/>. Used for cross-thread Invoke.
    /// </summary>
    private WinForms.Form? _trayPump;
    private WinForms.NotifyIcon? _trayIcon;

    // ── Application lifecycle ─────────────────────────────────────────────────

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MountService.MountsChanged += OnMountsChanged;

        _window = new MainWindow();
        _window.AppWindow.Closing += OnWindowClosing;
        _window.Activate();

        StartTrayThread();
        _ = RestoreConfiguredMountsAsync();
    }

    // Closing the window hides it to the tray instead of exiting.
    private static void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
    }

    // ── Tray thread ───────────────────────────────────────────────────────────

    private void StartTrayThread()
    {
        var thread = new Thread(() =>
        {
            // An invisible Form provides both the Win32 message pump that
            // NotifyIcon needs and the Invoke() method for cross-thread marshaling.
            _trayPump = new WinForms.Form
            {
                Text = "OwlMount Tray Pump",
                ShowInTaskbar = false,
                WindowState = WinForms.FormWindowState.Minimized,
                FormBorderStyle = WinForms.FormBorderStyle.None,
                Size = new Size(1, 1),
            };

            _trayPump.Load += (_, _) =>
            {
                _trayPump.Hide();

                Icon icon;
                try
                {
                    icon = Icon.ExtractAssociatedIcon(
                        Environment.ProcessPath
                        ?? System.Reflection.Assembly.GetExecutingAssembly().Location)
                        ?? SystemIcons.Application;
                }
                catch
                {
                    icon = SystemIcons.Application;
                }

                _trayIcon = new WinForms.NotifyIcon
                {
                    Text = "OwlMount",
                    Icon = icon,
                    Visible = true,
                    ContextMenuStrip = BuildContextMenu(),
                };

                _trayIcon.MouseDoubleClick += (_, _) => ShowWindow();
            };

            WinForms.Application.Run(_trayPump);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "OwlMount-Tray";
        thread.Start();
    }

    /// <summary>Builds a fresh context-menu reflecting current mount state.</summary>
    private WinForms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open OwlMount")
        {
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };
        openItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        IReadOnlyList<ActiveMount> mounts = MountService.ActiveMounts;
        foreach (ActiveMount m in mounts)
        {
            string letter = m.DriveLetter;
            var item = new WinForms.ToolStripMenuItem(
                $"Unmount {letter}  ({m.Provider})");
            item.Click += (_, _) => MountService.Unmount(letter);
            menu.Items.Add(item);
        }

        if (mounts.Count > 0)
        {
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        return menu;
    }

    // ── Cross-thread helpers ──────────────────────────────────────────────────

    private void OnMountsChanged(object? sender, EventArgs e)
    {
        // Update tray icon tooltip and context menu on the tray thread.
        InvokeTray(() =>
        {
            if (_trayIcon is null) return;
            int count = MountService.ActiveMounts.Count;
            _trayIcon.Text = count > 0 ? $"OwlMount — {count} mount(s)" : "OwlMount";
            var old = _trayIcon.ContextMenuStrip;
            _trayIcon.ContextMenuStrip = BuildContextMenu();
            old?.Dispose();
        });

        // Refresh the main window's mount list on the WinUI dispatcher.
        _window?.DispatcherQueue.TryEnqueue(() => _window.RefreshMountsFromService());
    }

    private void ShowWindow()
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.AppWindow.Show();
            _window.Activate();
        });
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
            }
            else if (successCount > 0 && failures.Count > 0)
            {
                _window.SetExternalStatus(
                    $"Restored {successCount} saved mount point configuration(s); failures: {string.Join(" | ", failures)}");
            }
            else if (failures.Count > 0)
            {
                _window.SetExternalStatus($"Failed to restore saved mount point configurations: {string.Join(" | ", failures)}");
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

        InvokeTray(() =>
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            WinForms.Application.ExitThread();
        });

        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is not null)
                _window.AppWindow.Closing -= OnWindowClosing;
            if (exportFailures.Count > 0)
                _window?.SetExternalStatus($"In-memory filesystem export failures: {string.Join(" | ", exportFailures)}");
            Current.Exit();
        });
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto the tray-pump thread. Falls back to
    /// direct invocation when the pump handle isn't available yet.
    /// </summary>
    private void InvokeTray(Action action)
    {
        try
        {
            if (_trayPump is { IsHandleCreated: true })
                _trayPump.Invoke(action);
            else
                action();
        }
        catch (ObjectDisposedException) { /* tray already torn down — ignore */ }
    }
}
