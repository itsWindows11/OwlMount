using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OwlMount.Core.Windows.Backends;

namespace OwlMount.WinUI.Views;

/// <summary>
/// A <see cref="ContentDialog"/> that informs the user when one or more
/// VFS backends (Dokany, WinFsp, ProjFS) cannot be found, and lets them
/// either navigate to Settings to configure a custom DLL path or open the
/// vendor download page in the browser.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class BackendAvailabilityDialog
{
    private const string DokanyDownloadUrl = "https://github.com/dokan-dev/dokany/releases";
    private const string WinFspDownloadUrl = "https://winfsp.dev/rel/";

    /// <summary>
    /// Checks availability of all three backends and, if any are missing,
    /// shows a <see cref="ContentDialog"/> that lets the user configure paths
    /// or go to the installation page. Does nothing when all backends are present.
    /// </summary>
    public static async Task ShowIfNeededAsync(
        Window window,
        Action navigateToSettings)
    {
        bool winfspOk = WinFspBackend.IsAvailable();
        bool dokanyOk = DokanyBackend.IsAvailable();
        bool projFsOk = ProjFsBackend.IsAvailable();

        if (winfspOk && dokanyOk && projFsOk)
            return;

        var content = BuildContent(winfspOk, dokanyOk, projFsOk);

        var dialog = new ContentDialog
        {
            Title = "Backend availability",
            Content = content,
            PrimaryButtonText = "Configure paths",
            SecondaryButtonText = "Dismiss",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = window.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            navigateToSettings();
    }

    private static UIElement BuildContent(bool winfspOk, bool dokanyOk, bool projFsOk)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "One or more VFS backends could not be found. " +
                   "You can still use available backends, or install / configure " +
                   "the missing ones now.",
            TextWrapping = TextWrapping.WrapWholeWords,
        });

        // WinFsp row
        panel.Children.Add(BuildBackendRow(
            name:        "WinFsp",
            available:   winfspOk,
            downloadUrl: WinFspDownloadUrl,
            installNote: $"Download from {WinFspDownloadUrl} then restart OwlMount, " +
                         "or configure the path to an existing installation in Settings."));

        // Dokany row
        panel.Children.Add(BuildBackendRow(
            name:        "Dokany",
            available:   dokanyOk,
            downloadUrl: DokanyDownloadUrl,
            installNote: $"Download from {DokanyDownloadUrl} then restart OwlMount, " +
                         "or configure the path to an existing installation in Settings."));

        // ProjFS row
        panel.Children.Add(BuildProjFsRow(projFsOk));

        return new ScrollViewer
        {
            Content = panel,
            MaxHeight = 420,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static UIElement BuildBackendRow(
        string name, bool available,
        string downloadUrl, string installNote)
    {
        var row = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = available ? "\u2714" : "\u2718",
            Foreground = StatusBrush(available),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = available ? "Available" : "Not found",
            Foreground = StatusBrush(available),
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(header);

        if (!available)
        {
            row.Children.Add(new TextBlock
            {
                Text = installNote,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = SecondaryForegroundBrush(),
                Margin = new Thickness(28, 0, 0, 0),
            });

            row.Children.Add(new HyperlinkButton
            {
                Content = $"Download {name}",
                NavigateUri = new Uri(downloadUrl),
                Margin = new Thickness(24, 0, 0, 0),
            });
        }

        return row;
    }

    private static UIElement BuildProjFsRow(bool available)
    {
        var row = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = available ? "\u2714" : "\u2718",
            Foreground = StatusBrush(available),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = "ProjFS",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = available ? "Available" : "Not enabled",
            Foreground = StatusBrush(available),
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(header);

        if (!available)
        {
            row.Children.Add(new TextBlock
            {
                Text = "Windows Projected File System is a built-in optional Windows feature. " +
                       "Enable it with the following command in an elevated PowerShell prompt, then restart:",
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = SecondaryForegroundBrush(),
                Margin = new Thickness(28, 0, 0, 0),
            });

            row.Children.Add(new TextBlock
            {
                Text = "Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart",
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(28, 4, 0, 0),
            });
        }

        return row;
    }

    /// <summary>Returns a green brush for "available", red for "not available".</summary>
    private static Brush StatusBrush(bool available)
    {
        string key = available ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush";
        if (Application.Current.Resources.TryGetValue(key, out object? obj) && obj is Brush b)
            return b;
        return new SolidColorBrush(available ? Colors.Green : Colors.Red);
    }

    /// <summary>Returns the secondary text foreground brush (falls back to default foreground).</summary>
    private static Brush SecondaryForegroundBrush()
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? obj)
            && obj is Brush b)
            return b;
        return new SolidColorBrush(Colors.Gray);
    }
}
