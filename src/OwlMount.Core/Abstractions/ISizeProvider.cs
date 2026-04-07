using OwlCore.Storage;

namespace OwlMount.Core.Abstractions;

/// <summary>
/// Provides file size information for a given <see cref="IFile"/>.
/// Returns <c>null</c> when the size cannot be determined cheaply.
/// </summary>
public interface ISizeProvider
{
    /// <summary>Returns the size of <paramref name="file"/> in bytes, or <c>null</c> if unknown.</summary>
    Task<long?> GetSizeAsync(IFile file, CancellationToken ct = default);
}
