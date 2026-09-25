# Windows installer plan

The first installer will use Inno Setup and produce
`ListenSphere-Setup-<version>-win-x64.exe`.

Planned layout:

```text
%ProgramFiles%\ListenSphere\
  Controller\
  Sender\
```

Controller is the default component and Sender is optional. Start Menu entries
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

`ListenSphere.iss` and the build scripts are intentionally deferred to P11-C.
Unsigned Beta installers may trigger Windows SmartScreen. Future signing will
use a CI-provided certificate and `signtool`; no key material belongs in Git.
