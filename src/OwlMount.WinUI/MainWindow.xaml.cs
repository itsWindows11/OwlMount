using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OwlMount.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        ViewModel = new MainWindowViewModel(App.MountService, () => ((App)Application.Current).ExitApp());
        InitializeComponent();
        ViewModel.SetS3SecretProvider(() => S3SecretKeyTextBox.Password);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
    }

    // ── Public API called by App ──────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the active-mounts list from the in-process <see cref="MountService"/>.
    /// Called both locally and from <see cref="App.OnMountsChanged"/>.
    /// </summary>
    public void RefreshMountsFromService()
    {
        ViewModel.RefreshMountsFromService();
    }

    // ── Unmount ───────────────────────────────────────────────────────────────

    private void UnmountSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        // Union selections from both the inline list (narrow/normal) and the
        // side panel (wide mode) so the button works regardless of layout state.
        List<MountEntry> selected = MountsListView.SelectedItems
            .Cast<MountEntry>()
            .Concat(MountsListViewSide.SelectedItems.Cast<MountEntry>())
            .Distinct()
            .ToList();

        ViewModel.UnmountSelected(selected);
    }

    public void SetExternalStatus(string message) => ViewModel.SetStatus(message);
}
