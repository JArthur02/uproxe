# AGENTS.md

## Cursor Cloud specific instructions

### What this repo is
`uProxe` / **μProxy Tool** is a Windows proxy scraper & checker.

- The `main` branch currently holds only the **legacy 1.81 release artifacts** (`tool.exe`, `Ionic.Zip.dll`, `check.ini`, `Country.mmdb`, `gate.txt`, `all.js`, `assembly.txt`) — there is no buildable source here.
- The active development codebase is the **.NET 10 rewrite** (`μProxy Tool 2.0`), currently on branch `cursor/uproxy-net10-rewrite-b2b1` (a `UProxyTool.sln` with `src/UProxy.Core`, `src/UProxy.UI`, `tests/UProxy.Core.Tests`). Build/test/run commands below only apply where that solution is present (that branch, or `main` once the rewrite is merged).

### Toolchain
- The **.NET 10 SDK** (`dotnet`, 10.0.3xx) is preinstalled in the VM snapshot at `/usr/share/dotnet` and symlinked to `/usr/local/bin/dotnet` (already on `PATH`). It is not reinstalled by the update script.
- The update script runs `dotnet restore UProxyTool.sln` only when that file exists, so it is a no-op on `main` and refreshes NuGet packages on the rewrite branch.

### Build / test / run (only when `UProxyTool.sln` is present)
Standard commands are documented in the rewrite branch `README.md`. In short:
- Build: `dotnet build UProxyTool.sln -c Release`
- Test: `dotnet test UProxyTool.sln -c Release` (xUnit; 18 tests covering `ProxyParser` + `AnonymityClassifier`)
- Run UI: `dotnet run --project src/UProxy.UI -c Release`

### Non-obvious caveats
- `src/UProxy.UI` targets `net10.0-windows` (WinForms). It **builds** on Linux only because `Directory.Build.props` sets `EnableWindowsTargeting=true`, but the GUI **cannot run on Linux** — it requires Windows. Do not attempt to launch the UI in this Linux VM.
- `src/UProxy.Core` and the test project target plain `net10.0` and are fully cross-platform (build, test, and run on Linux).
- To exercise/demo core functionality on Linux without the UI, reference `UProxy.Core` from a throwaway console app and call `UProxy.Core.Parsing.ProxyParser.TryParse` and `UProxy.Core.GeoIp.MaxMindGeoIpResolver` (point it at the repo's `Country.mmdb`).
- `check.ini`, `Ionic.Zip.dll`, `all.js`, and `assembly.txt` are legacy 1.81 files and are **not** used by the 2.0 solution.
