# Microsoft Store restricted-capability reconsideration

Use this text for a new Partner Center submission of package version
`2.0.0.1`. Also set:

- **Support URL:** <https://github.com/JArthur02/uproxe/issues>
- **Developer website:** <https://github.com/JArthur02/uproxe>

## `unvirtualizedResources` business justification

> uproxy is a user-operated Windows proxy scraper, checker, and management
> utility. Scraping and checking do not require unvirtualized resources. The
> capability is used only by the optional, user-initiated **Set System Proxy**
> feature. That feature applies a proxy selected by the user to the current
> user's WinINET configuration so browsers and other applications configured
> to use Windows proxy settings can use it.
>
> MSIX normally redirects writes under HKEY_CURRENT_USER to a package-private
> registry. A virtualized proxy value is visible only to uproxy and therefore
> cannot configure Windows or other applications. We evaluated the supported
> WinINet InternetSetOption API, but global WinINET options are persisted in
> the same current-user Internet Settings registry location and remain subject
> to package registry virtualization. A per-process proxy would affect only
> uproxy and would not implement the user-requested system-proxy function.
> WinHTTP machine configuration, a service, elevation, or a driver would be
> broader and less appropriate. Sending the user to Windows Settings would not
> allow uproxy to atomically save, apply, and restore the selected proxy.
>
> Package version 2.0.0.1 requires Windows 11 and uses the fine-grained
> RegistryWriteVirtualization ExcludedKey mechanism. The only unvirtualized
> path is:
>
> HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings
>
> All other HKCU and AppData writes remain virtualized. Within the excluded
> key, uproxy reads or writes only ProxyEnable, ProxyServer, ProxyOverride, and
> AutoConfigURL. It does not access HKLM, request elevation, install a service
> or driver, run in the background, or change proxy settings without a direct
> user action.
>
> Before applying a proxy, uproxy displays a privacy and connectivity warning
> with **No** as the default choice. It captures the exact previous values and
> saves a recovery backup before making any change. The window title and
> status bar identify when the system proxy is active. Emergency Reset
> restores the saved values. On normal exit, uproxy asks the user to restore
> the previous settings with **Yes** as the recommended default and keeps the
> application open if restoration fails. If the process was interrupted, the
> next launch detects the backup and offers recovery before opening the main
> window.
>
> We assure that unvirtualizedResources is used solely for the stated,
> user-initiated system-proxy function. Registry writes will be limited to the
> specified key and values. Future versions will not expand this access
> without updating the Store declaration and business justification.
>
> Support: https://github.com/JArthur02/uproxe/issues
> Developer website: https://github.com/JArthur02/uproxe

## `runFullTrust` business justification

> uproxy is an existing .NET 10 WinForms desktop application packaged as MSIX.
> The package launches `uproxy.exe` as a full-trust desktop application at the
> signed-in user's normal medium-integrity level. Full trust is required for
> the WinForms desktop process, user-selected file import/export, HTTP and
> SOCKS network checks, the optional user-selected TruffleHog command-line
> integration, and the explicitly initiated WinINET proxy function. The app
> does not request administrator elevation, install a service or driver,
> inject into other processes, or perform hidden background activity.

## Certification test notes

1. Load or scrape proxies and check them.
2. Select an alive proxy and choose **Set System Proxy**.
3. Confirm that the warning defaults to **No**.
4. After opting in, verify that the title identifies the active proxy.
5. Close uproxy and choose **Yes** in the restore prompt.
6. Verify that the original Windows proxy values were restored.
7. Repeat the change, terminate the process without closing normally, relaunch
   uproxy, and verify that startup recovery offers to restore the backup.
