using OwlCore.Storage;

namespace OwlMount.Core.Abstractions;

/// <summary>
/// Provides ranged read access to a file, enabling efficient partial reads without
/// downloading the entire file.
/// </summary>
public interface IRangeReader
{
    /// <summary>
    /// Reads up to <c>buffer.Length</c> bytes from <paramref name="file"/>
    /// starting at byte <paramref name="offset"/>.
    /// </summary>
    /// <returns>Number of bytes actually read; may be less than buffer size at EOF.</returns>
    Task<int> ReadAsync(IFile file, long offset, Memory<byte> buffer, CancellationToken ct = default);
}
