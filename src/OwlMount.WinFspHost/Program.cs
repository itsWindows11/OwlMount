using System.Diagnostics;
using System.Runtime.InteropServices;
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
        // Support both the new subcommand form ("mount"/"unmount"/"list") and the
        // legacy flag-only form ("--provider …") for backward compatibility.
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        if (sub.StartsWith("--") || args.Length == 0)
        {
            // Legacy: no subcommand — treat the whole args array as "mount" args.
            return await RunMountAsync(args);
        }

        return sub switch
        {
            "mount"   => await RunMountAsync(args[1..]),
            "unmount" => RunUnmount(args[1..]),
            "list"    => RunList(),
            _         => PrintUsage(),
        };
    }

    // ── mount ─────────────────────────────────────────────────────────────────

    static async Task<int> RunMountAsync(string[] args)
    {
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
            FileSystemName           = "OwlMount",
            SectorSize               = 512,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength       = 255,
            CasePreservedNames       = true,
            CaseSensitiveSearch      = false,
            UnicodeOnDisk            = true,
            VolumeSerialNumber       = 0x4F574C4D, // "OWLM"
            FileInfoTimeout          = 1000,
            VolumeInfoTimeout        = 1000,
            DirInfoTimeout           = 1000,
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

        // ── Write PID file so 'owlmount unmount' can signal this process ──────
        string pidFile = GetPidFilePath(letter);
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());

        Console.WriteLine($"Mounted successfully. Browsable at {mountPoint}\\");
        Console.WriteLine($"  PID    : {Environment.ProcessId}");
        Console.WriteLine($"Unmount with: owlmount unmount --letter {letter.TrimEnd(':').ToUpperInvariant()}");
        Console.WriteLine("Or press Ctrl+C.");

        // ── Unmount on Ctrl+C ─────────────────────────────────────────────────
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nUnmounting…");
            host.Unmount();
            DeletePidFile(letter);
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

    // ── unmount ───────────────────────────────────────────────────────────────

    static int RunUnmount(string[] args)
    {
        string? letter = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].ToLowerInvariant() == "--letter")
                letter = args[++i];
        }

        if (letter is null)
        {
            Console.Error.WriteLine("Usage: owlmount unmount --letter <X>");
            return 1;
        }

        string pidFile = GetPidFilePath(letter);
        if (!File.Exists(pidFile))
        {
            Console.Error.WriteLine(
                $"No active mount found for drive '{letter.TrimEnd(':').ToUpperInvariant()}:'.");
            Console.Error.WriteLine("Run 'owlmount list' to see active mounts.");
            return 1;
        }

        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
        {
            Console.Error.WriteLine($"PID file is corrupt: {pidFile}");
            File.Delete(pidFile);
            return 1;
        }

        // Verify the process is still alive before trying to signal it.
        try
        {
            var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                Console.Error.WriteLine(
                    $"Mount process (PID {pid}) has already exited.");
                File.Delete(pidFile);
                return 1;
            }
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine(
                $"Mount process (PID {pid}) is no longer running.");
            File.Delete(pidFile);
            return 1;
        }

        if (!SendCtrlC(pid))
        {
            Console.Error.WriteLine(
                $"Failed to send unmount signal to process {pid}.");
            return 1;
        }

        Console.WriteLine(
            $"Unmount signal sent to '{letter.TrimEnd(':').ToUpperInvariant()}:' (PID {pid}).");
        return 0;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    static int RunList()
    {
        string pidsDir = GetPidsDir();
        if (!Directory.Exists(pidsDir))
        {
            Console.WriteLine("No active mounts.");
            return 0;
        }

        string[] pidFiles = Directory.GetFiles(pidsDir, "*.pid");
        if (pidFiles.Length == 0)
        {
            Console.WriteLine("No active mounts.");
            return 0;
        }

        bool any = false;
        foreach (string pidFile in pidFiles.OrderBy(f => f))
        {
            string driveLetter = Path.GetFileNameWithoutExtension(pidFile).ToUpperInvariant();
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                continue;

            bool alive;
            try
            {
                var proc = Process.GetProcessById(pid);
                alive = !proc.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false;
            }

            if (alive)
            {
                Console.WriteLine($"  {driveLetter}: — PID {pid}");
                any = true;
            }
            else
            {
                // Stale entry — clean up silently.
                try { File.Delete(pidFile); } catch { /* best-effort */ }
            }
        }

        if (!any)
            Console.WriteLine("No active mounts.");

        return 0;
    }

    // ── usage ─────────────────────────────────────────────────────────────────

    static int PrintUsage()
    {
        Console.WriteLine("OwlMount — mount OwlCore.Storage providers as Windows drive letters.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  owlmount mount   --provider <name> --path <dir> --letter <X>");
        Console.WriteLine("  owlmount unmount --letter <X>");
        Console.WriteLine("  owlmount list");
        Console.WriteLine();
        Console.WriteLine("Options (mount):");
        Console.WriteLine("  --provider   Provider tag (default: systemio)");
        Console.WriteLine("  --path       Root path for the provider (default: current directory)");
        Console.WriteLine("  --letter     Drive letter to mount on (default: M)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine(@"  owlmount mount --provider systemio --path C:\Data --letter D");
        Console.WriteLine("  owlmount unmount --letter D");
        Console.WriteLine("  owlmount list");
        return 1;
    }

    // ── PID file helpers ──────────────────────────────────────────────────────

    static string GetPidsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount", "pids");

    static string GetPidFilePath(string letter) =>
        Path.Combine(GetPidsDir(), letter.TrimEnd(':').ToUpperInvariant() + ".pid");

    static void DeletePidFile(string letter)
    {
        try { File.Delete(GetPidFilePath(letter)); } catch { /* best-effort */ }
    }

    // ── Win32 helpers for cross-process Ctrl+C ────────────────────────────────

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(nint HandlerRoutine, bool Add);

    private const uint CtrlCEvent = 0;

    /// <summary>
    /// Sends a CTRL+C event to <paramref name="pid"/> by temporarily attaching to
    /// its console, suppressing the signal in our own process, firing the event,
    /// then restoring normal state.
    /// </summary>
    static bool SendCtrlC(int pid)
    {
        // Detach from our own console so we can attach to the target's.
        FreeConsole();

        if (!AttachConsole(pid))
            return false;

        // Ignore CTRL+C in this process so we don't terminate ourselves.
        SetConsoleCtrlHandler(nint.Zero, true);

        bool ok = GenerateConsoleCtrlEvent(CtrlCEvent, 0);

        // Brief pause so the mount process can start handling the signal.
        Thread.Sleep(500);

        FreeConsole();

        // Restore default CTRL+C behaviour for this process.
        SetConsoleCtrlHandler(nint.Zero, false);

        return ok;
    }
}
