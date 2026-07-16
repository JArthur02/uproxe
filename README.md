# μProxy Tool 2.0

Windows proxy scraper and checker. Rewrite of μProxy Tool 1.81 for **.NET 10 LTS**.

## Layout

| Project | Role |
|---------|------|
| `src/UProxy.Core` | Cross-platform library: parse, scrape, check (HTTP/SOCKS), anonymity, GeoIP, export |
| `src/UProxy.UI` | Windows-only WinForms UI (`net10.0-windows`) |
| `tests/UProxy.Core.Tests` | Unit tests |

Legacy 1.81 binary (`tool.exe`) remains in the repo for reference. Unused package files (`check.ini`, `Ionic.Zip.dll`, `all.js`, `assembly.txt`) are not used by 2.0.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows to run the UI (Core + tests build on Linux/macOS)

## Build

```bash
dotnet build UProxyTool.sln -c Release
dotnet test UProxyTool.sln -c Release
```

Run UI (Windows):

```bash
dotnet run --project src/UProxy.UI -c Release
```

Output: `src/UProxy.UI/bin/Release/net10.0-windows/uProxy Tool.exe`

## Data files

Shipped next to the UI:

- `Data/Source/HttpSource.txt` — HTTP(S) scrape URLs (one per line; `#` comments)
- `Data/Source/SocksSource.txt` — SOCKS scrape URLs
- `Data/Country.mmdb` — MaxMind GeoLite2-Country (local only; no ip2c.org)

Settings are stored under `%LocalAppData%\uProxyTool\settings.json`.

## Privacy notes

- No Pastebin / update phone-home.
- GeoIP uses the local MMDB only (checked proxy IPs are not sent to a third-party GeoIP API).
- System proxy (WinINET) is **opt-in** with a warning; prior settings are backed up for restore and crash recovery.
- Proxy credentials are not included in exports unless you choose that explicitly (plain export defaults to `host:port`).

## Checking

- Default judge: `http://azenv.net` (still returns classic `REMOTE_ADDR` / `HTTP_*` bodies). Configurable; fallbacks in settings.
- A proxy is marked alive only if the judge body looks like azenv (not a captive portal).
- Anonymity: Transparent / Anonymous / Elite with corrected header rules vs 1.81.
- HTTPS probe uses Google’s `generate_204` connectivity check through the proxy.
- SOCKS4/5: full handshake + HTTP response through the tunnel (not merely `Connected`).
- **Fake-IP DNS** (Proxifier-compatible `127.8.x.x` placeholders) + SOCKS4a / remote hostname resolve through the proxy.
- **Embedded auth**: HTTP Basic (`Proxy-Authorization`), SOCKS5 user/pass, SOCKS4 userid; NTLM is detected on 407 but not sent by default (privacy).


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
