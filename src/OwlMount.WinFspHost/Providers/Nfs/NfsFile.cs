using NfsSharp;
using NfsSharp.Protocol;
using OwlCore.Storage;

namespace OwlMount.WinFspHost.Providers.Nfs;

/// <summary>
/// An <see cref="IChildFile"/> backed by a file on an NFS server.
/// </summary>
internal sealed partial class NfsFile : IChildFile
{
    internal readonly NfsClient _nfsClient;

    public NfsFile(NfsClient nfsClient, string path, NfsFileAttributes? attributes = null)
    {
        _nfsClient = nfsClient;
        Path = path;
        Attributes = attributes;
    }

    /// <summary>Gets the last-known NFS file attributes for this file, if available.</summary>
    public NfsFileAttributes? Attributes { get; }

    /// <summary>Gets the full NFS path of this file (e.g. <c>/reports/q4.csv</c>).</summary>
    public string Path { get; }

    /// <inheritdoc/>
    public string Id => Path;

    /// <inheritdoc/>
    public string Name => global::System.IO.Path.GetFileName(Path);

    /// <inheritdoc/>
    public async Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default)
    {
        var parentPath = NfsHelpers.GetParentPath(Path);
        if (parentPath is null) return null;

        var attrs = await _nfsClient.GetAttrAsync(parentPath, cancellationToken);
        return new NfsFolder(_nfsClient, parentPath, attrs);
    }

    /// <inheritdoc/>
    public Task<Stream> OpenStreamAsync(FileAccess accessMode, CancellationToken cancellationToken = default)
    {
        if (accessMode is not (FileAccess.Read or FileAccess.Write or FileAccess.ReadWrite))
            throw new ArgumentOutOfRangeException(nameof(accessMode));

        // NfsStream derives from Stream, but Task<NfsStream> is not assignable to Task<Stream>
        // (generics are invariant in C#). ContinueWith performs the widening without a state machine.
        return _nfsClient.OpenFileAsync(Path, accessMode, create: false, cancellationToken)
            .ContinueWith(
                static t => (Stream)t.GetAwaiter().GetResult(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    // ── Static factory helpers ────────────────────────────────────────────────

    /// <summary>Gets an <see cref="NfsFile"/> from the specified NFS path.</summary>
    /// <exception cref="FileNotFoundException">Thrown when no file is found at <paramref name="path"/>.</exception>
    public static async Task<NfsFile> GetFromNfsPathAsync(
        NfsClient nfsClient, string path, CancellationToken cancellationToken = default)
    {
        var file = await TryGetFromNfsPathAsync(nfsClient, path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return file ?? throw new FileNotFoundException(
            $"Cannot find a file on the NFS server at path \"{path}\".");
    }

    /// <summary>
    /// Tries to get an <see cref="NfsFile"/> from the specified NFS path.
    /// Returns <c>null</c> if the path does not exist or is not a file.
    /// </summary>
    public static async Task<NfsFile?> TryGetFromNfsPathAsync(
        NfsClient nfsClient, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = await NfsHelpers.GetStorableAsync(nfsClient, path, cancellationToken);
        return item as NfsFile;
    }
}
