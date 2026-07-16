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

## Hotkeys

| Keys | Action |
|------|--------|
| Ctrl+O | Load list |
| Ctrl+V | Paste proxies |
| Ctrl+C | Copy selected |
| Ctrl+X | Clear list |
