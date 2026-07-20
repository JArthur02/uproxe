# Packaging (μProxy Tool 2.0 release line)

| Path | Purpose |
|------|---------|
| [`exe/build-installer.ps1`](exe/build-installer.ps1) | Build unsigned Inno Setup installer (`uproxy_<version>_x64_setup.exe`) |
| [`exe/uproxy.iss`](exe/uproxy.iss) | Inno Setup script |
| [`../scripts/build-portable-zip.ps1`](../scripts/build-portable-zip.ps1) | Build unsigned portable ZIP |
| [`../.github/workflows/release-v2-signpath.yml`](../.github/workflows/release-v2-signpath.yml) | CI build + SignPath signing + GitHub Release |

Signing is performed by [SignPath Foundation](https://signpath.org/) in CI — local builds are **unsigned**. See [`docs/SIGNPATH.md`](../docs/SIGNPATH.md).
