# MeshVenes
MeshVenes is a community-driven Windows desktop client compatible with Meshtastic radios.
Not affiliated with or endorsed by the Meshtastic organization.

MeshVenes is a self-contained WinUI 3 (.NET 8) application for interacting with Meshtastic nodes over:

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
