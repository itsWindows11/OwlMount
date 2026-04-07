using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.Core.Registry;

/// <summary>
/// Default <see cref="IRangeReader"/> that opens the file stream and seeks to the
/// requested offset when the stream supports seeking.
/// <para>
/// For non-seekable streams, bytes from position 0 up to <c>offset</c> are discarded
/// (a warning in spirit — callers should prefer seekable streams or a registered
/// provider-specific reader where possible).
/// </para>
/// </summary>
public sealed class DefaultRangeReader : IRangeReader
{
    public async Task<int> ReadAsync(
        IFile file,
        long offset,
        Memory<byte> buffer,
        CancellationToken ct = default)
    {
        Stream stream = await file.OpenStreamAsync(FileAccess.Read, ct);
        await using (stream)
        {
            if (stream.CanSeek)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            else if (offset > 0)
            {
                await DiscardBytesAsync(stream, offset, ct);
            }

            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer[totalRead..], ct);
                if (read == 0) break; // EOF
                totalRead += read;
            }

            return totalRead;
        }
    }

    private static async Task DiscardBytesAsync(Stream stream, long count, CancellationToken ct)
    {
        byte[] discard = new byte[(int)Math.Min(81920, count)];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(discard.Length, remaining);
            int read = await stream.ReadAsync(discard.AsMemory(0, toRead), ct);
            if (read == 0) break;
            remaining -= read;
        }
    }
}
