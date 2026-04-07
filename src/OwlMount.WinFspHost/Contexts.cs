using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.WinFspHost;

/// <summary>Context object for an open file handle.</summary>
internal sealed class FileContext : IDisposable
{
    public IFile File { get; }
    public PathIndexEntry Entry { get; }
    private bool _disposed;

    public FileContext(IFile file, PathIndexEntry entry)
    {
        File = file;
        Entry = entry;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (File is IDisposable d) d.Dispose();
    }
}

/// <summary>Context object for an open directory handle.</summary>
internal sealed class FolderContext
{
    public IFolder Folder { get; }

    /// <summary>The normalized (forward-slash, no leading slash) VFS path of this folder.</summary>
    public string NormalizedPath { get; }

    public FolderContext(IFolder folder, string normalizedPath)
    {
        Folder = folder;
        NormalizedPath = normalizedPath;
    }
}
