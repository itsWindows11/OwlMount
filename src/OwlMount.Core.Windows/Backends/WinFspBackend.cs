using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fsp;
using OwlCore.Storage;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;

namespace OwlMount.Core.Windows.Backends;

/// <summary>
/// <see cref="IOwlMountBackend"/> implementation that uses WinFsp
/// (<see cref="FileSystemHost"/> / <see cref="OwlMountFileSystem"/>) to expose
/// an <see cref="IFolder"/> hierarchy as a Windows drive letter.
/// Supports both read-only and read-write modes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspBackend : IOwlMountBackend
{
    private static readonly string[] WinFspDllNames = ["winfsp-x64.dll", "winfsp-x86.dll", "winfsp-arm64.dll"];
    private static string? _customPath;

    private readonly OwlMountFileSystem _fs;
    private FileSystemHost? _host;
    private readonly string? _volumeLabel;
    private readonly string _fileSystemName;

    /// <inheritdoc/>
    public string Name => "WinFsp";

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    /// <inheritdoc/>
    public event EventHandler? Stopped;

    public WinFspBackend(
        IFolder root,
        BlockCache? blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        bool readOnly = false,
        ulong? totalSize = null,
        ulong? freeSize = null,
        string? volumeLabel = null,
        string? providerName = null)
    {
        IsReadOnly = readOnly;
        _volumeLabel = volumeLabel;
        _fileSystemName = BuildFileSystemName("WinFsp", providerName);

        _fs = new OwlMountFileSystem(
            root, blockCache, rangeReaders, sizeProviders,
            readOnly:            IsReadOnly,
            totalSize:           totalSize,
            freeSize:            freeSize,
            volumeLabel:         volumeLabel,
            onDispatcherStopped: OnDispatcherStopped);
    }

    /// <summary>
    /// Sets a custom directory path to search for the WinFsp DLL
    /// (<c>winfsp-x64.dll</c>, <c>winfsp-x86.dll</c>, or <c>winfsp-arm64.dll</c>).
    /// When non-null the DLL is pre-loaded from that directory so that the WinFsp
    /// managed wrapper can find it even if WinFsp is not installed system-wide.
    /// Pass <c>null</c> to clear the custom path.
    /// </summary>
    public static void SetCustomPath(string? path)
    {
        _customPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();

        if (_customPath is null) return;

        foreach (string dllName in WinFspDllNames)
        {
            string fullPath = Path.Combine(_customPath, dllName);
            if (File.Exists(fullPath))
            {
                NativeLibrary.TryLoad(fullPath, Assembly.GetExecutingAssembly(),
                    DllImportSearchPath.AssemblyDirectory, out _);
                break;
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when WinFsp appears to be installed on this machine
    /// or is present at the custom path configured via <see cref="SetCustomPath"/>.
    /// Performs a lightweight probe — does not start the driver.
    /// </summary>
    public static bool IsAvailable()
    {
        if (_customPath is not null)
        {
            if (WinFspDllNames.Any(name => File.Exists(Path.Combine(_customPath, name))))
                return true;
        }

        string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return File.Exists(Path.Combine(pf,   "WinFsp", "bin", "winfsp-x64.dll"))
            || File.Exists(Path.Combine(pf,   "WinFsp", "bin", "winfsp-x86.dll"))
            || File.Exists(Path.Combine(pf86, "WinFsp", "bin", "winfsp-x64.dll"))
            || File.Exists(Path.Combine(pf86, "WinFsp", "bin", "winfsp-x86.dll"));
    }

    /// <inheritdoc/>
    public bool Start(string mountPoint, string volumeLabel)
    {
        try
        {
            _host = new FileSystemHost(_fs)
            {
                FileSystemName           = _fileSystemName,
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
        }
        catch (DllNotFoundException)
        {
            PrintNotInstalledError();
            return false;
        }
        catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException)
        {
            PrintNotInstalledError();
            return false;
        }

        int result = _host.Mount(mountPoint);
        if (result >= 0) return true;

        // Negative NTSTATUS values indicate failure.
        // STATUS_OBJECT_NAME_NOT_FOUND / STATUS_OBJECT_PATH_NOT_FOUND → driver not loaded.
        if (result == unchecked((int)0xC0000034) ||
            result == unchecked((int)0xC000003A))
        {
            PrintNotInstalledError();
        }
        else
        {
            Console.Error.WriteLine($"Failed to mount at {mountPoint} (WinFsp error {result}).");
            Console.Error.WriteLine("Ensure WinFsp is installed and the drive letter is not already in use.");
            Console.Error.WriteLine("  WinFsp installer: https://winfsp.dev/rel/");
        }
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

    private static void PrintNotInstalledError()
    {
        Console.Error.WriteLine("Error: WinFsp is not installed or the driver service is not running.");
        Console.Error.WriteLine("  Download WinFsp: https://winfsp.dev/rel/");
        Console.Error.WriteLine("  After installing, restart this application.");
    }

    private static string BuildFileSystemName(string backendName, string? providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? $"OwlMount ({backendName})"
            : $"OwlMount ({backendName} with {providerName.Trim()})";
}
