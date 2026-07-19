# Proxifier chain logic reference

μProxy v3 proxy-chain behavior is aligned with **Proxifier v4.14** where the
gateway architecture allows it. Full decompile annotations and cross-reference
live in the companion repo:

**[proxe-code `extracted/proxy-chain`](https://github.com/JArthur02/proxe-code/tree/proxy-chain-logic/extracted/proxy-chain)**

| Document | Purpose |
|----------|---------|
| `00_OVERVIEW.md` | CChain/CProxy model, XML attributes, runtime flow |
| `MAPPING.md` | Function → uproxe class map + gap analysis |
| `01`–`04_*.c` | Annotated Ghidra pseudo-C |

## Mode correspondence

| Proxifier chain type | uproxe `ChainMode` |
|----------------------|-------------------|
| `simple` | `StrictMultiHop` |
| `redundancy` | `FastFailover` |
| `load_balancing` | not yet implemented |

## Implemented from mapping (this branch)

- **HTTP-last rule** — `ChainDialer.ValidateHopOrder` (Proxifier `FUN_140068a50`)
  enforced at dial time and on `ChainManager.SwitchProfile` for multi-hop profiles.

## Known gaps (see MAPPING.md P1–P3)

- Ordered redundancy walk (profile hop order vs pool ranking)
- Per-profile `RedundancyTimeout`
- `load_balancing` chain type + PID stickiness
- WFP rule → chain binding (driver layer, separate branch)
