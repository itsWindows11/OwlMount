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
using System.Runtime.InteropServices;

namespace OwlMount.WinUI.Services;

/// <summary>
/// Result of building an <see cref="IFolder"/> root for a provider.
/// Carries volume-size hints and any provider-level disposable so <see cref="MountService"/>
/// can clean up when the mount is stopped.
/// </summary>
internal sealed class ProviderCreationResult
{
    public required IFolder Root { get; init; }
    public bool ForceReadOnly { get; init; }
    public ulong? TotalSize { get; init; }
    public ulong? FreeSize { get; init; }
    public ISizeProvider? SizeProvider { get; init; }
    public Func<IFile, bool>? SizePredicate { get; init; }
    /// <summary>Provider-owned resource to dispose when the mount is stopped (e.g. S3 HttpClientFactory).</summary>
    public IDisposable? ExtraDisposable { get; init; }
}

/// <summary>
/// Creates the <see cref="IFolder"/> root and supporting objects for a given
/// <see cref="ProviderOptions"/>. This is an async port of the provider-selection logic
/// from <c>OwlMount.WinFspHost/Program.cs</c> without any dependency on the console app.
/// </summary>
internal static partial class ProviderFactory
{
    public static async Task<ProviderCreationResult> CreateAsync(
        ProviderOptions opts, IFolder? existingRoot = null, CancellationToken ct = default)
    {
        ulong? totalSize = null;
        ulong? freeSize = null;
        ISizeProvider? sizeProvider = null;
        Func<IFile, bool>? sizePredicate = null;
        IDisposable? extraDisposable = null;
        bool forceReadOnly = opts.ForceReadOnly;

        IFolder root;

        switch (opts.Provider.ToLowerInvariant())
        {
            // ── memory ─────────────────────────────────────────────────────────
            case "memory":
                if (forceReadOnly)
                    throw new ArgumentException("Memory provider cannot be read-only.", nameof(opts));
                root = existingRoot ?? new MemoryFolder(id: "memory-root", name: "memory-root");
                (totalSize, freeSize) = GetMemoryVolumeSpace(opts.MemorySizeLimitBytes);
                if (opts.MemorySizeLimitBytes.HasValue && opts.MemorySizeLimitBytes.Value > 0 && freeSize.HasValue && (ulong)opts.MemorySizeLimitBytes.Value > freeSize.Value)
                {
                    throw new InvalidOperationException($"Not enough free physical memory. Requested: {opts.MemorySizeLimitBytes.Value} bytes, Available: {freeSize.Value} bytes.");
                }
                break;

            // ── archive ────────────────────────────────────────────────────────
            case "archive":
            {
                string archivePath = opts.ArchiveFile ?? opts.Path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(archivePath))
                    throw new ArgumentException(
                        "An archive file path is required for the 'archive' provider.", nameof(opts));

                string fullPath = System.IO.Path.GetFullPath(archivePath);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Archive file not found: {fullPath}", fullPath);

                root = new ArchiveFolder(new SystemFile(fullPath));
                totalSize = TryGetArchiveVolumeSize(fullPath);
                if (new System.IO.FileInfo(fullPath).IsReadOnly) forceReadOnly = true;
                break;
            }

            // ── local ──────────────────────────────────────────────────────────
            case "local":
            {
                string path = opts.Path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    throw new ArgumentException(
                        "A valid existing directory path is required for the 'local' provider.", nameof(opts));

                root = new SystemFolder(System.IO.Path.GetFullPath(path));
                break;
            }

            // ── kubo-mfs ───────────────────────────────────────────────────────
            case "kubo-mfs":
            {
                string mfsPath = opts.Path ?? "/";
                var client = new IpfsClient(opts.ApiUrl ?? "http://127.0.0.1:5001");
                root = new MfsFolder(mfsPath, client);
                (totalSize, freeSize) = await TryGetKuboRepositorySpaceAsync(client, ct);
                break;
            }

            // ── kubo-ipfs ──────────────────────────────────────────────────────
            case "kubo-ipfs":
            {
                if (string.IsNullOrWhiteSpace(opts.Cid))
                    throw new ArgumentException(
                        "A CID is required for the 'kubo-ipfs' provider.", nameof(opts));

                var client = new IpfsClient(opts.ApiUrl ?? "http://127.0.0.1:5001");
                root = new IpfsFolder(opts.Cid, client);
                (totalSize, freeSize) = await TryGetDagVolumeSpaceAsync(client, $"/ipfs/{opts.Cid}", ct);
                break;
            }

            // ── kubo-ipns ──────────────────────────────────────────────────────
            case "kubo-ipns":
            {
                if (string.IsNullOrWhiteSpace(opts.IpnsAddress))
                    throw new ArgumentException(
                        "An IPNS address is required for the 'kubo-ipns' provider.", nameof(opts));

                var client = new IpfsClient(opts.ApiUrl ?? "http://127.0.0.1:5001");
                root = new IpnsFolder(opts.IpnsAddress, client);
                (totalSize, freeSize) = await TryGetKuboRepositorySpaceAsync(client, ct);
                break;
            }

            // ── s3 ─────────────────────────────────────────────────────────────
            case "s3":
            {
                if (string.IsNullOrWhiteSpace(opts.S3Bucket))
                    throw new ArgumentException(
                        "An S3 bucket name is required.", nameof(opts));

                var s3Config = new AmazonS3Config { ForcePathStyle = true };

                if (!string.IsNullOrWhiteSpace(opts.S3Region))
                    s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(opts.S3Region);
                if (!string.IsNullOrWhiteSpace(opts.S3Endpoint))
                    s3Config.ServiceURL = opts.S3Endpoint;

                var tlsFactory = new TlsHttpClientFactory();
                s3Config.HttpClientFactory = tlsFactory;
                extraDisposable = tlsFactory;

                IAmazonS3 s3Client = (opts.S3AccessKey, opts.S3SecretKey) switch
                {
                    ({ } k, { } s) when !string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(s)
                        => new AmazonS3Client(k, s, s3Config),
                    _ => new AmazonS3Client(s3Config),
                };

                root = new S3Folder(s3Client, opts.S3Bucket, opts.S3Prefix ?? string.Empty);
                sizePredicate = f => f is S3File;
                sizeProvider = new S3HeadSizeProvider();
                break;
            }

            // ── nfs ────────────────────────────────────────────────────────────
            case "nfs":
            {
                if (string.IsNullOrWhiteSpace(opts.NfsHost) || string.IsNullOrWhiteSpace(opts.NfsExport))
                    throw new ArgumentException(
                        "NFS host and export path are both required.", nameof(opts));

                var nfsClient = new NfsClient(opts.NfsHost, opts.NfsExport, NfsVersion.Auto, 0, 0);
                await nfsClient.ConnectAsync();
                root = await NfsFolder.GetFromNfsPathAsync(nfsClient, opts.NfsPath);
                (totalSize, freeSize) = await TryGetNfsVolumeSpaceAsync(nfsClient, ct);
                sizePredicate = f => f is NfsFile;
                sizeProvider = new NfsStatSizeProvider(nfsClient);
                break;
            }

            default:
                throw new NotSupportedException($"Unknown provider: '{opts.Provider}'.");
        }

