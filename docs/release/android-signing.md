# Android signing design

Debug APKs use the normal disposable Android debug signing mechanism. No
permanent release keystore is generated or stored in this repository.

Future release signing uses these GitHub Secrets:

```text
LISTENSPHERE_ANDROID_KEYSTORE
LISTENSPHERE_ANDROID_KEYSTORE_PASSWORD
LISTENSPHERE_ANDROID_KEY_ALIAS
LISTENSPHERE_ANDROID_KEY_PASSWORD
```

`LISTENSPHERE_ANDROID_KEYSTORE` contains base64-encoded keystore bytes. A
release-only workflow will decode it to a temporary runner path, expose values
to Gradle only through environment variables, build the APK, verify its
signature, and remove the temporary file in an `always()` cleanup step. Logs and
artifacts must not include secrets or the keystore.

Ordinary CI must succeed when these secrets are absent and will continue to
produce the debug APK. A manually invoked unsigned release build may produce
`app-release-unsigned.apk`; it must never be presented as a signed public
release. Tag signing remains disabled until the keystore owner approves the
secret setup and recovery procedure.
