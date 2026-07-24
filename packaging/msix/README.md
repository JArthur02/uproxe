# Microsoft Store MSIX

This directory builds the existing **uproxy** WinForms application as an
unsigned, self-contained x64 MSIX for Microsoft Store submission. Microsoft
signs the package after it passes certification.

## Partner Center identity

The manifest uses the Store identity supplied for this product:

| Field | Value |
|---|---|
| `Package/Identity/Name` | `leekmadeek.uproxy` |
| `Package/Identity/Publisher` | `CN=F60517ED-816A-4235-AB58-62B0A8BA554D` |
| Installed display name | `uproxy` |

Before uploading, confirm the exact
`Package/Properties/PublisherDisplayName` shown in Partner Center. The default
is `leekmadeek`; override it with `-PublisherDisplayName` if Partner Center
shows a different value.

## Build

Prerequisites:

- Windows 11
- .NET 10 SDK
- Windows 10/11 SDK (`MakeAppx.exe`)

From the repository root:

```powershell
.\packaging\msix\build-msix.ps1 `
  -Version 2.0.0.1 `
  -PublisherDisplayName "leekmadeek"
```

Output:

```text
artifacts\release\msix\uproxy_2.0.0.1_x64.msix
artifacts\release\msix\SHA256SUMS.txt
```

The script runs Core tests, publishes `uproxy.exe` and all runtime/data files,
stages the manifest and visual assets, and invokes `MakeAppx.exe`.

## Store submission and signing

Upload the **unsigned** `.msix` from `artifacts\release\msix` on the Packages
page of the existing Partner Center product. Do not replace the manifest
identity and do not sign it with an unrelated certificate. Microsoft Store
re-signs MSIX packages after certification.

The package cannot be installed directly until it is signed. For local
sideload testing, sign a separate copy with a test certificate whose subject
exactly matches the manifest Publisher and trust that certificate on the test
machine.

## Restricted capability declaration

The package declares:

- `runFullTrust`, required for the existing WinForms desktop application.
- `unvirtualizedResources` with a Windows 11 fine-grained exclusion for only
  `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings`.

The second declaration is necessary to preserve uproxy's opt-in **Set as
system proxy** and restore features. Without it, MSIX redirects writes under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` to a
package-private registry, so Windows and other applications do not see the
selected proxy.

All other HKCU writes remain virtualized. The app prompts to restore the saved
proxy configuration on normal exit, offers crash recovery at the next launch,
and exposes its support page at <https://github.com/JArthur02/uproxe/issues>.

In the Partner Center restricted-capability justification, state:

> uproxy is a proxy management utility. At the user's explicit request it
> writes and later restores the current user's WinINET proxy values under
> HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings. Registry
> write virtualization must be disabled so those settings are visible to
> Windows and other applications. The app runs as the current user, does not
> require elevation, saves the previous values before changing them, and
> provides a restore action.

Removing this capability would require removing the system-proxy feature from
the Store build; it would not preserve all v2 features.

For reconsideration after a capability denial, use
[`docs/MSIX_STORE_APPEAL.md`](../../docs/MSIX_STORE_APPEAL.md) and set the same
support URL in **Partner Center → Properties → Support info**.
