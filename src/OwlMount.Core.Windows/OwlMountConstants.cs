namespace OwlMount.Core.Windows;

public static class OwlMountConstants
{
    public const string DefaultProvider = MemoryProvider;
    public const string DefaultBackend = DokanyBackend;
    public const string DefaultDriveLetter = "M";
    public const string DefaultNfsPath = "/";

    public const string DefaultProviderSettingKey = "DefaultProvider";
    public const string DefaultBackendSettingKey = "DefaultBackend";
    public const string ThemeSettingKey = "AppTheme";
    public const string DefaultBlockCacheSizeSettingKey = "DefaultBlockCacheSize";
    public const string EnableBlockCacheSettingKey = "EnableBlockCache";
    public const string DokanyPathSettingKey = "DokanyPath";
    public const string WinFspPathSettingKey = "WinFspPath";

    public const string MemoryProvider = "memory";
    public const string ArchiveProvider = "archive";
    public const string LocalProvider = "local";
    public const string KuboMfsProvider = "kubo-mfs";
    public const string KuboIpfsProvider = "kubo-ipfs";
    public const string KuboIpnsProvider = "kubo-ipns";
    public const string S3Provider = "s3";
    public const string NfsProvider = "nfs";

    public const string DokanyBackend = "dokany";
    public const string WinFspBackend = "winfsp";
    public const string ProjFsBackend = "projfs";

    public static IReadOnlyList<string> ProviderIds { get; } =
    [
        MemoryProvider,
        ArchiveProvider,
        LocalProvider,
        KuboMfsProvider,
        KuboIpfsProvider,
        KuboIpnsProvider,
        S3Provider,
        NfsProvider,
    ];

    public static IReadOnlyList<string> BackendIds { get; } =
    [
        DokanyBackend,
        WinFspBackend,
        ProjFsBackend,
    ];

    public static string GetProviderDisplayName(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            MemoryProvider => "Memory",
            ArchiveProvider => "Archive file",
            LocalProvider => "Local folder",
            KuboMfsProvider => "Kubo MFS",
            KuboIpfsProvider => "Kubo IPFS",
            KuboIpnsProvider => "Kubo IPNS",
            S3Provider => "Amazon S3",
            NfsProvider => "NFS",
            _ => provider,
        };
}
