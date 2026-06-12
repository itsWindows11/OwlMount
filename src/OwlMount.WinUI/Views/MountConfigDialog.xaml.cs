using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwlMount.WinUI.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.Extensions.DependencyInjection;

namespace OwlMount.WinUI.Views;

public sealed partial class MountConfigDialog : UserControl
{
    private Window? _window;
    private long _maxMemoryMb;   // free RAM in MiB at dialog-open time

    public MountConfigDialog()
    {
        InitializeComponent();
        BackendBox.ItemsSource = new[] { "winfsp", "projfs" };
        ProviderBox.ItemsSource = new[] { "default", "memory", "archive", "local", "kubo-mfs", "kubo-ipfs", "kubo-ipns", "s3", "nfs" };
    }

    public void Initialize(Window window, ProviderOptions? existing = null)
    {
        _window = window;
        var app = (App)Application.Current;
        var settings = app.Services.GetRequiredService<AppSettingsService>();
        string defaultProvider = settings.GetSetting<string>("DefaultProvider");
        string defaultBackend = settings.GetSetting<string>("DefaultBackend");

        // Compute free RAM (in MiB) for the memory size slider ceiling.
        ulong availBytes = NativeMethods.GetAvailablePhysicalMemory();
        _maxMemoryMb = availBytes > 0
            ? Math.Max(64, (long)(availBytes / (1024 * 1024)))
            : 4096;

        MemorySizeSlider.Maximum = _maxMemoryMb;
        long defaultMb = existing?.MemorySizeLimitBytes is > 0
            ? existing.MemorySizeLimitBytes.Value / (1024 * 1024)
            : _maxMemoryMb;
        MemorySizeSlider.Value = Math.Clamp(defaultMb, 64, _maxMemoryMb);
        UpdateMemorySizeLabel();

        ProviderBox.SelectedItem = existing?.Provider ?? defaultProvider;
        ProviderBox.IsEnabled = existing is null;
        BackendBox.SelectedItem = existing?.Backend ?? defaultBackend;
        BackendBox.IsEnabled = existing is null;
        RefreshDriveLetterChoices(existing?.Letter);
        LetterBox.SelectedItem = NormalizeDriveLetter(existing?.Letter) ?? LetterBox.Items.FirstOrDefault() as string;
        LabelBox.Text = existing?.Label ?? string.Empty;
        PathBox.Text = existing?.Path ?? string.Empty;
        ArchiveBox.Text = existing?.ArchiveFile ?? string.Empty;
        ApiUrlBox.Text = existing?.ApiUrl ?? string.Empty;
        CidBox.Text = existing?.Cid ?? string.Empty;
        IpnsBox.Text = existing?.IpnsAddress ?? string.Empty;
        BucketBox.Text = existing?.S3Bucket ?? string.Empty;
        PrefixBox.Text = existing?.S3Prefix ?? string.Empty;
        AccessKeyBox.Text = existing?.S3AccessKey ?? string.Empty;
        SecretKeyBox.Password = existing?.S3SecretKey ?? string.Empty;
        RegionBox.Text = existing?.S3Region ?? string.Empty;
        EndpointBox.Text = existing?.S3Endpoint ?? string.Empty;
        NfsHostBox.Text = existing?.NfsHost ?? string.Empty;
        NfsExportBox.Text = existing?.NfsExport ?? string.Empty;
        NfsPathBox.Text = existing?.NfsPath ?? "/";

        // Initialize block cache settings
        string enableBlockCacheText = existing?.EnableBlockCache switch
        {
            true => "Enabled",
            false => "Disabled",
            null => "Use global setting"
        };
        EnableBlockCacheBox.SelectedItem = EnableBlockCacheBox.Items.FirstOrDefault(i => 
            i is ComboBoxItem item && item.Content?.ToString() == enableBlockCacheText) ?? 
            EnableBlockCacheBox.Items[0];

        // For size, find matching tag or default to "Use global setting"
        if (existing?.BlockCacheSizeBytes is > 0)
        {
            BlockCacheSizeBox.SelectedItem = BlockCacheSizeBox.Items.FirstOrDefault(i =>
                i is ComboBoxItem item && item.Tag?.ToString() == existing.BlockCacheSizeBytes.ToString()) ??
                BlockCacheSizeBox.Items[0];
        }
        else
        {
            BlockCacheSizeBox.SelectedItem = BlockCacheSizeBox.Items[0];
        }

        UpdateVisibility();
    }

