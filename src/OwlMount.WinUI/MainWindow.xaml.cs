using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwlMount.WinUI.Services;

namespace OwlMount.WinUI;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    // ── Bindable state ────────────────────────────────────────────────────────

    public ObservableCollection<MountEntry> Mounts { get; } = [];

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Construction ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        ProviderComboBox.SelectedIndex = 0;
        BackendComboBox.SelectedIndex = 0;
        DriveLettersTextBox.Text = "M";
        NfsPathTextBox.Text = "/";

        RefreshMountsFromService();
        UpdateProviderPanels();
    }

    // ── Public API called by App ──────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the active-mounts list from the in-process <see cref="MountService"/>.
    /// Called both locally and from <see cref="App.OnMountsChanged"/>.
    /// </summary>
    public void RefreshMountsFromService()
    {
        Mounts.Clear();
        foreach (ActiveMount m in App.MountService.ActiveMounts)
        {
            string state = m.IsReadOnly ? "Running (R/O)" : "Running";
            Mounts.Add(new MountEntry(m.DriveLetter, m.Label, m.Provider, state));
        }

        if (Mounts.Count == 0)
            SetStatus("No active mounts.");
    }

    // ── Mount ─────────────────────────────────────────────────────────────────

    private async void MountButton_Click(object sender, RoutedEventArgs e)
    {
        string? provider = GetSelectedComboValue(ProviderComboBox);
        string? backend = GetSelectedComboValue(BackendComboBox);

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(backend))
        {
            SetStatus("Provider and backend are required.");
            return;
        }

        IReadOnlyList<string> letters = ParseDriveLetters(DriveLettersTextBox.Text);
        if (letters.Count == 0)
        {
            SetStatus("Enter one or more drive letters (e.g. M or M,R,X).");
            return;
        }

        // Capture all field values up-front (UI fields must be read on UI thread).
        string? label = NullIfBlank(LabelTextBox.Text);
        bool readOnly = ReadOnlyCheckBox.IsChecked is true;
        string? path = NullIfBlank(PathTextBox.Text);
        string? archiveFile = NullIfBlank(ArchiveFileTextBox.Text);
        string? apiUrl = NullIfBlank(ApiUrlTextBox.Text);
        string? cid = NullIfBlank(CidTextBox.Text);
        string? ipns = NullIfBlank(IpnsTextBox.Text);
        string? s3Bucket = NullIfBlank(S3BucketTextBox.Text);
        string? s3Prefix = NullIfBlank(S3PrefixTextBox.Text);
        string? s3Key = NullIfBlank(S3AccessKeyTextBox.Text);
        string? s3Secret = NullIfBlank(S3SecretKeyTextBox.Password);
        string? s3Region = NullIfBlank(S3RegionTextBox.Text);
        string? s3Endpoint = NullIfBlank(S3EndpointTextBox.Text);
        string? nfsHost = NullIfBlank(NfsHostTextBox.Text);
        string? nfsExport = NullIfBlank(NfsExportTextBox.Text);
        string nfsPath = NullIfBlank(NfsPathTextBox.Text) ?? "/";

        int successCount = 0;
        var failures = new List<string>();

        foreach (string letter in letters)
        {
            var opts = new ProviderOptions
            {
                Provider = provider,
                Backend = backend,
                Letter = letter,
                Label = label,
                ForceReadOnly = readOnly,
                Path = path,
                ArchiveFile = archiveFile,
                ApiUrl = apiUrl,
                Cid = cid,
                IpnsAddress = ipns,
                S3Bucket = s3Bucket,
                S3Prefix = s3Prefix,
                S3AccessKey = s3Key,
                S3SecretKey = s3Secret,
                S3Region = s3Region,
                S3Endpoint = s3Endpoint,
                NfsHost = nfsHost,
                NfsExport = nfsExport,
                NfsPath = nfsPath,
            };

            var (success, error) = await App.MountService.MountAsync(opts);
            if (success)
                successCount++;
            else
                failures.Add($"{letter}: {error ?? "Unknown error"}");
        }

        RefreshMountsFromService();

        if (failures.Count == 0)
            SetStatus($"Mounted {successCount} drive(s): {string.Join(", ", letters.Select(l => l + ":"))}.");
        else if (successCount == 0)
            SetStatus($"All mounts failed: {string.Join(" | ", failures)}");
        else
            SetStatus($"Mounted {successCount} drive(s); failures: {string.Join(" | ", failures)}");
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

        if (selected.Count == 0)
        {
            SetStatus("Select one or more active mounts first.");
            return;
        }

        foreach (MountEntry mount in selected)
            App.MountService.Unmount(mount.DriveLetter);

        RefreshMountsFromService();
        SetStatus(selected.Count == 1
            ? $"Unmounted {selected[0].DriveLetter}."
            : $"Unmounted {selected.Count} drive(s).");
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshMountsFromService();

    private void ExitButton_Click(object sender, RoutedEventArgs e) =>
        ((App)Current).ExitApp();

    // ── Provider panel visibility ─────────────────────────────────────────────

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateProviderPanels();

    private void UpdateProviderPanels()
    {
        string provider = GetSelectedComboValue(ProviderComboBox) ?? "memory";

        LocalOrArchiveExpander.Visibility = provider is "local" or "archive" or "kubo-mfs"
            ? Visibility.Visible : Visibility.Collapsed;
        KuboExpander.Visibility = provider is "kubo-mfs" or "kubo-ipfs" or "kubo-ipns"
            ? Visibility.Visible : Visibility.Collapsed;
        S3Expander.Visibility = provider == "s3"
            ? Visibility.Visible : Visibility.Collapsed;
        NfsExpander.Visibility = provider == "nfs"
            ? Visibility.Visible : Visibility.Collapsed;

        if (provider == "archive")
            ReadOnlyCheckBox.IsChecked = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetSelectedComboValue(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> ParseDriveLetters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd(':').ToUpperInvariant())
            .Where(s => s.Length == 1 && char.IsLetter(s[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetStatus(string message) => StatusMessage = message;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>View model for a single row in the active-mounts list.</summary>
public sealed record MountEntry(string DriveLetter, string Label, string Provider, string State);

