# MeshVenes
MeshVenes is a community-driven Windows desktop client compatible with Meshtastic radios.
Not affiliated with or endorsed by the Meshtastic organization.

MeshVenes is a self-contained WinUI 3 (.NET 10) application for interacting with Meshtastic nodes over:

Serial (COM)  
TCP/IP  
Bluetooth LE (BLE)

It provides a desktop alternative to the web client, with support for:

Direct Messages and Public Channels  
Traceroute  
Map view  
Device / position / environment metrics  
Full local and remote node configuration (including LoRa settings)  
Import / export of settings

Download & install

The PowerShell installer in `installers/install-windows.ps1` installs the official
Windows x64 ZIP for the current user and creates a Start menu shortcut. It needs
64-bit Windows PowerShell 5.1 or PowerShell 7, Windows 10 version 2004 or later,
and no administrator privileges. Supported Windows 11 is recommended.

From a reviewed source checkout, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installers\install-windows.ps1
```

`-DesktopShortcut` also creates a desktop shortcut; `-Rollback` switches shortcuts
back to the previous installation without downloading anything. Close MeshVenes
first. Repeating installation refreshes shortcuts when the same version is already
installed. Bypass applies only to this PowerShell process, not to machine policy;
organizational execution policy may still prevent execution.

The planned web command, **only after the installer has been explicitly published**
at the stated URL, is:

```powershell
$installer = Join-Path $env:TEMP ('MeshVenes-install-' + [guid]::NewGuid().ToString('N') + '.ps1')
Invoke-WebRequest -UseBasicParsing -Uri https://venes.org/meshvenes/install-windows.ps1 -OutFile $installer -ErrorAction Stop
# Inspect the downloaded script before executing it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
```

The script validates the HTTPS origin, exact ZIP size, SHA-256 and archive paths
before activation. Programs are stored under `%LOCALAPPDATA%\Programs\MeshVenes`;
settings, messages and logs remain under `%LOCALAPPDATA%\MeshVenes`. Previous
releases and download files are retained, so disk usage grows with installations.
The script does not enable autostart or install a background service. Windows
profile paths containing percent signs are rejected to avoid shortcut API expansion;
use the portable ZIP in that case.

The installer and application require a separate RSA/SHA-256 signature over the
exact release manifest bytes. Their pinned public key is independent of the web
server. The script and Windows executable remain unsigned by Authenticode: acquire
the initial installer from a trusted source and inspect it. Versions through 1.5.1
do not verify manifest signatures; that first upgrade still relies on HTTPS. The
new installer requires the coordinated signed feed to be published before use.
See [the security review](docs/security-review-2026-09-29.md).

Sensitive data and backups

Exports default to password-protected `.mvbackup` files (AES-256-GCM). Use a strong,
unique passphrase and keep it separately: a forgotten passphrase cannot be reset.
Existing JSON/CFG imports remain supported; unencrypted export is an explicit
compatibility option. Private keys and network passwords are masked by default.

New message, GPS, waypoint and diagnostic archives use Windows current-user DPAPI.
Legacy archives remain readable and are protected on their next write. Older
MeshVenes versions cannot read the protected format. DPAPI protection depends on
the Windows user profile: copying these files alone to a reinstalled PC or another
account is insufficient. Before reinstalling Windows, export required logs through
the app and create portable encrypted configuration backups. Plain log exports
contain sensitive information. Protection does not defend against malware running
as the same Windows user; filenames and connection preferences remain visible.

Radio TCP connections are unencrypted: use a trusted LAN or VPN. MQTT uses normal
certificate validation and always enables TLS for the public Meshtastic broker
and `mqtts://` addresses. Custom local brokers may still use plain MQTT; enable TLS
when sending credentials over an untrusted network.

Manual installation remains available: download `MeshVenes-<version>-win-x64.zip`
from GitHub Releases, extract it to a writable folder and run `MeshVenes.exe`.
No package registration is required. The map requires Microsoft Edge WebView2
Runtime; the installer does not install that runtime automatically.

MeshVenes is distributed only as an unsigned, self-contained Windows ZIP. MSIX, APPX, and MSIXBundle packages are not built or published because an unsigned package is not a useful installation path for end users. GitHub Actions validates the same self-contained publish flow used for releases, but does not retain artifacts or publish releases.
The full source code is available in the repository if you want to review or build it yourself.
The project is fully open-source and licensed under GPL-3.0.

If you encounter bugs or have feature requests, post them on GitHub. Changes and improvements will be considered based on feedback.

Open-source hobby project focused on providing a full-featured Windows desktop experience for Meshtastic users.


Screenshots  

Send DMs and Public Channel Messages  
<img width="1424" height="960" alt="image" src="https://github.com/user-attachments/assets/6860ac30-35ab-4ed2-8292-e3f04d483d77" />


Nodelist with map view  
<img width="1471" height="1004" alt="image" src="https://github.com/user-attachments/assets/bc591c88-d664-4e3e-a80d-5c4630a72294" />


Settingspage with Remote Admin  
<img width="1471" height="1294" alt="image" src="https://github.com/user-attachments/assets/5e933de1-b6b7-464f-b042-c9c5f57bb831" />


Reporting Bugs

This project is under active development and bugs may occur.  
If you encounter an issue, go to the Issues tab on GitHub and create a new issue.

Please include:

What you expected to happen  
What actually happened  
Steps to reproduce  
Connection type used (COM / TCP / BLE)  
Screenshots and debug logs if possible

License

MeshVenes is licensed under the GNU General Public License v3.0 (GPL-3.0).  
You are free to use, modify and distribute this software under the terms of the GPL-3.0 license.  
See the LICENSE file for full details.

Contributing

Contributions, bug reports and feature suggestions are welcome.  
Feel free to open issues or submit pull requests.
