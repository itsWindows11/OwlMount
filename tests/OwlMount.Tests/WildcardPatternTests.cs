using OwlMount.Core.IO;

namespace OwlMount.Tests;

/// <summary>
/// Unit tests for <see cref="WildcardPattern.Match"/>.
/// Covers the subset of Windows wildcard semantics used by the ProjFS directory-enumeration filter.
/// </summary>
public sealed class WildcardPatternTests
{
    // ── Exact-match patterns ──────────────────────────────────────────────────

    [Fact]
    public void Exact_SameName_Matches()
        => Assert.True(WildcardPattern.Match("readme.txt", "readme.txt"));

    [Fact]
    public void Exact_DifferentName_DoesNotMatch()
        => Assert.False(WildcardPattern.Match("readme.txt", "other.txt"));

    [Fact]
    public void Exact_CaseInsensitive_Matches()
        => Assert.True(WildcardPattern.Match("README.TXT", "readme.txt"));

    [Fact]
    public void Exact_EmptyPatternAndName_Matches()
        => Assert.True(WildcardPattern.Match("", ""));

    [Fact]
    public void Exact_EmptyPattern_NonEmptyName_DoesNotMatch()
        => Assert.False(WildcardPattern.Match("", "file.txt"));

    // ── Star wildcard ─────────────────────────────────────────────────────────

    [Fact]
    public void Star_MatchesAnyName()
        => Assert.True(WildcardPattern.Match("*", "anything.txt"));

    [Fact]
    public void Star_MatchesEmptyName()
        => Assert.True(WildcardPattern.Match("*", ""));

    [Fact]
    public void StarDotTxt_MatchesTxtFiles()
        => Assert.True(WildcardPattern.Match("*.txt", "readme.txt"));

    [Fact]
    public void StarDotTxt_DoesNotMatchNonTxtFiles()
        => Assert.False(WildcardPattern.Match("*.txt", "readme.cs"));

    [Fact]
    public void StarDotTxt_DoesNotMatchBareExtension()
        => Assert.False(WildcardPattern.Match("*.txt", ".cs"));

    [Fact]
    public void StarDotTxt_MatchesSingleCharPrefix()
        => Assert.True(WildcardPattern.Match("*.txt", "a.txt"));

    [Fact]
    public void StarInMiddle_MatchesAnyMiddleSegment()
        => Assert.True(WildcardPattern.Match("f*e.txt", "file.txt"));

    [Fact]
    public void StarInMiddle_MatchesLongerMiddleSegment()
        => Assert.True(WildcardPattern.Match("f*e.txt", "frame.txt"));

    [Fact]
    public void StarInMiddle_MatchesZeroMiddleChars()
        => Assert.True(WildcardPattern.Match("fi*le.txt", "file.txt"));

    [Fact]
    public void StarInMiddle_DoesNotMatchWrongExtension()
        => Assert.False(WildcardPattern.Match("f*e.txt", "file.cs"));

    [Fact]
    public void MultipleStars_MatchesCorrectly()
        => Assert.True(WildcardPattern.Match("*.*", "archive.tar.gz"));

    [Fact]
    public void MultipleStars_DoesNotMatchNameWithoutDot()
        => Assert.False(WildcardPattern.Match("*.*", "nodot"));

    [Fact]
    public void StarAtStart_MatchesAnySuffix()
        => Assert.True(WildcardPattern.Match("*suffix", "longsuffix"));

    [Fact]
    public void StarAtEnd_MatchesAnyPrefix()
        => Assert.True(WildcardPattern.Match("prefix*", "prefixLongTail"));

    // ── Question-mark wildcard ────────────────────────────────────────────────

    [Fact]
    public void QuestionMark_MatchesSingleChar()
        => Assert.True(WildcardPattern.Match("?.txt", "a.txt"));

    [Fact]
    public void QuestionMark_DoesNotMatchTwoChars()
        => Assert.False(WildcardPattern.Match("?.txt", "ab.txt"));

    [Fact]
    public void QuestionMark_DoesNotMatchZeroChars()
        => Assert.False(WildcardPattern.Match("?.txt", ".txt"));

    [Fact]
    public void QuestionMark_CaseInsensitive_MatchesUpperOrLower()
    {
        Assert.True(WildcardPattern.Match("file.?xt", "file.txt"));
        Assert.True(WildcardPattern.Match("file.?xt", "file.Txt"));
    }

    [Fact]
    public void MultipleQuestionMarks_MatchExactCount()
    {
        Assert.True(WildcardPattern.Match("???.txt",  "abc.txt"));
        Assert.False(WildcardPattern.Match("???.txt", "ab.txt"));
        Assert.False(WildcardPattern.Match("???.txt", "abcd.txt"));
    }

    // ── Mixed star and question-mark ──────────────────────────────────────────

    [Fact]
    public void Mixed_StarAndQuestion_MatchesCorrectly()
        => Assert.True(WildcardPattern.Match("f?le*.txt", "file_big.txt"));

    [Fact]
    public void Mixed_StarAndQuestion_DoesNotMatchIfQuestionFails()
        => Assert.False(WildcardPattern.Match("f?le*.txt", "fle_big.txt"));

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void PatternLongerThanName_DoesNotMatch()
        => Assert.False(WildcardPattern.Match("averylongpattern", "short"));

    [Fact]
    public void Name_Empty_PatternNonEmpty_DoesNotMatch()
        => Assert.False(WildcardPattern.Match("a", ""));

    [Fact]
    public void AllQuestionMarks_MatchExactLength()
    {
        Assert.True(WildcardPattern.Match("????", "abcd"));
        Assert.False(WildcardPattern.Match("????", "abc"));
        Assert.False(WildcardPattern.Match("????", "abcde"));
    }

    [Fact]
    public void StarOnly_MatchesAnyLength()
    {
        Assert.True(WildcardPattern.Match("*", ""));
        Assert.True(WildcardPattern.Match("*", "a"));
        Assert.True(WildcardPattern.Match("*", "abcdefghij"));
    }

    // ── Theory: representative table ─────────────────────────────────────────

    [Theory]
    [InlineData("*",         "file.txt",    true)]
    [InlineData("*",         "",            true)]
    [InlineData("*.txt",     "hello.txt",   true)]
    [InlineData("*.txt",     "hello.cs",    false)]
    [InlineData("?.txt",     "a.txt",       true)]
    [InlineData("?.txt",     "ab.txt",      false)]
    [InlineData("FILE.TXT",  "file.txt",    true)]
    [InlineData("file.txt",  "FILE.TXT",    true)]
    [InlineData("",          "",            true)]
    [InlineData("",          "nonempty",    false)]
    [InlineData("exact",     "exact",       true)]
    [InlineData("exact",     "Exact",       true)]
    [InlineData("exact",     "different",   false)]
    [InlineData("a*b",       "ab",          true)]
    [InlineData("a*b",       "acb",         true)]
    [InlineData("a*b",       "aXYZb",       true)]
    [InlineData("a*b",       "ac",          false)]
    [InlineData("*.*",       "foo.bar",     true)]
    [InlineData("*.*",       "nodot",       false)]
    public void Theory_PatternMatchesExpected(string pattern, string name, bool expected)
        => Assert.Equal(expected, WildcardPattern.Match(pattern, name));
}
