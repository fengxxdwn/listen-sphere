# Release checklist

## Automated gates

- [ ] Windows CI
- [ ] Android CI
- [ ] Release Build
- [ ] 212+ .NET tests
- [ ] Android unit tests
- [ ] Protocol tests
- [ ] Architecture tests
- [ ] SHA256 generated

## Package and install verification

- [ ] Controller portable test
- [ ] Sender portable test
- [ ] Installer clean install
- [ ] Installer upgrade install
- [ ] Installer uninstall
- [ ] User settings preserved
- [ ] Controller launches
- [ ] Sender launches
- [ ] Android APK installs

## Interoperability and long-run verification

- [ ] Windows ↔ Android connect
- [ ] Windows ↔ Windows connect
- [ ] Audio playback
- [ ] Microphone
- [ ] Device reconnect
- [ ] Two-hour real-device long-run test
- [ ] Diagnostics export

## Manual scenarios

### Test A — clean install

On a machine without ListenSphere, run Setup, install the default Controller
component, and launch Controller from the finish page and Start Menu.

### Test B — upgrade

Create settings and pairing trust with the previous Beta, install the new Beta
over it, and confirm settings, scenes and trust remain intact without duplicate
shortcuts.

### Test C — uninstall

Uninstall ListenSphere and confirm Program Files and shortcuts are removed while
`%LocalAppData%\ListenSphere` remains.

### Test D — portable

Extract each ZIP on a clean machine and launch its executable without installing
the .NET runtime or the ListenSphere installer.
