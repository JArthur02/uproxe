# Branch guide

How branches in [JArthur02/uproxe](https://github.com/JArthur02/uproxe) are organized.

## Tier 1 — integration (keep)

| Branch | Role | Base |
|--------|------|------|
| `main` | Default branch; latest accepted μProxy 2.x / v3 gateway release line | — |
| `cursor/v3-proxychains` | Active proxy-chain gateway development; merge here first | `main` |

**Workflow:** feature branches → PR into `cursor/v3-proxychains` → when stable, PR `cursor/v3-proxychains` → `main`.

## Tier 2 — topic / archive (keep, do not delete)

| Branch | Role |
|--------|------|
| `fyer-clone` | Proxifier v4.14 binary drop (reference only; not built by solution) |
| `cursor/publish-win-x64-zip-35cc` | Immutable pre-chains Win x64 ZIP snapshot |

## Tier 3 — open work (keep until merged)

| Branch | PR | Notes |
|--------|-----|-------|
| `codex/investigate-proxychains-implementation` | [#16](https://github.com/JArthur02/uproxe/pull/16) (open) | proxychains-ng config export |

## Tier 4 — safe to delete

Branches whose commits are already in `cursor/v3-proxychains` (0 unique commits vs integration tip). Delete after confirming the linked PR is **merged**:

- `codex/review-code-for-errors-and-improvements` — #18 merged
- `cursor/dotnet-dev-environment-setup-7632` — #2 merged
- `cursor/fix-secretscan-toolbar-clip-f851` — #15 merged
- `cursor/proxychecker-diagnostics-upgrades-35cc` — #8, #11 merged
- `cursor/trufflehog-secret-scanner-35cc` — #9 merged
- `cursor/fix-v3-gateway-blockers-f851` — #19–#23 merged
- `cursor/proxy-chain-mapping-58cf` — #29 merged
- `cursor/ux-routing-firstcut-f851` — #24–#26 merged
- `cursor/ux-pass4-recheck-dirty-f851` — #27 merged
- `cursor/fix-dpi-layout-and-geoip-35cc` — #14 merged
- `cursor/fix-nonascii-ua-and-ui-overlap-35cc` — #13 merged
- `cursor/fix-parser-build-and-host-validation-35cc` — #4, #6, #12 merged
- `cursor/uproxy-net10-rewrite-b2b1` — #1, #7 merged
- `cursor/fix-toolbar-chain-layout-f851` — #30 merged

## Naming conventions

| Prefix | Meaning |
|--------|---------|
| `cursor/` | Cursor Cloud Agent feature branches (`cursor/<topic>-<id>`) |
| `codex/` | Codex / alternate agent experiments |
| `fyer-clone` | Third-party binary reference drop |

## Related repos

| Repo | Branch | Content |
|------|--------|---------|
| [proxe-code](https://github.com/JArthur02/proxe-code) | `proxy-chain-logic` | Proxifier chain decompile + uproxe mapping |
| [proxe-code](https://github.com/JArthur02/proxe-code) | `wfp-driver-hooks` | Proxifier WFP driver extracts |

## Maintenance commands

```bash
# List branches not merged into integration tip
gh api repos/JArthur02/uproxe/compare/cursor/v3-proxychains...BRANCH --jq '.ahead_by'

# Delete a merged branch (0 ahead_by)
git push origin --delete BRANCH
```

_Last updated: 2026-07-19 (post #30 toolbar layout merge)_
