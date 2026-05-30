namespace OwlMount.WinUI.Services;

/// <summary>
/// All parameters needed to create and mount a single OwlCore.Storage provider as a drive.
/// </summary>
public sealed class ProviderOptions
{
    public string Provider { get; init; } = "memory";
    public string Backend { get; init; } = "winfsp";
    public string Letter { get; init; } = "M";
    public string? Label { get; init; }
    public bool ForceReadOnly { get; init; }

    /// <summary>
    /// Optional size limit in bytes for the memory provider.
    /// When null, defaults to the machine's total available physical memory.
    /// </summary>
    public long? MemorySizeLimitBytes { get; init; }

    // Block cache settings
    /// <summary>
    /// Whether to enable block cache for this mount.
    /// When null, uses the global setting from AppSettingsService.
    /// </summary>
    public bool? EnableBlockCache { get; init; }

    /// <summary>
    /// Block cache size in bytes for this mount.
    /// When null, uses the global setting from AppSettingsService.
    /// </summary>
    public long? BlockCacheSizeBytes { get; init; }

    // local / archive
    public string? Path { get; init; }
    public string? ArchiveFile { get; init; }

    // Kubo
    public string? ApiUrl { get; init; }
    public string? Cid { get; init; }
    public string? IpnsAddress { get; init; }

    // S3
    public string? S3Bucket { get; init; }
    public string? S3Prefix { get; init; }
    public string? S3AccessKey { get; init; }
    public string? S3SecretKey { get; init; }
    public string? S3Region { get; init; }
    public string? S3Endpoint { get; init; }

    // NFS
    public string? NfsHost { get; init; }
    public string? NfsExport { get; init; }
    public string NfsPath { get; init; } = "/";
}
