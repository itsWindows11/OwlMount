using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using OwlMount.WinUI.Services;

namespace OwlMount.WinUI.Views;

public sealed partial class HomePage : Page
{
    public MainWindowViewModel ViewModel { get; }

    // Stores the contextual mounts for the currently shown context menu
    private IReadOnlyList<MountEntry> _contextMenuMounts = [];

    public HomePage()
    {
        var app = (App)Application.Current;
        ViewModel = app.Services.GetRequiredService<MainWindowViewModel>();
        InitializeComponent();

        ViewModel.SetAddMountDialog(ShowAddMountDialogAsync);
        ViewModel.SetEditMountDialog(ShowEditMountDialogAsync);
        ViewModel.SetConfirmUnmount(ConfirmUnmountAsync);
        ViewModel.SetConfirmDisable(ConfirmDisableAsync);
        ViewModel.Mounts.CollectionChanged += Mounts_CollectionChanged;
        HookMountSelectionEvents();
        UpdateEmptyState();
    }

    private async Task<ProviderOptions?> ShowAddMountDialogAsync()
    {
        var app = (App)Application.Current;
        var window = (MainWindow)app.Services.GetRequiredService<MainWindow>();
        return await MountConfigDialog.ShowAsync(window);
    }

    private async Task<ProviderOptions?> ShowEditMountDialogAsync(MountEntry selected)
    {
        var app = (App)Application.Current;
        var window = (MainWindow)app.Services.GetRequiredService<MainWindow>();
        ProviderOptions existing = new()
        {
            Provider = selected.Provider,
            Backend = "winfsp",
            Letter = selected.DriveLetter,
            Label = selected.Label,
        };
        return await MountConfigDialog.ShowAsync(window, existing);
    }

    private async Task<bool> ConfirmUnmountAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Unmount selected mounts?",
            Content = "Unmounting can interrupt active file access and may cause data loss if anything is still writing to the mounted drive. Continue?",
            PrimaryButtonText = "Unmount",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmDisableAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Disable selected mounts?",
            Content = "Disabling will unmount the selected drives but keep their configurations for later use. Any active file access will be interrupted. Continue?",
            PrimaryButtonText = "Disable",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Mounts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HookMountSelectionEvents();
        UpdateEmptyState();
    }

    private void MountItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MountEntry.IsSelected))
            ViewModel.SetSelectedMounts(ViewModel.Mounts.Where(m => m.IsSelected));
    }

    private void HookMountSelectionEvents()
    {
        foreach (MountEntry mount in ViewModel.Mounts)
            mount.PropertyChanged -= MountItem_PropertyChanged;

        foreach (MountEntry mount in ViewModel.Mounts)
            mount.PropertyChanged += MountItem_PropertyChanged;
    }

    private void UpdateEmptyState()
    {
        bool hasMounts = ViewModel.Mounts.Count > 0;
        EmptyStatePanel.Visibility = hasMounts ? Visibility.Collapsed : Visibility.Visible;
        MountsListView.Visibility = hasMounts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MountCard_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement card)
            return;
        if (FlyoutBase.GetAttachedFlyout(card) is not MenuFlyout flyout)
            return;

        // Get the right-clicked mount entry
        if (card.DataContext is not MountEntry rightClickedMount)
            return;

        // Determine which mounts to use for context menu logic:
        // - If the right-clicked item is selected, use all selected items
        // - If the right-clicked item is NOT selected, treat it as a single-item selection for menu purposes only
        IReadOnlyList<MountEntry> contextMounts = rightClickedMount.IsSelected
            ? ViewModel.SelectedMounts
            : new[] { rightClickedMount };

        // Store for use by flyout item handlers
        _contextMenuMounts = contextMounts;

        bool multiSelect = contextMounts.Count > 1;
        bool allEnabled = true;
        bool allDisabled = true;
        bool hasMixed = false;
        bool hasAnyDisabled = false;

        // Check if selection is all enabled, all disabled, or mixed
        foreach (MountEntry m in contextMounts)
        {
            if (m.IsEnabled)
                allDisabled = false;
            else
            {
                allEnabled = false;
                hasAnyDisabled = true;
            }
        }
        hasMixed = !allEnabled && !allDisabled;

        foreach (MenuFlyoutItemBase item in flyout.Items)
        {
            item.Visibility = item switch
            {
                MenuFlyoutItem { Text: "Edit" }       => multiSelect ? Visibility.Collapsed : Visibility.Visible,
                MenuFlyoutItem { Text: "Enable" }     => (hasMixed || allEnabled) ? Visibility.Collapsed : Visibility.Visible,
                MenuFlyoutItem { Text: "Disable" }    => (hasMixed || allDisabled) ? Visibility.Collapsed : Visibility.Visible,
                MenuFlyoutItem { Text: "Browse" }     => hasAnyDisabled ? Visibility.Collapsed : Visibility.Visible,
                MenuFlyoutItem { Text: "Unmount" }    => allDisabled ? Visibility.Collapsed : Visibility.Visible,
                MenuFlyoutSeparator                   => multiSelect ? Visibility.Collapsed : Visibility.Visible,
                MenuFlyoutItem { Text: "Properties" } => (multiSelect || allDisabled) ? Visibility.Collapsed : Visibility.Visible,
                _                                     => item.Visibility,
            };
        }

        flyout.ShowAt(card, e.GetPosition(card));
    }

    private async void EditFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        // Edit always operates on a single entry
        if (_contextMenuMounts.Count == 1)
            await ViewModel.EditMountAsync(_contextMenuMounts[0]);
    }

    private async void DisableFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuMounts.Count == 0)
            return;

        // Call the view model helper that operates on an explicit selection to avoid
        // relying on SelectedMounts state when invoked from a contextual menu.
        await ViewModel.DisableSelected(_contextMenuMounts);
        // ViewModel.DisableSelected clears selection and refreshes mounts.
    }

    private async void EnableFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuMounts.Count == 0)
            return;

        // Temporarily set selection to the context so the existing EnableSelectedAsync
        // (which operates on SelectedMounts) can be reused.
        ViewModel.SetSelectedMounts(_contextMenuMounts);
        await ViewModel.EnableSelectedCommand.ExecuteAsync(null);
    }

    private void BrowseFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        foreach (MountEntry mount in _contextMenuMounts)
        {
            // Skip disabled mounts
            if (!mount.IsEnabled)
                continue;

            string letter = mount.DriveLetter.TrimEnd(':');
            try { System.Diagnostics.Process.Start("explorer.exe", $"{letter}:\\"); }
            catch { /* best-effort */ }
        }
    }

    private async void UnmountFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuMounts.Count == 0)
            return;

        // Temporarily set selection to match the context for the unmount operation
        ViewModel.SetSelectedMounts(_contextMenuMounts);
        await ViewModel.UnmountSelectedCommand.ExecuteAsync(null);
        // Selection is cleared by UnmountSelectedAsync, so no need to restore
    }

    private void PropertiesFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is MountEntry entry)
        {
            string letter = entry.DriveLetter.TrimEnd(':');
            try
            {
                NativeMethods.SHObjectProperties(nint.Zero, NativeMethods.SHOP_FILEPATH, $"{letter}:\\", null);
            }
            catch { /* best-effort */ }
        }
    }
}
