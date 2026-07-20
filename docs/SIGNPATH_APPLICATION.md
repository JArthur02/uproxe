# SignPath Foundation application (copy-paste)

Use this page when submitting [signpath.org/apply.html](https://signpath.org/apply.html). All policy URLs point at branch **`release/v2.0`**.

## Pre-submission checklist

- [ ] Branch `release/v2.0` is pushed and public
- [ ] `LICENSE` is MIT at repo root
- [ ] [Code signing policy](CODE_SIGNING_POLICY.md) and [Privacy policy](PRIVACY.md) are linked from [README](../README.md)
- [ ] GitHub Actions workflow [`.github/workflows/release-v2-signpath.yml`](../.github/workflows/release-v2-signpath.yml) builds unsigned artifacts on Windows
- [ ] Maintainer uses MFA on GitHub (and will on SignPath after approval)

Optional before applying: run **Actions → Release v2.0 (SignPath) → Run workflow** on `release/v2.0` with version `2.0.0` to confirm the unsigned build succeeds (SignPath secrets can stay empty).

## Application form fields

| Field | Value |
|-------|-------|
| **Project / repository URL** | `https://github.com/JArthur02/uproxe` |
| **License** | MIT — see `https://github.com/JArthur02/uproxe/blob/release/v2.0/LICENSE` |
| **Release / download URL** | `https://github.com/JArthur02/uproxe/releases` (first signed release after approval) |
| **Release branch** | `release/v2.0` |
| **Code signing policy** | `https://github.com/JArthur02/uproxe/blob/release/v2.0/docs/CODE_SIGNING_POLICY.md` |
| **Privacy policy** | `https://github.com/JArthur02/uproxe/blob/release/v2.0/docs/PRIVACY.md` |

### Short description

```
μProxy Tool 2.0 is a Windows desktop proxy scraper and checker (.NET 10).
It loads public HTTP/SOCKS proxy lists, tests anonymity and latency through
user-configured judges, resolves country via a local MaxMind GeoLite2 database,
and exports results. No telemetry, analytics, or automatic updates.
Signed artifacts: Inno Setup installer (uproxy_<version>_x64_setup.exe) and
optional portable ZIP, built only from CI on release/v2.0 or v2.* tags.
```

### What gets signed

| Artifact | Built by |
|----------|----------|
| `uproxy_<version>_x64_setup.exe` | `packaging/exe/build-installer.ps1` in CI |
| `uProxyTool-<version>-win-x64-portable.zip` | `scripts/build-portable-zip.ps1` in CI |

Only `uproxy.exe` (self-contained .NET publish) and bundled `Data/` files are included. Legacy `tool.exe` (1.81 reference) is **not** packaged or signed.

## After approval

Follow [SIGNPATH.md](SIGNPATH.md) sections 2–4: import artifact configs from `.signpath/artifact-configurations/`, create signing policies, set GitHub secrets/vars, then tag `v2.0.0` on `release/v2.0`.
