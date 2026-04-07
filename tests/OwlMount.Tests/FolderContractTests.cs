using OwlCore.Storage;
using OwlCore.Storage.Memory;
using OwlCore.Storage.System.IO;

namespace OwlMount.Tests;

/// <summary>
/// Verifies that <see cref="MemoryFolder"/> (the "projected in-memory filesystem")
/// and <see cref="SystemFolder"/> (normal local files on disk) behave 1:1 when
/// accessed through the same <see cref="IFolder"/> API that <c>OwlMountProvider</c>
/// uses internally.
/// <para>
/// Each test is parameterized with <see cref="FolderBackend"/> so failures are
/// immediately attributed to one or both backends.
/// </para>
/// </summary>
public sealed class FolderContractTests : IAsyncLifetime
{
    /// <summary>Selects which <see cref="IFolder"/> implementation to test.</summary>
    public enum FolderBackend { Memory, LocalFilesystem }

    // Shared tree used by every test:
    //   root/
    //     alpha.txt
    //     Beta.txt      ← mixed case to exercise case-insensitive sort
    //     gamma/
    //       inner.txt
    //       deep/
    //         bottom.txt
    //     zZz.txt       ← mixed case
    //     empty/        ← no children

    private const string FileAlpha  = "alpha.txt";
    private const string FileBeta   = "Beta.txt";
    private const string FileZzz    = "zZz.txt";
    private const string DirGamma   = "gamma";
    private const string DirDeep    = "deep";
    private const string DirEmpty   = "empty";
    private const string FileInner  = "inner.txt";
    private const string FileBottom = "bottom.txt";

    // Expected names from the root after case-insensitive alphabetical sort:
    private static readonly string[] ExpectedRootNamesSorted =
        [FileAlpha, FileBeta, DirEmpty, DirGamma, FileZzz];

