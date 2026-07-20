# Code signing policy

μProxy Tool uses free code signing provided by [SignPath.io](https://about.signpath.io/), with a free code signing certificate by the [SignPath Foundation](https://signpath.org/).

## Team roles

| Role | Responsibility | GitHub user |
|------|----------------|-------------|
| **Author / Committer** | Implements changes on `release/v2.0` and opens pull requests | [@JArthur02](https://github.com/JArthur02) |
| **Reviewer** | Reviews source changes before merge to the release branch | [@JArthur02](https://github.com/JArthur02) |
| **Approver** | Approves SignPath signing requests in the SignPath UI | [@JArthur02](https://github.com/JArthur02) |

This is a single-maintainer open-source project. The same person may hold all roles.

## What is signed

Only artifacts built by [`.github/workflows/release-v2-signpath.yml`](../.github/workflows/release-v2-signpath.yml) on the **`release/v2.0`** branch or from **`v2.*` tags** are submitted to SignPath:

| Artifact | Description |
|----------|-------------|
| `uproxy_<version>_x64_setup.exe` | Inno Setup offline installer (self-contained `uproxy.exe` + `Data/`) |
| `uProxyTool-<version>-win-x64-portable.zip` | Optional portable ZIP (signed `uproxy.exe` + `Data/`) |

**Not signed:** legacy `tool.exe` (1.81 reference binary), third-party DLLs, GeoIP database files, or any binary built outside the trusted CI workflow.

## Release process

1. Changes merge to `release/v2.0` (μProxy Tool **2.0** line — no v3 proxy-chains gateway).
2. Maintainer pushes a `v2.x.y` tag or runs the release workflow manually.
3. GitHub Actions builds **unsigned** artifacts from source (tests must pass).
4. CI submits artifacts to SignPath with origin verification.
5. A designated **Approver** manually approves each signing request in SignPath.
6. Signed artifacts are attached to the matching [GitHub Release](https://github.com/JArthur02/uproxe/releases).

## Privacy policy

See [PRIVACY.md](PRIVACY.md). μProxy Tool does not collect personal data or phone home.

## Attribution

Windows installers and executables distributed through this program display **SignPath Foundation** as the Authenticode publisher. The application itself is developed by net'n'yahoo under the MIT license.
