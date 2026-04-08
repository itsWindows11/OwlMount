using System.Runtime.Versioning;
using System.Text;
using Microsoft.Windows.ProjFS;
using OwlCore.Storage;
using OwlCore.Storage.Memory;
using OwlCore.Storage.System.IO;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;
using OwlMount.Core.Windows;

namespace OwlMount.Tests;

/// <summary>
/// Compares a <see cref="SystemFolder"/> backed by a ProjFS virtualization root
/// (whose data comes from an in-memory <see cref="MemoryFolder"/>) against a
/// <see cref="SystemFolder"/> on a normal local temp directory — verifying
/// completely 1:1 behavior through the OS <see cref="IFolder"/> API.
/// <para>
/// All tests require Windows 10 1803+ (build 17763) with the ProjFS optional feature
/// enabled (<c>Client-ProjFS</c>). Tests skip silently on other platforms.
/// </para>
/// </summary>
[SupportedOSPlatform(OsPlatform)]
public sealed class FolderContractTests : IAsyncLifetime
{
    // ── Minimum OS version for ProjFS ─────────────────────────────────────────

    private const int    OsMajor     = 10;
    private const int    OsMinor     = 0;
    private const int    OsBuild     = 17763; // Windows 10 1803
    private const string OsPlatform  = "windows10.0.17763.0";

    private static bool IsProjFsSupported() =>
        OperatingSystem.IsWindowsVersionAtLeast(OsMajor, OsMinor, OsBuild);

    // ── Shared tree layout ────────────────────────────────────────────────────
    //   root/
    //     alpha.txt       ("alpha content")
    //     Beta.txt        ("beta content")   ← mixed case
    //     zZz.txt         ("zzz content")    ← mixed case
    //     gamma/
    //       inner.txt     ("inner content")
    //       deep/
    //         bottom.txt  ("bottom content")
    //     empty/          ← no children

    private const string FileAlpha  = "alpha.txt";
    private const string FileBeta   = "Beta.txt";
    private const string FileZzz    = "zZz.txt";
    private const string DirGamma   = "gamma";
    private const string DirDeep    = "deep";
    private const string DirEmpty   = "empty";
    private const string FileInner  = "inner.txt";
    private const string FileBottom = "bottom.txt";

    private static readonly (string Name, string Content)[] RootFiles =
    [
        (FileAlpha, "alpha content"),
        (FileBeta,  "beta content"),
        (FileZzz,   "zzz content"),
    ];

    private static readonly (string Name, string Content)[] GammaFiles =
        [(FileInner, "inner content")];

    private static readonly (string Name, string Content)[] DeepFiles =
        [(FileBottom, "bottom content")];

    // Expected names from root after case-insensitive alphabetical sort
    private static readonly string[] ExpectedRootSorted =
        [FileAlpha, FileBeta, DirEmpty, DirGamma, FileZzz];

    // ── Per-test state ────────────────────────────────────────────────────────

