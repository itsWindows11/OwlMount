using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Ipfs.Http;
using NfsSharp;
using NfsSharp.Protocol;
using OwlCore.Kubo;
using OwlCore.Storage;
using OwlCore.Storage.AmazonS3;
using OwlCore.Storage.Memory;
using OwlCore.Storage.NfsSharp;
using OwlCore.Storage.SharpCompress;
using OwlCore.Storage.System.IO;
using OwlMount.Core.Abstractions;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;
using OwlMount.Core.Windows;
using OwlMount.Core.Windows.Backends;

[SupportedOSPlatform("windows")]
static partial class Program
{
    static async Task<int> Main(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        if (sub.StartsWith("--") || args.Length == 0)
            return await RunMountAsync(args);

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
        string  provider      = OwlMountConstants.DefaultProvider;
        string  backend       = OwlMountConstants.DefaultBackend;
        string? letter        = null;
        string? label         = null;
        string? path          = null;
        bool    forceReadOnly = false;
        ulong?  totalSize     = null;
        ulong?  freeSize      = null;
        // Provider paths (Dokany & WinFsp only)
        string? dokanyPath    = null;
        string? winfspPath    = null;
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
        string? nfsPath    = OwlMountConstants.DefaultNfsPath;
        // Archive
        string? archiveFile = null;
        // Memory
        long? memorySizeBytes = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--provider":     provider    = args[++i]; break;
                case "--backend":      backend     = args[++i]; break;
                case "--letter":       letter      = args[++i]; break;
                case "--label":        label       = args[++i]; break;
                case "--path":         path        = args[++i]; break;
                case "--archive-file": archiveFile = args[++i]; break;
                case "--bucket":       s3Bucket    = args[++i]; break;
                case "--prefix":       s3Prefix    = args[++i]; break;
                case "--access-key":   s3Key       = args[++i]; break;
                case "--secret-key":   s3Secret    = args[++i]; break;
                case "--region":       s3Region    = args[++i]; break;
                case "--endpoint":     s3Endpoint  = args[++i]; break;
                case "--api-url":      apiUrl      = args[++i]; break;
                case "--cid":          cid         = args[++i]; break;
                case "--ipns":         ipnsAddr    = args[++i]; break;
                case "--host":         nfsHost     = args[++i]; break;
                case "--export":       nfsExport   = args[++i]; break;
                case "--nfs-path":     nfsPath     = args[++i]; break;
                case "--dokany-path":  dokanyPath  = args[++i]; break;
                case "--winfsp-path":  winfspPath  = args[++i]; break;
                case "--read-only":
                case "--readonly":
                    forceReadOnly = true;
                    break;
                case "--memory-size":
                {
                    string raw = args[++i].Trim().ToUpperInvariant();
                    if (raw.EndsWith("G") && ulong.TryParse(raw[..^1], out ulong gb))
                        memorySizeBytes = (long)(gb * 1024UL * 1024 * 1024);
                    else if (raw.EndsWith("M") && ulong.TryParse(raw[..^1], out ulong mb))
                        memorySizeBytes = (long)(mb * 1024UL * 1024);
                    else if (ulong.TryParse(raw, out ulong byteVal))
                        memorySizeBytes = (long)byteVal;
                    else
                    {
                        Console.Error.WriteLine($"Error: invalid --memory-size value '{args[i]}'. Use e.g. 4G, 512M, or bytes.");
                        return 1;
                    }
                    break;
                }
            }
        }

        // ── Validate backend ──────────────────────────────────────────────────
        backend = backend.ToLowerInvariant();
        if (!OwlMountConstants.BackendIds.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"Error: unknown backend '{backend}'. Valid values: {string.Join(", ", OwlMountConstants.BackendIds)}");
            return 1;
        }

        // ── Resolve the root IFolder ──────────────────────────────────────────
        IFolder      root;
        string       displayRoot;
        IDisposable? extraDisposable = null; // tracks any provider-owned resource (e.g. S3 HttpClientFactory)
        // Provider-specific fast-path size fetcher registered after the switch.
        ISizeProvider? providerSizeProvider = null;
        Func<IFile, bool>? providerSizePredicate = null;

        switch (provider.ToLowerInvariant())
        {
            // ── memory ────────────────────────────────────────────────────────
            case "memory":
                if (forceReadOnly)
                {
                    Console.Error.WriteLine("Error: memory provider cannot be read-only.");
                    return 1;
                }
                root = new MemoryFolder(id: "memory-root", name: "memory-root");
                displayRoot = "(in-memory, starts empty)";
                (totalSize, freeSize) = GetMemoryVolumeSpace(memorySizeBytes);
                
                if (memorySizeBytes.HasValue && memorySizeBytes.Value > 0 && freeSize.HasValue && (ulong)memorySizeBytes.Value > freeSize.Value)
                {
                    Console.Error.WriteLine($"Error: Not enough free physical memory. Requested: {FormatBytes((ulong)memorySizeBytes.Value)}, Available: {FormatBytes(freeSize.Value)}.");
                    return 1;
                }
                break;

            // ── archive ───────────────────────────────────────────────────────
            case "archive":
            {
                string resolvedArchivePath = archiveFile ?? path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(resolvedArchivePath))
                {
                    Console.Error.WriteLine("Error: --archive-file is required for provider 'archive'.");
                    return 1;
                }

                string fullArchivePath = Path.GetFullPath(resolvedArchivePath);
                if (!File.Exists(fullArchivePath))
                {
                    Console.Error.WriteLine($"Error: archive file not found: {fullArchivePath}");
                    return 1;
                }

                root = new ArchiveFolder(new SystemFile(fullArchivePath));
                displayRoot = fullArchivePath;
                totalSize = TryGetArchiveVolumeSize(fullArchivePath);
                forceReadOnly = new FileInfo(fullArchivePath).IsReadOnly;
                break;
            }

            // ── local (filesystem path) ───────────────────────────────────────
            case "local":
            {
                string resolvedPath = path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(resolvedPath) || !Directory.Exists(resolvedPath))
                {
                    Console.Error.WriteLine(
                        $"Error: --path must be an existing directory for provider 'local'.");
                    return 1;
                }

                root = new SystemFolder(Path.GetFullPath(resolvedPath));
                displayRoot = path!;
                break;
            }

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
                var tlsFactory = new TlsHttpClientFactory();
                s3Config.HttpClientFactory = tlsFactory;
                s3Config.ForcePathStyle = true;
                extraDisposable = tlsFactory;

                IAmazonS3 s3Client = (s3Key, s3Secret) switch
                {
                    ({ } k, { } s) => new AmazonS3Client(k, s, s3Config),
                    _ => new AmazonS3Client(s3Config),
                };

                root = new S3Folder(s3Client, s3Bucket, s3Prefix ?? string.Empty);
                displayRoot = $"s3://{s3Bucket}/{s3Prefix ?? string.Empty}";
                providerSizePredicate = f => f is S3File;
                providerSizeProvider  = new S3HeadSizeProvider();
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
                providerSizePredicate = f => f is NfsFile;
                providerSizeProvider  = new NfsStatSizeProvider(nfsClient);
                break;
            }

            // ── unknown ───────────────────────────────────────────────────────
            default:
                Console.Error.WriteLine(
                    $"Error: unknown provider '{provider}'. Run 'owlmount' for help.");
                return 1;
        }

        // ── Derive defaults ───────────────────────────────────────────────────
        string driveLetter = string.IsNullOrWhiteSpace(letter)
            ? GetFirstFreeDriveLetter()
            : letter.TrimEnd(':').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            Console.Error.WriteLine("Error: no free drive letters are available.");
            return 1;
        }
        string resolvedLabel = label ?? provider.ToUpperInvariant() switch
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

        string mountPoint  = driveLetter + ":";

        // Enforce read-only for immutable providers or when explicitly requested.
        bool isReadOnly = forceReadOnly || root is not IModifiableFolder;

        Console.WriteLine($"OwlMount — provider: {provider}  backend: {backend}");
        Console.WriteLine($"  Root   : {displayRoot}");
        Console.WriteLine($"  Label  : {resolvedLabel}");
        Console.WriteLine($"  Mode   : {(isReadOnly ? "read-only" : "read-write")}");
        Console.WriteLine($"  Mount  : {mountPoint}\\");
        if (totalSize.HasValue)
        {
            Console.WriteLine($"  Size   : {FormatBytes(totalSize.Value)} total" +
                (freeSize.HasValue ? $", {FormatBytes(freeSize.Value)} free" : string.Empty));
        }
        Console.WriteLine();

        // ── Build the VFS components ──────────────────────────────────────────
        var rangeReaders  = new RangeReaderRegistry();
        var sizeProviders = new SizeProviderRegistry();
        // For in-memory, archive, and local filesystem providers we don't need a disk-backed
        // block cache — read directly from the provider. Create a cache only for
        // providers that may benefit from on-disk caching.
        BlockCache? blockCache = (provider == "memory" || provider == "archive" || provider == "local")
            ? null
            : new BlockCache(providerId: $"{provider}_{root.Id}");

        // Register provider-specific optimisations.
        // S3: use a HEAD request (GetObjectMetadata) to get file size rather than
        //     opening a full GET stream, which avoids a full download per file.
        // NFS: use GetAttr (single stat RPC) instead of opening and seeking a stream.
        // Both providers run their size requests in parallel during directory listing.
        if (providerSizeProvider is not null && providerSizePredicate is not null)
            sizeProviders.Register(providerSizePredicate, providerSizeProvider);

        // CTS shared by CancelKeyPress and backend.Stopped so either path exits cleanly.
        var cts = new CancellationTokenSource();

        // ── Apply custom provider paths (Dokany & WinFsp) ────────────────────
        if (!string.IsNullOrWhiteSpace(winfspPath))
            WinFspBackend.SetCustomPath(winfspPath);
        if (!string.IsNullOrWhiteSpace(dokanyPath))
            DokanyBackend.SetCustomPath(dokanyPath);

        // ── Create the backend ────────────────────────────────────────────────
        IOwlMountBackend vfsBackend;
        if (backend == OwlMountConstants.ProjFsBackend)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                Console.Error.WriteLine(
                    "Error: ProjFS backend requires Windows 10 version 1803 (build 17763) or later.");
                return 1;
            }
