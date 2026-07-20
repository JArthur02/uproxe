# Privacy policy

μProxy Tool (uProxy) is a local Windows desktop application. This policy describes what the software does and does not do with user data.

## Summary

μProxy Tool does **not** collect, transmit, or sell personal information. It does not include analytics, crash reporting, or automatic update phone-home.

## Data stored locally

| Data | Location | Purpose |
|------|----------|---------|
| User settings | `%LocalAppData%\uProxyTool\settings.json` | Preferences, judge URLs, UI state |
| WinINET proxy backup | `%LocalAppData%\uProxyTool\wininet-proxy-backup.json` | Restore system proxy after opt-in use |
| Loaded proxy lists | In-memory / user-initiated files | Core functionality |

The user controls when lists are loaded, saved, or exported.

## Network activity

μProxy Tool connects to the network only when the user initiates an action:

- **Scraping** — fetches public proxy list URLs defined in `Data/Source/*.txt` or user-provided sources.
- **Checking** — connects through user-selected proxies to configured judge URLs (default `http://azenv.net`) to test anonymity and latency.
- **GeoIP** — uses a **local** MaxMind GeoLite2 Country database (`Data/Country.mmdb`). Checked proxy IP addresses are looked up offline; they are **not** sent to MaxMind or any third-party GeoIP API.

There is no background telemetry, license validation server, or Pastebin/update endpoint.

## Optional third-party tools

If the user runs **Tools → Scan for secrets (TruffleHog)…**, the external [TruffleHog](https://github.com/trufflesecurity/trufflehog) binary scans files or the in-memory proxy list locally. Online secret verification is **disabled by default** (`--no-verification`).

## System proxy (opt-in)

Setting the Windows system proxy routes **other applications'** traffic through the user-selected proxy. μProxy Tool shows a warning and backs up prior WinINET settings before enabling this.

## Contact

Issues and privacy questions: [github.com/JArthur02/uproxe/issues](https://github.com/JArthur02/uproxe/issues).

_Last updated: 2026-07-20_
