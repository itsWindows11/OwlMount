# OwlMount

A Windows-only .NET 10 console application that mounts any
[OwlCore.Storage](https://github.com/Arlodotexe/OwlCore.Storage) `IFolder` provider as a
Windows drive letter using [WinFsp](https://winfsp.dev).

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
* **System.IO example provider** — bundled via `OwlCore.Storage.System.IO`;
  use a local folder as a quick sanity-check

## Prerequisites

1. **.NET 10 SDK** — <https://dot.net>
2. **WinFsp** (Windows File System Proxy) must be installed on the host machine:
   * Download the latest MSI from <https://winfsp.dev/rel/>
   * Minimal install: *WinFsp Core* component is sufficient

## Building

```bash
git clone https://github.com/itsWindows11/OwlMount
cd OwlMount
dotnet build
```

## Running

```
owlmount mount --provider systemio --path "C:\MyFolder" --letter X
```

### Mount options

| Flag | Default | Description |
|---|---|---|
| `--provider` | `systemio` | Provider name. See table below. |
| `--letter` | `M` | Drive letter to mount (without the colon) |
| `--label` | *(auto)* | Volume label shown in Explorer (e.g. `"My Files"`) |
| `--path` | current directory | Root path for `systemio`; MFS path for `kubo-mfs` |

Press **Ctrl+C**, or run `owlmount unmount --letter <X>` from another terminal, to unmount cleanly.

### Subcommands

| Command | Description |
|---|---|
| `mount` | Mount a provider as a drive letter |
| `unmount --letter <X>` | Unmount a running mount by drive letter |
| `list` | List all active mounts and their PIDs |

### Supported providers

| `--provider` | Extra flags | Description |
|---|---|---|
| `systemio` | `--path <dir>` | Local filesystem folder |
| `memory` | *(none)* | Empty in-memory filesystem (lives until process exits) |
| `kubo-mfs` | `--path <mfs-path>` `[--api-url]` | Kubo MFS (Mutable File System) |
| `kubo-ipfs` | `--cid <CID>` `[--api-url]` | Immutable IPFS directory by CID |
| `kubo-ipns` | `--ipns <address>` `[--api-url]` | IPNS-addressed directory |
| `s3` | `--bucket` `[--prefix]` `[--access-key]` `[--secret-key]` `[--region]` `[--endpoint]` | Amazon S3 bucket/prefix |
| `nfs` | `--host <ip>` `--export </path>` `[--nfs-path <path>]` | NFS v3 share |

> **OneDrive** is supported as a code-level provider (`OneDriveFolder` / `OneDriveFile` from `OwlCore.Storage.OneDrive`) but requires a pre-authenticated `GraphServiceClient` from MSAL — see the *Adding a custom provider* section.

### Example — mount a local folder as `D:` with a custom label

```bat
owlmount mount --provider systemio --path "C:\Users\Alice\Documents" --letter D --label "Alice Docs"
```

Then open `D:\` in Explorer. Unmount from a second terminal:

```bat
owlmount unmount --letter D
```

### Example — empty in-memory filesystem as `R:`

```bat
owlmount mount --provider memory --letter R --label "RAM Drive"
```

The drive starts completely empty. Any files or folders you copy into `R:\` exist only in RAM and are gone when the process exits.

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
│       ├── OwlMountFileSystem.cs WinFsp FileSystemBase implementation
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
