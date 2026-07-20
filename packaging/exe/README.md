# Microsoft Store / signed EXE packaging

Builds a **self-contained, single-file** `uproxy.exe` and wraps it in a signed Inno Setup installer for Microsoft Store submission or sideloading.

## Layout

| File | Role |
|------|------|
| `build-store-exe.ps1` | Publish, Authenticode-sign PE files, compile Inno installer |
| `uproxy.iss` | Inno Setup script |
| `uproxy-store-logo.png` | Store listing artwork (not used by the installer script) |

Output lands under `artifacts/store-exe/` (gitignored):

- `artifacts/store-exe/publish-win-x64/uproxy.exe`
- `artifacts/store-exe/installer/uproxy_<version>_x64_setup.exe`
- `artifacts/store-exe/SHA256SUMS.txt`

## Local build (unsigned test)

```powershell
.\packaging\exe\build-store-exe.ps1 -SkipSigning -SkipTests
```

## Signed build

```powershell
.\packaging\exe\build-store-exe.ps1 `
  -Version 2.0.0 `
  -Publisher "Your Publisher Name" `
  -PfxPath C:\certs\signing.pfx `
  -PfxPassword (Read-Host -AsSecureString)
```

Requires Windows, .NET 10 SDK, Inno Setup 6, and the Windows SDK (`signtool.exe`).

## GitHub Actions

Workflow: [`.github/workflows/build-store-exe.yml`](../../.github/workflows/build-store-exe.yml) (`workflow_dispatch`).

Configure repository secrets:

- `WINDOWS_SIGNING_PFX_BASE64`
- `WINDOWS_SIGNING_PFX_PASSWORD`

This workflow is **independent** of the ZIP drops in `dist/` on `cursor/publish-win-x64-zip-35cc`.