        return new ProviderCreationResult
        {
            Root = root,
            ForceReadOnly = forceReadOnly,
            TotalSize = totalSize,
            FreeSize = freeSize,
            SizeProvider = sizeProvider,
            SizePredicate = sizePredicate,
            ExtraDisposable = extraDisposable,
        };
    }

    // ── Volume size helpers ──────────────────────────────────────────────────

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

    private static (ulong? TotalSize, ulong? FreeSize) GetMemoryVolumeSpace(long? limitBytes = null)
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
            ? (ulong)Math.Min(limitBytes.Value, (long)physicalTotal)
            : physicalTotal;
        ulong free = Math.Min(physicalFree, total);
        return (total, free);
    }

    private static ulong? TryGetArchiveVolumeSize(string archivePath)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(archivePath));
            if (string.IsNullOrWhiteSpace(root)) return null;
            long available = new DriveInfo(root).AvailableFreeSpace;
            return available > 0 ? (ulong)available : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(ulong?, ulong?)> TryGetKuboRepositorySpaceAsync(
        IpfsClient client, CancellationToken ct)
    {
        try
        {
            var repo = await client.Stats.RepositoryAsync(ct);
            ulong total = repo.StorageMax;
            ulong used = repo.RepoSize;
            ulong free = used >= total ? 0 : total - used;
            return total == 0 ? (null, null) : (total, free);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<(ulong?, ulong?)> TryGetDagVolumeSpaceAsync(
        IpfsClient client, string path, CancellationToken ct)
    {
        try
        {
            var stats = await client.Dag.StatAsync(path, progress: null, ct);
            return stats.TotalSize is > 0 ? (stats.TotalSize.Value, 0) : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<(ulong?, ulong?)> TryGetNfsVolumeSpaceAsync(
        NfsClient client, CancellationToken ct)
    {
        try
        {
            var stats = await client.FsStatAsync(ct);
            return (stats.TotalBytes, stats.AvailBytes);
        }
        catch
        {
            return (null, null);
        }
    }

    // ── Provider-specific size fast-path helpers (ported from WinFspHost/Program.cs) ──

    private sealed class S3HeadSizeProvider : ISizeProvider
    {
        public async Task<long?> GetSizeAsync(IFile file, CancellationToken ct = default)
        {
            if (file is not S3File s3File) return null;
            GetObjectMetadataResponse meta = await s3File.GetMetadataAsync(ct);
            return meta.ContentLength;
        }
    }

    private sealed class NfsStatSizeProvider(INfsClient nfsClient) : ISizeProvider
    {
        public async Task<long?> GetSizeAsync(IFile file, CancellationToken ct = default)
        {
            if (file is not NfsFile nfsFile) return null;
            NfsFileAttributes attrs = await nfsClient.GetAttrAsync(nfsFile.Path, ct);
            return (long)attrs.Size;
        }
    }

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
        public override bool UseSDKHttpClientCaching(IClientConfig config) => true;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public override string GetConfigUniqueString(IClientConfig config) => "owlmount-tls";
        public void Dispose() => _client.Dispose();
    }
}
