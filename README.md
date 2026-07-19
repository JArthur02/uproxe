# μProxy Tool 2.0 (v3 proxychains development)

Windows proxy scraper, checker, and **local TCP proxy-chain gateway**. Rewrite of μProxy Tool 1.81 for **.NET 10 LTS**.

Product version remains **2.x** until MVP acceptance on Windows; this branch ships as a *v3 proxychains development build*.

## Layout

| Project | Role |
|---------|------|
| `src/UProxy.Core` | Cross-platform library: parse, scrape, check (HTTP/SOCKS), anonymity, GeoIP, export, **chaining + local gateways** |
| `src/UProxy.UI` | Windows-only WinForms UI (`net10.0-windows`) |
| `tests/UProxy.Core.Tests` | Unit tests (handshakes, chains, gateways, health, persistence) |

Active development branch: `cursor/v3-proxychains`. Immutable 2.0 ZIP snapshot (pre-chains): `cursor/publish-win-x64-zip-35cc`.

See **[docs/BRANCHES.md](docs/BRANCHES.md)** for the full branch taxonomy, open work, and cleanup policy.

Legacy 1.81 binary (`tool.exe`) remains in the repo for reference. Unused package files (`check.ini`, `Ionic.Zip.dll`, `all.js`, `assembly.txt`) are not used by 2.0.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows to run the UI (Core + tests build on Linux/macOS)
- **No administrator rights** required for MVP chaining (loopback listeners + optional WinINET)

## Build

```bash
dotnet build UProxyTool.sln -c Release
dotnet test tests/UProxy.Core.Tests/UProxy.Core.Tests.csproj -c Release
```

Prefer the Core test project on Linux CI (avoid `dotnet test` on the whole solution as the WinForms project can hang under some runners).

Run UI (Windows):

```bash
dotnet run --project src/UProxy.UI -c Release
```

Output: `src/UProxy.UI/bin/Release/net10.0-windows/uProxy Tool.exe`

## Data files

Shipped next to the UI:

- `Data/Source/HttpSource.txt` — HTTP(S) scrape URLs (one per line; `#` comments)
- `Data/Source/SocksSource.txt` — SOCKS scrape URLs
- `Data/Country.mmdb` — MaxMind GeoLite2-Country (local only; no ip2c.org). Bundled build date ~2026-07-10; MaxMind’s GeoLite EULA asks that you refresh it periodically (official updates require a free MaxMind license key).

Settings and chain data live under `%LocalAppData%\uProxyTool\`:

| Path | Purpose |
|------|---------|
| `settings.json` | App settings (ports, judges, window bounds, …) |
| `chains/` | Named fixed / profile chains |
| `pools/` | Named smart pools |
| `credentials/protected.dat` | DPAPI-protected hop credentials (Windows) |
| `health/proxy-health.json` | Passive cooldown / success history |
| `wininet-proxy-backup.json` | Crash-recovery WinINET snapshot |

## Proxy chaining (v3)

**Tools → Proxy Chains…** (or the toolbar button) opens the chain manager.

### What it does
- Shared **ChainDialer** builds TCP tunnels through ordered hops (SOCKS5 preferred; SOCKS4a + HTTP CONNECT supported).
- Local **HTTP** listener `127.0.0.1:8877` and **SOCKS5** listener `127.0.0.1:8878` (configurable; loopback only).
- Modes:
  - **Fast Failover** — one healthy hop from a pool; auto-replace after confirmed failure + cooldown.
  - **Strict Multi-hop** — every hop in order (privacy default: 2 hops; UI max 5).
- Optional **Windows system proxy** points WinINET at the **local HTTP gateway** (not at an external proxy). One-hop “use this proxy” also goes through the gateway.
- Named saved profiles; **only one started** at a time (switching keeps listeners, swaps the dialer).
- **Check Exit IP** is off by default; when clicked, uses a configurable HTTPS URL (default `https://api.ipify.org`).

### What it does *not* do (deferred)
- UDP / QUIC
- Transparent all-application routing (WFP / WinDivert / TUN)
- Per-process interception rules
- System-wide DNS interception
- TLS interception / local CA
- LAN exposure of the gateway

Proxy chaining is **not Tor**. The first hop sees your source IP; the last hop sees the destination.

### Configure apps
- Browser / WinINET-aware apps: enable “Windows system proxy when gateway starts” in Settings or the chain UI, **or** set HTTP proxy to `127.0.0.1:8877`.
- SOCKS-aware apps: set SOCKS5 to `127.0.0.1:8878`.
- If a port is busy, start fails with a suggested free port (no silent move).

## Privacy notes

