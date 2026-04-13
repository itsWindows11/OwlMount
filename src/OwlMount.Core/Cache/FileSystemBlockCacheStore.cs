namespace OwlMount.Core.Cache;

internal sealed class FileSystemBlockCacheStore : IBlockCacheStore
{
    private readonly string _dir;

    public FileSystemBlockCacheStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    public Task<bool> ExistsAsync(string name, CancellationToken ct) => Task.FromResult(File.Exists(PathFor(name)));

    public Task<byte[]> ReadAllBytesAsync(string name, CancellationToken ct) => File.ReadAllBytesAsync(PathFor(name), ct);

    public async Task WriteAllBytesAtomicAsync(string name, byte[] data, CancellationToken ct)
    {
        string path = PathFor(name);
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public IEnumerable<string> EnumerateFiles(string fileKeyPrefix)
    {
        try
        {
            return Directory.EnumerateFiles(_dir, $"{fileKeyPrefix}_*.blk").Select(Path.GetFileName).Select(n => n!);
        }
        catch { return []; }
    }

    public void Delete(string name)
    {
        try { File.Delete(PathFor(name)); } catch { }
    }
}
