# OwlMount

A Windows-only .NET 10 console application that mounts any
[OwlCore.Storage](https://github.com/Arlodotexe/OwlCore.Storage) `IFolder` provider as a
Windows drive letter using the built-in
[Windows Projected File System (ProjFS)](https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system).

## Features (MVP)

* **Read-only** drive-letter mount backed by any `IFolder` / `IFile` provider
* **Block cache** — 256 KiB blocks persisted to disk under
  `%LocalAppData%\OwlMount\Cache\` to avoid re-downloading data
* **Adapter registry** — plug in custom `IRangeReader` / `ISizeProvider`
  implementations for specific provider types without touching core VFS logic
* **Path index** — in-memory normalized-path → entry map populated during
  directory enumeration; avoids redundant provider look-ups
* **Directory cache** — short TTL (15 s default) per-folder listing cache for
  snappy Explorer browsing
* **In-memory provider** — zero-config drive backed by `OwlCore.Storage.Memory`; great for testing and ephemeral scratch space

## Prerequisites

1. **.NET 10 SDK** — <https://dot.net>
2. **Windows 10 version 1809 (build 17763) or later** with the
   *Windows Projected File System* optional feature enabled.
   Run once in an elevated PowerShell prompt if not already enabled:
   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
   ```
   No third-party drivers or installers are required.

## Building

```bash
git clone https://github.com/itsWindows11/OwlMount
cd OwlMount
dotnet build
```

## Running

```
owlmount mount --provider memory --letter R
```

### Mount options

| Flag | Default | Description |
|---|---|---|
| `--provider` | `memory` | Provider name. See table below. |
| `--letter` | `M` | Drive letter to mount (without the colon) |
| `--label` | *(auto)* | Volume label shown in Explorer (e.g. `"My Files"`) |

Pressing **Ctrl+C**, running `owlmount unmount --letter <X>` from another terminal, or ejecting/unmounting the drive from Windows Explorer all cleanly exit the process.

### Subcommands

| Command | Description |
|---|---|
| `mount` | Mount a provider as a drive letter |
| `unmount --letter <X>` | Unmount a running mount by drive letter |
| `list` | List all active mounts and their PIDs |

### Supported providers

| `--provider` | Extra flags | Description |
|---|---|---|
| `memory` | *(none)* | Empty in-memory filesystem (default; lives until process exits) |
| `kubo-mfs` | `--path <mfs-path>` `[--api-url]` | Kubo MFS (Mutable File System) |
| `kubo-ipfs` | `--cid <CID>` `[--api-url]` | Immutable IPFS directory by CID |
| `kubo-ipns` | `--ipns <address>` `[--api-url]` | IPNS-addressed directory |
| `s3` | `--bucket` `[--prefix]` `[--access-key]` `[--secret-key]` `[--region]` `[--endpoint]` | Amazon S3 bucket/prefix |
| `nfs` | `--host <ip>` `--export </path>` `[--nfs-path <path>]` | NFS v3 share |

> **OneDrive** is supported as a code-level provider (`OneDriveFolder` / `OneDriveFile` from `OwlCore.Storage.OneDrive`) but requires a pre-authenticated `GraphServiceClient` from MSAL — see the *Adding a custom provider* section.

### Example — empty in-memory filesystem as `R:`

```bat
owlmount mount --provider memory --letter R --label "RAM Drive"
```

The drive starts completely empty. Any files or folders you copy into `R:\` exist only in RAM and are gone when the process exits. Unmount from a second terminal, or eject from Explorer:

```bat
owlmount unmount --letter R
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
OwlMount.sln
├── src/
│   ├── OwlMount.Core/            Cross-platform .NET 10 library
│   │   ├── Abstractions/         IRangeReader, ISizeProvider, PathIndexEntry
│   │   ├── Cache/                BlockCache (disk-backed, block-sized reads)
│   │   ├── Index/                PathIndex (in-memory normalized-path map)
│   │   └── Registry/             RangeReaderRegistry, SizeProviderRegistry,
│   │                             DefaultRangeReader, DefaultSizeProvider
│   └── OwlMount.WinFspHost/      Windows-only .NET 10 console app
│       ├── OwlMountProvider.cs   ProjFS IRequiredCallbacks implementation
│       ├── DirectoryCache.cs     TTL-based per-folder listing cache
│       ├── Contexts.cs           FileContext / FolderContext open-handle objects
│       └── Program.cs            CLI entry point
└── tests/
    └── OwlMount.Tests/           Cross-platform xUnit tests
        ├── PathNormalizationTests.cs
        └── BlockCacheTests.cs
```

### Adding a custom provider

1. Create a class implementing `OwlCore.Storage.IFolder` (and `IFile` for its children).
2. Instantiate your `IFolder` in `Program.cs` (or wire it via DI) and pass it to
   `OwlMountFileSystem`.
3. Optionally register a provider-specific `IRangeReader` for optimised ranged reads:

```csharp
rangeReaders.Register(
    matcher: f => f is MyCustomFile,
    reader:  new MyCustomRangeReader());
```

### Block cache location

```
%LocalAppData%\OwlMount\Cache\<providerId>\<fileHash>_<blockIndex>.blk
```

Block size defaults to **256 KiB**; pass a custom value to the `BlockCache` constructor.

## Running Tests

```bash
dotnet test tests/OwlMount.Tests/
```

Tests are cross-platform and do not require WinFsp or Windows.
