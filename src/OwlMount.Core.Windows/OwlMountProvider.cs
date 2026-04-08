using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Windows.ProjFS;
using OwlCore.Storage;
using OwlMount.Core.Abstractions;
using OwlMount.Core.Cache;
using OwlMount.Core.Index;
using OwlMount.Core.IO;
using OwlMount.Core.Registry;

namespace OwlMount.Core.Windows;

/// <summary>
/// Windows Projected File System (ProjFS) <see cref="IRequiredCallbacks"/> implementation
/// that exposes an <see cref="IFolder"/> hierarchy as a read-only virtualized directory.
/// <para>
/// Write operations are not supported — this is a read-only provider.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class OwlMountProvider : IRequiredCallbacks
{
    private readonly IFolder _root;
    private readonly PathIndex _index;
    private readonly BlockCache _blockCache;
    private readonly RangeReaderRegistry _rangeReaders;
    private readonly SizeProviderRegistry _sizeProviders;

    private VirtualizationInstance? _vi;
    private string? _virtRoot;

    /// <summary>
    /// Called by <see cref="Backends.ProjFsBackend"/> before
    /// <see cref="VirtualizationInstance.StartVirtualizing"/> is invoked.
    /// Registers write-back notification delegates on <paramref name="vi"/> when
    /// the provider is not read-only.
    /// </summary>
    public void SetInstance(VirtualizationInstance vi, string virtRoot)
    {
        _vi       = vi;
        _virtRoot = virtRoot;

        if (_isReadOnly) return;

        // Wire up write-back notification delegates.
        vi.OnNotifyNewFileCreated       = OnNotifyNewFileCreated;
        vi.OnNotifyFileOverwritten      = OnNotifyFileOverwritten;
        vi.OnNotifyFileRenamed          = OnNotifyFileRenamed;
        vi.OnNotifyPreDelete            = OnNotifyPreDelete;
        vi.OnNotifyPreRename            = OnNotifyPreRename;
        vi.OnNotifyPreCreateHardlink    = OnNotifyPreCreateHardlink;
        vi.OnNotifyFilePreConvertToFull = OnNotifyFilePreConvertToFull;
        vi.OnNotifyFileHandleClosedFileModifiedOrDeleted = OnNotifyFileHandleClosedFileModifiedOrDeleted;
    }

    /// <summary>Throws if <see cref="SetInstance"/> has not been called yet.</summary>
    private VirtualizationInstance RequireInstance() =>
        _vi ?? throw new InvalidOperationException(
            "VirtualizationInstance has not been set. Call SetInstance before starting virtualization.");

    private readonly bool _isReadOnly;
    private readonly ConcurrentDictionary<Guid, EnumerationState> _enumerations = new();

    public OwlMountProvider(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        bool readOnly = false)
    {
        _root          = root;
        _index         = new PathIndex();
        _blockCache    = blockCache;
        _rangeReaders  = rangeReaders;
        _sizeProviders = sizeProviders ?? new SizeProviderRegistry();
        _isReadOnly    = readOnly || root is not IModifiableFolder;
    }

    // ── IRequiredCallbacks ────────────────────────────────────────────────────

    /// <summary>
    /// Called when the OS begins enumerating a directory. Fetches, sorts, and indexes
    /// the listing in a single pass so subsequent <see cref="GetDirectoryEnumerationCallback"/>
    /// calls are fast.
    /// </summary>
    public HResult StartDirectoryEnumerationCallback(
        int commandId, Guid enumerationId, string relativePath,
        uint triggeringProcessId, string triggeringProcessImageFileName)
    {
        string norm = NormalizeProjFsPath(relativePath);
        IFolder? folder = string.IsNullOrEmpty(norm) ? _root : ResolveFolder(norm);
        if (folder is null) return HResult.FileNotFound;

        List<DirectoryEntry> entries;
        try
        {
            entries = folder.GetItemsAsync()
                .ToBlockingEnumerable()
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    bool isDir = item is IFolder;
                    DateTimeOffset? created  = GetCreatedAt(item);
                    DateTimeOffset? modified = GetLastModifiedAt(item);

                    string childPath = string.IsNullOrEmpty(norm)
                        ? item.Name
                        : norm + "/" + item.Name;

                    _index.AddOrUpdate(childPath, new PathIndexEntry
                    {
                        Id             = item.Id,
                        Name           = item.Name,
                        IsFile         = !isDir,
                        CreatedAt      = created,
                        LastModifiedAt = modified,
                    });

                    return new DirectoryEntry(
                        item.Name,
                        isDir,
                        0,
                        created  ?? DateTimeOffset.UnixEpoch,
                        modified ?? DateTimeOffset.UnixEpoch,
                        modified ?? DateTimeOffset.UnixEpoch);
                })
                .ToList();
        }
        catch
        {
            entries = new List<DirectoryEntry>();
        }

        _enumerations[enumerationId] = new EnumerationState(entries);
        return HResult.Ok;
    }

    /// <summary>
    /// Called when the OS finishes enumerating a directory. Removes the enumeration state.
    /// </summary>
    public HResult EndDirectoryEnumerationCallback(Guid enumerationId)
    {
        _enumerations.TryRemove(enumerationId, out _);
        return HResult.Ok;
    }

    /// <summary>
    /// Called (possibly multiple times) to fill the output buffer with directory entries.
    /// When the buffer is full, ProjFS calls again — we resume from the saved position.
    /// </summary>
    public HResult GetDirectoryEnumerationCallback(
        int commandId, Guid enumerationId, string filterFileName,
        bool restartScan, IDirectoryEnumerationResults results)
    {
        if (!_enumerations.TryGetValue(enumerationId, out var state))
            return HResult.InternalError;

        if (restartScan)
        {
            state.Index  = 0;
            state.Filter = filterFileName;
        }

        string? filter = state.Filter;

        while (state.Index < state.Entries.Count)
        {
            DirectoryEntry entry = state.Entries[state.Index];

            // Apply wildcard filter (case-insensitive, ? and * supported)
            if (!string.IsNullOrEmpty(filter) && filter != "*" &&
                !WildcardPattern.Match(filter, entry.Name))
            {
                state.Index++;
                continue;
            }

            bool added = results.Add(
                entry.Name,
                entry.IsDirectory ? 0L : entry.Size,
                entry.IsDirectory,
                entry.IsDirectory
                    ? (FileAttributes.Directory | (_isReadOnly ? FileAttributes.ReadOnly : 0))
                    : (_isReadOnly ? FileAttributes.ReadOnly : FileAttributes.Archive),
                entry.CreatedAt.DateTime,
                entry.LastAccessed.DateTime,
                entry.LastModified.DateTime,
                entry.LastModified.DateTime);

            if (!added) break; // output buffer full; ProjFS will call again to resume
            state.Index++;
        }

        return HResult.Ok;
    }

    /// <summary>
    /// Called when the OS needs metadata for a specific path. We write placeholder
    /// info (file/directory metadata) back via <see cref="VirtualizationInstance.WritePlaceholderInfo"/>.
    /// </summary>
    public HResult GetPlaceholderInfoCallback(
        int commandId, string relativePath,
        uint triggeringProcessId, string triggeringProcessImageFileName)
    {
        string norm  = NormalizeProjFsPath(relativePath);
        PathIndexEntry? entry = _index.TryGet(norm) ?? ResolveAndIndex(norm);
        if (entry is null) return HResult.FileNotFound;

        long size = 0;
        if (entry.IsFile)
        {
            if (!entry.Size.HasValue)
            {
                IFile? file = ResolveFile(norm);
                if (file is null) return HResult.FileNotFound;
                entry.Size = _sizeProviders.GetProvider(file)
                    .GetSizeAsync(file).GetAwaiter().GetResult();
            }
            size = entry.Size ?? 0;
        }

        return _vi!.WritePlaceholderInfo(
            relativePath:    relativePath,
            creationTime:    entry.CreatedAt?.DateTime ?? DateTime.MinValue,
            lastAccessTime:  (entry.LastModifiedAt ?? entry.CreatedAt)?.DateTime ?? DateTime.MinValue,
            lastWriteTime:   entry.LastModifiedAt?.DateTime ?? DateTime.MinValue,
            changeTime:      entry.LastModifiedAt?.DateTime ?? DateTime.MinValue,
            fileAttributes:  entry.IsFile
                ? (_isReadOnly ? FileAttributes.ReadOnly : FileAttributes.Archive)
                : (FileAttributes.Directory | (_isReadOnly ? FileAttributes.ReadOnly : 0)),
            endOfFile:       size,
            isDirectory:     !entry.IsFile,
            contentId:       null,
            providerId:      null);
    }

    /// <summary>
    /// Called when the OS reads file content. We fetch data via the block cache and
    /// write it into the ProjFS write buffer.
    /// </summary>
    public HResult GetFileDataCallback(
        int commandId, string relativePath,
        ulong byteOffset, uint length,
        Guid dataStreamId,
        byte[] contentId, byte[] providerId,
        uint triggeringProcessId, string triggeringProcessImageFileName)
    {
        string norm = NormalizeProjFsPath(relativePath);
        IFile? file = ResolveFile(norm);
        if (file is null) return HResult.FileNotFound;

        IRangeReader reader = _rangeReaders.GetReader(file);

        IWriteBuffer writeBuffer = RequireInstance().CreateWriteBuffer(
            byteOffset, length,
            out ulong alignedByteOffset, out uint alignedLength);

        try
        {
            byte[] tmp = new byte[alignedLength];
            int read = _blockCache
                .ReadAsync(file, reader, (long)alignedByteOffset, tmp.AsMemory(0, (int)alignedLength))
                .GetAwaiter().GetResult();

            if (read > 0)
                Marshal.Copy(tmp, 0, writeBuffer.Pointer, read);

            return RequireInstance().WriteFileData(dataStreamId, writeBuffer, alignedByteOffset, (uint)read);
        }
        finally
        {
            (writeBuffer as IDisposable)?.Dispose();
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Converts a ProjFS-style relative path (backslash-separated) to the normalized
    /// forward-slash form used by <see cref="PathIndex"/>.
    /// </summary>
    private static string NormalizeProjFsPath(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Resolves a normalized path segment-by-segment and adds the leaf to the index.
    /// Returns <c>null</c> if any segment is not found.
    /// </summary>
    private PathIndexEntry? ResolveAndIndex(string normalizedPath)
    {
        string[] segments = normalizedPath.Split('/');
        IFolder current   = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            IFolder? sub = GetChildFolder(current, segments[i]);
            if (sub is null) return null;
            current = sub;
        }

        string lastName = segments[^1];
        IStorableChild? child = GetChild(current, lastName);
        if (child is null) return null;

        bool isFile = child is IFile;
        var entry = new PathIndexEntry
        {
            Id             = child.Id,
            Name           = child.Name,
            IsFile         = isFile,
            CreatedAt      = GetCreatedAt(child),
            LastModifiedAt = GetLastModifiedAt(child),
        };
        _index.AddOrUpdate(normalizedPath, entry);
        return entry;
    }

    private IFile? ResolveFile(string normalizedPath)
    {
        string[] segments = normalizedPath.Split('/');
        IFolder current   = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            IFolder? sub = GetChildFolder(current, segments[i]);
            if (sub is null) return null;
            current = sub;
        }

        return GetChildFile(current, segments[^1]);
    }

    private IFolder? ResolveFolder(string normalizedPath)
    {
        string[] segments = normalizedPath.Split('/');
        IFolder current   = _root;

        foreach (string seg in segments)
        {
            IFolder? sub = GetChildFolder(current, seg);
            if (sub is null) return null;
            current = sub;
        }

        return current;
    }

    private IStorableChild? GetChild(IFolder folder, string name)
    {
        try
        {
            return folder.GetFirstByNameAsync(name).GetAwaiter().GetResult();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private IFile?   GetChildFile(IFolder folder, string name)   => GetChild(folder, name) as IFile;
    private IFolder? GetChildFolder(IFolder folder, string name) => GetChild(folder, name) as IFolder;

    private static DateTimeOffset? GetCreatedAt(IStorable item)      => StorageTimestampHelper.GetCreatedAt(item);
    private static DateTimeOffset? GetLastModifiedAt(IStorable item) => StorageTimestampHelper.GetLastModifiedAt(item);

    

    // ── Write-back notification delegates ────────────────────────────────────
    // Registered on VirtualizationInstance in SetInstance when !_isReadOnly.
    // Delegate signatures must match the exact types in Microsoft.Windows.ProjFS.

    private void OnNotifyNewFileCreated(
        string relativePath, bool isDirectory,
        uint triggeringProcessId, string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.None;
        string norm = NormalizeProjFsPath(relativePath);
        if (isDirectory) TryCreateBackingDirectory(norm);
        // New file content is synced when handle closes (OnNotifyFileHandleClosedFileModifiedOrDeleted).
    }

    private void OnNotifyFileOverwritten(
        string relativePath, bool isDirectory,
        uint triggeringProcessId, string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.None;
        if (!isDirectory) TrySyncFileFromVirtRoot(NormalizeProjFsPath(relativePath));
    }

    private bool OnNotifyPreDelete(
        string relativePath, bool isDirectory,
        uint triggeringProcessId, string triggeringProcessImageFileName)
        => true; // allow — actual delete synced in OnNotifyFileHandleClosedFileModifiedOrDeleted

    private bool OnNotifyPreRename(
        string relativePath, string destinationPath,
        uint triggeringProcessId, string triggeringProcessImageFileName)
        => true; // allow — rename synced in OnNotifyFileRenamed

    private bool OnNotifyPreCreateHardlink(
        string relativePath, string destinationPath,
        uint triggeringProcessId, string triggeringProcessImageFileName)
        => false; // hard links are not supported

    private void OnNotifyFileRenamed(
        string relativePath, string destinationPath, bool isDirectory,
        uint triggeringProcessId, string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.None;
        TryRenameInBackingStore(
            NormalizeProjFsPath(relativePath),
            NormalizeProjFsPath(destinationPath),
            isDirectory);
    }

    private bool OnNotifyFilePreConvertToFull(
        string relativePath,
        uint triggeringProcessId, string triggeringProcessImageFileName)
        => true; // allow hydration for write

    private void OnNotifyFileHandleClosedFileModifiedOrDeleted(
        string relativePath, bool isDirectory,
        bool isFileModified, bool isFileDeleted,
        uint triggeringProcessId, string triggeringProcessImageFileName)
    {
        string norm = NormalizeProjFsPath(relativePath);
        if (isFileDeleted)
            TryDeleteFromBackingStore(norm, isDirectory);
        else if (isFileModified && !isDirectory)
            TrySyncFileFromVirtRoot(norm);
    }

    // ── Write-back helpers ────────────────────────────────────────────────────

    private void TryCreateBackingDirectory(string normalizedPath)
    {
        try
        {
            string[] segments = normalizedPath.Split('/');
            IFolder current   = _root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                IFolder? sub = GetChildFolder(current, segments[i]);
                if (sub is null) return;
                current = sub;
            }
            if (current is IModifiableFolder mf)
                mf.CreateFolderAsync(segments[^1], overwrite: false, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }

    private void TrySyncFileFromVirtRoot(string normalizedPath)
    {
        if (_virtRoot is null) return;
        try
        {
            string diskPath = Path.Combine(_virtRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(diskPath)) return;

            string[] segments = normalizedPath.Split('/');
            IFolder current   = _root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                IFolder? sub = GetChildFolder(current, segments[i]);
                if (sub is null)
                {
                    // Ensure the parent directory exists in the backing store.
                    if (current is IModifiableFolder mf2)
                        current = mf2.CreateFolderAsync(segments[i], overwrite: false, CancellationToken.None).GetAwaiter().GetResult();
                    else
                        return;
                }
                else
                {
                    current = sub;
                }
            }
            if (current is not IModifiableFolder mf) return;

            using var diskStream = File.OpenRead(diskPath);
            IFile backingFile = mf.CreateFileAsync(segments[^1], overwrite: true, CancellationToken.None).GetAwaiter().GetResult();
            using Stream backingStream = backingFile.OpenStreamAsync(FileAccess.Write).GetAwaiter().GetResult();
            diskStream.CopyTo(backingStream);
        }
        catch { /* best-effort */ }
    }

    private void TryDeleteFromBackingStore(string normalizedPath, bool isDirectory)
    {
        try
        {
            string[] segments = normalizedPath.Split('/');
            IFolder current   = _root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                IFolder? sub = GetChildFolder(current, segments[i]);
                if (sub is null) return;
                current = sub;
            }
            if (current is not IModifiableFolder mf) return;
            IStorableChild? child = GetChild(current, segments[^1]);
            if (child is null) return;
            mf.DeleteAsync(child).GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }

    private void TryRenameInBackingStore(string oldNorm, string newNorm, bool isDirectory)
    {
        // IModifiableFolder has no rename/move API; implement as copy-then-delete.
        if (_virtRoot is null) return;
        try
        {
            string[] newSegs  = newNorm.Split('/');
            IFolder newParent = _root;
            for (int i = 0; i < newSegs.Length - 1; i++)
            {
                IFolder? sub = GetChildFolder(newParent, newSegs[i]);
                if (sub is null) return;
                newParent = sub;
            }

            if (!isDirectory)
            {
                // Copy content from virtRoot at the new path, then delete old backing entry.
                TrySyncFileFromVirtRoot(newNorm);
                TryDeleteFromBackingStore(oldNorm, isDirectory: false);
            }
            else
            {
                // For directories, create the new dir in backing and delete the old one.
                TryCreateBackingDirectory(newNorm);
                TryDeleteFromBackingStore(oldNorm, isDirectory: true);
            }
        }
        catch { /* best-effort */ }
    }

    // ── Enumeration state ─────────────────────────────────────────────────────

    private sealed class EnumerationState
    {
        public IReadOnlyList<DirectoryEntry> Entries { get; }
        public int    Index  { get; set; }
        public string? Filter { get; set; }

        public EnumerationState(IReadOnlyList<DirectoryEntry> entries) =>
            Entries = entries;
    }
}
