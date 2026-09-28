# Release checklist — 0.6.0-beta.1 RC

Status values are `[PASS]`, `[FAIL]`, `[PENDING]`, and `[N/A]`. Automated
evidence applies only to source commit
`4d44f5cbbafddc46b643534dd8a7ffa03b144aed` and Packaging run
`36302397838`.

## Automated gates

- [PASS] Windows CI — successful checks on the candidate source commit.
- [PASS] Android CI — successful checks on the candidate source commit.
- [PASS] Packaging CI — unsigned run `36302397838` completed successfully.
- [PASS] Release build and .NET tests — Packaging run retained the full build/test gates.
- [PASS] Android unit tests — Packaging run retained Debug and Release unit-test gates.
- [PASS] Protocol and architecture tests — included in the Windows test gate.
- [PASS] Five candidate packages exist, are non-empty, and their actual hashes match both `SHA256SUMS.txt` and the committed RC manifest.
- [PASS] Windows portable structure and executable metadata validation.
- [PASS] Installer version-resource metadata validation.
- [PASS] Android package metadata, Debug signature, and expected unsigned Release state.
- [PASS] Git index excludes RC artifacts, APKs, keystores, and signing secrets.

## Clean Windows and installer verification

- [PENDING] Controller portable launch on clean Windows x64 without a separately installed .NET Desktop Runtime.
- [PENDING] Sender portable launch on clean Windows x64 without a separately installed .NET Desktop Runtime.
- [PENDING] Installer default clean install (Controller only; no default desktop shortcut).
- [PENDING] Optional Sender installation and Start Menu shortcut.
- [PENDING] Same-version reinstall without duplicate entries or shortcuts.
- [PENDING] Uninstall removes program files, shortcuts, and registration.
- [PENDING] `%LocalAppData%\ListenSphere` survives reinstall and uninstall with hashes unchanged.
- [PENDING] SmartScreen and Windows Defender Firewall behavior observed without weakening security policy.

## Device, interoperability, and recovery verification

- [PENDING] Windows Controller ↔ Windows Sender discovery, pairing, TLS control, and UDP audio.
- [PENDING] Windows Controller ↔ Android Debug APK discovery, pairing, TLS control, and audio.
- [PENDING] Android microphone, notification (where applicable), and MediaProjection permissions through normal UI consent.
- [PENDING] Sustained audio playback, volume, mute, microphone/capture path, and subjective audio checks.
- [PENDING] Wi-Fi/client disconnect and reconnect restores audio without clearing configuration.
- [PENDING] Controller restart recovery.
- [PENDING] Windows Sender restart recovery.
- [PENDING] Android application restart recovery.
- [PENDING] Windows output switch / USB audio hotplug when hardware is available.
- [PENDING] Diagnostics export opens successfully and contains no passwords, private keys, keystores, or complete secrets.
- [PENDING] Error-log review for fatal exceptions, resource exhaustion, and high-rate repeated errors.
- [PENDING] Two-hour real-device soak with active audio traffic and periodic resource/drift observations.

## Deferred / not applicable to this unsigned candidate

- [N/A] Production-signed Android Release installation — no production keystore is configured; public Android distribution still requires a real signed Release APK.
- [N/A] Git tag and GitHub Release creation — explicitly outside P11-F.

The candidate cannot be marked `RC READY` until every required manual item above
passes and no unresolved `RC-BLOCKER` remains. Any product-code change invalidates
run `36302397838` and requires a new Packaging run and affected RC retesting.
