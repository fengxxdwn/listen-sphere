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

P11-C implements `packaging/windows/ListenSphere.iss` and
`scripts/Build-ListenSphereInstaller.ps1`. Inno Setup consumes the two P11-B
publish directories without rebuilding them. Run:

```powershell
./scripts/Build-ListenSphereInstaller.ps1
```

The script reads product and numeric versions from
`eng/ListenSphere.Version.props`, validates the approved Windows ICO and both
self-contained inputs, locates Inno Setup 6, and produces:

```text
artifacts/packages/ListenSphere-Setup-<version>-win-x64.exe
artifacts/packages/SHA256SUMS.txt
```

The installer uses the stable AppId
`{73B23AE6-AFA2-402B-B0E0-993531F7527B}`. Controller is always installed;
Sender is optional and off by default. Files remain separated under
`%ProgramFiles%\ListenSphere\Controller` and `Sender`. Start Menu names do
not contain a version, the optional desktop shortcut targets Controller, and
the finish action can launch Controller only.

Upgrade and uninstall never target `%LocalAppData%\ListenSphere`. The Beta
installer creates no startup entry, service, driver, PATH change, or firewall
rule. It is currently unsigned and may trigger Windows SmartScreen. See
`packaging/windows/README.md` and the P11-C validation report for details.

## Android packages (P11-D)

P11-D implements `scripts/Build-ListenSphereAndroid.ps1`. It reads
`versionName` and `versionCode` from `eng/ListenSphere.Version.props`, runs
Debug and Release unit tests/builds, verifies APK metadata and content with
Android SDK Build Tools 34.0.0, validates signatures with `apksigner`, and
updates the shared `SHA256SUMS.txt`.

Without release credentials it produces:

```text
ListenSphere-Mobile-debug-<version>.apk
ListenSphere-Mobile-release-unsigned-<version>.apk
```

With all four documented runtime signing variables it additionally supports a
genuinely signed `ListenSphere-Mobile-<version>.apk`. The signed filename is
used only after signature verification succeeds. Partial signing configuration
and `-RequireSigned` without credentials fail immediately.

Debug uses Android Debug signing and is for testing only. An unsigned Release
uses the Release build type but is not a public signed release. AAB and Play
Store publishing remain out of scope.

## Packaging workflow (P11-E)

A separate `.github/workflows/package.yml` uses `workflow_dispatch` only and
must be run from `main`. In GitHub, open **Actions > Packaging > Run workflow**,
select `main`, choose the Android signing mode, and start the run. The default
`unsigned` mode requires no signing secret. The workflow sets up .NET from
`global.json`, Temurin JDK 17, Android SDK 34 / Build Tools 34.0.0, Gradle
Wrapper caching and validation, and Inno Setup major version 6.

Packaging deliberately invokes the same local entry points in this order:

```powershell
./scripts/Publish-ListenSphereWindows.ps1
./scripts/Build-ListenSphereInstaller.ps1
./scripts/Build-ListenSphereAndroid.ps1
./scripts/Test-ListenSpherePackageSet.ps1 -AndroidSigning unsigned
```

Signed mode passes `-RequireSigned` to the Android script and validates the
signed package set. No core packaging command is duplicated in YAML. The final
validator requires exactly the five versioned packages, rejects stale or empty
files, and independently checks every entry in `SHA256SUMS.txt`.

Successful runs upload these artifact groups for 14 days:

- `windows-controller-portable`
- `windows-sender-portable`
- `windows-installer`
- `android-debug-apk`
- `android-release-apk`
- `checksums`

Download all six groups and use `SHA256SUMS.txt` to verify the inner package
files, not GitHub's outer artifact ZIP files. These are GitHub Actions Artifacts,
not a public GitHub Release. The workflow does not create a tag or Release.

The workflow source can be reviewed and tested by ordinary CI on its feature
branch. Its first official manual Packaging run remains pending until the
workflow is merged to the default branch.
