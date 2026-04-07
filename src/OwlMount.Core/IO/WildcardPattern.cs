namespace OwlMount.Core.IO;

/// <summary>
/// Case-insensitive wildcard matching using <c>*</c> (any sequence) and <c>?</c> (single char).
/// Used by the directory-enumeration filter in the ProjFS provider.
/// </summary>
public static class WildcardPattern
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> satisfies <paramref name="pattern"/>.
    /// Matching is case-insensitive. <c>*</c> matches any sequence of characters (including
    /// the empty sequence); <c>?</c> matches exactly one character.
    /// </summary>
    public static bool Match(string pattern, string name) =>
        MatchCore(pattern.AsSpan(), name.AsSpan());

    private static bool MatchCore(ReadOnlySpan<char> p, ReadOnlySpan<char> n)
    {
        while (true)
        {
            if (p.IsEmpty) return n.IsEmpty;

            if (p[0] == '*')
            {
                p = p[1..];
                if (p.IsEmpty) return true;
                for (int i = 0; i <= n.Length; i++)
                    if (MatchCore(p, n[i..])) return true;
                return false;
            }

            if (n.IsEmpty) return false;

            if (p[0] != '?' && char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(n[0]))
                return false;

            p = p[1..];
            n = n[1..];
        }
    }
}
