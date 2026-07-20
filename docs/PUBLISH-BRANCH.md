# Publish branch guide

Branch: [`cursor/publish-win-x64-zip-35cc`](https://github.com/JArthur02/uproxe/tree/cursor/publish-win-x64-zip-35cc)

## Purpose

Immutable **distribution** line for Windows x64 ZIP builds. Source development happens on `cursor/v3-proxychains` → `main`; this branch carries prebuilt binaries plus packaging scripts.

## Layout

| Path | Role |
|------|------|
| `dist/` | Committed ZIP artifacts + `SHA256SUMS.txt` + `MANIFEST.json` + [README](dist/README.md) |
| `scripts/publish-win-x64-zip.ps1` | Rebuild ZIPs from a source commit (Windows only) |
| `packaging/exe/` | Signed Microsoft Store Inno installer pipeline |
| `.github/workflows/build-store-exe.yml` | Manual workflow for signed Store EXE |

## Current snapshot

- **Label:** `v3-proxychains-preview` (μProxy 2.0 with proxy-chain gateway UI)
- **Source commit:** `dfc7b53298c41648ba5b2f23cc322198039a5563` (`dfc7b53`)
- **Artifacts:** 2 ZIPs (self-contained + framework-dependent) — see `dist/MANIFEST.json`

## Workflow

1. Merge or cherry-pick desired UI/features into `cursor/v3-proxychains` on the dev line.
2. Note the release commit SHA.
3. On this branch (Windows): `.\scripts\publish-win-x64-zip.ps1 -SourceCommit <sha>`
4. Commit `dist/*`, `dist/MANIFEST.json`, `dist/SHA256SUMS.txt` with message `dist: republish <summary> (<short-sha>)`.
5. Do **not** merge this branch back into `main` (binary bloat). Keep it as a sibling archive branch.

## Relation to `main`

This branch **diverges** from `main` by design: it adds `dist/` binaries and packaging while the source tree on the branch tip may lag `main`. For the latest source, use `main` or `cursor/v3-proxychains`; for the latest **preview ZIP**, use this branch's `dist/`.

## History cleanup (2026-07-20)

- Removed byte-identical `uProxyTool-2.0-win-x64-*.zip` aliases (duplicates of the `v3-proxychains-preview` files).
- Moved store-build scripts from repo root into `packaging/exe/` and workflow into `.github/workflows/`.

For the full repo branch taxonomy, see `docs/BRANCHES.md` on `main`.
