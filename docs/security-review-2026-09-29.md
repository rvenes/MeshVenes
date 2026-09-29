# MeshVenes security and installation review

Date: 2026-09-29. Scope: Windows source, installation, update handling, WebView2,
NuGet advisories, transport configuration and local data. This is a targeted review
with regression tests, not a penetration-test certification. Existing local MQTT
and protocol improvements were reviewed and preserved.

## Installation choice

Use MeshPi's Windows interaction: download, inspect and run a PowerShell script as
the current user. MeshVenes remains an unsigned, unpackaged, self-contained x64 ZIP.
It does not need MeshPi's Python runtime, background service or autostart, or
Fjoscam's Electron/NSIS packaging toolchain.

`installers/install-windows.ps1` creates a Start menu shortcut and installs into
`%LOCALAPPDATA%\Programs\MeshVenes\releases\<version>-<unique-id>`. It supports
`-DesktopShortcut`, repeat installation and `-Rollback`. Close the app first.
Previous program folders and downloads are retained. Rollback changes program
selection, not user data, and does not undo an in-place self-update in that folder.
Profile paths containing percent signs are rejected because the Windows shortcut
API expands environment-variable syntax; use a portable ZIP in that case.

## Five findings and their fixes

| Priority | Finding | Implemented change |
| --- | --- | --- |
| High | The update feed could select arbitrary origins and follow redirects. | Exact official HTTPS package URL, bounded downloads, strict version/hash/size validation, no redirects, and signed metadata in both installer and updater. Test overrides are restricted to explicit loopback HTTP(S); signatures remain mandatory. GitHub fallback is display-only. |
| Medium | ASCII CMD interpolation mishandled special paths, and a failed copy could restart a partially updated app. | UTF-8 PowerShell with encoded path data, literal file operations, backups before copying, exclusive installation lock, rollback on copy failure and a retained recovery journal. No restart on failure. |
| Medium | Archives lacked application-specific resource bounds and mandatory PRI validation. | Reject unsafe names, links, duplicate paths, packaged artifacts, more than 20,000 entries or over 4 GiB expanded size. Require nonempty EXE and PRI at the root. The installer also verifies native executable version. The original .NET extractor already rejected ordinary path traversal; this is additional hardening. |
| Medium | Map bridge messages and navigation were not restricted to the intended source. | Require the exact local HTTPS map page; block other navigation, frames, popups, downloads and permissions; disable host objects and restrict cross-origin virtual-host access. Existing label escaping remains. |
| Medium | Configuration exports could expose keys and passwords without sufficient warning. | Default to encrypted portable `.mvbackup` exports, with password confirmation, explicit warnings and an opt-in legacy plaintext export path. JSON/CFG import compatibility remains. |

## Five additional measures

1. **Release authenticity.** RSA-3072/SHA-256 detached signatures authenticate raw
   manifest bytes, including the ZIP hash and size. The public key is pinned in the
   app and installer. The private key is outside Git and staging, protected with
   current-user DPAPI and a restricted ACL. Recovery and planned rotation are
   documented in `RELEASING.md`. The initial downloaded script still needs a trusted
   acquisition path. Old clients through 1.5.1 rely on HTTPS for their first upgrade.
   Application/script Authenticode signing is not part of this change.
2. **Runtime maintenance.** Projects and CI target .NET 10. System.IO.Ports and the
   Microsoft DPAPI package use 10.0.12. Self-contained releases must be rebuilt to
   deliver runtime servicing updates. Windows App SDK uses 1.8.260921001. UI freezes
   also occurred in comparison builds using the previous SDK and .NET 8. A fresh
   manual session without Windows UI automation responded better and passed the
   USB message test below; the cause of the earlier freezes is not established.
