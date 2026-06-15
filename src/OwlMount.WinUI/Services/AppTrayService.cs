using System.Drawing;
using OwlMount.WinUI.Services;
using WinForms = System.Windows.Forms;

namespace OwlMount.WinUI.Services;

public interface IAppTrayService
{
    void Initialize(Action showWindow, Action showSettings, Action exitApp, Func<bool> getRunOnStartup, Action<bool> setRunOnStartup);
    void Start();
    void NotifyMountsChanged();
    void Dispose();
}

public sealed class AppTrayService : IAppTrayService
{
    private readonly MountService _mountService;
    private Action? _showWindow;
    private Action? _showSettings;
    private Action? _exitApp;
    private Func<bool> _getRunOnStartup = () => false;
    private Action<bool> _setRunOnStartup = _ => { };

    private WinForms.Form? _trayPump;
    private WinForms.NotifyIcon? _trayIcon;

    public AppTrayService(MountService mountService)
    {
        _mountService = mountService;
    }

    public void Initialize(Action showWindow, Action showSettings, Action exitApp, Func<bool> getRunOnStartup, Action<bool> setRunOnStartup)
    {
        _showWindow = showWindow;
        _showSettings = showSettings;
        _exitApp = exitApp;
        _getRunOnStartup = getRunOnStartup;
        _setRunOnStartup = setRunOnStartup;
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
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

                _trayIcon.MouseDoubleClick += (_, _) => _showWindow?.Invoke();
            };

            WinForms.Application.Run(_trayPump);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "OwlMount-Tray";
        thread.Start();
    }

    public void NotifyMountsChanged()
    {
        InvokeTray(() =>
        {
            if (_trayIcon is null) return;
            int count = _mountService.ActiveMounts.Count;
            _trayIcon.Text = count > 0 ? $"OwlMount — {count} mount(s)" : "OwlMount";
            var old = _trayIcon.ContextMenuStrip;
            _trayIcon.ContextMenuStrip = BuildContextMenu();
            old?.Dispose();
        });
    }

    public void Dispose()
    {
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
    }

    private WinForms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open OwlMount")
        {
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };
        openItem.Click += (_, _) => _showWindow?.Invoke();
        menu.Items.Add(openItem);

        var settingsItem = new WinForms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => _showSettings?.Invoke();
        menu.Items.Add(settingsItem);

        var startupItem = new WinForms.ToolStripMenuItem("Run on Windows startup")
        {
            CheckOnClick = true,
            Checked = _getRunOnStartup(),
        };
        startupItem.CheckedChanged += (_, _) => _setRunOnStartup(startupItem.Checked);
        menu.Items.Add(startupItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        IReadOnlyList<ActiveMount> mounts = _mountService.ActiveMounts;
        foreach (ActiveMount m in mounts)
        {
            string letter = m.DriveLetter;
            var item = new WinForms.ToolStripMenuItem($"Unmount {letter}  ({m.Provider})");
            item.Click += (_, _) => _mountService.Unmount(letter);
            menu.Items.Add(item);
        }

        if (mounts.Count > 0)
            menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => _exitApp?.Invoke();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void InvokeTray(Action action)
    {
        try
        {
            if (_trayPump is { IsHandleCreated: true })
                _trayPump.Invoke(action);
            else
                action();
        }
        catch (ObjectDisposedException) { }
    }
}
