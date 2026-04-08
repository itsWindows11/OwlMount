using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Windows.ProjFS;
using OwlCore.Storage;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;

namespace OwlMount.WinFspHost;

/// <summary>
/// <see cref="IOwlMountBackend"/> implementation that uses the Windows Projected
/// File System (ProjFS) to expose an <see cref="IFolder"/> hierarchy as a Windows
/// drive letter via <c>DefineDosDevice</c>.
/// ProjFS is always read-only in the current provider.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ProjFsBackend : IOwlMountBackend
{
    private readonly OwlMountProvider _provider;
    private VirtualizationInstance?   _vi;
    private string?                   _virtRoot;
    private string?                   _mountPoint;

    /// <inheritdoc/>
    public string Name => "ProjFS";

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    /// <remarks>
    /// ProjFS does not have a built-in "stopped" notification equivalent to WinFsp's
    /// <c>DispatcherStopped</c>. This event is never fired by this backend; the
    /// process must be stopped via Ctrl+C or the unmount command.
    /// </remarks>
#pragma warning disable CS0067 // intentional API event that is never raised by this backend
    public event EventHandler? Stopped;
#pragma warning restore CS0067

    public ProjFsBackend(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null)
    {
        _provider = new OwlMountProvider(root, blockCache, rangeReaders, sizeProviders);
    }

    /// <inheritdoc/>
    public bool Start(string mountPoint, string volumeLabel)
    {
        _mountPoint = mountPoint;

        // Derive a stable per-drive virtualization root under %LocalAppData%
        string driveLetter = mountPoint.TrimEnd(':').ToUpperInvariant();
        _virtRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "VirtRoot", driveLetter);

        // Start fresh — delete any leftover placeholder tree from a prior run.
        try { Directory.Delete(_virtRoot, recursive: true); } catch { /* best-effort */ }
        Directory.CreateDirectory(_virtRoot);

        _vi = new VirtualizationInstance(
            _virtRoot,
            poolThreadCount:         0,
            concurrentThreadCount:   0,
            enableNegativePathCache: false,
            notificationMappings:    Array.Empty<NotificationMapping>());

        _provider.Instance = _vi;

        HResult markResult = VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(
            _virtRoot, _vi.VirtualizationInstanceId);

        if (markResult != HResult.Ok)
        {
            Console.Error.WriteLine($"Failed to mark virtualization root (HResult {markResult}).");
            Console.Error.WriteLine(
                "Ensure the 'Windows Projected File System' optional feature is enabled:");
            Console.Error.WriteLine(
                "  Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart");
            return false;
        }

        HResult startResult = _vi.StartVirtualizing(_provider);
        if (startResult != HResult.Ok)
        {
            Console.Error.WriteLine($"Failed to start ProjFS virtualization (HResult {startResult}).");
            return false;
        }

        if (!DefineDosDevice(0, mountPoint, _virtRoot))
        {
            int err = Marshal.GetLastWin32Error();
            _vi.StopVirtualizing();
            Console.Error.WriteLine($"Failed to map drive letter {mountPoint} (Win32 error {err}).");
            Console.Error.WriteLine("Ensure the drive letter is not already in use.");
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_mountPoint is not null)
        {
            if (!DefineDosDevice(DDD_REMOVE_DEFINITION, _mountPoint, null))
                Console.Error.WriteLine(
                    $"Warning: failed to remove drive letter {_mountPoint} " +
                    $"(Win32 error {Marshal.GetLastWin32Error()}).");
            _mountPoint = null;
        }

        if (_vi is not null)
        {
            _vi.StopVirtualizing();
            _vi = null;
        }

        if (_virtRoot is not null)
        {
            try { Directory.Delete(_virtRoot, recursive: true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Warning: could not delete virtualization root '{_virtRoot}': {ex.Message}");
            }
            _virtRoot = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DefineDosDevice(
        uint dwFlags, string lpDeviceName, string? lpTargetPath);

    private const uint DDD_REMOVE_DEFINITION = 0x00000002u;
}
