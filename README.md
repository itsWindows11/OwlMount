# OwlMount

A Windows-only .NET 9 console application that mounts any
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

1. **.NET 9 SDK** — <https://dot.net>
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
owlmount --provider systemio --path "C:\MyFolder" --letter X
```

| Flag | Default | Description |
|---|---|---|
| `--provider` | `systemio` | Provider name tag (cosmetic; used to namespace the block cache) |
| `--path` | current directory | Root path to expose. For `systemio` this is a local directory. |
| `--letter` | `M` | Drive letter to mount (without the colon) |

Press **Ctrl+C** to unmount cleanly.

### Example — mount `C:\Users\Alice\Documents` as `D:`

```bat
owlmount --provider systemio --path "C:\Users\Alice\Documents" --letter D
```

Then open `D:\` in Explorer or any application.

## Architecture

```
OwlMount.sln
├── src/
│   ├── OwlMount.Core/            Cross-platform .NET 9 library
│   │   ├── Abstractions/         IRangeReader, ISizeProvider, PathIndexEntry
│   │   ├── Cache/                BlockCache (disk-backed, block-sized reads)
│   │   ├── Index/                PathIndex (in-memory normalized-path map)
│   │   └── Registry/             RangeReaderRegistry, SizeProviderRegistry,
│   │                             DefaultRangeReader, DefaultSizeProvider
│   └── OwlMount.WinFspHost/      Windows-only .NET 9 console app
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
