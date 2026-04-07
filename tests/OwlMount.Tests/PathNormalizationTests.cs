using OwlMount.Core.Index;

namespace OwlMount.Tests;

public sealed class PathNormalizationTests
{
    [Theory]
    [InlineData(@"\foo\bar.txt",   "foo/bar.txt")]
    [InlineData(@"\foo\bar\",      "foo/bar")]
    [InlineData(@"\",              "")]
    [InlineData("",                "")]
    [InlineData("foo/bar",         "foo/bar")]
    [InlineData(@"foo\bar",        "foo/bar")]
    [InlineData(@"\foo\bar\baz",   "foo/bar/baz")]
    [InlineData(@"\\server\share", "server/share")]
    public void Normalize_ConvertsToForwardSlashWithNoLeadingOrTrailingSlash(
        string input, string expected)
    {
        Assert.Equal(expected, PathIndex.Normalize(input));
    }

    [Fact]
    public void Normalize_RootBackslash_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PathIndex.Normalize("\\"));
    }

    [Fact]
    public void Normalize_RootForwardSlash_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PathIndex.Normalize("/"));
    }

    [Fact]
    public void Normalize_AlreadyNormalizedPath_IsIdempotent()
    {
        const string path = "some/deep/path/file.txt";
        Assert.Equal(path, PathIndex.Normalize(path));
    }

    [Fact]
    public void PathIndex_AddAndRetrieve_WorksCaseInsensitively()
    {
        var index = new PathIndex();
        var entry = new OwlMount.Core.Abstractions.PathIndexEntry
        {
            Id     = "id1",
            Name   = "file.txt",
            IsFile = true,
        };

        index.AddOrUpdate("foo/bar/file.txt", entry);

        Assert.Same(entry, index.TryGet("foo/bar/file.txt"));
        Assert.Same(entry, index.TryGet("FOO/BAR/FILE.TXT")); // case-insensitive
    }

    [Fact]
    public void PathIndex_Remove_DeletesEntry()
    {
        var index = new PathIndex();
        var entry = new OwlMount.Core.Abstractions.PathIndexEntry
        {
            Id     = "id2",
            Name   = "doc.pdf",
            IsFile = true,
        };

        index.AddOrUpdate("docs/doc.pdf", entry);
        index.Remove("docs/doc.pdf");

        Assert.Null(index.TryGet("docs/doc.pdf"));
    }
}
