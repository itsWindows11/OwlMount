using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.Core.Registry;

/// <summary>
/// Registry that maps <see cref="IFile"/> runtime types to their optimal
/// <see cref="IRangeReader"/>. Registration is last-in-first-matched.
/// Falls back to <see cref="DefaultRangeReader"/> when no specific reader is registered.
/// </summary>
public sealed class RangeReaderRegistry
{
    private readonly List<(Func<IFile, bool> Matcher, IRangeReader Reader)> _readers = [];
    private readonly DefaultRangeReader _default = new();

    /// <summary>
    /// Registers a custom reader for files that satisfy <paramref name="matcher"/>.
    /// More recently registered readers take priority.
    /// </summary>
    public void Register(Func<IFile, bool> matcher, IRangeReader reader) =>
        _readers.Insert(0, (matcher, reader));

    /// <summary>Returns the best available reader for <paramref name="file"/>.</summary>
    public IRangeReader GetReader(IFile file)
    {
        foreach (var (matcher, reader) in _readers)
        {
            if (matcher(file)) return reader;
        }
        return _default;
    }
}
