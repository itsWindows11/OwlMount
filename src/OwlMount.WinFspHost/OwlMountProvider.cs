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

namespace OwlMount.WinFspHost;

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

    /// <summary>
    /// Set to the active <see cref="VirtualizationInstance"/> before
    /// <see cref="VirtualizationInstance.StartVirtualizing"/> is called so callbacks
    /// can write placeholder info and file data back to ProjFS.
    /// </summary>
    internal VirtualizationInstance? Instance { get; set; }

    /// <summary>Throws if <see cref="Instance"/> has not been set yet.</summary>
    private VirtualizationInstance RequireInstance() =>
        Instance ?? throw new InvalidOperationException(
            "VirtualizationInstance has not been set. Assign OwlMountProvider.Instance before starting virtualization.");

    private readonly ConcurrentDictionary<Guid, EnumerationState> _enumerations = new();

    public OwlMountProvider(
        IFolder root,
        BlockCache blockCache,
        RangeReaderRegistry rangeReaders,
        SizeProviderRegistry? sizeProviders = null)
    {
        _root         = root;
        _index        = new PathIndex();
        _blockCache   = blockCache;
        _rangeReaders = rangeReaders;
        _sizeProviders = sizeProviders ?? new SizeProviderRegistry();
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
                    ? FileAttributes.Directory | FileAttributes.ReadOnly
                    : FileAttributes.ReadOnly,
                entry.CreatedAt.DateTime,
                entry.LastModified.DateTime,
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

        return RequireInstance().WritePlaceholderInfo(
            relativePath:    relativePath,
            creationTime:    entry.CreatedAt?.DateTime ?? DateTime.MinValue,
            lastAccessTime:  (entry.LastModifiedAt ?? entry.CreatedAt)?.DateTime ?? DateTime.MinValue,
            lastWriteTime:   entry.LastModifiedAt?.DateTime ?? DateTime.MinValue,
            changeTime:      entry.LastModifiedAt?.DateTime ?? DateTime.MinValue,
            fileAttributes:  entry.IsFile
                ? FileAttributes.ReadOnly
                : FileAttributes.Directory | FileAttributes.ReadOnly,
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

    // ── Timestamp helpers ─────────────────────────────────────────────────────

    private static DateTimeOffset? GetCreatedAt(IStorable item)
    {
        if (item is ICreatedAtOffset cao)
        {
            DateTimeOffset? v = cao.CreatedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTimeOffset.MinValue) return v;
        }
        if (item is ICreatedAt ca)
        {
            DateTime? v = ca.CreatedAt.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTime.MinValue)
                return new DateTimeOffset(v.Value, TimeSpan.Zero);
        }
        return null;
    }

    private static DateTimeOffset? GetLastModifiedAt(IStorable item)
    {
        if (item is ILastModifiedAtOffset lmo)
        {
            DateTimeOffset? v = lmo.LastModifiedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTimeOffset.MinValue) return v;
        }
        if (item is ILastModifiedAt lm)
        {
            DateTime? v = lm.LastModifiedAt.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTime.MinValue)
                return new DateTimeOffset(v.Value, TimeSpan.Zero);
        }
        return null;
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
