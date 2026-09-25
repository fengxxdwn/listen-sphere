# Versioning

`eng/ListenSphere.Version.props` is the product-version source of truth for
Windows assemblies, Android and future packaging workflows.

Current P11 Beta values:

| Field | Value |
|---|---|
| Product / informational / Android `versionName` | `0.6.0-beta.1` |
| Assembly version | `0.6.0.0` |
| File version | `0.6.0.0` |
| Android `versionCode` | `26` |
| Installer version | derived as `0.6.0`; display version is the full product version |

`Directory.Build.props` imports the version file and applies the assembly
metadata. Android's Kotlin build script reads the same XML during configuration.
Packaging scripts must read it rather than duplicate a default version.

The Android repository already used a monotonically increasing integer
(`25` for `0.5.1-stage5`), so P11 continues that compatible rule with `26`.
Increment the code for every APK that may be installed over an earlier build;
never reuse or decrease it. A future release changes the product values and the
Android code in one commit. Protocol and settings-schema versions are separate
compatibility contracts and must not be changed with the product version.
