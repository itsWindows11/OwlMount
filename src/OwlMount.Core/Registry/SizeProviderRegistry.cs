using OwlCore.Storage;
using OwlMount.Core.Abstractions;

namespace OwlMount.Core.Registry;

/// <summary>
/// Registry that maps <see cref="IFile"/> runtime types to their optimal
/// <see cref="ISizeProvider"/>. Registration is last-in-first-matched.
/// Falls back to <see cref="DefaultSizeProvider"/> when no specific provider is registered.
/// </summary>
public sealed class SizeProviderRegistry
{
    private readonly List<(Func<IFile, bool> Matcher, ISizeProvider Provider)> _providers = [];
    private readonly DefaultSizeProvider _default = new();

    /// <summary>
    /// Registers a custom size provider for files that satisfy <paramref name="matcher"/>.
    /// More recently registered providers take priority.
    /// </summary>
    public void Register(Func<IFile, bool> matcher, ISizeProvider provider) =>
        _providers.Insert(0, (matcher, provider));

    /// <summary>Returns the best available size provider for <paramref name="file"/>.</summary>
    public ISizeProvider GetProvider(IFile file)
    {
        foreach (var (matcher, provider) in _providers)
        {
            if (matcher(file)) return provider;
        }
        return _default;
    }
}
