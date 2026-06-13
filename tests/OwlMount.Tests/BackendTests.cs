using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using OwlCore.Storage;
using OwlCore.Storage.Memory;
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
    public async Task MemoryProvider_CanCreateFoldersAndWriteThenReadBackFiles()
    {
        var root = new MemoryFolder("memory-root", "memory-root");
        var docs = (IModifiableFolder)await root.CreateFolderAsync("docs", overwrite: false);
        var file = (IFile)await docs.CreateFileAsync("hello.txt", overwrite: false);

        const string expected = "hello from memory";
        await using (Stream stream = await file.OpenStreamAsync(FileAccess.Write))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(expected);
            await stream.WriteAsync(bytes);
        }

        var folder = (IFolder)await root.GetFirstByNameAsync("docs");
        var roundTrip = (IFile)await folder.GetFirstByNameAsync("hello.txt");
        await using Stream readStream = await roundTrip.OpenStreamAsync(FileAccess.Read);
        using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);

        Assert.Equal(expected, await reader.ReadToEndAsync());
    }

    // ── Factory helpers ───────────────────────────────────────────────────────

    private WinFspBackend MakeWinFsp(bool readOnly = false)
    {
        var blockCache   = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        return new WinFspBackend(
            new MemoryFolder("root", "root"),
            blockCache, rangeReaders, readOnly: readOnly);
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private ProjFsBackend MakeProjFs(bool readOnly = false)
    {
        var blockCache   = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        return new ProjFsBackend(
            new MemoryFolder("root", "root"),
            blockCache, rangeReaders, readOnly: readOnly);
    }

    private DokanyBackend MakeDokany(bool readOnly = false)
    {
        var blockCache = new BlockCache("backend-test", cacheDir: _tempDir);
        var rangeReaders = new RangeReaderRegistry();
        return new DokanyBackend(
            new MemoryFolder("root", "root"),
            blockCache, rangeReaders, readOnly: readOnly);
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