    private SystemFolder?           _local;
    private SystemFolder?           _projected;
    private string?                 _localRoot;
    private string?                 _projFsRoot;
    private string?                 _blockCacheDir;
    private VirtualizationInstance? _vi;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(OsMajor, OsMinor, OsBuild))
            await SetUpTreesAsync();
    }

    public Task DisposeAsync()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(OsMajor, OsMinor, OsBuild))
            StopProjFs();
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns <c>true</c> when the current test should be skipped.</summary>
    private bool ShouldSkip() => _local is null || _projected is null;

    private async Task SetUpTreesAsync()
    {
        _local     = await BuildLocalTreeAsync();
        _projected = await BuildProjectedTreeAsync();
    }

    private async Task<SystemFolder> BuildLocalTreeAsync()
    {
        _localRoot = TempDir("Local");

        foreach (var (name, content) in RootFiles)
            await File.WriteAllTextAsync(Path.Combine(_localRoot, name), content, Encoding.UTF8);

        string gammaDir = Path.Combine(_localRoot, DirGamma);
        Directory.CreateDirectory(gammaDir);
        foreach (var (name, content) in GammaFiles)
            await File.WriteAllTextAsync(Path.Combine(gammaDir, name), content, Encoding.UTF8);

        string deepDir = Path.Combine(gammaDir, DirDeep);
        Directory.CreateDirectory(deepDir);
        foreach (var (name, content) in DeepFiles)
            await File.WriteAllTextAsync(Path.Combine(deepDir, name), content, Encoding.UTF8);

        Directory.CreateDirectory(Path.Combine(_localRoot, DirEmpty));

        return new SystemFolder(_localRoot);
    }

    private async Task<SystemFolder> BuildProjectedTreeAsync()
    {
        MemoryFolder memRoot = await BuildMemoryTreeAsync();

        _blockCacheDir = TempDir("Cache");
        var blockCache    = new BlockCache("contract-test", cacheDir: _blockCacheDir);
        var rangeReaders  = new RangeReaderRegistry();
        var sizeProviders = new SizeProviderRegistry();
        var provider      = new OwlMountProvider(memRoot, blockCache, rangeReaders, sizeProviders);

        _projFsRoot = TempDir("ProjFs");

        _vi = new VirtualizationInstance(
            _projFsRoot,
            poolThreadCount:         0,
            concurrentThreadCount:   0,
            enableNegativePathCache: false,
            notificationMappings:    Array.Empty<NotificationMapping>());

        provider.SetInstance(_vi, _projFsRoot);

        HResult mark = VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(
            _projFsRoot, _vi.VirtualizationInstanceId);

        if (mark != HResult.Ok)
            throw new InvalidOperationException(
                $"MarkDirectoryAsVirtualizationRoot failed ({mark}). " +
                "Enable ProjFS: Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS");

        HResult start = _vi.StartVirtualizing(provider);
        if (start != HResult.Ok)
            throw new InvalidOperationException($"StartVirtualizing failed ({start}).");

        return new SystemFolder(_projFsRoot);
    }

    private static async Task<MemoryFolder> BuildMemoryTreeAsync()
    {
        var root  = new MemoryFolder("root", "root");
        var gamma = (IModifiableFolder)await root.CreateFolderAsync(DirGamma, overwrite: false);
        var deep  = (IModifiableFolder)await gamma.CreateFolderAsync(DirDeep, overwrite: false);
        await root.CreateFolderAsync(DirEmpty, overwrite: false);

        await WriteMemoryFilesAsync(root,  RootFiles);
        await WriteMemoryFilesAsync(gamma, GammaFiles);
        await WriteMemoryFilesAsync(deep,  DeepFiles);

        return root;
    }

    private static async Task WriteMemoryFilesAsync(
        IModifiableFolder folder, (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            var file   = (IFile)await folder.CreateFileAsync(name, overwrite: false);
            var stream = await file.OpenStreamAsync(FileAccess.ReadWrite);
            await using (stream)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
            }
        }
    }

    private void StopProjFs()
    {
        if (_vi is not null)
        {
            try
            {
                _vi.StopVirtualizing();
            }
            catch (Exception ex)
            {
                // StopVirtualizing should not normally throw, but we log and continue
                // so the rest of cleanup still runs.
                Console.Error.WriteLine(
                    $"[FolderContractTests] StopVirtualizing threw unexpectedly: {ex.Message}");
            }
            _vi = null;
        }

        TryDelete(_projFsRoot);
        TryDelete(_localRoot);
        TryDelete(_blockCacheDir);
    }

    private static string TempDir(string tag) =>
        Path.Combine(
            Path.GetTempPath(),
            $"OwlMountContract_{tag}_{Guid.NewGuid():N}");

    private static void TryDelete(string? path)
    {
        if (path is null || !Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[FolderContractTests] Could not delete temp directory '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates <paramref name="folder"/> through the same LINQ pipeline
    /// <c>OwlMountProvider.StartDirectoryEnumerationCallback</c> uses.
    /// </summary>
    private static List<(string Name, bool IsDirectory)> ProviderEnumerate(IFolder folder) =>
        [.. folder.GetItemsAsync()
              .ToBlockingEnumerable()
              .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
              .Select(x => (x.Name, IsDirectory: x is IFolder))];

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BothSides_ProduceIdenticalRootEnumeration()
    {
        if (ShouldSkip()) return;
        Assert.Equal(ProviderEnumerate(_local!), ProviderEnumerate(_projected!));
    }

    [Fact]
    public void BothSides_RootEnumeration_MatchesExpectedSortedNames()
    {
        if (ShouldSkip()) return;

        string[] localNames     = [.. ProviderEnumerate(_local!).Select(e => e.Name)];
        string[] projectedNames = [.. ProviderEnumerate(_projected!).Select(e => e.Name)];

        Assert.Equal(ExpectedRootSorted, localNames);
        Assert.Equal(ExpectedRootSorted, projectedNames);
    }

    [Fact]
    public void BothSides_RootEnumeration_ReturnsCorrectCount()
    {
        if (ShouldSkip()) return;
        Assert.Equal(5, ProviderEnumerate(_local!).Count);
        Assert.Equal(5, ProviderEnumerate(_projected!).Count);
    }

    [Fact]
    public void BothSides_FilesAreClassifiedAsIFile()
    {
        if (ShouldSkip()) return;

        foreach (var folder in new[] { _local!, _projected! })
        {
            string[] files = [.. ProviderEnumerate(folder)
                .Where(e => !e.IsDirectory).Select(e => e.Name)];
            Assert.Contains(FileAlpha, files);
            Assert.Contains(FileBeta,  files);
            Assert.Contains(FileZzz,   files);
        }
    }

    [Fact]
    public void BothSides_FoldersAreClassifiedAsIFolder()
    {
        if (ShouldSkip()) return;

        foreach (var folder in new[] { _local!, _projected! })
        {
            string[] folders = [.. ProviderEnumerate(folder)
                .Where(e => e.IsDirectory).Select(e => e.Name)];
            Assert.Contains(DirGamma, folders);
            Assert.Contains(DirEmpty, folders);
        }
    }

    [Fact]
    public async Task BothSides_EmptySubfolder_ReturnsNoItems()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var empty = (IFolder)await root.GetFirstByNameAsync(DirEmpty);
            Assert.Empty(ProviderEnumerate(empty));
        }
    }

    [Fact]
    public async Task BothSides_Subfolder_ReturnsSortedItems()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);
            var names = ProviderEnumerate(gamma).Select(e => e.Name).ToArray();
            Assert.Equal([DirDeep, FileInner], names);
        }
    }

    [Fact]
    public async Task BothSides_SubfolderEnumeration_IsIdentical()
    {
        if (ShouldSkip()) return;

        var localGamma     = (IFolder)await _local!.GetFirstByNameAsync(DirGamma);
        var projectedGamma = (IFolder)await _projected!.GetFirstByNameAsync(DirGamma);

        Assert.Equal(ProviderEnumerate(localGamma), ProviderEnumerate(projectedGamma));
    }

    [Fact]
    public async Task BothSides_DeeplyNestedFolder_ReturnsCorrectItem()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);
            var deep  = (IFolder)await gamma.GetFirstByNameAsync(DirDeep);
            var names = ProviderEnumerate(deep).Select(e => e.Name).ToArray();
            Assert.Equal([FileBottom], names);
        }
    }

    [Fact]
    public async Task BothSides_DeepFolderEnumeration_IsIdentical()
    {
        if (ShouldSkip()) return;

        var localDeep = (IFolder)await
            ((IFolder)await _local!.GetFirstByNameAsync(DirGamma)).GetFirstByNameAsync(DirDeep);
        var projectedDeep = (IFolder)await
            ((IFolder)await _projected!.GetFirstByNameAsync(DirGamma)).GetFirstByNameAsync(DirDeep);

        Assert.Equal(ProviderEnumerate(localDeep), ProviderEnumerate(projectedDeep));
    }

    [Fact]
    public async Task BothSides_GetFirstByNameAsync_ExistingFile_ReturnsIFile()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var item = await root.GetFirstByNameAsync(FileAlpha);
            Assert.IsAssignableFrom<IFile>(item);
            Assert.Equal(FileAlpha, item.Name);
        }
    }

    [Fact]
    public async Task BothSides_GetFirstByNameAsync_ExistingFolder_ReturnsIFolder()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var item = await root.GetFirstByNameAsync(DirGamma);
            Assert.IsAssignableFrom<IFolder>(item);
            Assert.Equal(DirGamma, item.Name);
        }
    }

    [Fact]
    public async Task BothSides_GetFirstByNameAsync_NonExistentItem_ThrowsFileNotFoundException()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => root.GetFirstByNameAsync("does-not-exist.txt"));
        }
    }

    [Fact]
    public async Task BothSides_FileInSubfolder_IsAccessible()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var gamma = (IFolder)await root.GetFirstByNameAsync(DirGamma);
            var inner = await gamma.GetFirstByNameAsync(FileInner);
            Assert.Equal(FileInner, inner.Name);
            Assert.IsAssignableFrom<IFile>(inner);
        }
    }

    [Fact]
    public async Task BothSides_DeeplyNestedFile_IsAccessibleThroughHierarchy()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var gamma  = (IFolder)await root.GetFirstByNameAsync(DirGamma);
            var deep   = (IFolder)await gamma.GetFirstByNameAsync(DirDeep);
            var bottom = await deep.GetFirstByNameAsync(FileBottom);
            Assert.Equal(FileBottom, bottom.Name);
            Assert.IsAssignableFrom<IFile>(bottom);
        }
    }

    [Fact]
    public async Task BothSides_ReadFileContent_MatchesExpected()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var file = (IFile)await root.GetFirstByNameAsync(FileAlpha);
            await using var stream = await file.OpenStreamAsync(FileAccess.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string content = await reader.ReadToEndAsync();
            Assert.Equal("alpha content", content);
        }
    }

    [Fact]
    public async Task BothSides_FileContent_IsIdentical()
    {
        if (ShouldSkip()) return;

        static async Task<string> ReadContent(IFolder root, string name)
        {
            var file = (IFile)await root.GetFirstByNameAsync(name);
            await using var stream = await file.OpenStreamAsync(FileAccess.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        foreach (var (name, expected) in RootFiles)
        {
            string localContent     = await ReadContent(_local!,     name);
            string projectedContent = await ReadContent(_projected!, name);
            Assert.Equal(expected,     localContent);
            Assert.Equal(localContent, projectedContent);
        }
    }

    [Fact]
    public void BothSides_SortOrder_IsCaseInsensitiveAscending()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var names  = ProviderEnumerate(root).Select(e => e.Name).ToList();
            var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(sorted, names);
        }
    }

    [Fact]
    public void BothSides_FileAndFolderMixed_CountsMatch()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var items = ProviderEnumerate(root);
            Assert.Equal(3, items.Count(e => !e.IsDirectory)); // alpha, Beta, zZz
            Assert.Equal(2, items.Count(e =>  e.IsDirectory)); // gamma, empty
        }
    }

    [Fact]
    public void BothSides_ReEnumerate_ReturnsSameResults()
    {
        if (ShouldSkip()) return;

        foreach (var root in new[] { _local!, _projected! })
        {
            var first  = ProviderEnumerate(root);
            var second = ProviderEnumerate(root);
            Assert.Equal(first, second);
        }
    }
}
