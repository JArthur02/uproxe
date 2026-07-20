# Release line: μProxy Tool 2.0 (v2)

This branch (`release/v2.0`) is the **signed distribution line** for μProxy Tool 2.0 — the proxy scraper/checker rewrite **without** the v3 proxy-chains gateway that lives on `main`.

| Branch | Scope | Signing |
|--------|-------|---------|
| `release/v2.0` | Scrape, check, export, GeoIP, secret scan | SignPath → GitHub Releases |
| `main` | Above + v3 proxy-chains / local gateway | Source only (unsigned) |

## Cut a release

```bash
git checkout release/v2.0
git pull
git tag v2.0.0
git push origin v2.0.0
```

Approve the SignPath request when the workflow emails you. Artifacts appear on [Releases](https://github.com/JArthur02/uproxe/releases).

## Apply to SignPath

Follow [SIGNPATH.md](SIGNPATH.md) before the first tagged release.
