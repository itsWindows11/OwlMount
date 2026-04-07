using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.Core.Registry;

/// <summary>
/// Default <see cref="ISizeProvider"/> that opens the file stream and returns
/// <see cref="Stream.Length"/> when the stream is seekable.
/// Returns <c>null</c> for non-seekable streams (size unknown cheaply).
/// </summary>
public sealed class DefaultSizeProvider : ISizeProvider
{
    public async Task<long?> GetSizeAsync(IFile file, CancellationToken ct = default)
    {
        try
        {
            Stream stream = await file.OpenStreamAsync(FileAccess.Read, ct);
            await using (stream)
            {
                return stream.CanSeek ? stream.Length : null;
            }
        }
        catch
        {
            return null;
        }
    }
}
