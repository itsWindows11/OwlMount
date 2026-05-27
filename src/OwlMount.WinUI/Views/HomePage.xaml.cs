using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using OwlMount.WinUI.Services;

namespace OwlMount.WinUI.Views;

public sealed partial class HomePage : Page
{
    public MainWindowViewModel ViewModel { get; }

    public HomePage()
    {
        var app = (App)Application.Current;
        ViewModel = app.Services.GetRequiredService<MainWindowViewModel>();
        InitializeComponent();

        ViewModel.SetAddMountDialog(ShowAddMountDialogAsync);
        ViewModel.SetEditMountDialog(ShowEditMountDialogAsync);
        ViewModel.SetConfirmUnmount(ConfirmUnmountAsync);
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
}
