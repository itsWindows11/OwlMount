using OwlCore.Storage;

namespace OwlMount.Core.Cache;

internal sealed class OwlCoreFolderBlockCacheStore(IModifiableFolder folder) : IBlockCacheStore
{
    public async Task<bool> ExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            var file = await folder.GetFirstByNameAsync(name, cancellationToken: ct).ConfigureAwait(false);
            return file is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<byte[]> ReadAllBytesAsync(string name, CancellationToken ct)
    {
        var child = await folder.GetFirstByNameAsync(name, cancellationToken: ct).ConfigureAwait(false) ?? throw new FileNotFoundException(name);
        if (child is not IFile file) throw new FileNotFoundException(name);

        using Stream s = await file.OpenStreamAsync(FileAccess.Read, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task WriteAllBytesAtomicAsync(string name, byte[] data, CancellationToken ct)
    {
        // Write to a temp file then rename via MoveFromAsync
        string tmpName = name + ".tmp." + Guid.NewGuid().ToString("N");
        var tmpFile = await folder.CreateFileAsync(tmpName, false, ct).ConfigureAwait(false);
        await using (Stream s = await tmpFile.OpenStreamAsync(FileAccess.Write, ct).ConfigureAwait(false))
        {
            await s.WriteAsync(data.AsMemory(), ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }

        // Move/rename temp -> target name within same folder
        await ModifiableFolderExtensions.MoveFromAsync(folder, tmpFile, folder, true, name, CancellationToken.None).ConfigureAwait(false);
    }

    public IEnumerable<string> EnumerateFiles(string fileKeyPrefix)
    {
        try
        {
            return folder
                .GetItemsAsync()
                .ToBlockingEnumerable()
                .OfType<IStorableChild>()
                .Select(i => i.Name)
                .Where(n => n.StartsWith(fileKeyPrefix + "_") && n.EndsWith(".blk"));
        }
        catch
        {
            return [];
        }
    }

    public void Delete(string name)
    {
        try
        {
            var child = folder.GetFirstByNameAsync(name).GetAwaiter().GetResult();
            if (child is not null)
                folder.DeleteAsync(child).GetAwaiter().GetResult();
        }
        catch { }
    }
}
