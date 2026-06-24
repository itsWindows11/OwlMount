using System.Collections.Generic;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Threading;
using OwlCore.Storage;
using OwlCore.Storage.SharpCompress;
using OwlCore.Storage.Memory;
using OwlCore.Storage.System.IO;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;
using OwlMount.Core.Windows.Backends;

namespace OwlMount.Tests;

/// <summary>
/// Unit tests for <see cref="WinFspBackend"/>, <see cref="ProjFsBackend"/>,
/// and <see cref="DokanyBackend"/>.
/// Tests that require a specific OS version skip silently on unsupported platforms.
/// </summary>
public sealed class BackendTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "OwlMountBackendTests_" + Guid.NewGuid().ToString("N"));

    public BackendTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── OS guards ─────────────────────────────────────────────────────────────

    private static bool IsWindows()    => OperatingSystem.IsWindows();
    private static bool IsProjFsOs()   => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    // ── WinFspBackend tests ───────────────────────────────────────────────────

    [Fact]
    public void WinFsp_Name_ReturnsWinFsp()
    {
        if (!IsWindows()) return;
        using var backend = MakeWinFsp();
        Assert.Equal("WinFsp", backend.Name);
    }

    [Fact]
    public void WinFsp_IsReadOnly_Default_IsFalse()
    {
        if (!IsWindows()) return;
        using var backend = MakeWinFsp(readOnly: false);
        Assert.False(backend.IsReadOnly);
    }

    [Fact]
    public void WinFsp_IsReadOnly_True_WhenForced()
    {
        if (!IsWindows()) return;
        using var backend = MakeWinFsp(readOnly: true);
        Assert.True(backend.IsReadOnly);
    }

    [Fact]
    public void WinFsp_IsAvailable_DoesNotThrow()
    {
        if (!IsWindows()) return;
        _ = WinFspBackend.IsAvailable();
    }

    [Fact]
    public void WinFsp_Dispose_BeforeStart_DoesNotThrow()
    {
        if (!IsWindows()) return;
        MakeWinFsp().Dispose(); // must not throw
    }

    [Fact]
    public void WinFsp_Stop_BeforeStart_DoesNotThrow()
    {
        if (!IsWindows()) return;
        using var backend = MakeWinFsp();
        backend.Stop(); // must not throw
    }

    // ── ProjFsBackend tests ───────────────────────────────────────────────────

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_Name_ReturnsProjFS()
    {
        if (!IsProjFsOs()) return;
        using var backend = MakeProjFs();
        Assert.Equal("ProjFS", backend.Name);
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_IsReadOnly_False_WithModifiableRoot()
    {
        if (!IsProjFsOs()) return;
        using var backend = MakeProjFs(readOnly: false);
        Assert.False(backend.IsReadOnly);
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_IsReadOnly_True_WhenForced()
    {
        if (!IsProjFsOs()) return;
        using var backend = MakeProjFs(readOnly: true);
        Assert.True(backend.IsReadOnly);
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_IsReadOnly_True_WhenRootIsNotModifiable()
    {
        if (!IsProjFsOs()) return;
        var blockCache   = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        using var backend = new ProjFsBackend(
            new ReadOnlyFolderStub("root", "root"),
            blockCache, rangeReaders);
        Assert.True(backend.IsReadOnly);
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_IsAvailable_DoesNotThrow()
    {
        if (!IsProjFsOs()) return;
        _ = ProjFsBackend.IsAvailable();
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_Dispose_BeforeStart_DoesNotThrow()
    {
        if (!IsProjFsOs()) return;
        MakeProjFs().Dispose(); // must not throw
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public void ProjFs_Stop_BeforeStart_DoesNotThrow()
    {
        if (!IsProjFsOs()) return;
        using var backend = MakeProjFs();
        backend.Stop(); // must not throw
    }

    // ── DokanyBackend tests ───────────────────────────────────────────────────

    [Fact]
    public void Dokany_Name_ReturnsDokany()
    {
        if (!IsWindows()) return;
        using var backend = MakeDokany();
        Assert.Equal("Dokany", backend.Name);
    }

    [Fact]
    public void Dokany_IsReadOnly_Default_IsFalse()
    {
        if (!IsWindows()) return;
        using var backend = MakeDokany(readOnly: false);
        Assert.False(backend.IsReadOnly);
    }

    [Fact]
    public void Dokany_IsReadOnly_True_WhenForced()
    {
        if (!IsWindows()) return;
        using var backend = MakeDokany(readOnly: true);
        Assert.True(backend.IsReadOnly);
    }

    [Fact]
    public void Dokany_IsReadOnly_True_WhenRootIsNotModifiable()
    {
        if (!IsWindows()) return;
        var blockCache = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        using var backend = new DokanyBackend(
            new ReadOnlyFolderStub("root", "root"),
            blockCache, rangeReaders);
        Assert.True(backend.IsReadOnly);
    }

    [Fact]
    public void Dokany_IsAvailable_DoesNotThrow()
    {
        if (!IsWindows()) return;
        _ = DokanyBackend.IsAvailable();
    }

    [Fact]
    public void Dokany_Dispose_BeforeStart_DoesNotThrow()
    {
        if (!IsWindows()) return;
        MakeDokany().Dispose(); // must not throw
    }

    [Fact]
    public void Dokany_Stop_BeforeStart_DoesNotThrow()
    {
        if (!IsWindows()) return;
        using var backend = MakeDokany();
        backend.Stop(); // must not throw
    }

    // ── Memory provider tests ────────────────────────────────────────────────

    [Fact]
    public async Task MemoryProvider_WinFsp_CanMount_AndReadWriteViaSystemIO()
    {
        if (!IsWindows() || !WinFspBackend.IsAvailable()) return;

        using var backend = MakeWinFsp(root: new MemoryFolder("memory-root", "memory-root"));
        await using var mount = await MountBackendAsync(backend);

        string rootPath = mount.MountPoint + Path.DirectorySeparatorChar;
        string docsPath = Path.Combine(rootPath, "docs");
        string filePath = Path.Combine(docsPath, "hello.txt");

        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(filePath, "hello from memory");

        Assert.True(Directory.Exists(docsPath));
        Assert.Equal("hello from memory", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task ArchiveProvider_WinFsp_CanMount_AndReadViaSystemIO()
    {
        if (!IsWindows() || !WinFspBackend.IsAvailable()) return;

        string archivePath = CreateZipArchive("sample.zip", [("readme.txt", "archive content")]);
        var root = new ArchiveFolder(new SystemFile(archivePath));
        using var backend = MakeWinFsp(root, readOnly: true);
        await using var mount = await MountBackendAsync(backend);

        string filePath = Path.Combine(mount.MountPoint, "readme.txt");
        Assert.Equal("archive content", await File.ReadAllTextAsync(filePath));
        Assert.True(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task MemoryProvider_Dokany_CanMount_AndReadWriteViaSystemIO()
    {
        if (!IsWindows() || !DokanyBackend.IsAvailable()) return;

        using var backend = MakeDokany(root: new MemoryFolder("memory-root", "memory-root"));
        await using var mount = await MountBackendAsync(backend);

        string rootPath = mount.MountPoint + Path.DirectorySeparatorChar;
        string docsPath = Path.Combine(rootPath, "docs");
        string filePath = Path.Combine(docsPath, "hello.txt");

        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(filePath, "hello from memory");

        Assert.True(Directory.Exists(docsPath));
        Assert.Equal("hello from memory", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task ArchiveProvider_Dokany_CanMount_AndReadViaSystemIO()
    {
        if (!IsWindows() || !DokanyBackend.IsAvailable()) return;

        string archivePath = CreateZipArchive("sample.zip", [("readme.txt", "archive content")]);
        var root = new ArchiveFolder(new SystemFile(archivePath));
        using var backend = MakeDokany(root, readOnly: true);
        await using var mount = await MountBackendAsync(backend);

        string filePath = Path.Combine(mount.MountPoint, "readme.txt");
        Assert.Equal("archive content", await File.ReadAllTextAsync(filePath));
        Assert.True(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly));
    }

    // ── Factory helpers ───────────────────────────────────────────────────────

    private WinFspBackend MakeWinFsp(IFolder? root = null, bool readOnly = false)
    {
        var blockCache   = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        return new WinFspBackend(
            root ?? new MemoryFolder("root", "root"),
            blockCache, rangeReaders, readOnly: readOnly);
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private ProjFsBackend MakeProjFs(IFolder? root = null, bool readOnly = false)
    {
        var blockCache   = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        return new ProjFsBackend(
            root ?? new MemoryFolder("root", "root"),
            blockCache, rangeReaders, readOnly: readOnly);
    }

    private DokanyBackend MakeDokany(IFolder? root = null, bool readOnly = false)
    {
        var blockCache = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        return new DokanyBackend(
            root ?? new MemoryFolder("root", "root"),
            blockCache, rangeReaders, readOnly: readOnly);
    }

    private static string CreateZipArchive(string fileName, IReadOnlyList<(string Name, string Content)> entries)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "OwlMountArchiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        foreach (var (name, content) in entries)
            File.WriteAllText(Path.Combine(sourceDir, name), content);

        string archivePath = Path.Combine(tempDir, fileName);
        ZipFile.CreateFromDirectory(sourceDir, archivePath);
        return archivePath;
    }

    private static async Task<MountedBackend> MountBackendAsync(IOwlMountBackend backend)
    {
        foreach (char driveLetter in GetFreeDriveLetters())
        {
            string mountPoint = driveLetter + ":";
            if (!backend.Start(mountPoint, "OwlMountTests"))
                continue;

            string driveRoot = mountPoint + Path.DirectorySeparatorChar;
            if (Directory.Exists(driveRoot))
            {
                await Task.Yield();
                return new MountedBackend(backend, mountPoint);
            }

            backend.Stop();
        }

        throw new InvalidOperationException("No usable free drive letter was available for the backend mount.");
    }

    private static IEnumerable<char> GetFreeDriveLetters()
    {
        HashSet<char> usedLetters = [.. DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .Where(c => c is >= 'A' and <= 'Z')];

        foreach (char letter in Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i))
            if (!usedLetters.Contains(letter))
                yield return letter;
    }

    private sealed record MountedBackend(IOwlMountBackend Backend, string MountPoint) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try { Backend.Stop(); }
            finally { Backend.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// Minimal <see cref="IFolder"/> that is intentionally NOT <see cref="IModifiableFolder"/>,
/// used to verify that <see cref="ProjFsBackend.IsReadOnly"/> is forced to <c>true</c>
/// when the backing root is read-only.
/// </summary>
file sealed class ReadOnlyFolderStub(string id, string name) : IFolder
{
    public string Id   { get; } = id;
    public string Name { get; } = name;

    public IAsyncEnumerable<IStorableChild> GetItemsAsync(
        StorableType type = StorableType.All,
        CancellationToken cancellationToken = default)
        => AsyncEnumerableEmpty<IStorableChild>();

    private static async IAsyncEnumerable<T> AsyncEnumerableEmpty<T>()
    {
        yield break;
    }
}