    private string? _tempDir;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the reference folder tree in either an in-memory or on-disk backend.
    /// </summary>
    private async Task<IFolder> BuildTreeAsync(FolderBackend backend)
    {
        if (backend == FolderBackend.Memory)
        {
            var root = new MemoryFolder("root", "root");

            await root.CreateFileAsync(FileAlpha, overwrite: false);
            await root.CreateFileAsync(FileBeta,  overwrite: false);
            await root.CreateFileAsync(FileZzz,   overwrite: false);

            var gamma = (IModifiableFolder)await root.CreateFolderAsync(DirGamma, overwrite: false);
            await gamma.CreateFileAsync(FileInner, overwrite: false);

            var deep = (IModifiableFolder)await gamma.CreateFolderAsync(DirDeep, overwrite: false);
            await deep.CreateFileAsync(FileBottom, overwrite: false);

            await root.CreateFolderAsync(DirEmpty, overwrite: false);

            return root;
        }
        else
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "OwlMountContractTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            File.WriteAllText(Path.Combine(_tempDir, FileAlpha), "alpha content");
            File.WriteAllText(Path.Combine(_tempDir, FileBeta),  "beta content");
            File.WriteAllText(Path.Combine(_tempDir, FileZzz),   "zzz content");

            string gammaDir = Path.Combine(_tempDir, DirGamma);
            Directory.CreateDirectory(gammaDir);
            File.WriteAllText(Path.Combine(gammaDir, FileInner), "inner content");

            string deepDir = Path.Combine(gammaDir, DirDeep);
            Directory.CreateDirectory(deepDir);
            File.WriteAllText(Path.Combine(deepDir, FileBottom), "bottom content");

            Directory.CreateDirectory(Path.Combine(_tempDir, DirEmpty));

            return new SystemFolder(_tempDir);
        }
    }

    /// <summary>
    /// Enumerates <paramref name="folder"/> through the same LINQ pipeline
    /// <c>OwlMountProvider.StartDirectoryEnumerationCallback</c> uses.
    /// </summary>
    private static List<(string Name, bool IsDirectory)> ProviderEnumerate(IFolder folder) =>
        folder.GetItemsAsync()
              .ToBlockingEnumerable()
              .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
              .Select(x => (x.Name, IsDirectory: x is IFolder))
              .ToList();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_Root_ReturnsSameNamesAfterProviderSort(FolderBackend backend)
    {
        IFolder root = await BuildTreeAsync(backend);
        var names = ProviderEnumerate(root).Select(e => e.Name).ToArray();
        Assert.Equal(ExpectedRootNamesSorted, names);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_Root_ReturnsCorrectCount(FolderBackend backend)
    {
        IFolder root = await BuildTreeAsync(backend);
        Assert.Equal(5, ProviderEnumerate(root).Count);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_FilesAreClassifiedAsIFile(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        var     items = ProviderEnumerate(root);
        var     files = items.Where(e => !e.IsDirectory).Select(e => e.Name).ToArray();
        Assert.Contains(FileAlpha, files);
        Assert.Contains(FileBeta,  files);
        Assert.Contains(FileZzz,   files);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_FoldersAreClassifiedAsIFolder(FolderBackend backend)
    {
        IFolder root    = await BuildTreeAsync(backend);
        var     items   = ProviderEnumerate(root);
        var     folders = items.Where(e => e.IsDirectory).Select(e => e.Name).ToArray();
        Assert.Contains(DirGamma, folders);
        Assert.Contains(DirEmpty, folders);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_EmptyFolder_ReturnsNoItems(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        IFolder empty = (IFolder)await root.GetFirstByNameAsync(DirEmpty);
        Assert.Empty(ProviderEnumerate(empty));
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_Subfolder_ReturnsSortedItems(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        IFolder gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);

        var names = ProviderEnumerate(gamma).Select(e => e.Name).ToArray();
        Assert.Equal([DirDeep, FileInner], names);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_DeeplyNestedFolder_ReturnsCorrectItem(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        IFolder gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);
        IFolder deep  = (IFolder)await gamma.GetFirstByNameAsync(DirDeep);

        var names = ProviderEnumerate(deep).Select(e => e.Name).ToArray();
        Assert.Equal([FileBottom], names);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetFirstByNameAsync_ExistingFile_ReturnsIFile(FolderBackend backend)
    {
        IFolder root = await BuildTreeAsync(backend);
        IStorableChild item = await root.GetFirstByNameAsync(FileAlpha);
        Assert.IsAssignableFrom<IFile>(item);
        Assert.Equal(FileAlpha, item.Name);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetFirstByNameAsync_ExistingFolder_ReturnsIFolder(FolderBackend backend)
    {
        IFolder root = await BuildTreeAsync(backend);
        IStorableChild item = await root.GetFirstByNameAsync(DirGamma);
        Assert.IsAssignableFrom<IFolder>(item);
        Assert.Equal(DirGamma, item.Name);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetFirstByNameAsync_NonExistentItem_ThrowsFileNotFoundException(FolderBackend backend)
    {
        IFolder root = await BuildTreeAsync(backend);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => root.GetFirstByNameAsync("does-not-exist.txt"));
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetFirstByNameAsync_FileInSubfolder_IsAccessible(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        IFolder gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);
        IStorableChild inner = await gamma.GetFirstByNameAsync(FileInner);
        Assert.Equal(FileInner, inner.Name);
        Assert.IsAssignableFrom<IFile>(inner);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetFirstByNameAsync_DeeplyNestedFile_IsAccessibleThroughHierarchy(FolderBackend backend)
    {
        IFolder root   = await BuildTreeAsync(backend);
        IFolder gamma  = (IFolder)await root.GetFirstByNameAsync(DirGamma);
        IFolder deep   = (IFolder)await gamma.GetFirstByNameAsync(DirDeep);
        IStorableChild bottom = await deep.GetFirstByNameAsync(FileBottom);
        Assert.Equal(FileBottom, bottom.Name);
        Assert.IsAssignableFrom<IFile>(bottom);
    }

    [Fact]
    public async Task ProviderEnumerate_BothBackends_ProduceIdenticalOutput()
    {
        // Build BOTH trees and compare output — the definitive 1:1 assertion.
        var mem = ProviderEnumerate(await BuildTreeAsync(FolderBackend.Memory));

        _tempDir = null; // reset so the next build creates a fresh temp dir
        var local = ProviderEnumerate(await BuildTreeAsync(FolderBackend.LocalFilesystem));

        Assert.Equal(mem, local);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task ProviderEnumerate_SortOrder_IsCaseInsensitiveAscending(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        var     names = ProviderEnumerate(root).Select(e => e.Name).ToList();
        var     sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task ProviderEnumerate_FileAndFolderMixed_AllPresent(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        var     items = ProviderEnumerate(root);
        int fileCount   = items.Count(e => !e.IsDirectory);
        int folderCount = items.Count(e =>  e.IsDirectory);
        Assert.Equal(3, fileCount);   // alpha.txt, Beta.txt, zZz.txt
        Assert.Equal(2, folderCount); // gamma, empty
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task ProviderEnumerate_SubfolderContents_MatchExpectedStructure(FolderBackend backend)
    {
        IFolder root  = await BuildTreeAsync(backend);
        IFolder gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);
        var     items = ProviderEnumerate(gamma);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, e => e.Name == DirDeep  && e.IsDirectory);
        Assert.Contains(items, e => e.Name == FileInner && !e.IsDirectory);
    }

    [Theory]
    [InlineData(FolderBackend.Memory)]
    [InlineData(FolderBackend.LocalFilesystem)]
    public async Task GetItemsAsync_ReEnumerate_ReturnsSameResultsOnSecondCall(FolderBackend backend)
    {
        IFolder root   = await BuildTreeAsync(backend);
        var     first  = ProviderEnumerate(root);
        var     second = ProviderEnumerate(root);
        Assert.Equal(first, second);
    }
}
