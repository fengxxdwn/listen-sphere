# Packaging design

## Windows portable packages (P11-B)

P11-B implements `scripts/Publish-ListenSphereWindows.ps1` as the local package
entry point. It reads the version from `eng/ListenSphere.Version.props`, cleans
only the ignored packaging output, restores, builds and tests the solution, then
publishes Controller and Sender independently with:

```text
Configuration=Release
RuntimeIdentifier=win-x64
SelfContained=true
PublishSingleFile=false
PublishTrimmed=false
```

Clean inputs and outputs will live under `artifacts/publish`; ZIPs and checksums
will live under `artifacts/packages`. Expected names are:

```text
ListenSphere-Controller-win-x64-<version>.zip
ListenSphere-Sender-win-x64-<version>.zip
SHA256SUMS.txt
```

Standard publish directories and PDB files are retained for native dependency
visibility and Beta diagnostics. The script validates executable metadata and
self-contained runtime files before creating root-level ZIP contents and
`SHA256SUMS.txt`. Single-file and trimming require separate validation and are
not enabled in P11.

Run the complete flow from the repository root:

```powershell
./scripts/Publish-ListenSphereWindows.ps1
```

`-Configuration`, `-Runtime` and `-SkipTests` are available for controlled
local use. Release candidates must use the defaults and must not skip tests.

## Windows installer (P11-C)

Inno Setup will consume the two publish directories without rebuilding them.
It will install under `%ProgramFiles%\ListenSphere`, create stable Start Menu
shortcuts, offer an off-by-default desktop shortcut, preserve LocalAppData on
upgrade/uninstall, and optionally launch Controller only. See
`packaging/windows/README.md` for security and firewall decisions.

## Android packages (P11-D)

Normal CI keeps `assembleDebug testDebugUnitTest`. Packaging adds
`assembleRelease`; without signing secrets it may retain the unsigned release
APK. Uploaded files will be renamed to
`ListenSphere-Mobile-debug-<version>.apk` and
`ListenSphere-Mobile-<version>.apk`. AAB/Play Store publishing is out of scope.

## Packaging workflow (P11-E)

A separate `.github/workflows/package.yml` will initially use
`workflow_dispatch` only. It will set up .NET from `global.json`, build/test,
publish both Windows apps, invoke Inno Setup, set up JDK 17/Android SDK 34,
build/test Android, create one `SHA256SUMS.txt`, and upload these artifact groups:

- `windows-controller-portable`
- `windows-sender-portable`
- `windows-installer`
- `android-debug-apk`
- `checksums`

Signed Android and tag-triggered GitHub Release jobs are future opt-in work.
No workflow will create a public Release without explicit approval.
