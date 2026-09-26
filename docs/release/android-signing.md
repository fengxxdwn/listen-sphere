# Android signing

Debug APKs use the normal disposable Android debug signing mechanism. No
permanent release keystore is generated or stored in this repository. When no
release credentials are configured, P11-D intentionally creates an unsigned
Release APK whose filename includes `release-unsigned`.

Future release signing uses these GitHub Secrets:

```text
LISTENSPHERE_ANDROID_KEYSTORE
LISTENSPHERE_ANDROID_KEYSTORE_PASSWORD
LISTENSPHERE_ANDROID_KEY_ALIAS
LISTENSPHERE_ANDROID_KEY_PASSWORD
```

`LISTENSPHERE_ANDROID_KEYSTORE` contains base64-encoded keystore bytes. It is
not a Gradle input. A future P11-E release-only workflow will:

```text
base64 GitHub Secret
  -> decode to a temporary .jks file
  -> set LISTENSPHERE_ANDROID_KEYSTORE_PATH
  -> invoke Gradle
  -> verify the APK signature
  -> delete the temporary keystore in always()
```

Gradle consumes only these runtime environment variables:

```text
LISTENSPHERE_ANDROID_KEYSTORE_PATH
LISTENSPHERE_ANDROID_KEYSTORE_PASSWORD
LISTENSPHERE_ANDROID_KEY_ALIAS
LISTENSPHERE_ANDROID_KEY_PASSWORD
```

All four values must be present or all four must be absent. A partial
configuration fails immediately instead of silently producing an unsigned APK.
Passwords are never printed.

Use the local packaging entry point:

```powershell
./scripts/Build-ListenSphereAndroid.ps1
./scripts/Build-ListenSphereAndroid.ps1 -RequireSigned
```

The first command permits the explicitly named unsigned Release APK. The second
fails unless complete credentials are available and the resulting APK passes
`apksigner verify`.

Ordinary CI continues to build and test Debug without requiring release
credentials. P11-D does not configure GitHub Secrets or workflows. Never store
passwords in `gradle.properties`, `local.properties`, or the repository.

Once a release keystore is used for public distribution, back it up securely.
Losing the signing key can prevent future versions from upgrading existing
installations. P11-D does not create a production key.