- No Pastebin / update phone-home.
- GeoIP uses the local MMDB only (checked proxy IPs are not sent to a third-party GeoIP API).
- System proxy (WinINET) is **opt-in**; prior settings are backed up for restore and crash recovery.
- Saved authenticated-proxy credentials are protected for the current Windows user (DPAPI). Ordinary exports omit credentials unless you choose otherwise.
- Exit-IP checks are explicit only; the chosen service will see the chain’s exit request.

## Checking

- Default judge: `http://azenv.net` (still returns classic `REMOTE_ADDR` / `HTTP_*` bodies). Configurable; fallbacks in settings.
- A proxy is marked alive only if the judge body looks like azenv (not a captive portal).
- Anonymity: Transparent / Anonymous / Elite with corrected header rules vs 1.81.
- **Reachability split** (Proxifier-style Test 1 vs Test 2): a proxy that answers TCP but cannot reach the target is reported as `TargetUnreachableThroughProxy`, distinct from a proxy that refuses/times out the connection.
- **Connect latency** (Test 3): a dedicated `Connect (ms)` metric measures the pure TCP round-trip to the proxy, separate from the full judge round-trip (`Latency (ms)`); both appear in the grid and exports.
- **HTTPS probe** issues a raw `CONNECT` and reads the tunnel status: `200` confirms HTTPS, `403/405` → `HttpsConnectForbidden` (proxy forbids CONNECT to the port, e.g. Squid `SSL_ports` / ISA), `407` → auth required.
- SOCKS4/5: full handshake + HTTP response through the tunnel (not merely `Connected`).
- **Fake-IP DNS** (Proxifier-compatible `127.8.x.x` placeholders) + SOCKS4a / remote hostname resolve through the proxy.
- **Embedded auth**: HTTP Basic (`Proxy-Authorization`), SOCKS5 user/pass, SOCKS4 userid; NTLM is detected on 407 but not sent by default (privacy).
- **User-Agent presets**: selectable in Settings (μProxy default, Chrome, Firefox, Internet Explorer 11) for judges/targets that block non-browser agents.

## Export

**Export** saves the filtered result list as plain text, CSV, JSON, or a **proxychains-ng** `*.conf` file.

### Proxychains-ng export

Choose **Proxychains config (`*.conf`)** in the save dialog. The generated proxychains-ng 4.x configuration uses `dynamic_chain` (skip unavailable entries), enables proxied DNS, and lists checked proxies in `[ProxyList]`. HTTPS-capable HTTP proxies are written as `http` (proxychains uses HTTP `CONNECT`); dual SOCKS4/5 proxies are written as `socks5`.

Credentials are excluded by default. Check **Include credentials** to opt in. Proxychains tokens cannot be empty or contain whitespace; the exporter rejects invalid credentials rather than writing a broken config.

On Linux, BSD, or macOS:

```bash
proxychains4 -f proxies.conf curl https://ifconfig.me
```

Proxychains only intercepts supported TCP calls in dynamically linked programs; it does not proxy UDP or ICMP.

## Secret scanning (TruffleHog)

Proxy lists — especially authenticated ones (`user:pass@host:port`) and pasted blobs — occasionally carry real API keys or tokens. **Tools → Scan for secrets (TruffleHog)…** runs [TruffleHog](https://github.com/trufflesecurity/trufflehog) so you can catch leaked credentials before exporting or sharing a list.

- **Targets**: pick any file or folder (e.g. an export or the sources directory), or click **Scan loaded proxies** to scan the current in-memory list (credentials included).
- **Findings**: detector, verified flag, a **redacted** preview of the secret (the full value is never shown), source location, and line.
- **Privacy**: verification is **off by default** — TruffleHog runs with `--no-verification` so candidate secrets are not sent to any provider API. Enable *Verify secrets online* in Settings only if you accept that trade-off.
- **Setup**: TruffleHog is an external binary. Install it from <https://github.com/trufflesecurity/trufflehog> (or `winget install trufflesecurity.trufflehog`) so `trufflehog` is on `PATH`, or set an explicit **TruffleHog path** in Settings. The scanner reports clearly when the binary is missing.

The scanner lives in `UProxy.Core` (`SecretScanner`), so it is usable headlessly/cross-platform; the UI is a thin dialog over it.

## Hotkeys

| Keys | Action |
|------|--------|
| Ctrl+O | Load list |
| Ctrl+V | Paste proxies |
| Ctrl+C | Copy selected |
| Ctrl+X | Clear list |
| Ctrl+A | Select all results |

Results columns are sortable (click a header), rows are tinted by anonymity (Elite / Anonymous / Transparent), and the status bar shows a live alive/anonymity breakdown (or a SOCKS4/5 breakdown in SOCKS mode).
