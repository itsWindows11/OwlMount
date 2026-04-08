using System.Runtime.Versioning;
using Fsp;
using OwlCore.Storage;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;

namespace OwlMount.WinFspHost;

/// <summary>
/// <see cref="IOwlMountBackend"/> implementation that uses WinFsp
/// (<see cref="FileSystemHost"/> / <see cref="OwlMountFileSystem"/>) to expose
/// an <see cref="IFolder"/> hierarchy as a Windows drive letter.
/// Supports both read-only and read-write modes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspBackend : IOwlMountBackend
{
    private readonly OwlMountFileSystem _fs;
    private FileSystemHost? _host;

    /// <inheritdoc/>
    public string Name => "WinFsp";

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    /// <inheritdoc/>
    public event EventHandler? Stopped;

    public WinFspBackend(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        bool readOnly = false,
        ulong? totalSize = null,
        ulong? freeSize = null)
    {
        // Caller is responsible for pre-computing the effective read-only flag
        // (e.g. forcing it for the archive provider or when IModifiableFolder is absent).
        IsReadOnly = readOnly;

        _fs = new OwlMountFileSystem(
            root, blockCache, rangeReaders, sizeProviders,
            readOnly:             IsReadOnly,
            totalSize:            totalSize,
            freeSize:             freeSize,
            onDispatcherStopped:  OnDispatcherStopped);
    }

    /// <inheritdoc/>
    public bool Start(string mountPoint, string volumeLabel)
    {
        _host = new FileSystemHost(_fs)
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

        int result = _host.Mount(mountPoint);
        if (result >= 0) return true;

        Console.Error.WriteLine($"Failed to mount at {mountPoint} (WinFsp error {result}).");
        Console.Error.WriteLine("Ensure WinFsp is installed and the drive letter is not already in use.");
        Console.Error.WriteLine("  WinFsp installer: https://winfsp.dev/rel/");
        return false;
    }

    /// <inheritdoc/>
    public void Stop() => _host?.Unmount();

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _host?.Dispose();
    }

    private void OnDispatcherStopped() => Stopped?.Invoke(this, EventArgs.Empty);
}
