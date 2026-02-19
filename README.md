MeshtasticWin – Native Windows (WinUI 3) desktop client for Meshtastic

MeshtasticWin is a self-contained WinUI 3 (.NET 8) application for interacting with Meshtastic nodes over:

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

Go to Releases on GitHub
Download the .zip file under Assets
Extract the zip
Run the .exe directly
No installer is required.

There is also an .msix package generated automatically by GitHub Actions, but it is not code-signed, so Windows will most likely block installation due to certificate trust. The recommended method is to use the zip release.
All release artifacts are built automatically by GitHub Actions. The full source code is available in the repository if you want to review or build it yourself.
The project is fully open-source and licensed under GPL-3.0.

If you encounter bugs or have feature requests, post them on GitHub. Changes and improvements will be considered based on feedback.

Open-source hobby project focused on providing a full-featured Windows desktop experience for Meshtastic users.

Screenshots
Send DMs and Public Channel Messages
<img width="1424" height="892" alt="image" src="https://github.com/user-attachments/assets/a219415b-676c-4822-9aef-6cc0ff6a89a5" />

Nodelist with map view
<img width="1424" height="966" alt="image" src="https://github.com/user-attachments/assets/b54d79d4-5c2c-483b-976b-d9bbc4e3e7f0" />

Settingspage with Remote Admin
<img width="1424" height="966" alt="image" src="https://github.com/user-attachments/assets/d7b2b2ce-1221-408b-983d-bff706762fe8" />




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

MeshtasticWin is licensed under the GNU General Public License v3.0 (GPL-3.0).
You are free to use, modify and distribute this software under the terms of the GPL-3.0 license.
See the LICENSE file for full details.

Contributing

Contributions, bug reports and feature suggestions are welcome.
Feel free to open issues or submit pull requests.
