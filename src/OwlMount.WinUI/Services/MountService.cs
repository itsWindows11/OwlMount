using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using OwlCore.Storage;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;
using OwlMount.Core.Windows.Backends;

namespace OwlMount.WinUI.Services;

/// <summary>
/// Manages the lifecycle of in-process VFS mounts. Each mount lives entirely
/// within this process — no external CLI process is spawned.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountService : IDisposable
{
    private readonly Dictionary<string, ActiveMount> _mounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderOptions> _mountConfigurations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string ConfigurationFileName = "mount-configurations.json";

    public MountService()
    {
        LoadConfigurationState();
    }

    /// <summary>Raised on the thread that changed the mount collection.</summary>
    public event EventHandler? MountsChanged;

    /// <summary>Snapshot of currently active mounts.</summary>
    public IReadOnlyList<ActiveMount> ActiveMounts
    {
        get { lock (_lock) { return [.. _mounts.Values]; } }
    }

    /// <summary>
    /// Gets whether mount-point configurations should be persisted to disk.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool SaveMountPointConfigurations { get; private set; } = true;

    /// <summary>Snapshot of current mount-point configurations in memory.</summary>
    public IReadOnlyList<ProviderOptions> MountConfigurations
    {
        get { lock (_lock) { return [.. _mountConfigurations.Values]; } }
    }

    /// <summary>
    /// Builds the provider root and starts the VFS backend for <paramref name="opts"/>.
    /// Returns <c>(true, null)</c> on success, or <c>(false, errorMessage)</c> on failure.
    /// </summary>
    public async Task<(bool Success, string? Error)> MountAsync(
        ProviderOptions opts, CancellationToken ct = default)
    {
        ProviderOptions normalizedOpts = NormalizeOptions(opts);
        string letter = normalizedOpts.Letter.TrimEnd(':').ToUpperInvariant();
        string mountPoint = letter + ":";

        lock (_lock)
        {
            if (_mounts.ContainsKey(letter))
                return (false, $"Drive {mountPoint} is already mounted by this application.");
        }

        // ── Build the IFolder root ─────────────────────────────────────────────
        ProviderCreationResult pr;
        try
        {
            pr = await ProviderFactory.CreateAsync(normalizedOpts, ct);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        // ── Build VFS components ───────────────────────────────────────────────
        bool isReadOnly = pr.ForceReadOnly || pr.Root is not IModifiableFolder;
        var rangeReaders = new RangeReaderRegistry();
        var sizeProviders = new SizeProviderRegistry();
        if (pr.SizeProvider is not null && pr.SizePredicate is not null)
            sizeProviders.Register(pr.SizePredicate, pr.SizeProvider);

        // Avoid disk cache overhead for in-memory and local providers.
        BlockCache? blockCache = normalizedOpts.Provider is "memory" or "local"
            ? null
            : new BlockCache(providerId: $"{normalizedOpts.Provider}_{pr.Root.Id}");

        string resolvedLabel = normalizedOpts.Label ?? DefaultLabel(normalizedOpts.Provider);

        // ── Create backend ─────────────────────────────────────────────────────
        IOwlMountBackend backend;
        try
        {
            if (normalizedOpts.Backend.Equals("projfs", StringComparison.OrdinalIgnoreCase))
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                {
                    pr.ExtraDisposable?.Dispose();
                    return (false, "ProjFS requires Windows 10 version 1803 (build 17763) or later.");
                }
#pragma warning disable CA1416 // guarded by version check above
                backend = new ProjFsBackend(pr.Root, blockCache, rangeReaders, sizeProviders, isReadOnly);
#pragma warning restore CA1416
            }
            else
            {
                backend = new WinFspBackend(
                    pr.Root, blockCache, rangeReaders, sizeProviders,
                    readOnly: isReadOnly,
                    totalSize: pr.TotalSize,
                    freeSize: pr.FreeSize);
            }
        }
        catch (Exception ex)
        {
            pr.ExtraDisposable?.Dispose();
            return (false, ex.Message);
        }

        // ── Start the backend (may block briefly; also captures Console.Error output) ──
        var errorBuffer = new StringBuilder();
        bool started;
        try
        {
            started = await Task.Run(() =>
            {
                // Temporarily redirect Console.Error so the backend's diagnostic
                // messages (which write to Console.Error) are surfaced to the GUI.
                var prev = Console.Error;
                Console.SetError(new StringWriter(errorBuffer));
                try { return backend.Start(mountPoint, resolvedLabel); }
                finally { Console.SetError(prev); }
            }, ct);
        }
        catch (Exception ex)
        {
            backend.Dispose();
            pr.ExtraDisposable?.Dispose();
            return (false, ex.Message);
        }

        if (!started)
        {
            backend.Dispose();
            pr.ExtraDisposable?.Dispose();
            string captured = errorBuffer.ToString().Trim();
            return (false, string.IsNullOrWhiteSpace(captured)
                ? $"Failed to mount {mountPoint}. Ensure the required backend (WinFsp or ProjFS) is installed."
                : captured);
        }

        var mount = new ActiveMount
        {
            DriveLetter = mountPoint,
            Label = resolvedLabel,
            Provider = normalizedOpts.Provider,
            BackendName = normalizedOpts.Backend,
            IsReadOnly = isReadOnly,
            BackendInstance = backend,
            ExtraDisposable = pr.ExtraDisposable,
        };

        // Handle backend-initiated unmount (e.g. user ejects drive from Explorer).
        backend.Stopped += (_, _) => HandleExternalStop(letter);

        lock (_lock)
        {
            _mounts[letter] = mount;
            _mountConfigurations[letter] = normalizedOpts;
        }
        MountsChanged?.Invoke(this, EventArgs.Empty);
        PersistConfigurationState();
        return (true, null);
    }

    /// <summary>Stops and removes the mount for the given drive letter.</summary>
    public void Unmount(string letter)
    {
        letter = letter.TrimEnd(':').ToUpperInvariant();
        ActiveMount? mount;
        lock (_lock)
        {
            if (!_mounts.Remove(letter, out mount))
                return;
            _mountConfigurations.Remove(letter);
        }
        StopMount(mount);
        MountsChanged?.Invoke(this, EventArgs.Empty);
        PersistConfigurationState();
    }

    /// <summary>Stops and removes all active mounts.</summary>
    public void UnmountAll()
    {
        List<ActiveMount> mounts;
        lock (_lock)
        {
            mounts = [.. _mounts.Values];
            foreach (string letter in _mounts.Keys.ToArray())
                _mountConfigurations.Remove(letter);
            _mounts.Clear();
        }
        foreach (ActiveMount m in mounts)
            StopMount(m);
        if (mounts.Count > 0)
        {
            MountsChanged?.Invoke(this, EventArgs.Empty);
            PersistConfigurationState();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => UnmountAll();

    // ── Private helpers ────────────────────────────────────────────────────────

    private void HandleExternalStop(string letter)
    {
        letter = letter.TrimEnd(':').ToUpperInvariant();
        ActiveMount? mount;
        lock (_lock)
        {
            if (!_mounts.Remove(letter, out mount))
                return;
            _mountConfigurations.Remove(letter);
        }
        // Backend already stopped itself; only clean up extras.
        try { mount.ExtraDisposable?.Dispose(); } catch { /* best-effort */ }
        MountsChanged?.Invoke(this, EventArgs.Empty);
        PersistConfigurationState();
    }

    /// <summary>Enables or disables persistence for mount-point configurations.</summary>
    public void SetSaveMountPointConfigurations(bool enabled)
    {
        lock (_lock) { SaveMountPointConfigurations = enabled; }
        PersistConfigurationState();
    }

    /// <summary>
    /// Attempts to mount all currently known configurations.
    /// Used at app startup to restore persisted mount points when enabled.
    /// </summary>
    public async Task<(int SuccessCount, IReadOnlyList<string> Failures)> RestoreConfiguredMountsAsync(
        CancellationToken ct = default)
    {
        List<ProviderOptions> configs = [.. MountConfigurations];
        int successCount = 0;
        var failures = new List<string>();

        foreach (ProviderOptions config in configs)
        {
            string letter = config.Letter.TrimEnd(':').ToUpperInvariant();
            var (success, error) = await MountAsync(config, ct);
            if (success)
                successCount++;
            else
                failures.Add($"{letter}: {error ?? "Unknown error"}");
        }

        return (successCount, failures);
    }

    private static void StopMount(ActiveMount mount)
    {
        try { mount.BackendInstance.Stop(); } catch { /* best-effort */ }
        try { mount.BackendInstance.Dispose(); } catch { /* best-effort */ }
        try { mount.ExtraDisposable?.Dispose(); } catch { /* best-effort */ }
    }

    private static string DefaultLabel(string provider) =>
        provider.ToUpperInvariant() switch
        {
            "MEMORY"    => "OwlMount (Memory)",
            "ARCHIVE"   => "OwlMount (Archive)",
            "LOCAL"     => "OwlMount (Local)",
            "KUBO-MFS"  => "OwlMount (MFS)",
            "KUBO-IPFS" => "OwlMount (IPFS)",
            "KUBO-IPNS" => "OwlMount (IPNS)",
            "S3"        => "OwlMount (S3)",
            "NFS"       => "OwlMount (NFS)",
            _           => "OwlMount",
        };

    private static ProviderOptions NormalizeOptions(ProviderOptions opts)
    {
        string provider = string.IsNullOrWhiteSpace(opts.Provider) ? "memory" : opts.Provider.Trim();
        string backend = string.IsNullOrWhiteSpace(opts.Backend) ? "winfsp" : opts.Backend.Trim();
        string letter = string.IsNullOrWhiteSpace(opts.Letter)
            ? "M"
            : opts.Letter.Trim().TrimEnd(':').ToUpperInvariant();

        return new ProviderOptions
        {
            Provider = provider,
            Backend = backend,
            Letter = letter,
            Label = opts.Label,
            ForceReadOnly = opts.ForceReadOnly,
            Path = opts.Path,
            ArchiveFile = opts.ArchiveFile,
            ApiUrl = opts.ApiUrl,
            Cid = opts.Cid,
            IpnsAddress = opts.IpnsAddress,
            S3Bucket = opts.S3Bucket,
            S3Prefix = opts.S3Prefix,
            S3AccessKey = opts.S3AccessKey,
            S3SecretKey = opts.S3SecretKey,
            S3Region = opts.S3Region,
            S3Endpoint = opts.S3Endpoint,
            NfsHost = opts.NfsHost,
            NfsExport = opts.NfsExport,
            NfsPath = string.IsNullOrWhiteSpace(opts.NfsPath) ? "/" : opts.NfsPath,
        };
    }

    private static string GetConfigurationFilePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount",
            "WinUI");
        return Path.Combine(dir, ConfigurationFileName);
    }

    private void LoadConfigurationState()
    {
        try
        {
            string path = GetConfigurationFilePath();
            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);
            MountConfigurationState? state = JsonSerializer.Deserialize<MountConfigurationState>(json, JsonOptions);
            if (state is null)
                return;

            SaveMountPointConfigurations = state.SaveMountPointConfigurations;
            _mountConfigurations.Clear();

            foreach (ProviderOptions opts in state.MountPoints)
            {
                ProviderOptions normalized = NormalizeOptions(opts);
                string letter = normalized.Letter.TrimEnd(':').ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(letter))
                    _mountConfigurations[letter] = normalized;
            }
        }
        catch
        {
            // best-effort load
        }
    }

    private void PersistConfigurationState()
    {
        try
        {
            MountConfigurationState state;
            lock (_lock)
            {
                state = new MountConfigurationState
                {
                    SaveMountPointConfigurations = SaveMountPointConfigurations,
                    MountPoints = SaveMountPointConfigurations
                        ? [.. _mountConfigurations.Values]
                        : [],
                };
            }

            string path = GetConfigurationFilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // best-effort persistence
        }
    }
}

internal sealed class MountConfigurationState
{
    public bool SaveMountPointConfigurations { get; init; } = true;
    public List<ProviderOptions> MountPoints { get; init; } = [];
}

/// <summary>Represents a single active in-process VFS mount.</summary>
public sealed class ActiveMount
{
    /// <summary>Drive letter with trailing colon, e.g. <c>M:</c>.</summary>
    public required string DriveLetter { get; init; }
    public required string Label { get; init; }
    public required string Provider { get; init; }
    public required string BackendName { get; init; }
    public required bool IsReadOnly { get; init; }
    internal required IOwlMountBackend BackendInstance { get; init; }
    internal IDisposable? ExtraDisposable { get; init; }
}
