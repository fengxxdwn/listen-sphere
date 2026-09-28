# P11-F release candidate report

## Candidate identity

- Product: 聆界 ListenSphere
- Version: `0.6.0-beta.1`
- Android versionCode: `26`
- Source commit: `4d44f5cbbafddc46b643534dd8a7ffa03b144aed`
- Packaging run: `36302397838`
- Packaging mode: unsigned
- Date: 2026-09-28
- RC tester: Codex automated checks; manual tester pending

The candidate under test is the downloaded output of the Packaging run above,
not a local build. A product-code change invalidates this identity and requires
a new Packaging run plus affected RC retesting.

## Artifact evidence

| Filename | Size (bytes) | SHA-256 | Automated validation |
| --- | ---: | --- | --- |
| `ListenSphere-Controller-win-x64-0.6.0-beta.1.zip` | 77,521,120 | `f5c42f67f30848fc41664d09556ffb0b9bd0e88c210ae07fe8d72997b72a73ec` | PASS |
| `ListenSphere-Sender-win-x64-0.6.0-beta.1.zip` | 77,187,577 | `6a0851b9bb98783a8eb71d5e770cc2065826d55fc6404897b648e1234970c53f` | PASS |
| `ListenSphere-Setup-0.6.0-beta.1-win-x64.exe` | 106,898,730 | `1682e85923d2090a80b6364a5443ae1f8bcd6d9286af4f64181834ffde297ac3` | PASS |
| `ListenSphere-Mobile-debug-0.6.0-beta.1.apk` | 55,965,485 | `2442762ee9aa0a02fdf918a404d642d2ab178b167343c3b3132671e4bdcba38a` | PASS |
| `ListenSphere-Mobile-release-unsigned-0.6.0-beta.1.apk` | 40,725,113 | `c3c6aebdbd41c914fa62bbebb0a4a02faf167664f89ae6ad61a7569341dd8261` | PASS |

AUTOMATED: the expected commit and Packaging run must match the committed RC
manifest. Each artifact's actual SHA-256 must then match both `SHA256SUMS.txt`
and that committed manifest before filenames, uniqueness, non-zero sizes,
portable ZIP layout, Windows version metadata/icons, installer metadata, APK
contents and metadata, Debug signature, and expected unsigned Release state are
validated by `scripts/Test-ListenSphereReleaseCandidate.ps1`.

Additional verification on the P11-F branch:

- Candidate remote Windows CI run `36302384981`: PASS.
- Candidate remote Android CI run `36302385054`: PASS.
- Candidate remote unsigned Packaging run `36302397838`: PASS.
- Android test files: 17; Debug unit tests: 36/36; Release unit tests:
  36/36 in the candidate Packaging run.
- Local .NET Release build: PASS, 0 warnings and 0 errors.
- Local .NET tests: PASS, 217/217 (Architecture 16, Core 105,
  Protocol 14, Windows Technical 82).
- Local Android unit-test rerun: NOT TESTED. Dependency resolution could not
  retrieve the pinned AGP 8.4.0 plugin, and it was not present in the local
  offline cache. The candidate's remote Android CI and Packaging test gates are
  the Android unit-test evidence; no local result is claimed.

The unsigned Release APK is not a public user-installable Android package and
is not the Android device test object. Device verification uses the Debug APK
until a genuine production-signed Release APK exists.

## Environment matrix

| Environment | Role | Result |
| --- | --- | --- |
| Current Windows development host | Automated artifact inspection with Android SDK Build Tools 34.0.0 | PASS |
| Clean Windows x64 machine / VM | Portable and installer acceptance | PENDING MANUAL VERIFICATION |
| Second Windows machine | Sender and Windows ↔ Windows interoperability | PENDING MANUAL VERIFICATION |
| Android physical device | Debug APK install, permissions, interoperability, and audio | PENDING MANUAL VERIFICATION |

No device serial, IMEI, account identifier, or secret is recorded in this report.

## RC test matrix

| Test | Mode | Status | Evidence / remaining work |
| --- | --- | --- | --- |
| Artifact checksum and identity | AUTOMATED | PASS | Downloaded run output matches the manifest. |
| Controller portable structure | AUTOMATED | PASS | Required self-contained runtime files are at ZIP root. |
| Sender portable structure | AUTOMATED | PASS | Required self-contained runtime files are at ZIP root. |
| Windows executable metadata/icon | AUTOMATED | PASS | Controller and Sender version resources and associated icons validated. |
| Installer metadata | AUTOMATED | PASS | Product fields validated; no default Inno placeholders. |
| Android metadata | AUTOMATED | PASS | Package, versions, SDK levels, and build-type debuggability validated. |
| Android signing state | AUTOMATED | PASS | Debug signature valid; Release signature invalid as expected. |
| Controller portable clean launch | MANUAL | PENDING | Requires clean Windows x64. |
| Sender portable clean launch | MANUAL | PENDING | Requires clean Windows x64. |
| Installer clean install / optional Sender / reinstall | MANUAL | PENDING | Requires clean Windows x64 with installer interaction. |
| Installer uninstall / LocalAppData preservation | MANUAL | PENDING | Hash real user data before and after reinstall/uninstall. |
| Windows ↔ Windows | MANUAL | PENDING | Test mDNS, pairing, TLS, UDP audio, controls, and mute. |
| Windows ↔ Android | MANUAL | PENDING | Install Debug APK and test normal permission flows and audio. |
| Reconnect and process restarts | MANUAL | PENDING | Exercise Wi-Fi/client, Controller, Sender, and Android restarts. |
| Diagnostics export and privacy review | MANUAL | PENDING | Export during a real connection and inspect content. |
| Two-hour real-device soak | MANUAL | PENDING | Active audio traffic and periodic observations required. |

## Two-hour real-device soak

- Topology: PENDING
- Duration: PENDING (must be at least two hours)
- Start / end: PENDING
- Memory start / end: PENDING
- Handles start / end: PENDING
- CPU observations: PENDING
- Packet loss / RTT / buffer / clock drift observations: PENDING
- Reconnects: PENDING
- Crashes: PENDING
- Fatal errors: PENDING
- Audio observation before / during / after: PENDING
- Buffer/drift observation: PENDING

The pass condition is bounded behavior rather than perfectly constant resource
usage. This stage does not claim sample-accurate synchronization, a common
absolute playout time, or automatic sound-card intrinsic-latency calibration.

## Known limitations and risks

- The Windows Beta installer is unsigned and may trigger SmartScreen. Security
  policy must not be disabled to suppress the warning.
- The first Controller network use may trigger the normal Windows Defender
  Firewall consent flow. The product must not disable the firewall or add a
  broad rule.
- Android AudioPlaybackCapture policy, protected/DRM content, and app opt-out
  can prevent capture independently of ListenSphere.
- Android public production release still requires a genuine production-signed
  Release APK and separate signature/installation verification.

## RC blockers

No blocker was found by automated artifact inspection. Manual scenarios have
not yet been executed, so this is not evidence that no real-device blocker
exists. Any launch failure, destructive uninstall, core connectivity failure,
repeatable crash, two-hour audio failure, or serious security issue must be
recorded as `RC-BLOCKER` with reproduction steps and evidence; product fixes
belong on a separate branch and require a new candidate package set.

## Final status

**RC VERIFICATION INCOMPLETE**

Clean Windows verification, Windows ↔ Windows, Windows ↔ Android, reconnect,
Diagnostics, and the two-hour real-device soak remain pending. No tag or GitHub
Release may be created from this report yet.
