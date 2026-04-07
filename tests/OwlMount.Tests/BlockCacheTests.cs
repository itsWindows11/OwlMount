using OwlCore.Storage;
using OwlMount.Core.Abstractions;
using OwlMount.Core.Cache;

namespace OwlMount.Tests;

/// <summary>
/// Unit tests for <see cref="BlockCache"/>, using in-memory test doubles.
/// Tests run cross-platform and do not require WinFsp or a Windows host.
/// </summary>
public sealed class BlockCacheTests : IDisposable
{
    // Use a private temp directory per test class instance
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "OwlMountTests_" + Guid.NewGuid().ToString("N"));

    public BlockCacheTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Read_SingleBlock_ReturnsCorrectBytes()
    {
        byte[] content = MakeContent(1000);
        var file   = new TestFile("f1", content);
        var reader = new TestRangeReader(content);
        var cache  = new BlockCache("test", blockSize: 512, cacheDir: _tempDir);

        byte[] buffer = new byte[100];
        int read = await cache.ReadAsync(file, reader, offset: 0, buffer.AsMemory());

        Assert.Equal(100, read);
        Assert.Equal(content[..100], buffer);
    }

    [Fact]
    public async Task Read_AcrossBlockBoundary_ReturnsCorrectBytes()
    {
        byte[] content = MakeContent(1500);
        var file   = new TestFile("f2", content);
        var reader = new TestRangeReader(content);
        var cache  = new BlockCache("test", blockSize: 512, cacheDir: _tempDir);

        // Request 600 bytes starting at offset 400 — crosses the 512-byte block boundary
        byte[] buffer = new byte[600];
        int read = await cache.ReadAsync(file, reader, offset: 400, buffer.AsMemory());

        Assert.Equal(600, read);
        Assert.Equal(content[400..1000], buffer);
    }

    [Fact]
    public async Task Read_BeyondEof_ReturnsOnlyAvailableBytes()
    {
        byte[] content = MakeContent(200);
        var file   = new TestFile("f3", content);
        var reader = new TestRangeReader(content);
        var cache  = new BlockCache("test", blockSize: 512, cacheDir: _tempDir);

        byte[] buffer = new byte[512]; // bigger than file
        int read = await cache.ReadAsync(file, reader, offset: 0, buffer.AsMemory());

        Assert.Equal(200, read);
        Assert.Equal(content, buffer[..200]);
    }

    [Fact]
    public async Task Read_FromOffset_AtEof_ReturnsZero()
    {
        byte[] content = MakeContent(100);
        var file   = new TestFile("f4", content);
        var reader = new TestRangeReader(content);
        var cache  = new BlockCache("test", blockSize: 512, cacheDir: _tempDir);

        byte[] buffer = new byte[50];
        int read = await cache.ReadAsync(file, reader, offset: 100, buffer.AsMemory());

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Read_UsesOnDiskCacheOnSecondCall()
    {
        byte[] content = MakeContent(300);
        var file          = new TestFile("f5", content);
        var countingReader = new CountingRangeReader(content);
        var cache          = new BlockCache("test", blockSize: 512, cacheDir: _tempDir);

        // First read — should hit the reader once (one block)
        byte[] buf1 = new byte[300];
        await cache.ReadAsync(file, countingReader, offset: 0, buf1.AsMemory());

        // Second read of same range — block is already on disk; reader should NOT be called again
        byte[] buf2 = new byte[300];
        await cache.ReadAsync(file, countingReader, offset: 0, buf2.AsMemory());

        Assert.Equal(1, countingReader.CallCount); // fetched only once
        Assert.Equal(buf1, buf2);
    }

    [Fact]
    public async Task Read_MultipleBlocks_AllBlocksFetched()
    {
        // 3 full blocks + partial 4th block
        byte[] content = MakeContent(512 * 3 + 100);
        var file   = new TestFile("f6", content);
        var reader = new TestRangeReader(content);
        var cache  = new BlockCache("test", blockSize: 512, cacheDir: _tempDir);

        byte[] buffer = new byte[content.Length];
        int read = await cache.ReadAsync(file, reader, offset: 0, buffer.AsMemory());

        Assert.Equal(content.Length, read);
        Assert.Equal(content, buffer);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] MakeContent(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 256)).ToArray();
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class TestFile(string id, byte[] content) : IFile
{
    public string Id   { get; } = id;
    public string Name { get; } = id;

    public Task<Stream> OpenStreamAsync(
        FileAccess desiredAccess = FileAccess.Read,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(content, writable: false));
}

file sealed class TestRangeReader(byte[] content) : IRangeReader
{
    public Task<int> ReadAsync(
        IFile file, long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        if (offset >= content.Length) return Task.FromResult(0);
        int available = (int)Math.Min(buffer.Length, content.Length - offset);
        content.AsMemory((int)offset, available).CopyTo(buffer);
        return Task.FromResult(available);
    }
}

file sealed class CountingRangeReader(byte[] content) : IRangeReader
{
    public int CallCount { get; private set; }

    public Task<int> ReadAsync(
        IFile file, long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        CallCount++;
        if (offset >= content.Length) return Task.FromResult(0);
        int available = (int)Math.Min(buffer.Length, content.Length - offset);
        content.AsMemory((int)offset, available).CopyTo(buffer);
        return Task.FromResult(available);
    }
}
