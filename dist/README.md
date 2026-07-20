# Windows x64 distribution (`dist/`)

Prebuilt **μProxy Tool 2.0 — v3 proxy-chains preview** builds for Windows x64. This folder lives on the [`cursor/publish-win-x64-zip-35cc`](https://github.com/JArthur02/uproxe/tree/cursor/publish-win-x64-zip-35cc) branch only; `main` and `cursor/v3-proxychains` carry source, not binaries.

## Artifacts

| File | Size (approx.) | Notes |
|------|----------------|-------|
| `uProxyTool-2.0-v3-proxychains-preview-win-x64-selfcontained.zip` | ~49 MB | Bundles .NET 10 runtime; no separate runtime install |
| `uProxyTool-2.0-v3-proxychains-preview-win-x64-framework-dependent.zip` | ~4.7 MB | Requires [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) on the machine |

Each ZIP contains:

- `μProxy.exe` — WinForms UI
- `Data/Country.mmdb` — GeoLite2-Country (local GeoIP)
- `Data/Source/HttpSource.txt`, `Data/Source/SocksSource.txt` — default scrape lists

Extract anywhere and run `μProxy.exe`. Settings persist under `%LocalAppData%\uProxyTool\`.

## Verify integrity

```powershell
Get-FileHash .\uProxyTool-2.0-v3-proxychains-preview-win-x64-selfcontained.zip -Algorithm SHA256
Get-FileHash .\uProxyTool-2.0-v3-proxychains-preview-win-x64-framework-dependent.zip -Algorithm SHA256
```

Compare against `SHA256SUMS.txt` and `MANIFEST.json` in this directory.

## Build provenance

See `MANIFEST.json` for the source commit, publish date, and artifact checksums. The latest snapshot on this branch was built from commit `dfc7b53298c41648ba5b2f23cc322198039a5563` (Proxy Chains Dock layout fix).

## Rebuilding locally

On Windows, from a checkout of the source commit:

```powershell
.\scripts\publish-win-x64-zip.ps1 -SourceCommit dfc7b53
```

Or build from the current tree without checking out:

```powershell
.\scripts\publish-win-x64-zip.ps1
```

For a signed Microsoft Store installer (separate from these ZIPs), see [`packaging/exe/README.md`](../packaging/exe/README.md).

## Naming note

Older republish commits also committed `uProxyTool-2.0-win-x64-*.zip` aliases that were **byte-identical** to the `v3-proxychains-preview` files. Those duplicates were removed to save ~53 MB per variant in the branch tip.
