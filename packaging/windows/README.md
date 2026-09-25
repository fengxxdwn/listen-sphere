# Windows installer

P11-C uses Inno Setup 6 and produces
`ListenSphere-Setup-<version>-win-x64.exe`.

Planned layout:

```text
%ProgramFiles%\ListenSphere\
  Controller\
  Sender\
```

Controller is a fixed default component and Sender is optional and off by
default. Start Menu entries
are stable across upgrades; a desktop shortcut is optional and off by default.
The finish page may launch Controller only. The installer must never launch
Sender automatically or configure startup tasks, services, PATH, drivers, or
unrelated registry entries.

The existing applications store settings, trust, logs, and scenes beneath
`%LocalAppData%\ListenSphere\Controller` and
`%LocalAppData%\ListenSphere\Sender`. The installer and uninstaller will not
delete those directories.

Controller listens on dynamically selected TCP/TLS and UDP ports and publishes
`_listensphere._tcp.local` over mDNS. Because fixed, narrow port rules are not
currently possible, the Beta installer will not create firewall rules. Normal
Windows Defender Firewall first-run consent is retained. This decision must be
revisited if stable configurable ports are introduced; the firewall must never
be disabled.

Unsigned Beta installers may trigger Windows SmartScreen. Future signing will
use a CI-provided certificate and `signtool`; no key material belongs in Git.

First generate or verify the P11-B publish directories:

```powershell
./scripts/Publish-ListenSphereWindows.ps1
```

Then build the installer without rebuilding the applications:

```powershell
./scripts/Build-ListenSphereInstaller.ps1
```

The build script reads `eng/ListenSphere.Version.props`, validates both
self-contained publish directories and the approved ICO, locates
`ISCC.exe`, and writes the installer plus the unified `SHA256SUMS.txt` under
`artifacts/packages`. Use `-IsccPath` for a non-standard Inno Setup 6
installation when registry discovery is unavailable.
