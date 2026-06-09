using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Runtime.Versioning;
using DokanNet;
using DokanNet.Logging;
using OwlCore.Storage;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;

namespace OwlMount.Core.Windows.Backends;

/// <summary>
/// <see cref="IOwlMountBackend"/> implementation that uses Dokany via Dokan.NET.
/// Supports read-only and read-write modes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DokanyBackend : IOwlMountBackend
{
    private readonly DokanyOperations _operations;
    private DokanInstance? _instance;
    private bool _stopping;

    /// <inheritdoc/>
    public string Name => "Dokany";

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    /// <inheritdoc/>
    public event EventHandler? Stopped;

    public DokanyBackend(
        IFolder root,
        BlockCache? blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        bool readOnly = false,
        ulong? totalSize = null,
        ulong? freeSize = null,
        string? volumeLabel = null)
    {
        IsReadOnly = readOnly || root is not IModifiableFolder;
        _operations = new DokanyOperations(
            root,
            blockCache,
            rangeReaders,
            sizeProviders,
            IsReadOnly,
            totalSize,
            freeSize,
            volumeLabel);
    }

    /// <summary>
    /// Returns <c>true</c> when Dokany appears to be installed on this machine.
    /// </summary>
    public static bool IsAvailable()
    {
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (File.Exists(Path.Combine(sys32, "dokan2.dll")) || File.Exists(Path.Combine(sys32, "dokan1.dll")))
            return true;

        static IEnumerable<string> EnumerateDokanInstallRoots(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) yield break;
            string dokanRoot = Path.Combine(basePath, "Dokan");
            if (!Directory.Exists(dokanRoot)) yield break;
            foreach (string dir in Directory.EnumerateDirectories(
                         dokanRoot, "Dokan Library-*", SearchOption.TopDirectoryOnly))
            {
                yield return dir;
            }
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return EnumerateDokanInstallRoots(pf)
            .Concat(EnumerateDokanInstallRoots(pf86))
            .SelectMany(d => new[]
            {
                Path.Combine(d, "dokan2.dll"),
                Path.Combine(d, "dokan1.dll"),
                Path.Combine(d, "dokanctl.exe"),
            })
            .Any(File.Exists);
    }

    /// <inheritdoc/>
    public bool Start(string mountPoint, string volumeLabel)
    {
        try
        {
            _instance = new DokanInstanceBuilder(new Dokan(new NullLogger()))
                .ConfigureOptions(options =>
                {
                    options.MountPoint = mountPoint;
                    options.Options = DokanOptions.FixedDrive | DokanOptions.MountManager |
                                      (IsReadOnly ? DokanOptions.WriteProtection : 0);
                    options.SectorSize = 512;
                    options.AllocationUnitSize = 512;
                    options.TimeOut = TimeSpan.FromSeconds(30);
                })
                .Build(_operations);
        }
        catch (DllNotFoundException)
        {
            PrintNotInstalledError();
            return false;
        }
        catch (DokanException ex)
        {
            Console.Error.WriteLine($"Failed to mount at {mountPoint} (Dokany status: {ex.ErrorStatus}).");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Ensure Dokany is installed and the drive letter is not already in use.");
            Console.Error.WriteLine("  Dokany installer: https://github.com/dokan-dev/dokany/releases");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to mount at {mountPoint} with Dokany: {ex.Message}");
            return false;
        }

        if (_instance is null || !_instance.IsFileSystemRunning())
        {
            Console.Error.WriteLine($"Failed to mount at {mountPoint} using Dokany.");
            Console.Error.WriteLine("Ensure Dokany is installed and the drive letter is not already in use.");
            Console.Error.WriteLine("  Dokany installer: https://github.com/dokan-dev/dokany/releases");
            return false;
        }

        _stopping = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await _instance.WaitForFileSystemClosedAsync(uint.MaxValue).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            if (!_stopping)
                Stopped?.Invoke(this, EventArgs.Empty);
        });

        return true;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _stopping = true;
        _instance?.Dispose();
        _instance = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    private static void PrintNotInstalledError()
    {
        Console.Error.WriteLine("Error: Dokany is not installed or the Dokany driver service is not running.");
        Console.Error.WriteLine("  Download Dokany: https://github.com/dokan-dev/dokany/releases");
        Console.Error.WriteLine("  After installing, restart this application.");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DokanyOperations : IDokanOperations
{
    private readonly IFolder _root;
    private readonly BlockCache? _blockCache;
    private readonly RangeReaderRegistry _rangeReaders;
    private readonly SizeProviderRegistry _sizeProviders;
    private readonly bool _isReadOnly;
    private readonly long _totalSize;
    private readonly long _freeSize;
    private readonly string _volumeLabel;
    private readonly ConcurrentDictionary<string, IFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    public DokanyOperations(
        IFolder root,
        BlockCache? blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders,
        bool isReadOnly,
        ulong? totalSize,
        ulong? freeSize,
        string? volumeLabel)
    {
        _root = root;
        _blockCache = blockCache;
        _rangeReaders = rangeReaders;
        _sizeProviders = sizeProviders ?? new SizeProviderRegistry();
        _isReadOnly = isReadOnly;
        _totalSize = (long)Math.Min(totalSize ?? (ulong)(512L * 1024 * 1024 * 1024), long.MaxValue);
        _freeSize = (long)Math.Min(freeSize ?? (ulong)(256L * 1024 * 1024 * 1024), long.MaxValue);
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "OwlMount" : volumeLabel.Trim();
        _folderCache.TryAdd(string.Empty, _root);
    }

    public NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        try
        {
            string path = NormalizePath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                info.IsDirectory = true;
                info.Context = _root;
                return NtStatus.Success;
            }

            var existing = ResolveItem(path);
            bool wantsDirectory = info.IsDirectory || attributes.HasFlag(FileAttributes.Directory);

            if (existing is not null)
            {
                if (wantsDirectory && existing is not IFolder)
                    return NtStatus.ObjectNameCollision;
                if (!wantsDirectory && existing is IFolder)
                    return NtStatus.ObjectNameCollision;

                if (mode == FileMode.CreateNew)
                    return NtStatus.ObjectNameCollision;

                if (_isReadOnly && mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate)
                    return NtStatus.AccessDenied;

                if (mode == FileMode.Truncate && existing is IFile truncateFile)
                {
                    using Stream stream = OpenWriteStream(truncateFile);
                    stream.SetLength(0);
                    _blockCache?.Invalidate(truncateFile.Id);
                }

                info.IsDirectory = existing is IFolder;
                info.Context = existing;
                return NtStatus.Success;
            }

            if (mode is FileMode.Open or FileMode.Truncate)
                return NtStatus.ObjectNameNotFound;

            if (_isReadOnly)
                return NtStatus.AccessDenied;

            string parentPath = GetParentPath(path);
            string leaf = GetLeafName(path);
            if (ResolveFolderOrRoot(parentPath) is not IModifiableFolder parent)
                return NtStatus.AccessDenied;

            IStorableChild created = wantsDirectory
                ? parent.CreateFolderAsync(leaf, overwrite: false).GetAwaiter().GetResult()
                : parent.CreateFileAsync(leaf, overwrite: false).GetAwaiter().GetResult();

            InvalidatePath(parentPath);
            info.IsDirectory = created is IFolder;
            info.Context = created;
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public void Cleanup(string fileName, IDokanFileInfo info) { }

    public void CloseFile(string fileName, IDokanFileInfo info) => info.Context = null;

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        try
        {
            IFile? file = (info.Context as IFile) ?? ResolveFile(NormalizePath(fileName));
            if (file is null)
                return NtStatus.ObjectNameNotFound;

            if (_blockCache is not null)
            {
                bytesRead = _blockCache.ReadAsync(
                    file,
                    _rangeReaders.GetReader(file),
                    offset,
                    buffer.AsMemory()).GetAwaiter().GetResult();
                return NtStatus.Success;
            }

            using Stream stream = file.OpenStreamAsync(System.IO.FileAccess.Read).GetAwaiter().GetResult();
            if (stream.CanSeek)
                stream.Seek(offset, SeekOrigin.Begin);
            else if (offset > 0)
                return NtStatus.NotImplemented;

            bytesRead = stream.Read(buffer, 0, buffer.Length);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (_isReadOnly) return NtStatus.AccessDenied;

        try
        {
            string path = NormalizePath(fileName);
            IFile? file = (info.Context as IFile) ?? ResolveFile(path);
            if (file is null)
            {
                string parentPath = GetParentPath(path);
                string leaf = GetLeafName(path);
                if (ResolveFolderOrRoot(parentPath) is not IModifiableFolder parent)
                    return NtStatus.AccessDenied;
                file = parent.CreateFileAsync(leaf, overwrite: false).GetAwaiter().GetResult();
                info.Context = file;
                InvalidatePath(parentPath);
            }

            using Stream stream = OpenWriteStream(file);
            stream.Seek(info.WriteToEndOfFile ? 0 : offset, info.WriteToEndOfFile ? SeekOrigin.End : SeekOrigin.Begin);
            stream.Write(buffer, 0, buffer.Length);
            stream.Flush();
            bytesWritten = buffer.Length;
            _blockCache?.Invalidate(file.Id);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        fileInfo = new FileInformation();
        try
        {
            string path = NormalizePath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                fileInfo = BuildDirectoryInfo(string.Empty, null);
                return NtStatus.Success;
            }

            IStorableChild? item = info.Context as IStorableChild ?? ResolveItem(path);
            if (item is null) return NtStatus.ObjectNameNotFound;

            fileInfo = item switch
            {
                IFolder => BuildDirectoryInfo(item.Name, item),
                IFile f => BuildFileInfo(item.Name, f),
                _ => fileInfo,
            };
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = [];
        try
        {
            IFolder? folder = (info.Context as IFolder) ?? ResolveFolderOrRoot(NormalizePath(fileName));
            if (folder is null) return NtStatus.ObjectPathNotFound;

            List<FileInformation> result = [];
            foreach (IStorableChild child in folder.GetItemsAsync().ToBlockingEnumerable())
            {
                switch (child)
                {
                    case IFolder:
                        result.Add(BuildDirectoryInfo(child.Name, child));
                        break;
                    case IFile file:
                        result.Add(BuildFileInfo(child.Name, file));
                        break;
                }
            }

            files = result;
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        NtStatus status = FindFiles(fileName, out files, info);
        if (status != NtStatus.Success) return status;

        files = files.Where(f =>
            DokanHelper.DokanIsNameInExpression(searchPattern, f.FileName, ignoreCase: true)).ToList();
        return NtStatus.Success;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) =>
        _isReadOnly ? NtStatus.AccessDenied : NtStatus.Success;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        if (_isReadOnly) return NtStatus.AccessDenied;

        try
        {
            string path = NormalizePath(fileName);
            IStorableChild? item = info.Context as IStorableChild ?? ResolveItem(path);
            if (item is null) return NtStatus.ObjectNameNotFound;

            if (lastAccessTime.HasValue && item is ILastAccessedAtOffset accessedOffset &&
                accessedOffset.LastAccessedAtOffset is IModifiableStorageProperty<DateTimeOffset?> accessedProp)
            {
                accessedProp.UpdateValueAsync(new DateTimeOffset(lastAccessTime.Value, TimeSpan.Zero), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            else if (lastAccessTime.HasValue && item is ILastAccessedAt accessed &&
                     accessed.LastAccessedAt is IModifiableStorageProperty<DateTime?> accessedProp2)
            {
                accessedProp2.UpdateValueAsync(lastAccessTime, CancellationToken.None).GetAwaiter().GetResult();
            }

            StorageTimestampHelper.ApplyTimestamps(item, creationTime, lastWriteTime);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        if (_isReadOnly) return NtStatus.AccessDenied;

        try
        {
            string path = NormalizePath(fileName);
            string parentPath = GetParentPath(path);
            IFolder? parent = ResolveFolderOrRoot(parentPath);
            IStorableChild? item = (info.Context as IStorableChild) ?? ResolveItem(path);
            if (parent is not IModifiableFolder modParent || item is null || item is IFolder)
                return NtStatus.ObjectNameNotFound;

            modParent.DeleteAsync(item).GetAwaiter().GetResult();
            _blockCache?.Invalidate(item.Id);
            InvalidatePath(path);
            InvalidatePath(parentPath);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        if (_isReadOnly) return NtStatus.AccessDenied;

        try
        {
            string path = NormalizePath(fileName);
            if (string.IsNullOrEmpty(path)) return NtStatus.AccessDenied;

            string parentPath = GetParentPath(path);
            IFolder? parent = ResolveFolderOrRoot(parentPath);
            IStorableChild? item = (info.Context as IStorableChild) ?? ResolveItem(path);
            if (parent is not IModifiableFolder modParent || item is not IFolder folder)
                return NtStatus.ObjectNameNotFound;

            if (FolderHasAnyChildren(folder))
                return NtStatus.DirectoryNotEmpty;

            modParent.DeleteAsync(item).GetAwaiter().GetResult();
            InvalidatePath(path);
            InvalidatePath(parentPath);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        if (_isReadOnly) return NtStatus.AccessDenied;

        try
        {
            string oldPath = NormalizePath(oldName);
            string newPath = NormalizePath(newName);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return NtStatus.Success;

            string oldParentPath = GetParentPath(oldPath);
            string newParentPath = GetParentPath(newPath);
            string newLeaf = GetLeafName(newPath);

            IStorableChild? sourceItem = ResolveItem(oldPath);
            if (sourceItem is null) return NtStatus.ObjectNameNotFound;

            if (ResolveFolderOrRoot(oldParentPath) is not IModifiableFolder sourceParent ||
                ResolveFolderOrRoot(newParentPath) is not IModifiableFolder destinationParent)
                return NtStatus.AccessDenied;

            IStorableChild? existingDestination = ResolveItem(newPath);
            if (existingDestination is not null)
            {
                if (!replace) return NtStatus.ObjectNameCollision;
                destinationParent.DeleteAsync(existingDestination).GetAwaiter().GetResult();
            }

            if (sourceItem is IChildFile sourceFile)
            {
                destinationParent.MoveFromAsync(sourceFile, sourceParent, overwrite: false, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            else if (sourceItem is IChildFolder sourceFolder && sourceFolder is IModifiableFolder sourceFolderMod)
            {
                MoveFolderRecursive(sourceFolder, sourceFolderMod, sourceParent, destinationParent, newLeaf);
            }
            else
            {
                return NtStatus.NotImplemented;
            }

            _blockCache?.Invalidate(sourceItem.Id);
            InvalidatePath(oldPath);
            InvalidatePath(newPath);
            InvalidatePath(oldParentPath);
            InvalidatePath(newParentPath);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (_isReadOnly) return NtStatus.AccessDenied;

        try
        {
            IFile? file = (info.Context as IFile) ?? ResolveFile(NormalizePath(fileName));
            if (file is null) return NtStatus.ObjectNameNotFound;
            using Stream stream = OpenWriteStream(file);
            stream.SetLength(length);
            _blockCache?.Invalidate(file.Id);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return DokanHelper.ToNtStatus(ex);
        }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) =>
        SetEndOfFile(fileName, length, info);

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = _totalSize;
        freeBytesAvailable = Math.Min(_freeSize, _totalSize);
        totalNumberOfFreeBytes = freeBytesAvailable;
        return NtStatus.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = _volumeLabel;
        fileSystemName = "DOKAN";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames
                 | FileSystemFeatures.UnicodeOnDisk
                 | FileSystemFeatures.PersistentAcls;
        if (_isReadOnly)
            features |= FileSystemFeatures.ReadOnlyVolume;
        return NtStatus.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = new FileSecurity();
        return NtStatus.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info) =>
        _isReadOnly ? NtStatus.AccessDenied : NtStatus.NotImplemented;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus Unmounted(IDokanFileInfo info) => NtStatus.Success;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = [];
        return NtStatus.NotImplemented;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

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

    private IStorableChild? ResolveItem(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        string parentPath = GetParentPath(normalizedPath);
        IFolder? parent = ResolveFolderOrRoot(parentPath);
        return parent is null ? null : GetChild(parent, GetLeafName(normalizedPath));
    }

    private IFile? ResolveFile(string normalizedPath) => ResolveItem(normalizedPath) as IFile;

    private IFolder? ResolveFolderOrRoot(string normalizedPath) =>
        string.IsNullOrEmpty(normalizedPath) ? _root : ResolveFolder(normalizedPath);

    private IFolder? ResolveFolder(string normalizedPath)
    {
        if (_folderCache.TryGetValue(normalizedPath, out IFolder? existing))
            return existing;

        string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IFolder current = _root;
        string currentPath = string.Empty;
        foreach (string seg in segments)
        {
            IFolder? sub = GetChildFolder(current, seg);
            if (sub is null) return null;
            current = sub;
            currentPath = string.IsNullOrEmpty(currentPath) ? seg : $"{currentPath}/{seg}";
            _folderCache.TryAdd(currentPath, current);
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
        catch
        {
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("autorun.inf", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return folder.GetItemsAsync()
                .ToBlockingEnumerable()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private IFolder? GetChildFolder(IFolder folder, string name) => GetChild(folder, name) as IFolder;

    private FileInformation BuildFileInfo(string fileName, IFile file)
    {
        long length = _sizeProviders.GetProvider(file).GetSizeAsync(file).GetAwaiter().GetResult() ?? 0L;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new FileInformation
        {
            FileName = fileName,
            Attributes = _isReadOnly ? FileAttributes.ReadOnly | FileAttributes.Archive : FileAttributes.Archive,
            CreationTime = StorageTimestampHelper.GetCreatedAt(file)?.UtcDateTime ?? now.UtcDateTime,
            LastAccessTime = StorageTimestampHelper.GetLastAccessedAt(file)?.UtcDateTime ?? now.UtcDateTime,
            LastWriteTime = StorageTimestampHelper.GetLastModifiedAt(file)?.UtcDateTime ?? now.UtcDateTime,
            Length = Math.Max(length, 0),
        };
    }

    private FileInformation BuildDirectoryInfo(string fileName, IStorable? folder)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new FileInformation
        {
            FileName = fileName,
            Attributes = _isReadOnly ? FileAttributes.Directory | FileAttributes.ReadOnly : FileAttributes.Directory,
            CreationTime = folder is not null ? StorageTimestampHelper.GetCreatedAt(folder)?.UtcDateTime : now.UtcDateTime,
            LastAccessTime = folder is not null ? StorageTimestampHelper.GetLastAccessedAt(folder)?.UtcDateTime : now.UtcDateTime,
            LastWriteTime = folder is not null ? StorageTimestampHelper.GetLastModifiedAt(folder)?.UtcDateTime : now.UtcDateTime,
            Length = 0,
        };
    }

    private static Stream OpenWriteStream(IFile file)
    {
        try
        {
            return file.OpenStreamAsync(System.IO.FileAccess.ReadWrite).GetAwaiter().GetResult();
        }
        catch
        {
            return file.OpenStreamAsync(System.IO.FileAccess.Write).GetAwaiter().GetResult();
        }
    }

    private static bool FolderHasAnyChildren(IFolder folder)
    {
        IAsyncEnumerator<IStorableChild> e = folder.GetItemsAsync().GetAsyncEnumerator();
        try
        {
            return e.MoveNextAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        finally
        {
            e.DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }

    private void InvalidatePath(string normalizedPath)
    {
        _folderCache.TryRemove(normalizedPath, out _);
        if (string.IsNullOrEmpty(normalizedPath)) return;
        string prefix = normalizedPath + "/";
        foreach (string key in _folderCache.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _folderCache.TryRemove(key, out _);
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
                    destinationFolderMod.MoveFromAsync(childFile, sourceFolderMod, false, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;
                case IChildFolder childFolder when childFolder is IModifiableFolder childFolderMod:
                    MoveFolderRecursive(childFolder, childFolderMod, sourceFolderMod, destinationFolderMod, childFolder.Name);
                    break;
            }
        }

        sourceParent.DeleteAsync(sourceFolder).GetAwaiter().GetResult();
    }
}
