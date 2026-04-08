using System.Collections.Concurrent;

namespace OwlMount.WinFspHost;

/// <summary>
/// A short-lived cache of directory listings keyed by normalized VFS path.
/// Entries expire after a configurable TTL to reduce repeated provider calls
/// while keeping the view reasonably fresh for Explorer browsing.
/// </summary>
public sealed class DirectoryCache
{
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public DirectoryCache(TimeSpan? ttl = null) =>
        _ttl = ttl ?? TimeSpan.FromSeconds(15);

    /// <summary>
    /// Returns cached entries for <paramref name="normalizedPath"/> if still valid,
    /// otherwise <c>null</c>.
    /// </summary>
    public IReadOnlyList<DirectoryEntry>? TryGet(string normalizedPath) =>
        _cache.TryGetValue(normalizedPath, out var e) && e.IsValid(_ttl) ? e.Items : null;

    /// <summary>Stores a directory listing for <paramref name="normalizedPath"/>.</summary>
    public void Set(string normalizedPath, IReadOnlyList<DirectoryEntry> items) =>
        _cache[normalizedPath] = new CacheEntry(items);

    /// <summary>Removes any cached listing for <paramref name="normalizedPath"/>.</summary>
    public void Invalidate(string normalizedPath) =>
        _cache.TryRemove(normalizedPath, out _);

    /// <summary>Removes cached listings for <paramref name="normalizedPath"/> and its descendants.</summary>
    public void InvalidateSubtree(string normalizedPath)
    {
        string prefix = string.IsNullOrEmpty(normalizedPath)
            ? string.Empty
            : normalizedPath + "/";

        foreach (string key in _cache.Keys)
        {
            if (key.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(prefix) && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private sealed class CacheEntry(IReadOnlyList<DirectoryEntry> items)
    {
        public IReadOnlyList<DirectoryEntry> Items { get; } = items;
        private readonly DateTime _created = DateTime.UtcNow;
        public bool IsValid(TimeSpan ttl) => DateTime.UtcNow - _created < ttl;
    }
}

/// <summary>A single entry in a cached directory listing.</summary>
public sealed record DirectoryEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessed,
    DateTimeOffset LastModified);
