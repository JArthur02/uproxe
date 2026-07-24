# uproxy 2.0 Microsoft Store package

[Download `uproxy_2.0.0.1_x64_unsigned_store.msix`](uproxy_2.0.0.1_x64_unsigned_store.msix)

This x64 MSIX is intentionally unsigned and is intended for upload to the
Microsoft Store product with package identity `leekmadeek.uproxy`. Microsoft
signs the package after certification. It cannot be installed directly until
it is signed.

Version `2.0.0.1` is the Windows 11 reconsideration package. It replaces the
broad HKCU virtualization exemption with one exact WinINET Internet Settings
key exclusion and prompts to restore the previous proxy on normal exit. Use
[`docs/MSIX_STORE_APPEAL.md`](../docs/MSIX_STORE_APPEAL.md) for the revised
restricted-capability justification.

Verify the download against [`SHA256SUMS.txt`](SHA256SUMS.txt):

```text
038f488e3756a15dfa93099e174bccb1db0b4f462c370c716a590d8295137eb3
```
