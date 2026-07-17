# AGENTS.md

## Cursor Cloud specific instructions

### What this repo is
`uProxe` / **μProxy Tool** is a Windows proxy scraper, checker, and (in v3) local proxy-chain gateway.

- **Immutable 2.0 release snapshot (binaries only):** `cursor/publish-win-x64-zip-35cc` (`dist/*.zip`). Do not develop features on that branch.
- **Active v3 development branch:** `cursor/v3-proxychains` — reconciled from PR #15 source tip (`cursor/fix-secretscan-toolbar-clip-f851`) + current `main`.
- Solution: `UProxyTool.sln` with `src/UProxy.Core`, `src/UProxy.UI`, `tests/UProxy.Core.Tests`.

### Toolchain
- The **.NET 10 SDK** (`dotnet`, 10.0.3xx) is preinstalled in the VM snapshot at `/usr/share/dotnet` and symlinked to `/usr/local/bin/dotnet` (already on `PATH`). It is not reinstalled by the update script.
- The update script runs `dotnet restore UProxyTool.sln` only when that file exists.

### Build / test / run
- Build: `dotnet build UProxyTool.sln -c Release`
- Test Core (preferred on Linux CI): `dotnet test tests/UProxy.Core.Tests/UProxy.Core.Tests.csproj -c Release`
- Avoid `dotnet test UProxyTool.sln` as the only gate — the WinForms project can hang under some runners.
- Run UI (Windows only): `dotnet run --project src/UProxy.UI -c Release`

### Non-obvious caveats
- `src/UProxy.UI` targets `net10.0-windows` (WinForms). It **builds** on Linux because `Directory.Build.props` sets `EnableWindowsTargeting=true`, but the GUI **cannot run on Linux**.
- `src/UProxy.Core` and the test project target plain `net10.0` and are fully cross-platform.
- v3 proxy chaining is **driver-free / TCP-only** for MVP: local loopback HTTP+SOCKS5 gateways + optional WinINET pointing at the local HTTP listener. Transparent all-app / UDP / WFP / WinDivert / TUN are deferred.
- Product version stays **2.x** until MVP acceptance; then bump to **3.0.0**. Development builds may show an informational “v3 proxychains development build” label.
- Legacy 1.81 files (`tool.exe`, `Ionic.Zip.dll`, `check.ini`, etc.) are not used by the 2.0/3.0 solution.
- Do **not** re-add third-party Proxifier binaries to this branch. They are compiled proprietary artifacts (no usable source to adapt), outside the driver-free v3 MVP. If transparent routing is ever needed later, prefer documented open-source designs (e.g. ProxiFyre / ProxyBridge / WFP docs)—not reverse-engineering commercial EXEs.
