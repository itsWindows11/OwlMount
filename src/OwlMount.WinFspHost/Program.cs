using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Amazon.S3;
using Fsp;
using Ipfs.Http;
using NfsSharp;
using OwlCore.Kubo;
using OwlCore.Storage;
using OwlCore.Storage.AmazonS3;
using OwlCore.Storage.Memory;
using OwlCore.Storage.NfsSharp;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;
using OwlMount.WinFspHost;

[SupportedOSPlatform("windows")]
static partial class Program
{
    static async Task<int> Main(string[] args)
    {
        // Support both the new subcommand form ("mount"/"unmount"/"list") and the
        // legacy flag-only form ("--provider …") for backward compatibility.
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        if (sub.StartsWith("--") || args.Length == 0)
        {
            // Legacy: no subcommand — treat the whole args array as "mount" args.
            return await RunMountAsync(args);
        }

        return sub switch
        {
            "mount"   => await RunMountAsync(args[1..]),
            "unmount" => RunUnmount(args[1..]),
            "list"    => RunList(),
            _         => PrintUsage(),
        };
    }

    // ── mount ─────────────────────────────────────────────────────────────────

    static async Task<int> RunMountAsync(string[] args)
    {
        string  provider      = "memory";
        string  letter        = "M";
        string? label         = null;
        string? path          = null;
        bool    forceReadOnly = false;
        ulong? totalSize      = null;
        ulong? freeSize       = null;
        // S3
        string? s3Bucket   = null;
        string? s3Prefix   = null;
        string? s3Key      = null;
        string? s3Secret   = null;
        string? s3Region   = null;
        string? s3Endpoint = null;
        // Kubo
        string? apiUrl     = null;
        string? cid        = null;
        string? ipnsAddr   = null;
        // NFS
        string? nfsHost    = null;
        string? nfsExport  = null;
        string? nfsPath    = "/";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--provider":    provider   = args[++i]; break;
                case "--letter":      letter     = args[++i]; break;
                case "--label":       label      = args[++i]; break;
                case "--path":        path       = args[++i]; break;
                case "--bucket":      s3Bucket   = args[++i]; break;
                case "--prefix":      s3Prefix   = args[++i]; break;
                case "--access-key":  s3Key      = args[++i]; break;
                case "--secret-key":  s3Secret   = args[++i]; break;
                case "--region":      s3Region   = args[++i]; break;
                case "--endpoint":    s3Endpoint = args[++i]; break;
                case "--api-url":     apiUrl     = args[++i]; break;
                case "--cid":         cid        = args[++i]; break;
                case "--ipns":        ipnsAddr   = args[++i]; break;
                case "--host":        nfsHost    = args[++i]; break;
                case "--export":      nfsExport  = args[++i]; break;
                case "--nfs-path":    nfsPath    = args[++i]; break;
                case "--read-only":
                case "--readonly":
                    forceReadOnly = true;
                    break;
            }
        }

        // ── Resolve the root IFolder ──────────────────────────────────────────
        IFolder root;
        string  displayRoot;

        switch (provider.ToLowerInvariant())
        {
            // ── memory ────────────────────────────────────────────────────────
            case "memory":
                root = new MemoryFolder(id: "memory-root", name: "memory-root");
                displayRoot = "(in-memory, starts empty)";
                (totalSize, freeSize) = GetMemoryVolumeSpace();
                break;

            // ── kubo-mfs ──────────────────────────────────────────────────────
            case "kubo-mfs":
            {
                string mfsPath = path ?? "/";
                var client = new IpfsClient(apiUrl ?? "http://127.0.0.1:5001");
                root        = new MfsFolder(mfsPath, client);
                displayRoot = $"kubo MFS {mfsPath} @ {client.ApiUri}";
                (totalSize, freeSize) = await TryGetKuboRepositorySpaceAsync(client);
                break;
            }

            // ── kubo-ipfs ─────────────────────────────────────────────────────
            case "kubo-ipfs":
            {
                if (string.IsNullOrWhiteSpace(cid))
                {
                    Console.Error.WriteLine("Error: --cid is required for provider 'kubo-ipfs'.");
                    return 1;
                }

                var client = new IpfsClient(apiUrl ?? "http://127.0.0.1:5001");
                root        = new IpfsFolder(cid, client);
                displayRoot = $"ipfs://{cid}";
                (totalSize, freeSize) = await TryGetDagVolumeSpaceAsync(client, $"/ipfs/{cid}");
                break;
            }

            // ── kubo-ipns ─────────────────────────────────────────────────────
            case "kubo-ipns":
            {
                if (string.IsNullOrWhiteSpace(ipnsAddr))
                {
                    Console.Error.WriteLine("Error: --ipns is required for provider 'kubo-ipns'.");
                    return 1;
                }

                var client = new IpfsClient(apiUrl ?? "http://127.0.0.1:5001");
                root        = new IpnsFolder(ipnsAddr, client);
                displayRoot = $"ipns://{ipnsAddr}";
                (totalSize, freeSize) = await TryGetKuboRepositorySpaceAsync(client);
                break;
            }

            // ── onedrive ──────────────────────────────────────────────────────
            case "onedrive":
            {
                Console.Error.WriteLine(
                    "Error: OneDrive requires a pre-authenticated GraphServiceClient.");
                Console.Error.WriteLine(
                    "Instantiate one with Microsoft.Graph and MSAL, then pass it in code:");
                Console.Error.WriteLine(
                    "  var root = new OwlCore.Storage.OneDrive.OneDriveFolder(graphClient, rootDriveItem);");
                return 1;
            }

            // ── s3 ────────────────────────────────────────────────────────────
            case "s3":
            {
                if (string.IsNullOrWhiteSpace(s3Bucket))
                {
                    Console.Error.WriteLine("Error: --bucket is required for provider 's3'.");
                    return 1;
                }

                var s3Config = new AmazonS3Config();
                if (!string.IsNullOrWhiteSpace(s3Region))
                    s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s3Region);
                if (!string.IsNullOrWhiteSpace(s3Endpoint))
                    s3Config.ServiceURL = s3Endpoint;

                IAmazonS3 s3Client = (s3Key, s3Secret) switch
                {
                    ({ } k, { } s) => new AmazonS3Client(k, s, s3Config),
                    _ => new AmazonS3Client(s3Config),
                };

                root = new S3Folder(s3Client, s3Bucket, s3Prefix ?? string.Empty);
                displayRoot = $"s3://{s3Bucket}/{s3Prefix ?? string.Empty}";
                break;
            }

            // ── nfs ───────────────────────────────────────────────────────────
            case "nfs":
            {
                if (string.IsNullOrWhiteSpace(nfsHost) || string.IsNullOrWhiteSpace(nfsExport))
                {
                    Console.Error.WriteLine(
                        "Error: --host and --export are required for provider 'nfs'.");
                    return 1;
                }

                var nfsClient = new NfsClient(nfsHost, nfsExport, NfsVersion.Auto, 0, 0);
                await nfsClient.ConnectAsync();
                root        = await NfsFolder.GetFromNfsPathAsync(nfsClient, nfsPath ?? "/");
                displayRoot = $"nfs://{nfsHost}{nfsExport}{nfsPath}";
                (totalSize, freeSize) = await TryGetNfsVolumeSpaceAsync(nfsClient);
                break;
            }

            // ── unknown ───────────────────────────────────────────────────────
            default:
                Console.Error.WriteLine(
                    $"Error: unknown provider '{provider}'. Run 'owlmount' for help.");
                return 1;
        }

        // Derive a default volume label from the provider name when none supplied
        string resolvedLabel = label ?? provider.ToUpperInvariant() switch
        {
            "MEMORY"    => "OwlMount (Memory)",
            "KUBO-MFS"  => "OwlMount (MFS)",
            "KUBO-IPFS" => "OwlMount (IPFS)",
            "KUBO-IPNS" => "OwlMount (IPNS)",
            "S3"        => "OwlMount (S3)",
            "NFS"       => "OwlMount (NFS)",
            _           => "OwlMount",
        };

        bool isReadOnly = forceReadOnly || root is not IModifiableFolder;

        Console.WriteLine($"OwlMount — provider: {provider}");
        Console.WriteLine($"  Root   : {displayRoot}");
        Console.WriteLine($"  Label  : {resolvedLabel}");
        Console.WriteLine($"  Mode   : {(isReadOnly ? "read-only" : "read-write")}");
        if (totalSize.HasValue)
        {
            Console.WriteLine($"  Size   : {FormatBytes(totalSize.Value)} total" +
                (freeSize.HasValue ? $", {FormatBytes(freeSize.Value)} free" : string.Empty));
        }

        // ── Build the VFS components ──────────────────────────────────────────
        var rangeReaders  = new RangeReaderRegistry();
        var sizeProviders = new SizeProviderRegistry();
        var blockCache    = new BlockCache(providerId: $"{provider}_{root.Id}");

        // CTS shared by CancelKeyPress and DispatcherStopped so either path exits cleanly.
        var cts = new CancellationTokenSource();

        var fs = new OwlMountFileSystem(
            root, blockCache, rangeReaders, sizeProviders,
            readOnly: forceReadOnly,
            totalSize: totalSize,
            freeSize: freeSize,
            volumeLabel: resolvedLabel,
            onDispatcherStopped: () =>
            {
                // Fires when WinFsp stops the dispatcher — covers both Ctrl+C signals
                // and the user ejecting/unmounting the drive from Explorer.
                Console.WriteLine("\nDrive unmounted. Exiting…");
                DeletePidFile(letter);
                cts.Cancel();
            });

        // ── Configure the WinFsp host ─────────────────────────────────────────
        var host = new FileSystemHost(fs)
        {
            FileSystemName           = "OwlMount",
            SectorSize               = 512,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength       = 255,
            CasePreservedNames       = true,
            CaseSensitiveSearch      = false,
            UnicodeOnDisk            = true,
            VolumeSerialNumber       = 0x4F574C4D, // "OWLM"
            FileInfoTimeout          = 1000,
            VolumeInfoTimeout        = 1000,
            DirInfoTimeout           = 1000,
        };

        string mountPoint = letter.TrimEnd(':') + ":";
        Console.WriteLine($"  Mount  : {mountPoint}\\");
        Console.WriteLine();

        int mountResult = host.Mount(mountPoint);
        if (mountResult < 0)
        {
            Console.Error.WriteLine(
                $"Failed to mount at {mountPoint} (WinFsp error {mountResult}).");
            Console.Error.WriteLine(
                "Ensure WinFsp is installed and the drive letter is not already in use.");
            Console.Error.WriteLine(
                "  WinFsp installer: https://winfsp.dev/rel/");
            return 1;
        }

        // ── Write PID file so 'owlmount unmount' can signal this process ──────
        string pidFile = GetPidFilePath(letter);
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());

        Console.WriteLine($"Mounted successfully. Browsable at {mountPoint}\\");
        Console.WriteLine($"  PID    : {Environment.ProcessId}");
        Console.WriteLine($"Unmount with: owlmount unmount --letter {letter.TrimEnd(':').ToUpperInvariant()}");
        Console.WriteLine("Or press Ctrl+C.");

        // ── Exit on Ctrl+C (cts is also cancelled by DispatcherStopped) ───────
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nUnmounting…");
            host.Unmount();
            DeletePidFile(letter);
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }

        Console.WriteLine("Done.");
        return 0;
    }

    // ── unmount ───────────────────────────────────────────────────────────────

    static int RunUnmount(string[] args)
    {
        string? letter = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--letter", StringComparison.InvariantCultureIgnoreCase))
                letter = args[++i];
        }

        if (letter is null)
        {
            Console.Error.WriteLine("Usage: owlmount unmount --letter <X>");
            return 1;
        }

        string pidFile = GetPidFilePath(letter);
        if (!File.Exists(pidFile))
        {
            Console.Error.WriteLine(
                $"No active mount found for drive '{letter.TrimEnd(':').ToUpperInvariant()}:'.");
            Console.Error.WriteLine("Run 'owlmount list' to see active mounts.");
            return 1;
        }

        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
        {
            Console.Error.WriteLine($"PID file is corrupt: {pidFile}");
            File.Delete(pidFile);
            return 1;
        }

        // Verify the process is still alive before trying to signal it.
        try
        {
            var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                Console.Error.WriteLine(
                    $"Mount process (PID {pid}) has already exited.");
                File.Delete(pidFile);
                return 1;
            }
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine(
                $"Mount process (PID {pid}) is no longer running.");
            File.Delete(pidFile);
            return 1;
        }

        if (!SendCtrlC(pid))
        {
            Console.Error.WriteLine(
                $"Failed to send unmount signal to process {pid}.");
            return 1;
        }

        Console.WriteLine(
            $"Unmount signal sent to '{letter.TrimEnd(':').ToUpperInvariant()}:' (PID {pid}).");
        return 0;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    static int RunList()
    {
        string pidsDir = GetPidsDir();
        if (!Directory.Exists(pidsDir))
        {
            Console.WriteLine("No active mounts.");
            return 0;
        }

        string[] pidFiles = Directory.GetFiles(pidsDir, "*.pid");
        if (pidFiles.Length == 0)
        {
            Console.WriteLine("No active mounts.");
            return 0;
        }

        bool any = false;
        foreach (string pidFile in pidFiles.OrderBy(f => f))
        {
            string driveLetter = Path.GetFileNameWithoutExtension(pidFile).ToUpperInvariant();
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                continue;

            bool alive;
            try
            {
                var proc = Process.GetProcessById(pid);
                alive = !proc.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false;
            }

            if (alive)
            {
                Console.WriteLine($"  {driveLetter}: — PID {pid}");
                any = true;
            }
            else
            {
                // Stale entry — clean up silently.
                try { File.Delete(pidFile); } catch { /* best-effort */ }
            }
        }

        if (!any)
            Console.WriteLine("No active mounts.");

        return 0;
    }

    // ── usage ─────────────────────────────────────────────────────────────────

    static int PrintUsage()
    {
        Console.WriteLine("OwlMount — mount OwlCore.Storage providers as Windows drive letters.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  owlmount mount   [options]");
        Console.WriteLine("  owlmount unmount --letter <X>");
        Console.WriteLine("  owlmount list");
        Console.WriteLine();
        Console.WriteLine("Mount options (all providers):");
        Console.WriteLine("  --provider   memory | kubo-mfs | kubo-ipfs | kubo-ipns | s3 | nfs  (default: memory)");
        Console.WriteLine("  --letter     Drive letter to mount on (default: M)");
        Console.WriteLine("  --label      Volume label shown in Explorer (default: auto)");
        Console.WriteLine("  --read-only  Force the mounted filesystem to open as read-only");
        Console.WriteLine();
        Console.WriteLine("  memory       (no extra flags; drive starts empty)");
        Console.WriteLine("  kubo-mfs     --path <mfs-path>  [--api-url http://127.0.0.1:5001]");
        Console.WriteLine("  kubo-ipfs    --cid <CID>        [--api-url http://127.0.0.1:5001]");
        Console.WriteLine("  kubo-ipns    --ipns <address>   [--api-url http://127.0.0.1:5001]");
        Console.WriteLine("  s3           --bucket <name>    [--prefix <path>]");
        Console.WriteLine("               [--access-key <k>] [--secret-key <s>]");
        Console.WriteLine("               [--region <r>]     [--endpoint <url>]");
        Console.WriteLine("  nfs          --host <ip>        --export </path>");
        Console.WriteLine("               [--nfs-path <path>]");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  owlmount mount --provider memory --letter R");
        Console.WriteLine("  owlmount mount --provider memory --letter R --read-only");
        Console.WriteLine("  owlmount mount --provider kubo-mfs --path /my/dir --letter K --label IPFS");
        Console.WriteLine("  owlmount mount --provider kubo-ipfs --cid bafybei... --letter I");
        Console.WriteLine("  owlmount mount --provider s3 --bucket my-bucket --prefix data/ --letter S");
        Console.WriteLine("  owlmount mount --provider nfs --host 192.168.1.10 --export /share --letter N");
        Console.WriteLine("  owlmount unmount --letter R");
        Console.WriteLine("  owlmount list");
        return 1;
    }

    // ── PID file helpers ──────────────────────────────────────────────────────

    static string GetPidsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "pids");

    static string GetPidFilePath(string letter) =>
        Path.Combine(GetPidsDir(), letter.TrimEnd(':').ToUpperInvariant() + ".pid");

    static void DeletePidFile(string letter)
    {
        try { File.Delete(GetPidFilePath(letter)); } catch { /* best-effort */ }
    }

    static (ulong? TotalSize, ulong? FreeSize) GetMemoryVolumeSpace()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        if (gcInfo.TotalAvailableMemoryBytes <= 0)
            return (null, null);

        ulong total = (ulong)gcInfo.TotalAvailableMemoryBytes;
        ulong used = (ulong)Math.Max(GC.GetTotalMemory(forceFullCollection: false), 0L);
        ulong free = used >= total ? 0 : total - used;
        return (total, free);
    }

    static async Task<(ulong? TotalSize, ulong? FreeSize)> TryGetKuboRepositorySpaceAsync(IpfsClient client)
    {
        try
        {
            var repository = await client.Stats.RepositoryAsync(CancellationToken.None);
            ulong total = repository.StorageMax;
            ulong used = repository.RepoSize;
            ulong free = used >= total ? 0 : total - used;
            return total == 0 ? (null, null) : (total, free);
        }
        catch
        {
            return (null, null);
        }
    }

    static async Task<(ulong? TotalSize, ulong? FreeSize)> TryGetDagVolumeSpaceAsync(IpfsClient client, string path)
    {
        try
        {
            var stats = await client.Dag.StatAsync(path, progress: null, CancellationToken.None);
            return stats.TotalSize is > 0 ? (stats.TotalSize.Value, 0) : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    static async Task<(ulong? TotalSize, ulong? FreeSize)> TryGetNfsVolumeSpaceAsync(NfsClient client)
    {
        try
        {
            var stats = await client.FsStatAsync(CancellationToken.None);
            return (stats.TotalBytes, stats.AvailBytes);
        }
        catch
        {
            return (null, null);
        }
    }

    static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    // ── Win32 helpers for cross-process Ctrl+C ────────────────────────────────

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    private const uint CtrlCEvent = 0;

    /// <summary>
    /// Sends a CTRL+C event to <paramref name="pid"/> by temporarily attaching to
    /// its console, suppressing the signal in our own process, firing the event,
    /// then restoring normal state.
    /// </summary>
    static bool SendCtrlC(int pid)
    {
        // Detach from our own console so we can attach to the target's.
        FreeConsole();

        if (!AttachConsole(pid))
            return false;

        // Ignore CTRL+C in this process so we don't terminate ourselves.
        SetConsoleCtrlHandler(nint.Zero, true);

        bool ok = GenerateConsoleCtrlEvent(CtrlCEvent, 0);

        // Brief pause so the mount process can start handling the signal.
        Thread.Sleep(500);

        FreeConsole();

        // Restore default CTRL+C behaviour for this process.
        SetConsoleCtrlHandler(nint.Zero, false);

        return ok;
    }
}
