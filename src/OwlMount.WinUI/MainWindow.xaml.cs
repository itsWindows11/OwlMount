using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OwlMount.WinUI;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MountProcessStartupDelayMs = 1200;

    public ObservableCollection<MountEntry> Mounts { get; } = [];

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();

        ProviderComboBox.SelectedIndex = 0;
        BackendComboBox.SelectedIndex = 0;
        DriveLettersTextBox.Text = "M";
        NfsPathTextBox.Text = "/";

        RefreshMounts();
        UpdateProviderPanels();
    }

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

        var run = ResolveOwlMountCommand();
        int successCount = 0;
        var failures = new List<string>();
        foreach (string letter in letters)
        {
            var args = new List<string>
            {
                "mount",
                "--provider", provider,
                "--backend", backend,
                "--letter", letter,
            };

            AppendIfNotEmpty(args, "--label", LabelTextBox.Text);
            if (ReadOnlyCheckBox.IsChecked is true)
                args.Add("--read-only");

            switch (provider)
            {
                case "archive":
                    AppendIfNotEmpty(args, "--archive-file", ArchiveFileTextBox.Text);
                    break;
                case "local":
                case "kubo-mfs":
                    AppendIfNotEmpty(args, "--path", PathTextBox.Text);
                    break;
                case "kubo-ipfs":
                    AppendIfNotEmpty(args, "--cid", CidTextBox.Text);
                    break;
                case "kubo-ipns":
                    AppendIfNotEmpty(args, "--ipns", IpnsTextBox.Text);
                    break;
                case "s3":
                    AppendIfNotEmpty(args, "--bucket", S3BucketTextBox.Text);
                    AppendIfNotEmpty(args, "--prefix", S3PrefixTextBox.Text);
                    AppendIfNotEmpty(args, "--access-key", S3AccessKeyTextBox.Text);
                    AppendIfNotEmpty(args, "--secret-key", S3SecretKeyTextBox.Password);
                    AppendIfNotEmpty(args, "--region", S3RegionTextBox.Text);
                    AppendIfNotEmpty(args, "--endpoint", S3EndpointTextBox.Text);
                    break;
                case "nfs":
                    AppendIfNotEmpty(args, "--host", NfsHostTextBox.Text);
                    AppendIfNotEmpty(args, "--export", NfsExportTextBox.Text);
                    AppendIfNotEmpty(args, "--nfs-path", NfsPathTextBox.Text);
                    break;
            }

            if (provider is "kubo-mfs" or "kubo-ipfs" or "kubo-ipns")
                AppendIfNotEmpty(args, "--api-url", ApiUrlTextBox.Text);

            ProcessStartInfo startInfo = BuildStartInfo(run.fileName, run.argumentsPrefix, args);

            try
            {
                using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start mount process.");

                await Task.Delay(MountProcessStartupDelayMs);
                if (process.HasExited)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    string details = string.Join(Environment.NewLine, new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    failures.Add($"{letter}: {(string.IsNullOrWhiteSpace(details) ? "mount command exited immediately" : details)}");
                    continue;
                }

                successCount++;
            }
            catch (Exception ex)
            {
                failures.Add($"{letter}: {ex.Message}");
            }
        }

        RefreshMounts();
        if (failures.Count == 0)
            SetStatus($"Mount started for {successCount} drive(s): {string.Join(", ", letters)}.");
        else if (successCount == 0)
            SetStatus($"All mount requests failed: {string.Join(" | ", failures)}");
        else
            SetStatus($"Started {successCount} drive(s); failures: {string.Join(" | ", failures)}");
    }

    private async void UnmountSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        List<MountEntry> selected = MountsListView.SelectedItems.Cast<MountEntry>().ToList();
        if (selected.Count == 0)
        {
            SetStatus("Select one or more active mounts first.");
            return;
        }

        var run = ResolveOwlMountCommand();
        int successCount = 0;
        var failures = new List<string>();
        foreach (MountEntry mount in selected)
        {
            var args = new List<string> { "unmount", "--letter", mount.DriveLetter.TrimEnd(':') };
            ProcessStartInfo startInfo = BuildStartInfo(run.fileName, run.argumentsPrefix, args);

            try
            {
                using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start unmount command.");
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    successCount++;
                    continue;
                }

                string details = string.Join(Environment.NewLine, new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
                failures.Add($"{mount.DriveLetter}: {details}");
            }
            catch (Exception ex)
            {
                failures.Add($"{mount.DriveLetter}: {ex.Message}");
            }
        }

        RefreshMounts();
        if (failures.Count == 0)
            SetStatus($"Unmount signaled for {successCount} drive(s).");
        else if (successCount == 0)
            SetStatus($"Unmount failed: {string.Join(" | ", failures)}");
        else
            SetStatus($"Unmount signaled for {successCount}; failures: {string.Join(" | ", failures)}");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshMounts();

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderPanels();

    private void RefreshMounts()
    {
        Mounts.Clear();

        string pidsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount",
            "pids");

        if (!Directory.Exists(pidsDir))
        {
            SetStatus("No active mounts.");
            return;
        }

        bool any = false;
        foreach (string pidFile in Directory.GetFiles(pidsDir, "*.pid").OrderBy(f => f))
        {
            string driveLetter = Path.GetFileNameWithoutExtension(pidFile).ToUpperInvariant() + ":";
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                continue;

            bool alive;
            try
            {
                using Process process = Process.GetProcessById(pid);
                alive = !process.HasExited;
            }
            catch
            {
                alive = false;
            }

            if (!alive)
            {
                try
                {
                    File.Delete(pidFile);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                continue;
            }

            any = true;
            Mounts.Add(new MountEntry(driveLetter, pid.ToString(), "Running"));
        }

        if (!any)
            SetStatus("No active mounts.");
    }

    private void UpdateProviderPanels()
    {
        string provider = GetSelectedComboValue(ProviderComboBox) ?? "memory";

        LocalOrArchiveExpander.Visibility = provider is "local" or "archive" or "kubo-mfs"
            ? Visibility.Visible
            : Visibility.Collapsed;
        KuboExpander.Visibility = provider is "kubo-mfs" or "kubo-ipfs" or "kubo-ipns"
            ? Visibility.Visible
            : Visibility.Collapsed;
        S3Expander.Visibility = provider == "s3"
            ? Visibility.Visible
            : Visibility.Collapsed;
        NfsExpander.Visibility = provider == "nfs"
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (provider == "archive")
            ReadOnlyCheckBox.IsChecked = true;
    }

    private static string? GetSelectedComboValue(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static void AppendIfNotEmpty(List<string> args, string flag, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.Add(flag);
            args.Add(value.Trim());
        }
    }

    private static ProcessStartInfo BuildStartInfo(string fileName, IReadOnlyList<string> prefixArgs, IReadOnlyList<string> args)
    {
        var allArgs = prefixArgs.Concat(args.Select(QuoteArgument));
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(' ', allArgs),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static (string fileName, IReadOnlyList<string> argumentsPrefix) ResolveOwlMountCommand()
    {
        string baseDir = AppContext.BaseDirectory;

        string[] candidateFiles =
        [
            Path.Combine(baseDir, "owlmount.exe"),
            Path.Combine(baseDir, "owlmount"),
            Path.Combine(baseDir, "owlmount.dll"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "OwlMount.WinFspHost", "bin", "Debug", "net10.0-windows", "owlmount.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "OwlMount.WinFspHost", "bin", "Debug", "net10.0-windows", "owlmount.dll")),
        ];

        foreach (string candidate in candidateFiles)
        {
            if (!File.Exists(candidate))
                continue;

            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return ("dotnet", [QuoteArgument(candidate)]);

            return (candidate, []);
        }

        return ("owlmount", []);
    }

    private static IReadOnlyList<string> ParseDriveLetters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd(':').ToUpperInvariant())
            .Where(s => s.Length == 1 && char.IsLetter(s[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetStatus(string message) => StatusMessage = message;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record MountEntry(string DriveLetter, string ProcessId, string State);
