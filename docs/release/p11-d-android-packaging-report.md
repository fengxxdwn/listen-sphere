# P11-D Android Release Packaging report

## Build identity

- Product version: `0.6.0-beta.1`
- Android versionCode: `26`
- Branch: `codex/p11-d-android-packaging`
- Implementation commit: `002a4f7817686614a29a5e1aa4b5882422c23500`
- applicationId / namespace: `io.listensphere.mobile`
- minSdk: 29
- targetSdk / compileSdk: 34

## Build environment

- JDK: Oracle JDK 17.0.16
- Gradle: 8.10.2
- Android Gradle Plugin: 8.4.0
- Android SDK: 34
- Android SDK Build Tools: 34.0.0
- APK tools: SDK-provided `aapt2` and `apksigner`

The final validation used an existing local Gradle cache in offline mode. The
packaging script defaults to normal Gradle behavior; `-Offline` is an optional
local validation aid and does not hardcode a cache location.

## Build and tests

- `testDebugUnitTest`: 36/36 passed across 17 test files
- `testReleaseUnitTest`: 36/36 passed across 17 test files
- `assembleDebug`: passed
- `assembleRelease`: passed
- Release .NET build: passed with 0 warnings and 0 errors
- Architecture/Governance tests: 14/14
- Core tests: 105/105
- Protocol tests: 14/14
- Windows technical tests: 82/82
- Total .NET tests: 215/215
- `git diff --check`: passed

## Debug APK

- Filename: `ListenSphere-Mobile-debug-0.6.0-beta.1.apk`
- Size: 55,965,645 bytes
- SHA-256: `5e7b1496a19082840cb90dd840d42ba5177078ea4697aa07cd433343f0b859e0`
- Signature: Android Debug signing; `apksigner verify --verbose` passed
- Verified signature scheme: APK Signature Scheme v2
- Purpose: testing only

## Release APK

- Mode: unsigned Release
- Filename: `ListenSphere-Mobile-release-unsigned-0.6.0-beta.1.apk`
- Size: 40,725,113 bytes
- SHA-256: `c3c6aebdbd41c914fa62bbebb0a4a02faf167664f89ae6ad61a7569341dd8261`
- Signature: expected unsigned; `apksigner verify` reported `DOES NOT VERIFY`
- Purpose: validates the Release build type and packaging path; it is not a
  public signed release and must not be presented as one

No signed Release APK was produced because no production release keystore was
provided. Signed output is optional for P11-D when credentials are absent.

## APK metadata and contents

Both APKs were verified with Android SDK Build Tools 34.0.0:

| Field | Verified value |
|---|---|
| package | `io.listensphere.mobile` |
| versionName | `0.6.0-beta.1` |
| versionCode | `26` |
| minSdk | `29` |
| targetSdk | `34` |
| compileSdk | `34` |

Both archives contain `AndroidManifest.xml`, one or more `classes*.dex`
files, and `resources.arsc`. Neither archive contains a JKS, keystore,
keystore properties, private-key file, PFX, P12, or PEM file.

The resource table contains `mipmap/ic_launcher`,
`mipmap/ic_launcher_round`,
`drawable/ic_launcher_adaptive_foreground`, and
`color/ic_launcher_background`. Release resource optimization changes
physical entry names, so validation uses the compiled resource table rather
than relying on a literal ZIP path.

## Signing behavior and safety

Gradle and the packaging script consume:

```text
LISTENSPHERE_ANDROID_KEYSTORE_PATH
LISTENSPHERE_ANDROID_KEYSTORE_PASSWORD
LISTENSPHERE_ANDROID_KEY_ALIAS
LISTENSPHERE_ANDROID_KEY_PASSWORD
```

Implemented behavior:

- All four absent: unsigned Release build succeeds.
- Only one value present: both the packaging script and Gradle fail with the
  incomplete-configuration error.
- `-RequireSigned` with no credentials: fails before building.
- All four present: the configured Release signing path is enabled and the
  script requires `apksigner verify` before assigning the signed filename.
  This path was not executed because no production keystore was supplied.

No production keystore was generated. No password, private key, keystore, or
GitHub Secret value was added to the repository or build logs. The
`LISTENSPHERE_ANDROID_KEYSTORE` GitHub Secret remains a future P11-E
base64-encoded input; Gradle itself accepts only a decoded temporary path.

## Device or emulator validation

`adb devices -l` reported no connected authorized device or emulator.
Installation, launch, and visual Launcher Icon checks were therefore not
executed. The unsigned Release APK was intentionally not installed.

## SHA-256 manifest

`artifacts/packages/SHA256SUMS.txt` was regenerated from current-version
ListenSphere packages that actually exist. It contains the two Android APKs
and the existing Controller ZIP, Sender ZIP, and Windows Installer. It does not
hash itself.

## Git hygiene

- `artifacts/` remains ignored.
- APK files remain ignored.
- `*.jks`, `*.keystore`, and `keystore.properties` are ignored.
- `git ls-files artifacts '*.apk' '*.jks' '*.keystore' keystore.properties`
  returned no tracked files.
- No Android product logic, ordinary CI workflow, Windows packaging, or icon
  source was modified.

## Known risks

- A production signed APK was not exercised because no production keystore was
  supplied. This is permitted for P11-D; a signed release must be validated
  when credentials are later configured.
- Device/emulator installation was unavailable on this machine.
- The unsigned Release APK cannot serve as an installable public production
  release.
- The local SDK emitted its existing SDK XML compatibility warning during
  Gradle configuration; build, tests, APK metadata, and signature validation
  still completed successfully.

## Result

P11-D local acceptance passed for the no-release-keystore case. Debug and
unsigned Release packages are reproducibly named and validated, the signed
Release interface fails safely when incomplete, and no signing secret or build
artifact is tracked. P11-E Packaging CI, AAB, Play Store, tags, and GitHub
Release remain out of scope.
