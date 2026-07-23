# Packaging (μProxy Tool 2.0 release line)

| Path | Purpose |
|------|---------|
| [`exe/build-installer.ps1`](exe/build-installer.ps1) | Build unsigned Inno Setup installer (`uproxy_<version>_x64_setup.exe`) |
| [`exe/uproxy.iss`](exe/uproxy.iss) | Inno Setup script |
| [`msix/build-msix.ps1`](msix/build-msix.ps1) | Build unsigned Microsoft Store MSIX (`uproxy_<version>_x64.msix`) |
| [`msix/AppxManifest.xml`](msix/AppxManifest.xml) | Store identity, capabilities, and visual assets |
| [`../scripts/build-portable-zip.ps1`](../scripts/build-portable-zip.ps1) | Build unsigned portable ZIP |
| [`../.github/workflows/release-v2-signpath.yml`](../.github/workflows/release-v2-signpath.yml) | CI build + SignPath signing + GitHub Release |

Signing is performed by [SignPath Foundation](https://signpath.org/) in CI — local builds are **unsigned**. See [`docs/SIGNPATH.md`](../docs/SIGNPATH.md).

Microsoft Store MSIX packages are submitted unsigned and signed by the Store
after certification. See [`msix/README.md`](msix/README.md).
