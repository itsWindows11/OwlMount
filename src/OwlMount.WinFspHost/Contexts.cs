using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.WinFspHost;

/// <summary>Context object for an open file handle.</summary>
internal sealed class FileContext : IDisposable
{
    public IFile File { get; set; }
    public PathIndexEntry Entry { get; set; }
    public string NormalizedPath { get; set; }
    public Stream? WriteStream => _writeStream;
    private Stream? _writeStream;
    private bool _disposed;

    public FileContext(IFile file, PathIndexEntry entry, string normalizedPath)
    {
        File = file;
        Entry = entry;
        NormalizedPath = normalizedPath;
    }

    public Stream GetOrOpenWriteStream(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _writeStream ??= File.OpenStreamAsync(FileAccess.ReadWrite, cancellationToken)
            .GetAwaiter().GetResult();
    }

    public void DisposeWriteStream()
    {
        try { _writeStream?.Dispose(); } catch { /* best-effort */ }
        _writeStream = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWriteStream();
        if (File is IDisposable d) d.Dispose();
    }
}

/// <summary>Context object for an open directory handle.</summary>
internal sealed class FolderContext
{
    public IFolder Folder { get; set; }

    /// <summary>The normalized (forward-slash, no leading slash) VFS path of this folder.</summary>
    public string NormalizedPath { get; set; }

    public FolderContext(IFolder folder, string normalizedPath)
    {
        Folder = folder;
        NormalizedPath = normalizedPath;
    }
}
