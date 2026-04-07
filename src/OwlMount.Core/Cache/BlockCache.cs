using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.Core.Cache;

/// <summary>
/// Disk-backed block cache for efficient ranged reads of <see cref="IFile"/> content.
/// <para>
/// Blocks are stored as individual files under a provider-scoped cache directory:
/// <c>%LocalAppData%\OwlMount\Cache\&lt;providerId&gt;\&lt;fileHash&gt;_&lt;blockIndex&gt;.blk</c>
/// </para>
/// In-flight fetches for the same block are deduplicated so a block is only
/// downloaded once even under concurrent access.
/// </summary>
public sealed class BlockCache : IDisposable
{
    private readonly int _blockSize;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Task<byte[]>> _inflight = new();

    /// <param name="providerId">
    ///   Unique identifier for this provider; used to namespace the on-disk cache.
    /// </param>
    /// <param name="blockSize">Block size in bytes. Defaults to 256 KiB.</param>
    /// <param name="cacheDir">
    ///   Override the cache directory (useful for testing). When <c>null</c> the
    ///   default <c>%LocalAppData%\OwlMount\Cache\&lt;providerId&gt;</c> is used.
    /// </param>
    public BlockCache(string providerId, int blockSize = 256 * 1024, string? cacheDir = null)
    {
        _blockSize = blockSize;
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "Cache", SanitizeName(providerId));
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Reads up to <c>destination.Length</c> bytes from <paramref name="file"/>
    /// starting at <paramref name="offset"/>, using the on-disk block cache to
    /// avoid redundant network or I/O fetches.
    /// </summary>
    /// <returns>Number of bytes actually read; may be less at EOF.</returns>
    public async Task<int> ReadAsync(
        IFile file,
        IRangeReader reader,
        long offset,
        Memory<byte> destination,
        CancellationToken ct = default)
    {
        string fileKey = ComputeFileKey(file.Id);
        int totalRead = 0;
        long remaining = destination.Length;
        long pos = offset;

        while (remaining > 0)
        {
            long blockIndex = pos / _blockSize;
            int blockOffset = (int)(pos % _blockSize);
            int blockAvailable = _blockSize - blockOffset;
            int toRead = (int)Math.Min(remaining, blockAvailable);

            byte[] block = await GetOrFetchBlockAsync(file, reader, fileKey, blockIndex, ct);

            int canRead = Math.Min(toRead, block.Length - blockOffset);
            if (canRead <= 0) break; // EOF

            block.AsMemory(blockOffset, canRead).CopyTo(destination.Slice(totalRead));
            totalRead += canRead;
            remaining -= canRead;
            pos += canRead;

            if (canRead < toRead) break; // partial last block == EOF
        }

        return totalRead;
    }

    private async Task<byte[]> GetOrFetchBlockAsync(
        IFile file,
        IRangeReader reader,
        string fileKey,
        long blockIndex,
        CancellationToken ct)
    {
        string blockPath = Path.Combine(_cacheDir, $"{fileKey}_{blockIndex}.blk");

        if (File.Exists(blockPath))
        {
            try
            {
                return await File.ReadAllBytesAsync(blockPath, ct);
            }
            catch
            {
                // Corrupt block file — fall through to re-fetch.
            }
        }

        string inflightKey = $"{fileKey}_{blockIndex}";
        Task<byte[]> task = _inflight.GetOrAdd(inflightKey,
            _ => FetchBlockAsync(file, reader, blockIndex, blockPath, ct));

        try
        {
            return await task;
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Task<byte[]>>(inflightKey, task));
        }
    }

    private async Task<byte[]> FetchBlockAsync(
        IFile file,
        IRangeReader reader,
        long blockIndex,
        string blockPath,
        CancellationToken ct)
    {
        long offset = blockIndex * _blockSize;
        byte[] buffer = new byte[_blockSize];
        int totalRead = await reader.ReadAsync(file, offset, buffer.AsMemory(), ct);

        byte[] actual = totalRead < _blockSize ? buffer[..totalRead] : buffer;

        // Write to cache atomically via a temp file + rename.
        string tmpPath = blockPath + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, actual, ct);
        File.Move(tmpPath, blockPath, overwrite: true);

        return actual;
    }

    private static string ComputeFileKey(string fileId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fileId));
        // Use first 16 hex characters (8 bytes) — sufficient for cache namespacing.
        return Convert.ToHexString(hash)[..16];
    }

    private static string SanitizeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    public void Dispose() { }
}
