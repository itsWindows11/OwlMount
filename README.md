# OwlMount

A Windows-only .NET 10 application that mounts any
[OwlCore.Storage](https://github.com/Arlodotexe/OwlCore.Storage) `IFolder` provider as a
Windows drive letter. Three filesystem backends are available:

| Backend | Flag | Description |
|---|---|---|
| **WinFsp** *(default)* | `--backend winfsp` | Full read-write support via the [WinFsp](https://winfsp.dev/) user-mode driver |
| **Dokany** | `--backend dokany` | Full read-write support via the [Dokany](https://github.com/dokan-dev/dokany) user-mode driver |
| **ProjFS** | `--backend projfs` | Read-only; uses the built-in [Windows Projected File System](https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system) — no additional installs required |

## Features

* **WinUI 3 GUI** — full-featured desktop app with per-mount cards, selection overlay, and system-tray integration
* **Multi-backend** — choose WinFsp or Dokany for read-write, or ProjFS for zero-install read-only
* **Backend abstraction layer** — `IOwlMountBackend` interface separates mount logic from the UI/CLI
* **Read-write support** (WinFsp) — create, write, rename, and delete files/folders
* **Read-only support** (ProjFS / `--read-only` flag)
* **Block cache** — 256 KiB blocks persisted to disk under `%LocalAppData%\OwlMount\Cache\` to avoid re-downloading data
* **Adapter registry** — plug in custom `IRangeReader` / `ISizeProvider` implementations without touching core VFS logic
* **Path index** — in-memory normalized-path → entry map populated during directory enumeration
* **Directory cache** (WinFsp) — short TTL (15 s default) per-folder listing cache; invalidated automatically on write operations
* **In-memory provider** — zero-config drive backed by `OwlCore.Storage.Memory`; configurable RAM size limit
* **Archive provider** — mount any `.zip`, `.tar`, `.rar`, etc. file read-only

## Showcase

### GUI/WinUI 3 app

![The OwlMount WinUI 3 app in action](./media/owlmount-gui-poc.mp4)

### CLI app

![The OwlMount CLI app in action](./media/owlmount-cli-poc.mp4)

## Prerequisites

1. **.NET 10 SDK** — <https://dot.net>
2. **Windows 10 version 1809 (build 17763) or later**
3. Backend-specific requirements:
   - **WinFsp** (default): [install WinFsp](https://winfsp.dev/rel/) — a fast, signed, free user-mode filesystem driver
   - **Dokany**: [install Dokany](https://github.com/dokan-dev/dokany/releases)
   - **ProjFS**: enable the optional Windows feature (run once in an elevated PowerShell prompt):
     ```powershell
     Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
     ```

## Building

```bash
git clone https://github.com/itsWindows11/OwlMount
cd OwlMount
dotnet build
```

## WinUI 3 GUI

The GUI app (`src/OwlMount.WinUI/`) provides a fully in-process mount/unmount workflow with parity to the CLI.

```bash
dotnet run --project src/OwlMount.WinUI/OwlMount.WinUI.csproj
```

### GUI features

* **Mount listing** — each active mount is shown as a card with drive letter, label, provider, capacity, and state
* **Per-mount context menu** — right-click any card to **Edit**, **Unmount**, or open Windows **Properties** for that drive
* **Selection overlay** — check one or more mounts to reveal a floating action bar with Edit (single selection only) and Unmount buttons
* **Add mount dialog** — choose provider, backend, drive letter, label, and provider-specific settings
* **Memory size limit** — when adding a memory drive, a slider lets you pick the maximum RAM the drive can use (capped at the PC's currently free physical memory)
* **System tray** — closing the window hides to tray; right-click the icon for **Open OwlMount**, **Settings**, per-drive **Unmount**, or **Exit**
* **Settings page** (reachable from the title bar button, `ShowSettingsCommand`, or tray **Settings**):
  - **Theme** — Default / Light / Dark
  - **Mount configuration persistence** — save mount points across restarts
  - **In-memory filesystem export** — export RAM-drive contents to a folder on exit
  - **Maintenance — Clear disk cache** — deletes all block-cache files under `%LocalAppData%\OwlMount\Cache\`; frees space used by remote provider caches
  - **Maintenance — Clear ProjFS residue** — deletes leftover virtualisation-root directories under `%LocalAppData%\OwlMount\VirtRoot\` from drives that are no longer mounted

## Running (CLI)

```bat
owlmount mount --provider memory --letter R
```

### Mount options

| Flag | Default | Description |
|---|---|---|
| `--provider` | `memory` | Provider name. See table below. |
| `--backend` | `dokany` | VFS backend: `dokany`, `winfsp`, or `projfs` |
| `--letter` | `M` | Drive letter to mount (without the colon) |
| `--label` | *(auto)* | Volume label shown in Explorer (e.g. `"My Files"`) |
| `--read-only` | *(off)* | Force read-only mode (always set for ProjFS and archive) |
| `--memory-size` | *(free RAM)* | Maximum RAM size in MiB for the `memory` provider |

Pressing **Ctrl+C**, running `owlmount unmount --letter <X>` from another terminal, or ejecting the drive from Explorer all cleanly exit the process.

### Subcommands

| Command | Description |
|---|---|
| `mount` | Mount a provider as a drive letter |
| `unmount --letter <X>` | Unmount a running mount by drive letter |
| `list` | List all active mounts and their PIDs |

### Supported providers

| `--provider` | Extra flags | Description |
|---|---|---|
| `memory` | `[--memory-size <MiB>]` | Empty in-memory filesystem (lives until process exits) |
| `archive` | `--archive-file <path>` | Any archive format (zip, tar, rar, …) via SharpCompress — read-only |
| `local` | `--path <dir>` | Local directory exposed as a drive letter |
| `kubo-mfs` | `--path <mfs-path>` `[--api-url]` | Kubo MFS (Mutable File System) |
| `kubo-ipfs` | `--cid <CID>` `[--api-url]` | Immutable IPFS directory by CID |
| `kubo-ipns` | `--ipns <address>` `[--api-url]` | IPNS-addressed directory |
| `s3` | `--bucket` `[--prefix]` `[--access-key]` `[--secret-key]` `[--region]` `[--endpoint]` | Amazon S3 bucket/prefix |
| `nfs` | `--host <ip>` `--export </path>` `[--nfs-path <path>]` | NFS v3 share |

> Pass `--read-only` with any provider to suppress write support for that mount.
>
> The `archive` provider is always read-only.
>
> **OneDrive** is supported as a code-level provider (`OneDriveFolder` / `OneDriveFile` from `OwlCore.Storage.OneDrive`) but requires a pre-authenticated `GraphServiceClient` from MSAL — see *Adding a custom provider* below.

### Example — empty in-memory filesystem as `R:` (WinFsp, read-write)

```bat
owlmount mount --provider memory --letter R --label "RAM Drive"
```

### Example — memory drive limited to 2 GB

```bat
owlmount mount --provider memory --letter R --label "RAM Drive" --memory-size 2048
```

### Example — same drive via ProjFS (read-only, no WinFsp needed)

```bat
owlmount mount --provider memory --letter R --backend projfs
```

### Example — same drive via Dokany (read-write)

```bat
owlmount mount --provider memory --letter R --backend dokany
```

### Example — archive file read-only as `A:`

```bat
owlmount mount --provider archive --archive-file C:\data\backup.zip --letter A
```

### Example — Kubo MFS as `K:`

```bat
owlmount mount --provider kubo-mfs --path / --letter K --label "IPFS Files"
```

Requires a running Kubo daemon (default API: `http://127.0.0.1:5001`). Override with `--api-url`.

### Example — S3 bucket as `S:`

```bat
owlmount mount --provider s3 --bucket my-bucket --prefix data/ --letter S ^
  --access-key AKIA... --secret-key secret --region us-east-1
```

### Example — NFS share as `N:`

```bat
owlmount mount --provider nfs --host 192.168.1.10 --export /srv/share --letter N
```

## Architecture

```
OwlMount.slnx
├── src/
│   ├── OwlMount.Core/                Cross-platform .NET 10 library
│   │   ├── Abstractions/             IRangeReader, ISizeProvider, PathIndexEntry
│   │   ├── Cache/                    BlockCache (disk-backed, block-sized reads)
│   │   ├── Index/                    PathIndex (in-memory normalized-path map)
│   │   ├── IO/                       WildcardPattern (cross-platform utility)
│   │   └── Registry/                 RangeReaderRegistry, SizeProviderRegistry,
│   │                                 DefaultRangeReader, DefaultSizeProvider
│   ├── OwlMount.Core.Windows/        Windows-only .NET 10 library
│   │   ├── Backends/
│   │   │   ├── IOwlMountBackend.cs   Abstraction interface (Start / Stop / Stopped)
│   │   │   ├── WinFspBackend.cs      WinFsp implementation (read-write)
│   │   │   ├── DokanyBackend.cs      Dokany implementation (read-write)
│   │   │   └── ProjFsBackend.cs      ProjFS implementation (read-only)
│   │   ├── OwlMountFileSystem.cs     WinFsp FileSystemBase implementation
│   │   ├── OwlMountProvider.cs       ProjFS IRequiredCallbacks implementation
│   │   ├── DirectoryCache.cs         TTL-based per-folder listing cache
│   │   └── Contexts.cs               FileContext / FolderContext open-handle objects
│   ├── OwlMount.WinFspHost/          Windows-only .NET 10 console app (CLI)
│   │   └── Program.cs                CLI entry point
│   └── OwlMount.WinUI/               Windows-only WinUI 3 desktop app
│       ├── Services/
│       │   ├── MountService.cs       In-process mount lifecycle + config persistence
│       │   ├── ProviderFactory.cs    Builds IFolder root from ProviderOptions
│       │   ├── ProviderOptions.cs    All mount parameters (provider, backend, letter, …)
│       │   ├── AppTrayService.cs     System-tray icon, menu, and notifications
│       │   ├── NavigationService.cs  Frame-level page navigation
│       │   └── AppSettingsService.cs Theme and app-wide settings persistence
│       ├── Views/
│       │   ├── HomePage.xaml         Mount listing, selection overlay, empty state
│       │   ├── SettingsPage.xaml     Settings + Maintenance section
│       │   └── MountConfigDialog.xaml Add/edit mount dialog
│       ├── MainWindow.xaml           Window shell, title bar, selection overlay
│       └── MainWindowViewModel.cs    App state, mount commands, selection logic
└── tests/
    └── OwlMount.Tests/               Cross-platform xUnit tests
```

### Block cache location

```
%LocalAppData%\OwlMount\Cache\<providerId>\<fileHash>_<blockIndex>.blk
```

Block size defaults to **256 KiB**; pass a custom value to the `BlockCache` constructor.

### ProjFS virtualisation root

ProjFS requires a physical directory as a virtualisation root (API requirement).
It is stored at `%LocalAppData%\OwlMount\VirtRoot\<DriveLetter>\` and cleaned up on every mount start.
Orphaned directories (from crashes) can be removed from Settings → **Clear ProjFS residue**.

### Adding a custom provider

1. Create a class implementing `OwlCore.Storage.IFolder` (and `IFile` for its children).
   - Implement `OwlCore.Storage.IModifiableFolder` too if you want full read-write backend behavior (create/write/rename/delete); `IFolder` alone is treated as read-only.
2. Instantiate your `IFolder` in `ProviderFactory.cs` (GUI) or `Program.cs` (CLI) and pass it to the chosen backend.
3. Optionally register a provider-specific `IRangeReader` for optimised ranged reads:

```csharp
rangeReaders.Register(
    matcher: f => f is MyCustomFile,
    reader:  new MyCustomRangeReader());
```

## Running Tests

```bash
dotnet test tests/OwlMount.Tests/
```

Tests are cross-platform (Windows-only tests skip automatically on other platforms).