3. **Sensitive data.** Exports use AES-256-GCM with PBKDF2-SHA256, 600,000 iterations,
   random salt and nonce. Wrong passwords, wrong import categories and tampering are
   rejected. Private key, Wi-Fi and MQTT fields are masked with explicit reveal.
   New message/GPS/waypoint/diagnostic records use current-user DPAPI; legacy files
   migrate on their next write. Unreadable protected files cannot be overwritten or
   appended through the protection helper. DPAPI is tied to the Windows profile;
   export needed data before reinstalling Windows. Old clients cannot read protected
   archives. Filenames and connection preferences remain visible, and malware running
   as the same user is outside this protection boundary.
4. **Transport limits.** TCP remains the firmware's unencrypted protocol; the
   connection page explains the trusted-LAN/VPN requirement. MQTT forces TLS for
   the public Meshtastic broker and `mqtts://` URLs, retains certificate validation
   and rejects ambiguous broker URLs. Custom local brokers can still use plain MQTT
   with a visible warning. Radio payload encryption does not protect the whole
   administrative transport.
5. **Operational recovery.** The updater retains backups, `recovery-plan.json` and
   `apply-result.txt` under the unique Updates directory. `installers/Repair-Update.ps1`
   checks every original-file hash before restoring an explicitly selected install,
   rejects links/escaping paths and takes the exclusive installation lock. Multiple
   app instances prevent updating until all close. A real power failure cannot be
   made transactional by a file-copy updater; an interrupted-copy fixture verifies
   manual recovery without claiming a power-loss durability guarantee.

## Verified checks

- .NET Release/x64 build and self-contained publish succeeded without warnings or errors.
- 171 .NET tests passed: source policy, archive bounds, map origins, encryption,
  local-file migration, signature tampering, MQTT TLS, literal updater paths, locked
  files, simultaneous app instances, rollback and interrupted-copy recovery.
- 31 installer checks passed on Windows PowerShell 5.1 and PowerShell 7, including
  signature failure, actual extraction/shortcuts, repeat install, upgrade, rollback,
  junction rejection and failed activation.
- Release-signing recovery/signing/overwrite-refusal tests passed using an ephemeral
  fixture key. The real portable recovery key was not imported during this review.
- NuGet's advisory check reported no vulnerable packages from the configured source.
  This is a dated advisory result, not proof of no vulnerabilities.
- A full UI-driven update from a separately built 1.5.1 test baseline to 1.5.2 passed:
  signed loopback feed, download, verification, staging, restart and native file
  version 1.5.2.0. The apply result confirmed success and retained the backup.
- An actual WebView2 map loaded Leaflet successfully without nodes; clicking its
  external attribution link kept the trusted local page. No nodes is not a map
  loading failure. Live TCP connection received node data.
- The user confirmed that the manual test app connected to a SenseCAP node over
  COM3, sent messages, and that another node received those messages. The installed
  Microsoft `usbser.inf` driver reported status OK; no extra USB driver was needed.

## Release gates and limitations

The reviewed changes are not yet approved as a public binary release. Earlier live
testing exposed an unresponsive window after connecting/navigating. Responsiveness
improved in a fresh session with Windows UI automation disconnected, and manual
USB messaging passed. This does not establish the cause or prove that the freeze
is fixed. A remote TCP endpoint also closed its connection; USB messaging succeeded
after switching to COM3. BLE discovery worked, but a BLE connection was not tested.

The new installer requires `version.json.sig`, which is not yet present in the
public feed. Do not advertise it as usable until the coordinated release is public.
The existing public 1.5.1 binary does not contain these changes.

The required venes.org publisher preview failed because the reinstalled Windows
account has no stored WinSCP session. Nothing was uploaded. Restore the configured
`Venes.org` session and WinSCP 6.5.6 or newer, then run the full-tree preview again.
A GitHub tag/release, verified artifacts and explicit release authorization remain
separate prerequisites under `RELEASING.md`. No historical ZIPs are to be removed.

## References

- [Microsoft WebView2 security](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [MeshPi installation](https://venes.org/meshpi/)
- [Fjoscam distribution](https://venes.org/fjoscam/)
- [Meshtastic nRF serial guidance](https://meshtastic.org/docs/getting-started/serial-drivers/nrf52/)
