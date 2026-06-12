using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly AppSettingsService _appSettings;
    private readonly Dictionary<string, IFolder> _memoryRoots = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _stateSemaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string ConfigurationFileName = "mount-configurations.json";
    private static readonly Regex InvalidFileNameCharsRegex =
        new($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]",
            RegexOptions.CultureInvariant);

    public MountService(AppSettingsService appSettings)
    {
        _appSettings = appSettings;
        LoadConfigurationState();
    }

    /// <summary>Raised on the thread that changed the mount collection.</summary>
    public event EventHandler? MountsChanged;

    /// <summary>Snapshot of currently active mounts.</summary>
    public IReadOnlyList<ActiveMount> ActiveMounts
    {
        get
        {
            _stateSemaphore.Wait();
            try { return [.. _mounts.Values]; }
            finally { _stateSemaphore.Release(); }
        }
    }

    /// <summary>
    /// Gets whether mount-point configurations should be persisted to disk.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool SaveMountPointConfigurations { get; private set; } = true;

    /// <summary>Snapshot of current mount-point configurations in memory.</summary>
    public IReadOnlyList<ProviderOptions> MountConfigurations
    {
        get
        {
            _stateSemaphore.Wait();
            try { return [.. _mountConfigurations.Values]; }
            finally { _stateSemaphore.Release(); }
        }
    }

    /// <summary>
    /// Gets whether in-memory provider files should be exported to disk when the app exits.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool PersistMemoryFileSystemOnExit { get; private set; }

    /// <summary>Target directory for exported in-memory filesystem content.</summary>
    public string MemoryFileSystemPersistPath { get; private set; } = GetDefaultMemoryPersistPath();

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

        await _stateSemaphore.WaitAsync(ct);
        try
        {
            if (_mounts.ContainsKey(letter))
                return (false, $"Drive {mountPoint} is already mounted by this application.");
        }
        finally
        {
            _stateSemaphore.Release();
        }

        // ── Build the IFolder root ─────────────────────────────────────────────
        ProviderCreationResult pr;
        try
        {
            IFolder? existingRoot = null;
            if (normalizedOpts.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                _stateSemaphore.Wait(ct);
                try
                {
                    _memoryRoots.TryGetValue(letter, out existingRoot);
                }
                finally
                {
                    _stateSemaphore.Release();
                }
            }

            pr = await ProviderFactory.CreateAsync(normalizedOpts, existingRoot, ct);
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

        // ── Determine block cache settings with precedence rules ─────────────────
        // Global enable/disable takes precedence over per-mount setting.
        // Per-mount size overrides global size when set.
        // Avoid disk cache overhead for in-memory, archive, and local providers.
        BlockCache? blockCache = null;
        if (normalizedOpts.Provider is not ("memory" or "archive" or "local"))
        {
            // Determine if block cache should be enabled
                bool blockCacheEnabled = normalizedOpts.EnableBlockCache ??
                    _appSettings.GetSetting<bool>("EnableBlockCache");

            if (blockCacheEnabled)
            {
                // Determine block cache size: per-mount overrides global
                long blockCacheSize = normalizedOpts.BlockCacheSizeBytes ??
                    _appSettings.GetSetting<long>("DefaultBlockCacheSize");

                blockCache = new BlockCache(
                    providerId: $"{normalizedOpts.Provider}_{pr.Root.Id}",
                    blockSize: (int)blockCacheSize);
            }
        }

        string resolvedLabel = normalizedOpts.Label ?? DefaultLabel(normalizedOpts.Provider, normalizedOpts.Letter);

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
                    freeSize: pr.FreeSize,
                    volumeLabel: resolvedLabel);
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
            RootFolder = pr.Root,
            BackendInstance = backend,
            ExtraDisposable = pr.ExtraDisposable,
        };

        // Handle backend-initiated unmount (e.g. user ejects drive from Explorer).
        backend.Stopped += (_, _) => HandleExternalStop(letter);

        await _stateSemaphore.WaitAsync(ct);
        try
        {
            _mounts[letter] = mount;
            _mountConfigurations[letter] = normalizedOpts;
            if (normalizedOpts.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
                _memoryRoots[letter] = pr.Root;
        }
        finally
        {
            _stateSemaphore.Release();
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
        _stateSemaphore.Wait();
        try
        {
            if (!_mounts.Remove(letter, out mount))
                return;
            _mountConfigurations.Remove(letter);
            // Preserve the in-memory root so a later re-enable reuses the same data.
            if (mount.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
                _memoryRoots[letter] = mount.RootFolder;
        }
        finally
        {
            _stateSemaphore.Release();
        }
        StopMount(mount);
        if (mount is not null && mount.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            _stateSemaphore.Wait();
            try { _memoryRoots[letter] = mount.RootFolder; }
            finally { _stateSemaphore.Release(); }
        }
        MountsChanged?.Invoke(this, EventArgs.Empty);
        PersistConfigurationState();
    }

    /// <summary>
    /// Stops the active mount for the given drive letter but retains its saved
    /// configuration so it can be re-mounted later.
    /// </summary>
    public void Disable(string letter)
    {
        letter = letter.TrimEnd(':').ToUpperInvariant();
        ActiveMount? mount;
        _stateSemaphore.Wait();
        try
        {
            // Remove from active mounts but leave _mountConfigurations intact.
            if (!_mounts.Remove(letter, out mount))
                return;
            if (mount.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
                _memoryRoots[letter] = mount.RootFolder;
        }
        finally
        {
            _stateSemaphore.Release();
        }
        StopMount(mount);
        MountsChanged?.Invoke(this, EventArgs.Empty);
        // No PersistConfigurationState call — configuration is intentionally kept.
    }

    /// <summary>Stops and removes all active mounts.</summary>
    public void UnmountAll()
    {
        List<ActiveMount> mounts;
        _stateSemaphore.Wait();
        try
        {
            mounts = [.. _mounts.Values];
            foreach (string letter in _mounts.Keys.ToArray())
            {
                if (_mounts.TryGetValue(letter, out ActiveMount? active) &&
                    active.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
                {
                    _memoryRoots[letter] = active.RootFolder;
                }
                _mountConfigurations.Remove(letter);
            }
            _mounts.Clear();
        }
        finally
        {
            _stateSemaphore.Release();
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

    /// <summary>
    /// Deletes all on-disk block-cache files written by <see cref="BlockCache"/>.
    /// Returns the number of bytes freed.
    /// </summary>
    public Task<long> ClearDiskCacheAsync()
    {
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "Cache");
        return Task.Run(() => DeleteDirectoryContents(cacheRoot));
    }

    /// <summary>
    /// Deletes leftover ProjFS virtualisation-root directories that are not
    /// associated with any currently-active mount.
    /// Returns the number of bytes freed.
    /// </summary>
    public Task<long> ClearProjFsResidueAsync()
    {
        string virtRootBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "VirtRoot");

        HashSet<string> activeLetters;
        _stateSemaphore.Wait();
        try
        {
            activeLetters = new HashSet<string>(
                _mounts.Keys.Select(k => k.ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _stateSemaphore.Release();
        }

        return Task.Run(() =>
        {
            long freed = 0;
            if (!Directory.Exists(virtRootBase))
                return freed;

            foreach (string subDir in Directory.EnumerateDirectories(virtRootBase))
            {
                string letter = Path.GetFileName(subDir).ToUpperInvariant();
                if (activeLetters.Contains(letter))
                    continue; // don't touch live mounts

                freed += GetDirectorySize(subDir);
                ForceDeleteDirectory(subDir);
            }

            return freed;
        });
    }

    private static long DeleteDirectoryContents(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        long freed = GetDirectorySize(path);
        ForceDeleteDirectory(path);
        return freed;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch
        {
            return 0;
        }
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            // Clear read-only / hidden attributes so Delete doesn't throw.
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best-effort */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private void HandleExternalStop(string letter)
    {
        letter = letter.TrimEnd(':').ToUpperInvariant();
        ActiveMount? mount;
        _stateSemaphore.Wait();
        try
        {
            if (!_mounts.Remove(letter, out mount))
                return;
            _mountConfigurations.Remove(letter);
        }
        finally
        {
            _stateSemaphore.Release();
        }
        // Backend already stopped itself; only clean up extras.
        try { mount.ExtraDisposable?.Dispose(); } catch { /* best-effort */ }
        MountsChanged?.Invoke(this, EventArgs.Empty);
        PersistConfigurationState();
    }

    /// <summary>Enables or disables persistence for mount-point configurations.</summary>
    public void SetSaveMountPointConfigurations(bool enabled)
    {
        _stateSemaphore.Wait();
        try { SaveMountPointConfigurations = enabled; }
        finally { _stateSemaphore.Release(); }
        PersistConfigurationState();
    }

    /// <summary>Updates in-memory filesystem persistence options.</summary>
    public void SetMemoryFileSystemPersistenceOptions(bool enabled, string? path)
    {
        _stateSemaphore.Wait();
        try
        {
            PersistMemoryFileSystemOnExit = enabled;
            MemoryFileSystemPersistPath = NormalizePersistPath(path);
        }
        finally
        {
            _stateSemaphore.Release();
        }
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

    /// <summary>
    /// Exports all currently mounted in-memory provider roots to disk when enabled.
    /// </summary>
    public async Task<IReadOnlyList<string>> PersistMemoryFileSystemsAsync(CancellationToken ct = default)
    {
        bool enabled;
        string targetBasePath;
        List<(string DriveLetter, IFolder RootFolder)> memoryMounts;

        _stateSemaphore.Wait();
        try
        {
            enabled = PersistMemoryFileSystemOnExit;
            targetBasePath = MemoryFileSystemPersistPath;
            memoryMounts = [.. _mounts.Values
                .Where(m => m.Provider.Equals("memory", StringComparison.OrdinalIgnoreCase))
                .Select(m => (m.DriveLetter, m.RootFolder))];
        }
        finally
        {
            _stateSemaphore.Release();
        }

        if (!enabled || memoryMounts.Count == 0)
            return [];

        var failures = new List<string>();
        string resolvedBasePath = Path.GetFullPath(targetBasePath);
        Directory.CreateDirectory(resolvedBasePath);

        foreach ((string driveLetter, IFolder rootFolder) in memoryMounts)
        {
            string mountDir = Path.GetFullPath(
                Path.Combine(resolvedBasePath, SanitizePathSegment(driveLetter.TrimEnd(':'))));
            try
            {
                EnsurePathIsUnderBasePath(resolvedBasePath, mountDir);

                if (Directory.Exists(mountDir))
                    Directory.Delete(mountDir, recursive: true);

                Directory.CreateDirectory(mountDir);
                await ExportFolderToDirectoryAsync(rootFolder, mountDir, ct);
            }
            catch (Exception ex)
            {
                failures.Add($"{driveLetter}: {ex.Message}");
            }
        }

        return failures;
    }

    private static void StopMount(ActiveMount mount)
    {
        try { mount.BackendInstance.Stop(); } catch { /* best-effort */ }
        try { mount.BackendInstance.Dispose(); } catch { /* best-effort */ }
        try { mount.ExtraDisposable?.Dispose(); } catch { /* best-effort */ }
    }

    private static string DefaultLabel(string provider, string driveLetter) =>
        provider.ToUpperInvariant() switch
        {
            "MEMORY"    => $"OwlMount-Memory-{driveLetter}",
            "ARCHIVE"   => $"OwlMount-Archive-{driveLetter}",
            "LOCAL"     => $"OwlMount-Local-{driveLetter}",
            "KUBO-MFS"  => $"OwlMount-MFS-{driveLetter}",
            "KUBO-IPFS" => $"OwlMount-IPFS-{driveLetter}",
            "KUBO-IPNS" => $"OwlMount-IPNS-{driveLetter}",
            "S3"        => $"OwlMount-S3-{driveLetter}",
            "NFS"       => $"OwlMount-NFS-{driveLetter}",
            _           => $"OwlMount-{driveLetter}",
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
            MemorySizeLimitBytes = opts.MemorySizeLimitBytes,
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

    private static string GetDefaultMemoryPersistPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount",
            "WinUI",
            "MemoryProviderExports");

    private static string NormalizePersistPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? GetDefaultMemoryPersistPath() : path.Trim();

    private static string SanitizePathSegment(string name)
    {
        string sanitized = InvalidFileNameCharsRegex.Replace(name, "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static async Task ExportFolderToDirectoryAsync(
        IFolder source,
        string targetDirectory,
        CancellationToken ct)
    {
        await foreach (IStorableChild child in source.GetItemsAsync().WithCancellation(ct))
        {
            string childPath = Path.Combine(targetDirectory, SanitizePathSegment(child.Name));

            if (child is IFolder folder)
            {
                Directory.CreateDirectory(childPath);
                await ExportFolderToDirectoryAsync(folder, childPath, ct);
            }
            else if (child is IFile file)
            {
                await using Stream sourceStream = await file.OpenStreamAsync(FileAccess.Read, ct);
                await using var destinationStream = new FileStream(
                    childPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await sourceStream.CopyToAsync(destinationStream, 81920, ct);
            }
        }
    }

    private static void EnsurePathIsUnderBasePath(string basePath, string childPath)
    {
        string normalizedBasePath = Path.TrimEndingDirectorySeparator(basePath);
        string baseWithSeparator = normalizedBasePath + Path.DirectorySeparatorChar;

        bool isUnderBase = childPath.StartsWith(
            baseWithSeparator,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

        if (!isUnderBase)
            throw new InvalidOperationException(
                $"Refusing to modify path outside configured export base path: {childPath}");
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
            PersistMemoryFileSystemOnExit = state.PersistMemoryFileSystemOnExit;
            MemoryFileSystemPersistPath = NormalizePersistPath(state.MemoryFileSystemPersistPath);
            _appSettings.SetSetting("DefaultProvider", state.DefaultProvider);
            _appSettings.SetSetting("DefaultBackend", state.DefaultBackend);
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
            _stateSemaphore.Wait();
            try
            {
                state = new MountConfigurationState
                {
                    SaveMountPointConfigurations = SaveMountPointConfigurations,
                    PersistMemoryFileSystemOnExit = PersistMemoryFileSystemOnExit,
                    MemoryFileSystemPersistPath = MemoryFileSystemPersistPath,
                    DefaultProvider = _appSettings.GetSetting<string>("DefaultProvider"),
                    DefaultBackend = _appSettings.GetSetting<string>("DefaultBackend"),
                    MountPoints = SaveMountPointConfigurations
                        ? [.. _mountConfigurations.Values]
                        : [],
                };
            }
            finally
            {
                _stateSemaphore.Release();
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

    public void ExportConfigurationToFile(string destinationPath)
    {
        try
        {
            MountConfigurationState state;
            _stateSemaphore.Wait();
            try
            {
                state = new MountConfigurationState
                {
                    SaveMountPointConfigurations = SaveMountPointConfigurations,
                    PersistMemoryFileSystemOnExit = PersistMemoryFileSystemOnExit,
                    MemoryFileSystemPersistPath = MemoryFileSystemPersistPath,
                    DefaultProvider = _appSettings.GetSetting<string>("DefaultProvider"),
                    DefaultBackend = _appSettings.GetSetting<string>("DefaultBackend"),
                    MountPoints = [.. _mountConfigurations.Values],
                };
            }
            finally
            {
                _stateSemaphore.Release();
            }

            string json = JsonSerializer.Serialize(state, JsonOptions);
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(destinationPath, json);
        }
        catch
        {
            // best-effort export
        }
    }

    public void ImportConfigurationFromFile(string sourcePath, bool importMountPoints = true, bool importSettings = true)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return;

            string json = File.ReadAllText(sourcePath);
            MountConfigurationState? state = JsonSerializer.Deserialize<MountConfigurationState>(json, JsonOptions);
            if (state is null)
                return;

            _stateSemaphore.Wait();
            try
            {
                if (importSettings)
                {
                    SaveMountPointConfigurations = state.SaveMountPointConfigurations;
                    PersistMemoryFileSystemOnExit = state.PersistMemoryFileSystemOnExit;
                    MemoryFileSystemPersistPath = NormalizePersistPath(state.MemoryFileSystemPersistPath);
                    _appSettings.SetSetting("DefaultProvider", state.DefaultProvider);
                }

                if (importMountPoints && state.MountPoints.Count > 0)
                {
                    _mountConfigurations.Clear();
                    foreach (var mountPoint in state.MountPoints)
                    {
                        string letter = mountPoint.Letter.TrimEnd(':').ToUpperInvariant();
                        _mountConfigurations[letter] = mountPoint;
                    }
                }
            }
            finally
            {
                _stateSemaphore.Release();
            }

            PersistConfigurationState();
        }
        catch
        {
            // best-effort import
        }
    }
}

internal sealed class MountConfigurationState
{
    public bool SaveMountPointConfigurations { get; init; } = true;
    public bool PersistMemoryFileSystemOnExit { get; init; }
    public string? MemoryFileSystemPersistPath { get; init; }
    public string DefaultProvider { get; init; } = "memory";
    public string DefaultBackend { get; init; } = "winfsp";
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
    public required IFolder RootFolder { get; init; }
    public required IOwlMountBackend BackendInstance { get; init; }
    internal IDisposable? ExtraDisposable { get; init; }
}
