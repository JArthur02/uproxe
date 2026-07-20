# GeoIP data (MaxMind GeoLite2)

μProxy Tool ships `Data/Country.mmdb`, a **MaxMind GeoLite2 Country** database used for offline country lookup when checking proxies.

## License

GeoLite2 data is provided by [MaxMind](https://www.maxmind.com/) under the [GeoLite2 End User License Agreement](https://www.maxmind.com/en/geolite2/eula).

Key points for distributors:

- The database is **included in installers and portable ZIPs** as a data file (not signed PE content).
- **Refresh periodically** — MaxMind updates GeoLite2 regularly; the bundled file includes a build date in release notes.
- **Attribution** — This product includes GeoLite2 data created by MaxMind, available from [https://www.maxmind.com](https://www.maxmind.com).

## Updating the database

1. Create a free MaxMind account and obtain a license key.
2. Download GeoLite2-Country MMDB from the MaxMind portal.
3. Replace `src/UProxy.UI/Data/Country.mmdb` (and the root `Country.mmdb` copy if present).
4. Rebuild and release.

μProxy Tool never sends checked IP addresses to MaxMind; lookups are performed entirely against the local file.
