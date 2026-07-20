# Windows installer packaging

Builds an **unsigned** self-contained `uproxy.exe` and wraps it in an Inno Setup installer. Code signing is performed in CI by [SignPath Foundation](https://signpath.org/), not locally.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)

## Build locally

```powershell
.\packaging\exe\build-installer.ps1 -Version 2.0.0
```

Output:

- `artifacts\release\publish-win-x64\uproxy.exe`
- `artifacts\release\installer\uproxy_2.0.0_x64_setup.exe`
- `artifacts\release\SHA256SUMS.txt`

Portable ZIP (no installer):

```powershell
.\scripts\build-portable-zip.ps1 -Version 2.0.0
```

## CI / SignPath

See [`docs/SIGNPATH.md`](../../docs/SIGNPATH.md) and [`.github/workflows/release-v2-signpath.yml`](../../.github/workflows/release-v2-signpath.yml).
