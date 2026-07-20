# SignPath Foundation setup guide

Step-by-step checklist to apply for free OSS code signing and run the first signed **μProxy Tool 2.0** release (v2 line, **without** v3 proxy-chains).

## 1. Apply at SignPath Foundation

See [SIGNPATH_APPLICATION.md](SIGNPATH_APPLICATION.md) for a copy-paste checklist and form values.

Submit the application at [signpath.org/apply.html](https://signpath.org/apply.html) with:

| Field | Value |
|-------|-------|
| **Repository** | `https://github.com/JArthur02/uproxe` |
| **License** | MIT (`LICENSE` in repo root) |
| **Release branch** | `release/v2.0` |
| **Download URL** | `https://github.com/JArthur02/uproxe/releases` (create first release after approval) |
| **Code signing policy** | `https://github.com/JArthur02/uproxe/blob/release/v2.0/docs/CODE_SIGNING_POLICY.md` |
| **Privacy policy** | `https://github.com/JArthur02/uproxe/blob/release/v2.0/docs/PRIVACY.md` |
| **Description** | Windows desktop proxy scraper/checker (.NET 10). Scrapes public proxy lists, tests HTTP/SOCKS anonymity and latency, exports results. No telemetry. |

## 2. Configure SignPath project (after approval)

In the SignPath UI:

1. **Create project** — suggested slug: `uproxe` (or `uProxy-Tool`).
2. **Repository URL** — `https://github.com/JArthur02/uproxe`
3. **Trusted build system** — link **GitHub.com** to this repository.
4. **Artifact configurations** — import from repo:
   - `.signpath/artifact-configurations/installer.xml` (slug: `installer`)
   - `.signpath/artifact-configurations/portable-zip.xml` (slug: `portable-zip`)
5. **Signing policies**
   - `test-signing` — for manual `workflow_dispatch` runs (no origin restriction).
   - `release-signing` — enable **origin verification**; allow branches `release/v2.0` and tags `v2.*`.

Upload an unsigned sample artifact from a local `.\packaging\exe\build-installer.ps1` run if SignPath offers to auto-generate configurations.

## 3. Configure GitHub repository

### Secrets

| Name | Description |
|------|-------------|
| `SIGNPATH_API_TOKEN` | API token for a user with **submitter** role on the project |

### Variables

| Name | Example | Description |
|------|---------|-------------|
| `SIGNPATH_ORGANIZATION_ID` | `(from SignPath UI)` | Organization UUID |
| `SIGNPATH_PROJECT_SLUG` | `uproxe` | Project slug |
| `SIGNPATH_SIGNING_POLICY_RELEASE` | `release-signing` | Policy for tagged releases |
| `SIGNPATH_SIGNING_POLICY_TEST` | `test-signing` | Policy for manual test runs |

## 4. First signed release

```bash
git checkout release/v2.0
git pull origin release/v2.0
git tag v2.0.0
git push origin v2.0.0
```

This triggers [`.github/workflows/release-v2-signpath.yml`](../.github/workflows/release-v2-signpath.yml):

1. Builds unsigned installer + portable ZIP on Windows.
2. Submits to SignPath (blocks until approved).
3. **You approve** the request in SignPath UI.
4. Workflow uploads signed artifacts to GitHub Releases.

For a dry run before tagging:

1. Actions → **Release v2.0 (SignPath)** → **Run workflow** on branch `release/v2.0`.
2. Use signing policy `test-signing`.

## 5. Local unsigned build (smoke test)

On Windows with .NET 10 SDK and Inno Setup 6:

```powershell
.\packaging\exe\build-installer.ps1 -Version 2.0.0
# Output: artifacts\release\installer\uproxy_2.0.0_x64_setup.exe
```

## Scope: v2 vs v3

| Branch | Contents | Signed? |
|--------|----------|---------|
| `release/v2.0` | μProxy Tool 2.0 core (scrape/check/export) | **Yes** — SignPath line |
| `main` / `cursor/v3-proxychains` | Includes v3 proxy-chains gateway | No (not in scope) |
| `cursor/publish-win-x64-zip-35cc` | Old v3-preview ZIP drops | No (superseded) |
