using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Windows.ProjFS;
using OwlCore.Storage;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;

namespace OwlMount.Core.Windows.Backends;

/// <summary>
/// <see cref="IOwlMountBackend"/> implementation that uses the Windows Projected
/// File System (ProjFS) to expose an <see cref="IFolder"/> hierarchy as a Windows
/// drive letter via <c>DefineDosDevice</c>.
/// Supports read-write access when the backing provider implements
/// <see cref="IModifiableFolder"/> and <paramref name="readOnly"/> is not forced.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed partial class ProjFsBackend : IOwlMountBackend
{
    private readonly OwlMountProvider _provider;
    private VirtualizationInstance?   _vi;
    private string?                   _virtRoot;
    private string?                   _mountPoint;

    /// <inheritdoc/>
    public string Name => "ProjFS";

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// ProjFS does not have a built-in "stopped" notification equivalent to WinFsp's
    /// <c>DispatcherStopped</c>. This event is never fired by this backend.
    /// </remarks>
#pragma warning disable CS0067
    public event EventHandler? Stopped;
#pragma warning restore CS0067

    public ProjFsBackend(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        bool readOnly = false)
    {
        IsReadOnly = readOnly || root is not IModifiableFolder;
        _provider  = new OwlMountProvider(root, blockCache, rangeReaders, sizeProviders, IsReadOnly);
    }

    /// <summary>
    /// Returns <c>true</c> when the Windows Projected File System optional feature is
    /// enabled on this machine (<c>ProjectedFSLib.dll</c> is present in System32).
    /// </summary>
    public static bool IsAvailable()
    {
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(sys32, "ProjectedFSLib.dll"));
    }

    /// <inheritdoc/>
    public bool Start(string mountPoint, string volumeLabel)
    {
        _mountPoint = mountPoint;

        string driveLetter = mountPoint.TrimEnd(':').ToUpperInvariant();
        _virtRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "VirtRoot", driveLetter);

        try { Directory.Delete(_virtRoot, recursive: true); } catch { /* best-effort */ }
        Directory.CreateDirectory(_virtRoot);

        NotificationMapping[] notificationMappings;
        if (!IsReadOnly)
        {
            notificationMappings =
            [
                new NotificationMapping
                {
                    NotificationRoot = string.Empty,
                    NotificationMask =
                        NotificationType.NewFileCreated              |
                        NotificationType.FileOverwritten             |
                        NotificationType.PreDelete                   |
                        NotificationType.PreRename                   |
                        NotificationType.FileRenamed                 |
                        NotificationType.FileHandleClosedFileModified|
                        NotificationType.FileHandleClosedFileDeleted,
                },
            ];
        }
        else
        {
            notificationMappings = [];
        }

        _vi = new VirtualizationInstance(
            _virtRoot,
            poolThreadCount:         0,
            concurrentThreadCount:   0,
            enableNegativePathCache: false,
            notificationMappings:    notificationMappings);

        _provider.SetInstance(_vi, _virtRoot);

        HResult markResult = VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(
            _virtRoot, _vi.VirtualizationInstanceId);

        if (markResult != HResult.Ok)
        {
            Console.Error.WriteLine($"Failed to mark virtualization root (HResult {(int)markResult:X8}).");
            Console.Error.WriteLine(
                "The Windows Projected File System optional feature may not be enabled.");
            Console.Error.WriteLine(
                "Enable it in an elevated PowerShell prompt, then restart:");
            Console.Error.WriteLine(
                "  Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart");
            return false;
        }

        HResult startResult = _vi.StartVirtualizing(_provider);
        if (startResult != HResult.Ok)
        {
            Console.Error.WriteLine(
                $"Failed to start ProjFS virtualization (HResult {(int)startResult:X8}).");
            return false;
        }

        if (!DefineDosDevice(0, mountPoint, _virtRoot))
        {
            int err = Marshal.GetLastPInvokeError();
            _vi.StopVirtualizing();
            Console.Error.WriteLine(
                $"Failed to map drive letter {mountPoint} (Win32 error {err}).");
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
                    $"(Win32 error {Marshal.GetLastPInvokeError()}).");
            _mountPoint = null;
        }

        _vi?.StopVirtualizing();
        _vi = null;

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

    [LibraryImport("kernel32", EntryPoint = "DefineDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DefineDosDevice(
        uint dwFlags, string lpDeviceName, string? lpTargetPath);

    private const uint DDD_REMOVE_DEFINITION = 0x00000002u;
}
