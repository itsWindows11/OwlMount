using System.Collections.Concurrent;
using OwlMount.Core.Abstractions;

namespace OwlMount.Core.Index;

/// <summary>
/// In-memory index mapping normalized VFS paths to their <see cref="PathIndexEntry"/>.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class PathIndex
{
    private readonly ConcurrentDictionary<string, PathIndexEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or updates an entry at <paramref name="normalizedPath"/>.</summary>
    public void AddOrUpdate(string normalizedPath, PathIndexEntry entry) =>
        _entries[normalizedPath] = entry;

    /// <summary>Returns the entry at <paramref name="normalizedPath"/>, or <c>null</c>.</summary>
    public PathIndexEntry? TryGet(string normalizedPath) =>
        _entries.TryGetValue(normalizedPath, out var e) ? e : null;

    /// <summary>Removes the entry at <paramref name="normalizedPath"/>.</summary>
    public void Remove(string normalizedPath) =>
        _entries.TryRemove(normalizedPath, out _);

    /// <summary>
    /// Removes the entry at <paramref name="normalizedPath"/> and all entries
    /// whose paths begin with <c><paramref name="normalizedPath"/>/</c>.
    /// </summary>
    public void RemoveSubtree(string normalizedPath)
    {
        _entries.TryRemove(normalizedPath, out _);

        if (string.IsNullOrEmpty(normalizedPath)) return;

        string prefix = normalizedPath + "/";
        foreach (string key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _entries.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Normalizes a Windows-style path to a forward-slash-separated path with no
    /// leading or trailing slashes. Examples:
    /// <list type="bullet">
    ///   <item><c>\foo\bar.txt</c> → <c>foo/bar.txt</c></item>
    ///   <item><c>\</c> → <c></c> (root)</item>
    ///   <item><c>foo/bar</c> → <c>foo/bar</c></item>
    /// </list>
    /// </summary>
    public static string Normalize(string path) =>
        path.Replace('\\', '/').Trim('/');
}
