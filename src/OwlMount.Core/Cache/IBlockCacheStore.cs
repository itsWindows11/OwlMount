namespace OwlMount.Core.Cache;

internal interface IBlockCacheStore
{
    Task<bool> ExistsAsync(string name, CancellationToken ct);
    Task<byte[]> ReadAllBytesAsync(string name, CancellationToken ct);
    Task WriteAllBytesAtomicAsync(string name, byte[] data, CancellationToken ct);
    IEnumerable<string> EnumerateFiles(string fileKeyPrefix);
    void Delete(string name);
}
