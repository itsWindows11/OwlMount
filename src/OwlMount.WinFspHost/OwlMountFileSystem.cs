using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fsp;
using Fsp.Interop;
using OwlCore.Storage;
using OwlMount.Core.Abstractions;
using OwlMount.Core.Cache;
using OwlMount.Core.Index;
using OwlMount.Core.Registry;
using FileInfo = Fsp.Interop.FileInfo;

namespace OwlMount.WinFspHost;

/// <summary>
/// WinFsp <see cref="FileSystemBase"/> implementation that exposes an
/// <see cref="IFolder"/> hierarchy as a Windows drive letter.
/// Immutable providers stay read-only, and mutable providers can also be forced
/// open as read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OwlMountFileSystem : FileSystemBase
{
    private readonly IFolder _root;
    private readonly PathIndex _index;
    private readonly DirectoryCache _dirCache;
    private readonly BlockCache _blockCache;
    private readonly RangeReaderRegistry _rangeReaders;
    private readonly SizeProviderRegistry _sizeProviders;
    private readonly string _volumeLabel;
    private readonly bool _isReadOnly;
    private readonly ulong _totalSize;
    private readonly ulong _freeSize;
    private readonly ConcurrentDictionary<string, WatchedFolder> _watchedFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _notifySync = new();

    private FileSystemHost? _host;

    private const ulong DefaultVolumeSize = 1UL << 40;
    private const uint AttrReadOnly = 0x00000001u;
    private const uint AttrDirectory = 0x00000010u;
    private const uint AttrArchive = 0x00000020u;
    private const uint CreateOptionsDirectoryFile = 0x00000001u;

    private readonly Action? _onDispatcherStopped;

    public OwlMountFileSystem(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        bool readOnly = false,
        ulong? totalSize = null,
        ulong? freeSize = null,
        string? volumeLabel = null,
        Action? onDispatcherStopped = null)
    {
        _root = root;
        _index = new PathIndex();
        _dirCache = new DirectoryCache();
        _blockCache = blockCache;
        _rangeReaders = rangeReaders;
        _sizeProviders = sizeProviders ?? new SizeProviderRegistry();
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "OwlMount" : volumeLabel;
        _onDispatcherStopped = onDispatcherStopped;
        _isReadOnly = readOnly || root is not IModifiableFolder;
        _totalSize = totalSize.GetValueOrDefault(DefaultVolumeSize);
        _freeSize = _isReadOnly ? 0 : Math.Min(freeSize ?? _totalSize, _totalSize);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by WinFsp when the dispatcher thread exits — including when the user
    /// ejects/unmounts the drive from Explorer or the system. We use this to signal
    /// the main loop so the process exits cleanly.
    /// </summary>
    public override void DispatcherStopped(bool normally) =>
        _onDispatcherStopped?.Invoke();

    /// <summary>
    /// Called by WinFsp when the filesystem is mounted. Capture the host so
    /// provider watcher events can be forwarded as filesystem notifications.
    /// </summary>
    public override int Mounted(object host)
    {
        _host = host as FileSystemHost;
        EnsureFolderWatcher(_root, string.Empty);
        return base.Mounted(host);
    }

    /// <summary>
    /// Called by WinFsp when the filesystem is unmounted. Dispose any active
    /// provider watchers.
    /// </summary>
    public override void Unmounted(object host)
    {
        foreach (WatchedFolder watcher in _watchedFolders.Values)
            watcher.Dispose();

        _watchedFolders.Clear();
        _host = null;
        base.Unmounted(host);
    }

    // ── Volume info ───────────────────────────────────────────────────────────

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = _totalSize;
        volumeInfo.FreeSize = _freeSize;
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    // ── Security / path existence ─────────────────────────────────────────────

    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        fileAttributes = 0;

        if (IsRoot(fileName))
        {
            fileAttributes = GetFileAttributes(isDirectory: true);
            return STATUS_SUCCESS;
        }

        string norm = PathIndex.Normalize(fileName);
        PathIndexEntry? entry = _index.TryGet(norm) ?? ResolveAndIndex(norm);
        if (entry is null) return STATUS_OBJECT_NAME_NOT_FOUND;

        fileAttributes = GetFileAttributes(!entry.IsFile);
        return STATUS_SUCCESS;
    }

    // ── Open / Close ──────────────────────────────────────────────────────────

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        if (IsRoot(fileName))
        {
            EnsureFolderWatcher(_root, string.Empty);
            fileNode = new FolderContext(_root, string.Empty);
            FillFolderInfo(ref fileInfo);
            return STATUS_SUCCESS;
        }

        string norm = PathIndex.Normalize(fileName);
        PathIndexEntry? entry = _index.TryGet(norm) ?? ResolveAndIndex(norm);
        if (entry is null) return STATUS_OBJECT_NAME_NOT_FOUND;

        if (entry.IsFile)
        {
            IFile? file = ResolveFile(norm);
            if (file is null) return STATUS_OBJECT_NAME_NOT_FOUND;

            // Populate size on first open if still unknown
            if (!entry.Size.HasValue)
                entry.Size = _sizeProviders.GetProvider(file)
                    .GetSizeAsync(file).GetAwaiter().GetResult();

            fileNode = new FileContext(file, entry, norm);
            FillFileInfo(entry, ref fileInfo);
        }
        else
        {
            IFolder? folder = ResolveFolder(norm);
            if (folder is null) return STATUS_OBJECT_NAME_NOT_FOUND;
            EnsureFolderWatcher(folder, norm);
            fileNode = new FolderContext(folder, norm);
            FillFolderInfo(ref fileInfo, entry);
        }

        return STATUS_SUCCESS;
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileNode is FileContext fc)
            fc.Dispose();
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (fileNode is not FileContext ctx) return STATUS_INVALID_DEVICE_REQUEST;

        byte[] tmp = new byte[length];
        IRangeReader reader = _rangeReaders.GetReader(ctx.File);

        int read = _blockCache
            .ReadAsync(ctx.File, reader, (long)offset, tmp.AsMemory(0, (int)length))
            .GetAwaiter().GetResult();

        if (read > 0)
            Marshal.Copy(tmp, 0, buffer, read);

        bytesTransferred = (uint)read;
        return STATUS_SUCCESS;
    }

    // ── File / directory info ─────────────────────────────────────────────────

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        switch (fileNode)
        {
            case FileContext fc:
                FillFileInfo(fc.Entry, ref fileInfo);
                break;
            case FolderContext dc:
                PathIndexEntry? entry = string.IsNullOrEmpty(dc.NormalizedPath)
                    ? null
                    : _index.TryGet(dc.NormalizedPath) ?? ResolveAndIndex(dc.NormalizedPath);
                FillFolderInfo(ref fileInfo, entry);
                break;
            default:
                return STATUS_INVALID_DEVICE_REQUEST;
        }

        return STATUS_SUCCESS;
    }

    // ── Directory enumeration ─────────────────────────────────────────────────

    /// <summary>
    /// Called repeatedly by WinFsp's buffered ReadDirectory helper.
    /// Each call returns the next matching entry; returning <c>false</c> signals
    /// end-of-directory.
    /// </summary>
    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object? context,
        out string? fileName,
        out FileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;

        if (fileNode is not FolderContext folderCtx) return false;

        IReadOnlyList<DirectoryEntry> entries = GetOrPopulateDirectory(folderCtx);
        int idx = context is int i ? i : 0;

        if (context is null && marker is not null)
        {
            while (idx < entries.Count &&
                   string.Compare(entries[idx].Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
            {
                idx++;
            }
        }

        while (idx < entries.Count)
        {
            DirectoryEntry entry = entries[idx++];

            if (pattern is not null && !WildcardMatch(pattern, entry.Name))
                continue;

            fileName = entry.Name;
            FillEntryInfo(entry, ref fileInfo);
            context = idx;
            return true;
        }

        return false;
    }

    // ── Write operations: all denied (read-only filesystem) ───────────────────

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        if (_isReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;

        string norm = PathIndex.Normalize(fileName);
        if (string.IsNullOrEmpty(norm)) return STATUS_ACCESS_DENIED;

        string parentPath = GetParentPath(norm);
        IFolder? parent = ResolveFolderOrRoot(parentPath);
        if (parent is not IModifiableFolder modifiableParent) return STATUS_ACCESS_DENIED;

        string name = GetLeafName(norm);
        bool isDirectory = (createOptions & CreateOptionsDirectoryFile) != 0;

        try
        {
            IStorableChild created = isDirectory
                ? (IStorableChild)modifiableParent.CreateFolderAsync(name, false).GetAwaiter().GetResult()
                : modifiableParent.CreateFileAsync(name, false).GetAwaiter().GetResult();

            PathIndexEntry entry = CreateEntry(created, isDirectory ? null : 0);
            _index.AddOrUpdate(norm, entry);
            _dirCache.Invalidate(parentPath);
            _dirCache.InvalidateSubtree(norm);

            normalizedName = fileName;

            if (created is IFile file)
            {
                fileNode = new FileContext(file, entry, norm);
                FillFileInfo(entry, ref fileInfo);
            }
            else if (created is IFolder folder)
            {
                EnsureFolderWatcher(folder, norm);
                fileNode = new FolderContext(folder, norm);
                FillFolderInfo(ref fileInfo, entry);
            }
            else
            {
                return STATUS_ACCESS_DENIED;
            }

            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return HandleWriteFailure("create", norm, ex, STATUS_UNEXPECTED_IO_ERROR);
        }
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_isReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileNode is not FileContext ctx) return STATUS_FILE_IS_A_DIRECTORY;

        try
        {
            Stream stream = ctx.GetOrOpenWriteStream();
            stream.SetLength((long)allocationSize);
            if (stream.CanSeek) stream.Position = 0;
            TouchFileEntry(ctx, stream.Length);
            FillFileInfo(ctx.Entry, ref fileInfo);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return HandleWriteFailure("overwrite", ctx.NormalizedPath, ex, STATUS_UNEXPECTED_IO_ERROR);
        }
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;

        if (_isReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileNode is not FileContext ctx) return STATUS_FILE_IS_A_DIRECTORY;

        try
        {
            Stream stream = ctx.GetOrOpenWriteStream();
            if (!stream.CanSeek && (offset != 0 || writeToEndOfFile))
                return STATUS_INVALID_PARAMETER;

            if (writeToEndOfFile)
                offset = (ulong)stream.Length;

            if (stream.CanSeek)
                stream.Seek((long)offset, SeekOrigin.Begin);

            byte[] tmp = new byte[length];
            Marshal.Copy(buffer, tmp, 0, (int)length);
            stream.Write(tmp, 0, (int)length);
            stream.Flush();

            bytesTransferred = length;
            TouchFileEntry(ctx, stream.CanSeek ? stream.Length : ctx.Entry.Size);
            FillFileInfo(ctx.Entry, ref fileInfo);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return HandleWriteFailure("write", ctx.NormalizedPath, ex, STATUS_UNEXPECTED_IO_ERROR);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;

        switch (fileNode)
        {
            case FileContext fc:
                try
                {
                    fc.WriteStream?.Flush();
                    FillFileInfo(fc.Entry, ref fileInfo);
                    return STATUS_SUCCESS;
                }
                catch (Exception ex)
                {
                    return HandleWriteFailure("flush", fc.NormalizedPath, ex, STATUS_UNEXPECTED_IO_ERROR);
                }

            case FolderContext dc:
                PathIndexEntry? entry = string.IsNullOrEmpty(dc.NormalizedPath)
                    ? null
                    : _index.TryGet(dc.NormalizedPath) ?? ResolveAndIndex(dc.NormalizedPath);
                FillFolderInfo(ref fileInfo, entry);
                return STATUS_SUCCESS;

            default:
                return STATUS_INVALID_DEVICE_REQUEST;
        }
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_isReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;

        try
        {
            switch (fileNode)
            {
                case FileContext fc:
                    ApplyBasicInfo(fc.File, creationTime, lastAccessTime, lastWriteTime);
                    fc.Entry.CreatedAt = GetCreatedAt(fc.File) ?? fc.Entry.CreatedAt;
                    fc.Entry.LastAccessedAt = GetLastAccessedAt(fc.File) ?? fc.Entry.LastAccessedAt;
                    fc.Entry.LastModifiedAt = GetLastModifiedAt(fc.File) ?? fc.Entry.LastModifiedAt;
                    _index.AddOrUpdate(fc.NormalizedPath, fc.Entry);
                    FillFileInfo(fc.Entry, ref fileInfo);
                    return STATUS_SUCCESS;

                case FolderContext dc:
                    ApplyBasicInfo(dc.Folder, creationTime, lastAccessTime, lastWriteTime);
                    PathIndexEntry? entry = string.IsNullOrEmpty(dc.NormalizedPath)
                        ? null
                        : _index.TryGet(dc.NormalizedPath) ?? ResolveAndIndex(dc.NormalizedPath);
                    FillFolderInfo(ref fileInfo, entry);
                    return STATUS_SUCCESS;

                default:
                    return STATUS_INVALID_DEVICE_REQUEST;
            }
        }
        catch (Exception ex)
        {
            string path = fileNode switch
            {
                FileContext fc => fc.NormalizedPath,
                FolderContext dc => dc.NormalizedPath,
                _ => string.Empty,
            };
            return HandleWriteFailure("set-basic-info", path, ex, STATUS_UNEXPECTED_IO_ERROR);
        }
    }

    public override int SetFileSize(
        object fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_isReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileNode is not FileContext ctx) return STATUS_FILE_IS_A_DIRECTORY;

        try
        {
            Stream stream = ctx.GetOrOpenWriteStream();
            stream.SetLength((long)newSize);
            if (stream.CanSeek && stream.Position > (long)newSize)
                stream.Position = (long)newSize;
            TouchFileEntry(ctx, (long)newSize);
            FillFileInfo(ctx.Entry, ref fileInfo);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return HandleWriteFailure("set-file-size", ctx.NormalizedPath, ex, STATUS_UNEXPECTED_IO_ERROR);
        }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (_isReadOnly || (flags & CleanupDelete) == 0)
            return;

        string normalizedPath = GetNormalizedPath(fileNode, fileName);
        if (string.IsNullOrEmpty(normalizedPath))
            return;

        string parentPath = GetParentPath(normalizedPath);
        IFolder? parent = ResolveFolderOrRoot(parentPath);
        if (parent is not IModifiableFolder modifiableParent)
            return;

        IStorableChild? child = GetChild(parent, GetLeafName(normalizedPath));
        if (child is null)
            return;

        if (fileNode is FileContext fc)
            fc.DisposeWriteStream();

        try
        {
            modifiableParent.DeleteAsync(child).GetAwaiter().GetResult();
            RemoveFolderWatchers(normalizedPath);
            InvalidatePath(normalizedPath, child.Id);
            _dirCache.Invalidate(parentPath);
        }
        catch (Exception ex)
        {
            LogWriteFailure("cleanup-delete", normalizedPath, ex, MapWriteException(ex, STATUS_UNEXPECTED_IO_ERROR));
        }
    }

    public override int Rename(
        object fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        if (_isReadOnly) return STATUS_MEDIA_WRITE_PROTECTED;

        string oldPath = GetNormalizedPath(fileNode, fileName);
        string newPath = PathIndex.Normalize(newFileName);

        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
            return STATUS_ACCESS_DENIED;

        if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            return STATUS_SUCCESS;

        if (newPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
            return STATUS_ACCESS_DENIED;

        string oldParentPath = GetParentPath(oldPath);
        string newParentPath = GetParentPath(newPath);

        IFolder? sourceParentFolder = ResolveFolderOrRoot(oldParentPath);
        IFolder? destinationParentFolder = ResolveFolderOrRoot(newParentPath);
        if (sourceParentFolder is not IModifiableFolder sourceParent ||
            destinationParentFolder is not IModifiableFolder destinationParent)
        {
            return STATUS_ACCESS_DENIED;
        }

        IStorableChild? sourceItem = GetChild(sourceParentFolder, GetLeafName(oldPath));
        if (sourceItem is null) return STATUS_OBJECT_NAME_NOT_FOUND;

        IStorableChild? destinationItem = GetChild(destinationParentFolder, GetLeafName(newPath));
        if (destinationItem is not null)
        {
            if (!replaceIfExists)
                return STATUS_OBJECT_NAME_COLLISION;

            if (sourceItem is IFolder && destinationItem is IFolder)
                return STATUS_OBJECT_NAME_COLLISION;

            try
            {
                destinationParent.DeleteAsync(destinationItem).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return HandleWriteFailure("rename-delete-destination", newPath, ex, STATUS_UNEXPECTED_IO_ERROR);
            }
        }

        try
        {
            if (sourceItem is IChildFile sourceFile)
            {
                IChildFile movedFile = ModifiableFolderExtensions
                    .MoveFromAsync(destinationParent, sourceFile, sourceParent, replaceIfExists, GetLeafName(newPath), CancellationToken.None)
                    .GetAwaiter().GetResult();

                PathIndexEntry entry = CreateEntry(movedFile, (fileNode as FileContext)?.Entry.Size);
                _index.AddOrUpdate(newPath, entry);

                if (fileNode is FileContext fc)
                {
                    fc.DisposeWriteStream();
                    fc.File = movedFile;
                    fc.Entry = entry;
                    fc.NormalizedPath = newPath;
                }
            }
            else if (sourceItem is IChildFolder sourceFolder && sourceFolder is IModifiableFolder sourceFolderMod)
            {
                MoveFolderRecursive(sourceFolder, sourceFolderMod, sourceParent, destinationParent, GetLeafName(newPath));

                if (fileNode is FolderContext dc)
                {
                    dc.Folder = ResolveFolder(newPath) ?? dc.Folder;
                    dc.NormalizedPath = newPath;
                    EnsureFolderWatcher(dc.Folder, dc.NormalizedPath);
                }
            }
            else
            {
                return STATUS_ACCESS_DENIED;
            }

            RemoveFolderWatchers(oldPath);
            InvalidatePath(oldPath, sourceItem.Id);
            InvalidatePath(newPath);
            _dirCache.Invalidate(oldParentPath);
            _dirCache.Invalidate(newParentPath);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return HandleWriteFailure("rename", oldPath, ex, STATUS_UNEXPECTED_IO_ERROR);
        }
    }

    public override int SetSecurity(
        object fileNode,
        object fileDesc,
        System.Security.AccessControl.AccessControlSections sections,
        byte[] securityDescriptor)
    {
        if (_isReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        return STATUS_SUCCESS;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private int HandleWriteFailure(string operation, string path, Exception exception, int fallbackStatus)
    {
        int status = MapWriteException(exception, fallbackStatus);
        LogWriteFailure(operation, path, exception, status);
        return status;
    }

    private int MapWriteException(Exception exception, int fallbackStatus)
    {
        return exception switch
        {
            DirectoryNotFoundException => STATUS_OBJECT_PATH_NOT_FOUND,
            FileNotFoundException => STATUS_OBJECT_NAME_NOT_FOUND,
            UnauthorizedAccessException => STATUS_ACCESS_DENIED,
            PathTooLongException => STATUS_INVALID_PARAMETER,
            ArgumentException => STATUS_INVALID_PARAMETER,
            NotSupportedException => STATUS_ACCESS_DENIED,
            OutOfMemoryException => STATUS_INSUFFICIENT_RESOURCES,
            IOException ioException => MapIOException(ioException, fallbackStatus),
            _ => fallbackStatus,
        };
    }

    private int MapIOException(IOException exception, int fallbackStatus)
    {
        const int ErrorFileNotFound = 2;
        const int ErrorPathNotFound = 3;
        const int ErrorAccessDenied = 5;
        const int ErrorSharingViolation = 32;
        const int ErrorHandleDiskFull = 39;
        const int ErrorFileExists = 80;
        const int ErrorDiskFull = 112;
        const int ErrorDirNotEmpty = 145;
        const int ErrorAlreadyExists = 183;

        int win32 = exception.HResult & 0xFFFF;
        return win32 switch
        {
            ErrorFileNotFound => STATUS_OBJECT_NAME_NOT_FOUND,
            ErrorPathNotFound => STATUS_OBJECT_PATH_NOT_FOUND,
            ErrorAccessDenied => STATUS_ACCESS_DENIED,
            ErrorSharingViolation => STATUS_SHARING_VIOLATION,
            ErrorHandleDiskFull or ErrorDiskFull => STATUS_DISK_FULL,
            ErrorDirNotEmpty => STATUS_DIRECTORY_NOT_EMPTY,
            ErrorFileExists or ErrorAlreadyExists => STATUS_OBJECT_NAME_COLLISION,
            _ => fallbackStatus,
        };
    }

    private void LogWriteFailure(string operation, string path, Exception exception, int status)
    {
        string target = string.IsNullOrWhiteSpace(path) ? "<unknown>" : path;
        Console.Error.WriteLine($"OwlMount {operation} failed for '{target}' (NTSTATUS {status}): {exception.Message}");
    }

    private void EnsureFolderWatcher(IFolder folder, string normalizedPath)
    {
        if (folder is not IMutableFolder mutableFolder || _watchedFolders.ContainsKey(normalizedPath))
            return;

        try
        {
            IFolderWatcher watcher = mutableFolder.GetFolderWatcherAsync().GetAwaiter().GetResult();
            WatchedFolder watchedFolder = new(watcher, normalizedPath, HandleFolderCollectionChanged);
            if (!_watchedFolders.TryAdd(normalizedPath, watchedFolder))
                watchedFolder.Dispose();
        }
        catch (NotSupportedException)
        {
        }
        catch (NotImplementedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void HandleFolderCollectionChanged(string normalizedFolderPath, NotifyCollectionChangedEventArgs args)
    {
        try
        {
            _dirCache.Invalidate(normalizedFolderPath);

            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (IStorableChild item in EnumerateItems(args.NewItems))
                        HandleAddedChild(normalizedFolderPath, item);
                    break;

                case NotifyCollectionChangedAction.Remove:
                    foreach (IStorableChild item in EnumerateItems(args.OldItems))
                        HandleRemovedChild(normalizedFolderPath, item);
                    break;

                case NotifyCollectionChangedAction.Replace:
                case NotifyCollectionChangedAction.Move:
                    foreach (IStorableChild item in EnumerateItems(args.OldItems))
                        HandleRemovedChild(normalizedFolderPath, item);
                    foreach (IStorableChild item in EnumerateItems(args.NewItems))
                        HandleAddedChild(normalizedFolderPath, item);
                    break;

                case NotifyCollectionChangedAction.Reset:
                    RemoveDescendantFolderWatchers(normalizedFolderPath);
                    _dirCache.InvalidateSubtree(normalizedFolderPath);
                    _index.RemoveSubtree(normalizedFolderPath);
                    NotifySingleChange(normalizedFolderPath, NotifyAction.Modified, NotifyFilter.ChangeDirName);
                    break;
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<IStorableChild> EnumerateItems(IList? items)
    {
        if (items is null)
            yield break;

        foreach (object? item in items)
        {
            if (item is IStorableChild child)
                yield return child;
        }
    }

    private void HandleAddedChild(string normalizedFolderPath, IStorableChild item)
    {
        string childPath = CombineNormalizedPath(normalizedFolderPath, item.Name);
        _index.AddOrUpdate(childPath, CreateEntry(item));
        _dirCache.InvalidateSubtree(childPath);

        if (item is IFolder folder)
            EnsureFolderWatcher(folder, childPath);

        NotifySingleChange(
            childPath,
            NotifyAction.Added,
            item is IFolder ? NotifyFilter.ChangeDirName : NotifyFilter.ChangeFileName);
    }

    private void HandleRemovedChild(string normalizedFolderPath, IStorableChild item)
    {
        string childPath = CombineNormalizedPath(normalizedFolderPath, item.Name);
        RemoveFolderWatchers(childPath);
        InvalidatePath(childPath, item.Id);

        NotifySingleChange(
            childPath,
            NotifyAction.Removed,
            item is IFolder ? NotifyFilter.ChangeDirName : NotifyFilter.ChangeFileName);
    }

    private void NotifySingleChange(string normalizedPath, NotifyAction action, NotifyFilter filter)
    {
        FileSystemHost? host = _host;
        if (host is null)
            return;

        NotifyInfo change = new()
        {
            FileName = ToNotifyPath(normalizedPath),
            Action = action,
            Filter = filter,
        };

        lock (_notifySync)
        {
            if (host.NotifyBegin(0) < 0)
                return;

            try
            {
                host.Notify([change]);
            }
            finally
            {
                host.NotifyEnd();
            }
        }
    }

    private void RemoveFolderWatchers(string normalizedPath)
    {
        string prefix = string.IsNullOrEmpty(normalizedPath)
            ? string.Empty
            : normalizedPath + "/";

        foreach (var pair in _watchedFolders.ToArray())
        {
            if (pair.Key.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(prefix) && pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (_watchedFolders.TryRemove(pair.Key, out WatchedFolder? watchedFolder))
                    watchedFolder.Dispose();
            }
        }
    }

    private void RemoveDescendantFolderWatchers(string normalizedPath)
    {
        string prefix = string.IsNullOrEmpty(normalizedPath)
            ? string.Empty
            : normalizedPath + "/";

        if (string.IsNullOrEmpty(prefix))
            return;

        foreach (var pair in _watchedFolders.ToArray())
        {
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                _watchedFolders.TryRemove(pair.Key, out WatchedFolder? watchedFolder))
            {
                watchedFolder.Dispose();
            }
        }
    }

    private IReadOnlyList<DirectoryEntry> GetOrPopulateDirectory(FolderContext ctx)
    {
        EnsureFolderWatcher(ctx.Folder, ctx.NormalizedPath);

        IReadOnlyList<DirectoryEntry>? cached = _dirCache.TryGet(ctx.NormalizedPath);
        if (cached is not null) return cached;

        List<IStorableChild> items = ctx.Folder
            .GetItemsAsync()
            .ToBlockingEnumerable()
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<DirectoryEntry> result = new(items.Count);
        foreach (IStorableChild item in items)
        {
            bool isDir = item is IFolder;
            DateTimeOffset? created = GetCreatedAt(item);
            DateTimeOffset? accessed = GetLastAccessedAt(item);
            DateTimeOffset? modified = GetLastModifiedAt(item);

            string childPath = CombineNormalizedPath(ctx.NormalizedPath, item.Name);
            if (item is IFolder childFolder)
                EnsureFolderWatcher(childFolder, childPath);

            long? knownSize = item is IFile file
                ? TryGetFileSize(file, childPath)
                : null;

            _index.AddOrUpdate(childPath, new PathIndexEntry
            {
                Id = item.Id,
                Name = item.Name,
                IsFile = !isDir,
                Size = knownSize,
                CreatedAt = created == DateTimeOffset.MinValue ? null : created,
                LastModifiedAt = modified == DateTimeOffset.MinValue ? null : modified,
                LastAccessedAt = accessed == DateTimeOffset.MinValue ? null : accessed,
            });

            result.Add(new DirectoryEntry(
                item.Name,
                isDir,
                knownSize ?? 0,
                created ?? DateTimeOffset.UnixEpoch,
                accessed ?? DateTimeOffset.UnixEpoch,
                modified ?? DateTimeOffset.UnixEpoch));
        }

        _dirCache.Set(ctx.NormalizedPath, result);
        return result;
    }

    private long? TryGetFileSize(IFile file, string? normalizedPath = null)
    {
        if (!string.IsNullOrEmpty(normalizedPath) && _index.TryGet(normalizedPath) is { Size: long cachedSize })
            return cachedSize;

        try
        {
            return _sizeProviders
                .GetProvider(file)
                .GetSizeAsync(file)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return null;
        }
    }

    private PathIndexEntry? ResolveAndIndex(string normalizedPath)
    {
        string[] segments = normalizedPath.Split('/');
        IFolder current = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            IFolder? sub = GetChildFolder(current, segments[i]);
            if (sub is null) return null;
            current = sub;
        }

        string lastName = segments[^1];
        IStorableChild? child = GetChild(current, lastName);
        if (child is null) return null;

        if (child is IFolder folder)
            EnsureFolderWatcher(folder, normalizedPath);

        PathIndexEntry entry = CreateEntry(child, child is IFile file ? TryGetFileSize(file, normalizedPath) : null);
        _index.AddOrUpdate(normalizedPath, entry);
        return entry;
    }

    private IFile? ResolveFile(string normalizedPath)
    {
        string[] segments = normalizedPath.Split('/');
        IFolder current = _root;

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
        IFolder current = _root;
        string currentPath = string.Empty;

        EnsureFolderWatcher(current, currentPath);

        foreach (string seg in segments)
        {
            IFolder? sub = GetChildFolder(current, seg);
            if (sub is null) return null;
            current = sub;
            currentPath = CombineNormalizedPath(currentPath, seg);
            EnsureFolderWatcher(current, currentPath);
        }

        return current;
    }

    private IStorableChild? ResolveItem(string normalizedPath)
    {
        string parentPath = GetParentPath(normalizedPath);
        IFolder? parent = ResolveFolderOrRoot(parentPath);
        return parent is null ? null : GetChild(parent, GetLeafName(normalizedPath));
    }

    private IFolder? ResolveFolderOrRoot(string normalizedPath) =>
        string.IsNullOrEmpty(normalizedPath) ? _root : ResolveFolder(normalizedPath);

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
        catch
        {
            return folder.GetItemsAsync()
                .ToBlockingEnumerable()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private IFile? GetChildFile(IFolder folder, string name) => GetChild(folder, name) as IFile;
    private IFolder? GetChildFolder(IFolder folder, string name) => GetChild(folder, name) as IFolder;

    private void FillFileInfo(PathIndexEntry entry, ref FileInfo fi)
    {
        fi.FileAttributes = GetFileAttributes(isDirectory: false);
        fi.AllocationSize = (ulong)Math.Max(entry.Size ?? 0, 0);
        fi.FileSize = (ulong)Math.Max(entry.Size ?? 0, 0);
        fi.CreationTime = ToFileTime(entry.CreatedAt);
        fi.LastAccessTime = ToFileTime(entry.LastAccessedAt ?? entry.CreatedAt);
        fi.LastWriteTime = ToFileTime(entry.LastModifiedAt);
        fi.ChangeTime = fi.LastWriteTime;
    }

    private void FillFolderInfo(ref FileInfo fi, PathIndexEntry? entry = null)
    {
        fi.FileAttributes = GetFileAttributes(isDirectory: true);
        fi.CreationTime = entry is not null ? ToFileTime(entry.CreatedAt) : 0;
        fi.LastAccessTime = entry is not null ? ToFileTime(entry.LastAccessedAt ?? entry.CreatedAt) : 0;
        fi.LastWriteTime = entry is not null ? ToFileTime(entry.LastModifiedAt) : 0;
        fi.ChangeTime = fi.LastWriteTime;
    }

    private void FillEntryInfo(DirectoryEntry entry, ref FileInfo fi)
    {
        fi.FileAttributes = GetFileAttributes(entry.IsDirectory);

        if (!entry.IsDirectory)
        {
            fi.FileSize = (ulong)Math.Max(entry.Size, 0);
            fi.AllocationSize = (ulong)Math.Max(entry.Size, 0);
        }

        fi.CreationTime = ToFileTime(entry.CreatedAt == DateTimeOffset.UnixEpoch ? null : entry.CreatedAt);
        fi.LastAccessTime = ToFileTime(entry.LastAccessed == DateTimeOffset.UnixEpoch ? null : entry.LastAccessed);
        fi.LastWriteTime = ToFileTime(entry.LastModified == DateTimeOffset.UnixEpoch ? null : entry.LastModified);
        fi.ChangeTime = fi.LastWriteTime;
    }

    private uint GetFileAttributes(bool isDirectory)
    {
        if (isDirectory)
            return AttrDirectory | (_isReadOnly ? AttrReadOnly : 0);

        return _isReadOnly ? AttrReadOnly : AttrArchive;
    }

    private void TouchFileEntry(FileContext ctx, long? size)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ctx.Entry.Size = size;
        ctx.Entry.LastModifiedAt = now;
        ctx.Entry.LastAccessedAt = now;
        _index.AddOrUpdate(ctx.NormalizedPath, ctx.Entry);
        _blockCache.Invalidate(ctx.File.Id);
        _dirCache.Invalidate(GetParentPath(ctx.NormalizedPath));
    }

    private void InvalidatePath(string normalizedPath, string? fileId = null)
    {
        _index.RemoveSubtree(normalizedPath);
        _dirCache.InvalidateSubtree(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fileId))
            _blockCache.Invalidate(fileId);
    }

    private static string GetParentPath(string normalizedPath)
    {
        int slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalizedPath[..slash];
    }

    private static string GetLeafName(string normalizedPath)
    {
        int slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? normalizedPath : normalizedPath[(slash + 1)..];
    }

    private string GetNormalizedPath(object fileNode, string fileName) =>
        fileNode switch
        {
            FileContext fc => fc.NormalizedPath,
            FolderContext dc => dc.NormalizedPath,
            _ => PathIndex.Normalize(fileName),
        };

    private static string CombineNormalizedPath(string parentPath, string name) =>
        string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;

    private static string ToNotifyPath(string normalizedPath) =>
        "\\" + normalizedPath.Replace('/', '\\');

    private static PathIndexEntry CreateEntry(IStorableChild item, long? knownSize = null)
    {
        bool isFile = item is IFile;
        return new PathIndexEntry
        {
            Id = item.Id,
            Name = item.Name,
            IsFile = isFile,
            Size = isFile ? knownSize : null,
            CreatedAt = GetCreatedAt(item),
            LastModifiedAt = GetLastModifiedAt(item),
            LastAccessedAt = GetLastAccessedAt(item),
        };
    }

    private void MoveFolderRecursive(
        IChildFolder sourceFolder,
        IModifiableFolder sourceFolderMod,
        IModifiableFolder sourceParent,
        IModifiableFolder destinationParent,
        string newName)
    {
        IChildFolder destinationFolder = destinationParent
            .CreateFolderAsync(newName, false)
            .GetAwaiter().GetResult();

        if (destinationFolder is not IModifiableFolder destinationFolderMod)
            throw new NotSupportedException();

        foreach (IStorableChild child in sourceFolder.GetItemsAsync().ToBlockingEnumerable().ToList())
        {
            switch (child)
            {
                case IChildFile childFile:
                    ModifiableFolderExtensions
                        .MoveFromAsync(destinationFolderMod, childFile, sourceFolderMod, false, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;

                case IChildFolder childFolder when childFolder is IModifiableFolder childFolderMod:
                    MoveFolderRecursive(childFolder, childFolderMod, sourceFolderMod, destinationFolderMod, childFolder.Name);
                    break;
            }
        }

        sourceParent.DeleteAsync(sourceFolder).GetAwaiter().GetResult();
    }

    private static void ApplyBasicInfo(
        IStorable storable,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime)
    {
        DateTimeOffset? created = FromFileTime(creationTime);
        DateTimeOffset? accessed = FromFileTime(lastAccessTime);
        DateTimeOffset? modified = FromFileTime(lastWriteTime);

        if (storable is ICreatedAtOffset createdOffset &&
            createdOffset.CreatedAtOffset is IModifiableStorageProperty<DateTimeOffset?> createdOffsetProperty &&
            created is not null)
        {
            createdOffsetProperty.UpdateValueAsync(created, CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (storable is ICreatedAt createdAt &&
                 createdAt.CreatedAt is IModifiableStorageProperty<DateTime?> createdProperty &&
                 created is not null)
        {
            createdProperty.UpdateValueAsync(created.Value.UtcDateTime, CancellationToken.None).GetAwaiter().GetResult();
        }

        if (storable is ILastAccessedAtOffset accessedOffset &&
            accessedOffset.LastAccessedAtOffset is IModifiableStorageProperty<DateTimeOffset?> accessedOffsetProperty &&
            accessed is not null)
        {
            accessedOffsetProperty.UpdateValueAsync(accessed, CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (storable is ILastAccessedAt accessedAt &&
                 accessedAt.LastAccessedAt is IModifiableStorageProperty<DateTime?> accessedProperty &&
                 accessed is not null)
        {
            accessedProperty.UpdateValueAsync(accessed.Value.UtcDateTime, CancellationToken.None).GetAwaiter().GetResult();
        }

        if (storable is ILastModifiedAtOffset modifiedOffset &&
            modifiedOffset.LastModifiedAtOffset is IModifiableStorageProperty<DateTimeOffset?> modifiedOffsetProperty &&
            modified is not null)
        {
            modifiedOffsetProperty.UpdateValueAsync(modified, CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (storable is ILastModifiedAt modifiedAt &&
                 modifiedAt.LastModifiedAt is IModifiableStorageProperty<DateTime?> modifiedProperty &&
                 modified is not null)
        {
            modifiedProperty.UpdateValueAsync(modified.Value.UtcDateTime, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static DateTimeOffset? GetCreatedAt(IStorable item)
    {
        if (item is ICreatedAtOffset cao)
        {
            DateTimeOffset? value = cao.CreatedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (value is not null && value != DateTimeOffset.MinValue)
                return value;
        }

        if (item is ICreatedAt ca)
        {
            DateTime? value = ca.CreatedAt.GetValueAsync().GetAwaiter().GetResult();
            if (value is not null && value != DateTime.MinValue)
                return new DateTimeOffset(value.Value, TimeSpan.Zero);
        }

        return null;
    }

    private static DateTimeOffset? GetLastAccessedAt(IStorable item)
    {
        if (item is ILastAccessedAtOffset lao)
        {
            DateTimeOffset? value = lao.LastAccessedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (value is not null && value != DateTimeOffset.MinValue)
                return value;
        }

        if (item is ILastAccessedAt la)
        {
            DateTime? value = la.LastAccessedAt.GetValueAsync().GetAwaiter().GetResult();
            if (value is not null && value != DateTime.MinValue)
                return new DateTimeOffset(value.Value, TimeSpan.Zero);
        }

        return null;
    }

    private static DateTimeOffset? GetLastModifiedAt(IStorable item)
    {
        if (item is ILastModifiedAtOffset lmo)
        {
            DateTimeOffset? value = lmo.LastModifiedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (value is not null && value != DateTimeOffset.MinValue)
                return value;
        }

        if (item is ILastModifiedAt lm)
        {
            DateTime? value = lm.LastModifiedAt.GetValueAsync().GetAwaiter().GetResult();
            if (value is not null && value != DateTime.MinValue)
                return new DateTimeOffset(value.Value, TimeSpan.Zero);
        }

        return null;
    }

    private static ulong ToFileTime(DateTimeOffset? dto)
    {
        if (dto is null || dto == DateTimeOffset.MinValue || dto == DateTimeOffset.UnixEpoch)
            return 0;

        long ft = dto.Value.ToFileTime();
        return ft < 0 ? 0 : (ulong)ft;
    }

    private static bool WildcardMatch(string pattern, string name)
    {
        ReadOnlySpan<char> p = pattern.AsSpan();
        ReadOnlySpan<char> n = name.AsSpan();
        return WildcardMatchCore(p, n);
    }

    private static bool WildcardMatchCore(ReadOnlySpan<char> p, ReadOnlySpan<char> n)
    {
        while (true)
        {
            if (p.IsEmpty)
                return n.IsEmpty;

            switch (p[0])
            {
                case '*':
                    p = p[1..];
                    if (p.IsEmpty)
                        return true;
                    while (!n.IsEmpty)
                    {
                        if (WildcardMatchCore(p, n))
                            return true;
                        n = n[1..];
                    }
                    return false;

                case '?':
                    if (n.IsEmpty)
                        return false;
                    p = p[1..];
                    n = n[1..];
                    break;

                default:
                    if (n.IsEmpty || char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(n[0]))
                        return false;
                    p = p[1..];
                    n = n[1..];
                    break;
            }
        }
    }

    private static DateTimeOffset? FromFileTime(ulong fileTime)
    {
        if (fileTime == 0)
            return null;

        return DateTimeOffset.FromFileTime((long)fileTime);
    }

    private static bool IsRoot(string fileName) =>
        fileName == "\\" || fileName == "/";

    private sealed class WatchedFolder : IDisposable
    {
        private readonly IFolderWatcher _watcher;
        private readonly NotifyCollectionChangedEventHandler _handler;
        private bool _disposed;

        public WatchedFolder(
            IFolderWatcher watcher,
            string normalizedPath,
            Action<string, NotifyCollectionChangedEventArgs> onChanged)
        {
            _watcher = watcher;
            _handler = (_, args) => onChanged(normalizedPath, args);
            _watcher.CollectionChanged += _handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.CollectionChanged -= _handler;
            _watcher.Dispose();
        }
    }
}
