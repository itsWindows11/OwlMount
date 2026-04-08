using System.Runtime.CompilerServices;
using NfsSharp;
using NfsSharp.Protocol;
using OwlCore.Storage;

namespace OwlMount.WinFspHost.Providers.Nfs;

/// <summary>
/// An <see cref="IFolder"/> backed by a directory on an NFS server.
/// </summary>
internal sealed partial class NfsFolder : IChildFolder, IGetItem, IGetFirstByName, IModifiableFolder
{
    internal readonly NfsClient _nfsClient;

    public NfsFolder(NfsClient nfsClient, string path, NfsFileAttributes? attributes = null)
    {
        _nfsClient = nfsClient;
        Path = path;
        Attributes = attributes;
    }

    /// <summary>Gets the last-known NFS attributes for this folder, if available.</summary>
    public NfsFileAttributes? Attributes { get; }

    /// <summary>Gets the full NFS path of this folder (e.g. <c>/reports</c>).</summary>
    public string Path { get; }

    /// <inheritdoc/>
    public string Id => Path;

    /// <inheritdoc/>
    public string Name => Path == "/"
        ? string.Empty
        : global::System.IO.Path.GetFileName(Path.TrimEnd('/'));

    /// <inheritdoc/>
    public async Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default)
    {
        var parentPath = NfsHelpers.GetParentPath(Path);
        if (parentPath is null) return null;

        var attrs = await _nfsClient.GetAttrAsync(parentPath, cancellationToken);
        return new NfsFolder(_nfsClient, parentPath, attrs);
    }

    /// <inheritdoc/>
    public Task<IStorableChild> GetFirstByNameAsync(string name, CancellationToken cancellationToken = default)
        => GetItemAsync(NfsHelpers.CombinePath(Path, name), cancellationToken);

    /// <inheritdoc/>
    public async Task<IStorableChild> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        var item = await NfsHelpers.GetStorableAsync(_nfsClient, id, cancellationToken)
            ?? throw new FileNotFoundException(
                $"Could not find an item at NFS path \"{id}\".");

        return (IStorableChild)item;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IStorableChild> GetItemsAsync(
        StorableType type = StorableType.All,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (type == StorableType.None)
            throw new ArgumentOutOfRangeException(
                nameof(type), $"{nameof(StorableType)}.{nameof(StorableType.None)} is not valid here.");

        await foreach (var entry in _nfsClient.ReadDirStreamAsync(Path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Name is "." or "..")
                continue;

            var entryPath = NfsHelpers.CombinePath(Path, entry.Name);
            var attrs = entry.Attributes;

            if (attrs is null)
            {
                try
                {
                    attrs = await _nfsClient.GetAttrAsync(entryPath, cancellationToken);
                }
                catch
                {
                    continue;
                }
            }

            bool isDirectory = attrs.Type == NfsFileType.Directory;

            if (isDirectory && type.HasFlag(StorableType.Folder))
                yield return new NfsFolder(_nfsClient, entryPath, attrs);
            else if (!isDirectory && type.HasFlag(StorableType.File))
                yield return new NfsFile(_nfsClient, entryPath, attrs);
        }
    }

    /// <inheritdoc/>
    public Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NFS folder watching is not supported.");

    /// <inheritdoc/>
    public async Task<IChildFolder> CreateFolderAsync(
        string name,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var childPath = NfsHelpers.CombinePath(Path, name);
        var existing = await NfsHelpers.GetStorableAsync(_nfsClient, childPath, cancellationToken);

        if (existing is not null)
        {
            if (!replaceExisting)
                throw new IOException($"An item already exists at NFS path \"{childPath}\".");

            await DeleteAsync((IStorableChild)existing, cancellationToken);
        }

        await _nfsClient.MkDirAsync(childPath, new NfsSetAttributes(), cancellationToken);
        var attrs = await _nfsClient.GetAttrAsync(childPath, cancellationToken);
        return new NfsFolder(_nfsClient, childPath, attrs);
    }

    /// <inheritdoc/>
    public async Task<IChildFile> CreateFileAsync(
        string name,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var childPath = NfsHelpers.CombinePath(Path, name);
        var existing = await NfsHelpers.GetStorableAsync(_nfsClient, childPath, cancellationToken);

        if (existing is not null)
        {
            if (!replaceExisting)
                throw new IOException($"An item already exists at NFS path \"{childPath}\".");

            await DeleteAsync((IStorableChild)existing, cancellationToken);
        }

        await using (var stream = await _nfsClient.OpenFileAsync(childPath, FileAccess.ReadWrite, create: true, cancellationToken))
        {
        }

        var attrs = await _nfsClient.GetAttrAsync(childPath, cancellationToken);
        return new NfsFile(_nfsClient, childPath, attrs);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(IStorableChild item, CancellationToken cancellationToken = default) =>
        item switch
        {
            NfsFile file => _nfsClient.RemoveAsync(file.Path, cancellationToken),
            NfsFolder folder => _nfsClient.RmDirAsync(folder.Path, cancellationToken),
            _ => _nfsClient.RemoveAsync(NfsHelpers.CombinePath(Path, item.Name), cancellationToken),
        };

    // ── Static factory helpers ────────────────────────────────────────────────

    /// <summary>Gets an <see cref="NfsFolder"/> from the specified NFS path.</summary>
    /// <exception cref="FileNotFoundException">Thrown when no directory exists at <paramref name="path"/>.</exception>
    public static async Task<NfsFolder> GetFromNfsPathAsync(
        NfsClient nfsClient, string path, CancellationToken cancellationToken = default)
    {
        var folder = await TryGetFromNfsPathAsync(nfsClient, path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return folder ?? throw new FileNotFoundException(
            $"Cannot find a directory on the NFS server at path \"{path}\".");
    }

    /// <summary>
    /// Tries to get an <see cref="NfsFolder"/> from the specified NFS path.
    /// Returns <c>null</c> if the path does not exist or is not a directory.
    /// </summary>
    public static async Task<NfsFolder?> TryGetFromNfsPathAsync(
        NfsClient nfsClient, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = await NfsHelpers.GetStorableAsync(nfsClient, path, cancellationToken);
        return item as NfsFolder;
    }
}