#pragma warning disable CA1416 // resolved by the version check above
            vfsBackend = new ProjFsBackend(root, blockCache, rangeReaders, sizeProviders, isReadOnly);
#pragma warning restore CA1416
        }
        else if (backend == OwlMountConstants.DokanyBackend)
        {
            vfsBackend = new DokanyBackend(
                root, blockCache, rangeReaders, sizeProviders,
                readOnly: isReadOnly,
                totalSize: totalSize,
                freeSize: freeSize,
                volumeLabel: resolvedLabel,
                providerName: provider);
        }
        else
        {
            vfsBackend = new WinFspBackend(
                root, blockCache, rangeReaders, sizeProviders,
                readOnly:    isReadOnly,
                totalSize:   totalSize,
                freeSize:    freeSize,
                volumeLabel: resolvedLabel,
                providerName: provider);
        }

        // DispatcherStopped (WinFsp) or equivalent fires when the drive is ejected externally.
        vfsBackend.Stopped += (_, _) =>
        {
            Console.WriteLine("\nDrive unmounted. Exiting…");
            DeletePidFile(letter);
            cts.Cancel();
        };

        if (!vfsBackend.Start(mountPoint, resolvedLabel))
        {
            vfsBackend.Dispose();
            return 1;
        }

        try
        {
        // ── Write PID file so 'owlmount unmount' can signal this process ──────
        string pidFile = GetPidFilePath(letter);
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());

        Console.WriteLine($"Mounted successfully. Browsable at {mountPoint}\\");
        Console.WriteLine($"  PID    : {Environment.ProcessId}");
        Console.WriteLine($"Unmount with: owlmount unmount --letter {driveLetter}");
        Console.WriteLine("Or press Ctrl+C.");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nUnmounting…");
            vfsBackend.Stop();
            DeletePidFile(letter);
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }
        }
        finally
        {
            vfsBackend.Dispose();
            extraDisposable?.Dispose();
        }

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

        try
        {
            var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                Console.Error.WriteLine($"Mount process (PID {pid}) has already exited.");
                File.Delete(pidFile);
                return 1;
            }
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine($"Mount process (PID {pid}) is no longer running.");
            File.Delete(pidFile);
            return 1;
        }

        if (!SendCtrlC(pid))
        {
            Console.Error.WriteLine($"Failed to send unmount signal to process {pid}.");
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
        Console.WriteLine(
            $"  --provider   memory | archive | local | kubo-mfs | kubo-ipfs | kubo-ipns | s3 | nfs  (default: {OwlMountConstants.DefaultProvider})");
        Console.WriteLine(
            $"  --backend    dokany | winfsp | projfs  (default: {OwlMountConstants.DefaultBackend})");
        Console.WriteLine("  --letter     Drive letter to mount on (default: first free drive letter)");
        Console.WriteLine("  --label      Volume label shown in Explorer (default: auto)");
        Console.WriteLine("  --read-only  Force the mounted filesystem to open as read-only");
        Console.WriteLine();
        Console.WriteLine("Provider path options (third-party backends only):");
        Console.WriteLine("  --winfsp-path  <dir>  Directory containing winfsp-x64.dll (or winfsp-x86.dll)");
        Console.WriteLine("  --dokany-path  <dir>  Directory containing dokan2.dll (or dokan1.dll)");
        Console.WriteLine();
        Console.WriteLine("  memory       --memory-size <limit>  (e.g. 4G, 512M, or bytes; capped at available RAM)");
        Console.WriteLine("  archive      --archive-file <local-archive-path>");
        Console.WriteLine("  local        --path <local-directory-path>");
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
        Console.WriteLine("  owlmount mount --provider memory --letter R --memory-size 2G");
        Console.WriteLine("  owlmount mount --provider memory --letter R --read-only");
        Console.WriteLine("  owlmount mount --provider memory --letter R --backend projfs");
        Console.WriteLine("  owlmount mount --provider memory --letter R --backend dokany");
        Console.WriteLine("  owlmount mount --provider memory --letter R --backend dokany --dokany-path \"C:\\Dokan\\bin\"");
        Console.WriteLine("  owlmount mount --provider memory --letter R --backend winfsp --winfsp-path \"C:\\WinFsp\\bin\"");
        Console.WriteLine("  owlmount mount --provider archive --archive-file C:\\data\\backup.zip --letter A");
        Console.WriteLine("  owlmount mount --provider local --path C:\\data --letter D");
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

    // ── Volume size helpers ───────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    static (ulong? TotalSize, ulong? FreeSize) GetMemoryVolumeSpace(long? limitBytes = null)
    {
        ulong physicalTotal = 0;
        ulong physicalFree = 0;

        var memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            physicalTotal = memStatus.ullTotalPhys;
            physicalFree = memStatus.ullAvailPhys;
        }
        else
        {
            var gcInfo = GC.GetGCMemoryInfo();
            if (gcInfo.TotalAvailableMemoryBytes > 0)
            {
                physicalTotal = (ulong)gcInfo.TotalAvailableMemoryBytes;
                ulong used = (ulong)Math.Max(GC.GetTotalMemory(forceFullCollection: false), 0L);
                physicalFree = used >= physicalTotal ? 0 : physicalTotal - used;
            }
        }

        if (physicalTotal == 0) return (null, null);

        ulong total = limitBytes.HasValue && limitBytes.Value > 0
            ? Math.Min((ulong)limitBytes.Value, physicalTotal)
            : physicalTotal;
        ulong free  = Math.Min(physicalFree, total);
        return (total, free);
    }

    static ulong? TryGetArchiveVolumeSize(string archivePath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(archivePath));
            if (string.IsNullOrWhiteSpace(root)) return null;
            long available = new DriveInfo(root).AvailableFreeSpace;
            return available > 0 ? (ulong)available : null;
        }
        catch
        {
            return null;
        }
    }

    static string GetFirstFreeDriveLetter()
    {
        HashSet<string> occupied = new(StringComparer.OrdinalIgnoreCase);
        foreach (string drive in Environment.GetLogicalDrives())
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(drive));
            if (string.IsNullOrWhiteSpace(root))
                continue;

            occupied.Add(root.TrimEnd('\\', ':').ToUpperInvariant());
        }

        for (char letter = 'M'; letter <= 'Z'; letter++)
        {
            string candidate = letter.ToString();
            if (!occupied.Contains(candidate))
                return candidate;
        }

        for (char letter = 'A'; letter < 'M'; letter++)
        {
            string candidate = letter.ToString();
            if (!occupied.Contains(candidate))
                return candidate;
        }

        return string.Empty;
    }

    static async Task<(ulong? TotalSize, ulong? FreeSize)> TryGetKuboRepositorySpaceAsync(IpfsClient client)
    {
        try
        {
            var repo  = await client.Stats.RepositoryAsync(CancellationToken.None);
            ulong total = repo.StorageMax;
            ulong used  = repo.RepoSize;
            ulong free  = used >= total ? 0 : total - used;
            return total == 0 ? (null, null) : (total, free);
        }
        catch
        {
            return (null, null);
        }
    }

    static async Task<(ulong? TotalSize, ulong? FreeSize)> TryGetDagVolumeSpaceAsync(
        IpfsClient client, string path)
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
    private static partial bool SetConsoleCtrlHandler(
        nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    private const uint CtrlCEvent = 0;

    /// <summary>
    /// Sends a CTRL+C event to <paramref name="pid"/> by temporarily attaching to
    /// its console, suppressing the signal in our own process, firing the event,
    /// then restoring normal state.
    /// </summary>
    static bool SendCtrlC(int pid)
    {
        FreeConsole();
        if (!AttachConsole(pid)) return false;

        SetConsoleCtrlHandler(nint.Zero, true);
        bool ok = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
        Thread.Sleep(500);

        FreeConsole();
        SetConsoleCtrlHandler(nint.Zero, false);

        return ok;
    }

    // ── Provider-specific size fastpath helpers ───────────────────────────────

    /// <summary>
    /// <see cref="ISizeProvider"/> for S3 files that uses a HEAD request
    /// (<see cref="S3File.GetMetadataAsync"/>) to retrieve the object's
    /// <see cref="GetObjectMetadataResponse.ContentLength"/> cheaply, instead of
    /// opening a full GET stream just to read <see cref="System.IO.Stream.Length"/>.
    /// </summary>
    private sealed class S3HeadSizeProvider : ISizeProvider
    {
        public async Task<long?> GetSizeAsync(IFile file, CancellationToken ct = default)
        {
            if (file is not S3File s3File) return null;
            GetObjectMetadataResponse meta = await s3File.GetMetadataAsync(ct);
            return meta.ContentLength;
        }
    }

    /// <summary>
    /// <see cref="ISizeProvider"/> for NFS files that retrieves file size via a single
    /// <c>GETATTR</c> RPC (<see cref="INfsClient.GetAttrAsync"/>) rather than opening
    /// a full stream.  The <see cref="NfsFileAttributes.Size"/> field returned by the
    /// server already contains the authoritative file size.
    /// </summary>
    private sealed class NfsStatSizeProvider(INfsClient nfsClient) : ISizeProvider
    {
        public async Task<long?> GetSizeAsync(IFile file, CancellationToken ct = default)
        {
            if (file is not NfsFile nfsFile) return null;
            NfsFileAttributes attrs = await nfsClient.GetAttrAsync(nfsFile.Path, ct);
            return (long)attrs.Size;
        }
    }

    /// <summary>
    /// Custom <see cref="Amazon.Runtime.HttpClientFactory"/> that creates an
    /// <see cref="System.Net.Http.HttpClient"/> backed by a
    /// <see cref="SocketsHttpHandler"/> with TLS 1.2 and 1.3 explicitly enabled.
    /// This prevents the <c>HandshakeFailure</c> TLS alert that can occur with the
    /// default AWSSDK v4 HTTP pipeline against standard S3 and S3-compatible endpoints.
    /// The single <see cref="HttpClient"/> instance is intentionally kept alive for the
    /// application lifetime; it is disposed when this factory is disposed.
    /// </summary>
    private sealed class TlsHttpClientFactory : HttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols =
                    System.Security.Authentication.SslProtocols.Tls12 |
                    System.Security.Authentication.SslProtocols.Tls13,
            },
        });

        public override HttpClient CreateHttpClient(IClientConfig config) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig config)    => true;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public override string GetConfigUniqueString(IClientConfig config)    => "owlmount-tls";

        public void Dispose() => _client.Dispose();
    }
}
