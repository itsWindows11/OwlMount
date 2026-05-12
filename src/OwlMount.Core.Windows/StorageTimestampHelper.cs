using System.Runtime.Versioning;
using OwlCore.Storage;

namespace OwlMount.Core.Windows;

/// <summary>
/// Shared helpers for reading and writing timestamps on <see cref="IStorable"/> items.
/// Both the WinFsp and ProjFS backends use these so the logic stays in one place.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StorageTimestampHelper
{
    internal static DateTimeOffset? GetCreatedAt(IStorable item)
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

    internal static DateTimeOffset? GetLastModifiedAt(IStorable item)
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

    internal static DateTimeOffset? GetLastAccessedAt(IStorable item)
    {
        if (item is ILastAccessedAtOffset lao)
        {
            DateTimeOffset? v = lao.LastAccessedAtOffset.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTimeOffset.MinValue) return v;
        }
        if (item is ILastAccessedAt la)
        {
            DateTime? v = la.LastAccessedAt.GetValueAsync().GetAwaiter().GetResult();
            if (v is not null && v != DateTime.MinValue)
                return new DateTimeOffset(v.Value, TimeSpan.Zero);
        }
        return null;
    }

    /// <summary>
    /// Writes <paramref name="createdAt"/> and <paramref name="lastModifiedAt"/> back to
    /// <paramref name="storable"/> if it implements the corresponding optional interfaces.
    /// No-op when the storable does not support timestamp mutation.
    /// </summary>
    internal static void ApplyTimestamps(
        IStorable storable,
        DateTime? createdAt,
        DateTime? lastModifiedAt)
    {
        if (createdAt.HasValue)
        {
            DateTimeOffset created = new(createdAt.Value, TimeSpan.Zero);
            if (storable is ICreatedAtOffset cao &&
                cao.CreatedAtOffset is IModifiableStorageProperty<DateTimeOffset?> cap)
                cap.UpdateValueAsync(created, CancellationToken.None).GetAwaiter().GetResult();
            else if (storable is ICreatedAt ca &&
                     ca.CreatedAt is IModifiableStorageProperty<DateTime?> caP)
                caP.UpdateValueAsync(createdAt, CancellationToken.None).GetAwaiter().GetResult();
        }

        if (lastModifiedAt.HasValue)
        {
            DateTimeOffset modified = new(lastModifiedAt.Value, TimeSpan.Zero);
            if (storable is ILastModifiedAtOffset lmo &&
                lmo.LastModifiedAtOffset is IModifiableStorageProperty<DateTimeOffset?> lmop)
                lmop.UpdateValueAsync(modified, CancellationToken.None).GetAwaiter().GetResult();
            else if (storable is ILastModifiedAt lm &&
                     lm.LastModifiedAt is IModifiableStorageProperty<DateTime?> lmP)
                lmP.UpdateValueAsync(lastModifiedAt, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Overload that accepts Windows FILETIME values (100-nanosecond intervals since 1601-01-01).
    /// </summary>
    internal static void ApplyTimestamps(
        IStorable storable,
        ulong creationTimeFiletime,
        ulong lastAccessTimeFiletime,
        ulong lastWriteTimeFiletime)
    {
        DateTime? created  = creationTimeFiletime  > 0 ? DateTime.FromFileTimeUtc((long)creationTimeFiletime)  : null;
        DateTime? accessed = lastAccessTimeFiletime > 0 ? DateTime.FromFileTimeUtc((long)lastAccessTimeFiletime) : null;
        DateTime? modified = lastWriteTimeFiletime  > 0 ? DateTime.FromFileTimeUtc((long)lastWriteTimeFiletime)  : null;

        if (accessed.HasValue)
        {
            DateTimeOffset ao = new(accessed.Value, TimeSpan.Zero);
            if (storable is ILastAccessedAtOffset laoo &&
                laoo.LastAccessedAtOffset is IModifiableStorageProperty<DateTimeOffset?> laop)
                laop.UpdateValueAsync(ao, CancellationToken.None).GetAwaiter().GetResult();
            else if (storable is ILastAccessedAt la &&
                     la.LastAccessedAt is IModifiableStorageProperty<DateTime?> laP)
                laP.UpdateValueAsync(accessed, CancellationToken.None).GetAwaiter().GetResult();
        }

        ApplyTimestamps(storable, created, modified);
    }
}
