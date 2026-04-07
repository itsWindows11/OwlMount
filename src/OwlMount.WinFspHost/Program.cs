using System.Runtime.Versioning;
using Fsp;
using OwlCore.Storage.System.IO;
using OwlMount.Core.Cache;
using OwlMount.Core.Registry;
using OwlMount.WinFspHost;

[SupportedOSPlatform("windows")]
static class Program
{
    static async Task<int> Main(string[] args)
    {
        // ── Parse CLI arguments ───────────────────────────────────────────────
        string provider = "systemio";
        string letter   = "M";
        string? path    = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--provider": provider = args[++i]; break;
                case "--letter":   letter   = args[++i]; break;
                case "--path":     path     = args[++i]; break;
            }
        }

        // ── Resolve the root IFolder ──────────────────────────────────────────
        string rootPath = path ?? Environment.CurrentDirectory;
        if (!Directory.Exists(rootPath))
        {
            Console.Error.WriteLine($"Error: path does not exist: {rootPath}");
            return 1;
        }

        var root = new SystemFolder(rootPath);
        Console.WriteLine($"OwlMount — provider: {provider}");
        Console.WriteLine($"  Root   : {rootPath}");

        // ── Build the VFS components ──────────────────────────────────────────
        var rangeReaders  = new RangeReaderRegistry();
        var sizeProviders = new SizeProviderRegistry();
        var blockCache    = new BlockCache(providerId: $"{provider}_{root.Id}");

        var fs = new OwlMountFileSystem(root, blockCache, rangeReaders, sizeProviders);

        // ── Configure the WinFsp host ─────────────────────────────────────────
        var host = new FileSystemHost(fs)
        {
            FileSystemName         = "OwlMount",
            SectorSize             = 512,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength     = 255,
            CasePreservedNames     = true,
            CaseSensitiveSearch    = false,
            UnicodeOnDisk          = true,
            VolumeSerialNumber     = 0x4F574C4D, // "OWLM"
            FileInfoTimeout        = 1000,
            VolumeInfoTimeout      = 1000,
            DirInfoTimeout         = 1000,
        };

        string mountPoint = letter.TrimEnd(':') + ":";
        Console.WriteLine($"  Mount  : {mountPoint}\\");
        Console.WriteLine();

        int mountResult = host.Mount(mountPoint);
        if (mountResult < 0)
        {
            Console.Error.WriteLine(
                $"Failed to mount at {mountPoint} (WinFsp error {mountResult}).");
            Console.Error.WriteLine(
                "Ensure WinFsp is installed and the drive letter is not already in use.");
            Console.Error.WriteLine(
                "  WinFsp installer: https://winfsp.dev/rel/");
            return 1;
        }

        Console.WriteLine($"Mounted successfully. Browsable at {mountPoint}\\");
        Console.WriteLine("Press Ctrl+C to unmount and exit.");

        // ── Unmount on Ctrl+C ─────────────────────────────────────────────────
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nUnmounting…");
            host.Unmount();
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }

        Console.WriteLine("Done.");
        return 0;
    }
}
