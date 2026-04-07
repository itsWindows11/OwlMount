namespace OwlMount.Core.Abstractions;

/// <summary>
/// An in-memory record describing a storable item tracked by the <see cref="Index.PathIndex"/>.
/// </summary>
public sealed class PathIndexEntry
{
    /// <summary>The provider-assigned unique identifier (e.g. full path for SystemFile).</summary>
    public required string Id { get; init; }

    /// <summary>Display name used in directory listings.</summary>
    public required string Name { get; init; }

    /// <summary><c>true</c> for files, <c>false</c> for folders.</summary>
    public required bool IsFile { get; init; }

    /// <summary>Cached file size in bytes; <c>null</c> if not yet known.</summary>
    public long? Size { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastModifiedAt { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
}
