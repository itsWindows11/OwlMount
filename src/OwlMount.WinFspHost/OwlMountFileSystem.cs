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
/// <see cref="IFolder"/> hierarchy as a read-only Windows drive letter.
/// <para>
/// Write operations (Create, Write, Rename, Delete, SetBasicInfo, …) all return
/// <c>STATUS_ACCESS_DENIED</c> — this is a read-only MVP.
/// </para>
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

    // Windows file attribute constants (subset used here)
    private const uint AttrReadOnly  = 0x00000001u; // FILE_ATTRIBUTE_READONLY
    private const uint AttrDirectory = 0x00000010u; // FILE_ATTRIBUTE_DIRECTORY

    public OwlMountFileSystem(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null,
        string? volumeLabel = null)
    {
        _root          = root;
        _index         = new PathIndex();
        _dirCache      = new DirectoryCache();
        _blockCache    = blockCache;
        _rangeReaders  = rangeReaders;
        _sizeProviders = sizeProviders ?? new SizeProviderRegistry();
        _volumeLabel   = string.IsNullOrWhiteSpace(volumeLabel) ? "OwlMount" : volumeLabel;
    }

    // ── Volume info ───────────────────────────────────────────────────────────

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = 1UL * 1024 * 1024 * 1024; // nominal 1 GiB
        volumeInfo.FreeSize  = 0;
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
            fileAttributes = AttrDirectory | AttrReadOnly;
            return STATUS_SUCCESS;
        }

        string norm = PathIndex.Normalize(fileName);
        PathIndexEntry? entry = _index.TryGet(norm) ?? ResolveAndIndex(norm);
        if (entry is null) return STATUS_OBJECT_NAME_NOT_FOUND;

        fileAttributes = entry.IsFile ? AttrReadOnly : AttrDirectory | AttrReadOnly;
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
        fileNode      = null;
        fileDesc      = null;
        fileInfo      = default;
        normalizedName = null;

        if (IsRoot(fileName))
        {
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

            fileNode = new FileContext(file, entry);
            FillFileInfo(entry, ref fileInfo);
        }
        else
        {
            IFolder? folder = ResolveFolder(norm);
            if (folder is null) return STATUS_OBJECT_NAME_NOT_FOUND;
            fileNode = new FolderContext(folder, norm);
            FillFolderInfo(ref fileInfo, entry);
        }

        return STATUS_SUCCESS;
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileNode is FileContext fc) fc.Dispose();
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
            case FolderContext:
                FillFolderInfo(ref fileInfo);
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

        // On the very first call (context == null), skip past the marker so that
        // WinFsp can resume a partial enumeration correctly.
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
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[] securityDescriptor, ulong allocationSize,
        out object? fileNode, out object? fileDesc,
        out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null; fileDesc = null; fileInfo = default; normalizedName = null;
        return STATUS_ACCESS_DENIED;
    }

    public override int Overwrite(
        object fileNode, object fileDesc,
        uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int Write(
        object fileNode, object fileDesc,
        IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0; fileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int SetBasicInfo(
        object fileNode, object fileDesc,
        uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int SetFileSize(
        object fileNode, object fileDesc,
        ulong newSize, bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) =>
        STATUS_ACCESS_DENIED;

    public override int Rename(
        object fileNode, object fileDesc,
        string fileName, string newFileName, bool replaceIfExists) =>
        STATUS_ACCESS_DENIED;

    public override int SetSecurity(
        object fileNode, object fileDesc,
        System.Security.AccessControl.AccessControlSections sections,
        byte[] securityDescriptor) =>
        STATUS_ACCESS_DENIED;

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static bool IsRoot(string fileName) =>
        fileName == "\\" || fileName == "/";

    /// <summary>
    /// Returns a cached directory listing or enumerates the folder via the provider
    /// and populates both the cache and the path index.
    /// Items are sorted alphabetically for stable Explorer ordering.
    /// </summary>
    private IReadOnlyList<DirectoryEntry> GetOrPopulateDirectory(FolderContext ctx)
    {
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
            long size  = 0;
            DateTimeOffset? created  = GetCreatedAt(item);
            DateTimeOffset? modified = GetLastModifiedAt(item);

            string childPath = string.IsNullOrEmpty(ctx.NormalizedPath)
                ? item.Name
                : ctx.NormalizedPath + "/" + item.Name;

            _index.AddOrUpdate(childPath, new PathIndexEntry
            {
                Id           = item.Id,
                Name         = item.Name,
                IsFile       = !isDir,
                Size         = isDir ? null : (long?)null,
                CreatedAt    = created  == DateTimeOffset.MinValue ? null : created,
                LastModifiedAt = modified == DateTimeOffset.MinValue ? null : modified,
            });

            result.Add(new DirectoryEntry(
                item.Name,
                isDir,
                size,
                created  ?? DateTimeOffset.UnixEpoch,
                modified ?? DateTimeOffset.UnixEpoch));
        }

        _dirCache.Set(ctx.NormalizedPath, result);
        return result;
    }

    /// <summary>
    /// Resolves a normalized path and adds the leaf entry to the index.
    /// Returns <c>null</c> if any path segment is not found.
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
        DateTimeOffset? created  = GetCreatedAt(child);
        DateTimeOffset? modified = GetLastModifiedAt(child);

        var entry = new PathIndexEntry
        {
            Id             = child.Id,
            Name           = child.Name,
            IsFile         = isFile,
            CreatedAt      = created,
            LastModifiedAt = modified,
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
        catch
        {
            // Fallback: iterate manually
            return folder.GetItemsAsync()
                .ToBlockingEnumerable()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private IFile?   GetChildFile(IFolder folder, string name)   => GetChild(folder, name) as IFile;
    private IFolder? GetChildFolder(IFolder folder, string name) => GetChild(folder, name) as IFolder;

    // ── FileInfo helpers ──────────────────────────────────────────────────────

    private static void FillFileInfo(PathIndexEntry entry, ref FileInfo fi)
    {
        fi.FileAttributes  = AttrReadOnly;
        fi.AllocationSize  = (ulong)(entry.Size ?? 0);
        fi.FileSize        = (ulong)(entry.Size ?? 0);
        fi.CreationTime    = ToFileTime(entry.CreatedAt);
        fi.LastAccessTime  = ToFileTime(entry.LastAccessedAt ?? entry.CreatedAt);
        fi.LastWriteTime   = ToFileTime(entry.LastModifiedAt);
        fi.ChangeTime      = fi.LastWriteTime;
    }

    private static void FillFolderInfo(ref FileInfo fi, PathIndexEntry? entry = null)
    {
        fi.FileAttributes  = AttrDirectory | AttrReadOnly;
        fi.CreationTime    = entry is not null ? ToFileTime(entry.CreatedAt) : 0;
        fi.LastAccessTime  = 0;
        fi.LastWriteTime   = entry is not null ? ToFileTime(entry.LastModifiedAt) : 0;
        fi.ChangeTime      = fi.LastWriteTime;
    }

    private static void FillEntryInfo(DirectoryEntry e, ref FileInfo fi)
    {
        if (e.IsDirectory)
        {
            fi.FileAttributes = AttrDirectory | AttrReadOnly;
        }
        else
        {
            fi.FileAttributes = AttrReadOnly;
            fi.FileSize        = (ulong)e.Size;
            fi.AllocationSize  = (ulong)e.Size;
        }

        fi.CreationTime   = ToFileTime(e.CreatedAt == DateTimeOffset.UnixEpoch ? null : (DateTimeOffset?)e.CreatedAt);
        fi.LastWriteTime  = ToFileTime(e.LastModified == DateTimeOffset.UnixEpoch ? null : (DateTimeOffset?)e.LastModified);
        fi.LastAccessTime = fi.LastWriteTime;
        fi.ChangeTime     = fi.LastWriteTime;
    }

    // ── Timestamp helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the creation timestamp for <paramref name="item"/> as a
    /// <see cref="DateTimeOffset"/>, preferring <see cref="ICreatedAtOffset"/> (preserves
    /// timezone offset) over <see cref="ICreatedAt"/> (UTC <see cref="DateTime"/>).
    /// Returns <c>null</c> when neither interface is implemented or the value is unavailable.
    /// </summary>
    private static DateTimeOffset? GetCreatedAt(IStorable item)
    {
        if (item is ICreatedAtOffset cao)
        {
            DateTimeOffset? v = cao.CreatedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTimeOffset.MinValue)
                return v;
        }

        if (item is ICreatedAt ca)
        {
            DateTime? v = ca.CreatedAt.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTime.MinValue)
                return new DateTimeOffset(v.Value, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Returns the last-modified timestamp for <paramref name="item"/>, with the same
    /// <see cref="ILastModifiedAtOffset"/> → <see cref="ILastModifiedAt"/> fallback.
    /// </summary>
    private static DateTimeOffset? GetLastModifiedAt(IStorable item)
    {
        if (item is ILastModifiedAtOffset lmo)
        {
            DateTimeOffset? v = lmo.LastModifiedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTimeOffset.MinValue)
                return v;
        }

        if (item is ILastModifiedAt lm)
        {
            DateTime? v = lm.LastModifiedAt.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTime.MinValue)
                return new DateTimeOffset(v.Value, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Matches <paramref name="name"/> against a WinFsp wildcard <paramref name="pattern"/>
    /// using <c>?</c> (any single char) and <c>*</c> (any sequence of chars) semantics.
    /// Comparison is case-insensitive.
    /// </summary>
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

            if (p[0] == '*')
            {
                p = p[1..];
                if (p.IsEmpty) return true;
                // Try matching '*' against every suffix of n.
                for (int i = 0; i <= n.Length; i++)
                {
                    if (WildcardMatchCore(p, n[i..]))
                        return true;
                }
                return false;
            }

            if (n.IsEmpty)
                return false;

            if (p[0] != '?' && char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(n[0]))
                return false;

            p = p[1..];
            n = n[1..];
        }
    }

    private static ulong ToFileTime(DateTimeOffset? dto)
    {
        if (dto is null || dto == DateTimeOffset.MinValue || dto == DateTimeOffset.UnixEpoch)
            return 0;
        long ft = dto.Value.ToFileTime();
        return ft < 0 ? 0 : (ulong)ft;
    }
}
