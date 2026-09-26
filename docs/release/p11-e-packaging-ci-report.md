# P11-E Packaging CI implementation report

## Build identity

- Branch: `codex/p11-e-packaging-ci`
- Baseline main: `9beff6dadb3b06f62e2004d5ee54685811fb489d`
- Implementation commit: pending until branch completion
- Product version source: `eng/ListenSphere.Version.props`
- Product version: `0.6.0-beta.1`
- Android versionCode: `26`

## Workflow design

- Workflow: `.github/workflows/package.yml`
- Display name: `Packaging`
- Trigger: manual `workflow_dispatch` only
- Branch guard: `refs/heads/main`
- Runner: `windows-latest`
- Timeout: 90 minutes
- Permissions: `contents: read`
- Checkout credentials: disabled after checkout

The workflow does not respond to pushes, pull requests, schedules, tags, or
releases. It creates GitHub Actions Artifacts only and has no repository write
permission.

## Toolchain

- .NET SDK: selected from `global.json`
- Java: Temurin JDK 17
- Android SDK platform: 34
- Android SDK Build Tools: 34.0.0
- Gradle: repository Wrapper 8.10.2 through `gradle/actions/setup-gradle`
- Installer: detected Inno Setup major version 6; patch version is recorded but
  not hardcoded

## Packaging order

1. `Publish-ListenSphereWindows.ps1`
2. `Build-ListenSphereInstaller.ps1`
3. `Build-ListenSphereAndroid.ps1`
4. `Test-ListenSpherePackageSet.ps1`
5. Artifact upload

The workflow does not pass `-SkipTests`. Windows and Android tests therefore
remain part of the packaging scripts' delivery gates. Android runs last so its
checksum regeneration sees all Windows and Android packages.

## Android signing modes

`android_signing=unsigned` is the default and requires no Secret. It produces
the Debug APK plus an explicitly named unsigned Release APK.

`android_signing=signed` requires all four documented GitHub Secrets. Missing
or invalid inputs fail the run; there is no unsigned fallback. The Android
packaging script receives `-RequireSigned`, and its existing `apksigner`
verification controls whether the signed filename can be produced.

No production keystore was generated or configured during P11-E. The signed
path is implemented but is not claimed as remotely exercised.

## Secret lifecycle

In signed mode only, the workflow decodes the base64 keystore Secret to
`$RUNNER_TEMP/listensphere-release.jks`. Runtime signing variables are scoped
to the Android signed build step. An `if: always()` cleanup step removes the
temporary file and fails if it remains. Passwords and base64 content are never
printed or uploaded.

## Artifact groups

- `windows-controller-portable`
- `windows-sender-portable`
- `windows-installer`
- `android-debug-apk`
- `android-release-apk`
- `checksums`

Every upload uses `if-no-files-found: error` and 14-day retention. Raw publish
directories, build logs, local properties, and signing material are excluded.

## Final package validation

`scripts/Test-ListenSpherePackageSet.ps1` reads the central version metadata
and requires exactly five non-empty ListenSphere packages for the selected
signing mode. It rejects stale or opposite-mode APKs, requires exactly five
relative and unique checksum entries, prevents the manifest from hashing
itself, and recomputes every SHA-256 value.

## Governance and validation status

- Lightweight architecture governance covers manual-only triggering,
  read-only permissions, approved script orchestration, central version use,
  strict uploads, cleanup, and prohibited Release/tag operations.
- Existing `windows-ci.yml` and `android-ci.yml` are unchanged.
- Local build, tests, hygiene checks, and script regression results are filled
  in at branch completion.
- Remote Packaging run: pending until workflow is merged to main.

## Known risks

- GitHub does not offer the new manual workflow as an official default-branch
  run before merge, so branch completion is not full P11-E acceptance.
- The runner's exact Inno Setup 6 patch version is intentionally discovered at
  runtime rather than frozen.
- Production Android signing remains untested until the repository owner
  deliberately configures the four production Secrets and selects signed mode.

## Result

P11-E branch implementation is prepared for ordinary CI and source review.
Full acceptance requires merge to `main`, one successful unsigned manual
Packaging run, six downloadable artifact groups, and independent checksum
verification of the downloaded inner package files.