    public ProviderOptions GetOptions()
    {
        string provider = (ProviderBox.SelectedItem as string) ?? "default";
        long? memorySizeLimit = provider == "memory"
            ? (long)MemorySizeSlider.Value * 1024 * 1024
            : null;

        // Parse block cache settings (null if "Use global setting")
        bool? enableBlockCache = null;
        long? blockCacheSizeBytes = null;

        if (EnableBlockCacheBox.SelectedItem is ComboBoxItem enableItem && enableItem.Content is string enableText)
        {
            enableBlockCache = enableText switch
            {
                "Enabled" => true,
                "Disabled" => false,
                _ => null
            };
        }

        if (BlockCacheSizeBox.SelectedItem is ComboBoxItem sizeItem)
        {
            if (sizeItem.Tag is long sizeTag && sizeTag > 0)
                blockCacheSizeBytes = sizeTag;
        }

        return new()
        {
            Provider = provider,
            Backend = (BackendBox.SelectedItem as string) ?? "winfsp",
            Letter = NormalizeDriveLetter(LetterBox.SelectedItem as string) ?? string.Empty,
            Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
            MemorySizeLimitBytes = memorySizeLimit,
            EnableBlockCache = enableBlockCache,
            BlockCacheSizeBytes = blockCacheSizeBytes,
            Path = string.IsNullOrWhiteSpace(PathBox.Text) ? null : PathBox.Text.Trim(),
            ArchiveFile = string.IsNullOrWhiteSpace(ArchiveBox.Text) ? null : ArchiveBox.Text.Trim(),
            ApiUrl = string.IsNullOrWhiteSpace(ApiUrlBox.Text) ? null : ApiUrlBox.Text.Trim(),
            Cid = string.IsNullOrWhiteSpace(CidBox.Text) ? null : CidBox.Text.Trim(),
            IpnsAddress = string.IsNullOrWhiteSpace(IpnsBox.Text) ? null : IpnsBox.Text.Trim(),
            S3Bucket = string.IsNullOrWhiteSpace(BucketBox.Text) ? null : BucketBox.Text.Trim(),
            S3Prefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? null : PrefixBox.Text.Trim(),
            S3AccessKey = string.IsNullOrWhiteSpace(AccessKeyBox.Text) ? null : AccessKeyBox.Text.Trim(),
            S3SecretKey = string.IsNullOrWhiteSpace(SecretKeyBox.Password) ? null : SecretKeyBox.Password.Trim(),
            S3Region = string.IsNullOrWhiteSpace(RegionBox.Text) ? null : RegionBox.Text.Trim(),
            S3Endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? null : EndpointBox.Text.Trim(),
            NfsHost = string.IsNullOrWhiteSpace(NfsHostBox.Text) ? null : NfsHostBox.Text.Trim(),
            NfsExport = string.IsNullOrWhiteSpace(NfsExportBox.Text) ? null : NfsExportBox.Text.Trim(),
            NfsPath = string.IsNullOrWhiteSpace(NfsPathBox.Text) ? "/" : NfsPathBox.Text.Trim(),
        };
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private async void BrowsePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
            return;

        var picker = new FolderPicker();
        InitializePicker(picker, _window);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            PathBox.Text = folder.Path;
    }

    private async void BrowseArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
            return;

        var picker = new FileOpenPicker();
        InitializePicker(picker, _window);
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            ArchiveBox.Text = file.Path;
    }

    private void UpdateVisibility()
    {
        string provider = (ProviderBox.SelectedItem as string) ?? "memory";
        MemorySection.Visibility = provider == "memory" ? Visibility.Visible : Visibility.Collapsed;
        LocalArchiveSection.Visibility = provider is "local" or "archive" ? Visibility.Visible : Visibility.Collapsed;
        KuboSection.Visibility = provider.StartsWith("kubo-", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        S3Section.Visibility = provider == "s3" ? Visibility.Visible : Visibility.Collapsed;
        NfsSection.Visibility = provider == "nfs" ? Visibility.Visible : Visibility.Collapsed;
        BlockCacheSection.Visibility = provider == "memory" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MemorySizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => UpdateMemorySizeLabel();

    private void UpdateMemorySizeLabel()
    {
        if (MemorySizeLabel is null) return;
        long mb = (long)MemorySizeSlider.Value;
        MemorySizeLabel.Text = mb >= 1024
            ? $"{mb / 1024.0:F1} GB / {_maxMemoryMb / 1024.0:F1} GB available"
            : $"{mb} MB / {_maxMemoryMb} MB available";
    }

    private void RefreshDriveLetterChoices(string? preferredLetter = null)
    {
        var usedLetters = new HashSet<string>(
            DriveInfo.GetDrives()
                .Select(d => NormalizeDriveLetter(d.Name))
                .Where(s => !string.IsNullOrWhiteSpace(s))!,
            StringComparer.OrdinalIgnoreCase);

        string? preferred = NormalizeDriveLetter(preferredLetter);

        var availableLetters = Enumerable.Range('A', 26)
            .Select(c => ((char)c).ToString())
            .Where(letter => !usedLetters.Contains(letter) || (preferred is not null && string.Equals(letter, preferred, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        LetterBox.ItemsSource = availableLetters;
    }

    private static string? NormalizeDriveLetter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string value = raw.Trim().TrimEnd(':', '\\', '/').ToUpperInvariant();
        return value.Length == 1 && value[0] is >= 'A' and <= 'Z' ? value : null;
    }

    public static async Task<ProviderOptions?> ShowAsync(Window window, ProviderOptions? existing = null)
    {
        var content = new MountConfigDialog();
        content.Initialize(window, existing);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Add mount configuration" : $"Edit {MainWindowViewModel.GetProviderDisplayName(existing.Provider)}",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = ((FrameworkElement)window.Content).XamlRoot,
            MinWidth = 760,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? content.GetOptions()
            : null;
    }

    private static void InitializePicker(FolderPicker picker, Window window)
    {
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }

    private static void InitializePicker(FileOpenPicker picker, Window window)
    {
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
