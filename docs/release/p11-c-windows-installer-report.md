# P11-C Windows Installer report

## Build identity

- Product version: `0.6.0-beta.1`
- Numeric file version: `0.6.0.0`
- Branch: `codex/p11-c-windows-installer`
- Installer implementation commit: `89855eee123bc5bbf366908e9c6f29350eed9b56`
- Approved icon prerequisite commit: `282a173`
- Inno Setup: 6.7.3
- Stable AppId: `{73B23AE6-AFA2-402B-B0E0-993531F7527B}`

## Installer artifact

- Filename: `ListenSphere-Setup-0.6.0-beta.1-win-x64.exe`
- Size: 106,782,604 bytes
- SHA-256: `d1c30a5bb300a7ba230b6d05213c36dd0e0a8b7424105327b213b183ad7b5fb9`
- Output directory: `artifacts/packages` (ignored by Git)

The unified `SHA256SUMS.txt` contains the Controller ZIP, Sender ZIP, and
installer. The installer Windows properties were verified as:

| Property | Value |
|---|---|
| FileDescription | 聆界 ListenSphere Installer |
| ProductName | 聆界 ListenSphere |
| ProductVersion | 0.6.0-beta.1 |
| FileVersion | 0.6.0.0 |
| OriginalFilename | ListenSphere-Setup-0.6.0-beta.1-win-x64.exe |

## Component and directory layout

Controller is a fixed component and the default setup type installs only
Controller. Sender is optional and is not selected by default.

```text
C:\Program Files\ListenSphere\
  Controller\
    ListenSphere.Controller.exe
  Sender\
    ListenSphere.Sender.exe
```

The complete self-contained publish directories remain separate. The
installer does not configure startup, a service, a driver, PATH, a firewall
rule, or code signing.

## Build and automated tests

- Inno Setup compile: passed
- Release build: passed with 0 warnings and 0 errors
- Architecture/Governance tests: 13/13
- Core tests: 105/105
- Protocol tests: 14/14
- Windows technical tests: 82/82
- Total .NET tests: 214/214
- `git diff --check`: passed

The governance test protects the fixed AppId, central version source, approved
ICO reference, LocalAppData preservation, and prohibited system changes.

## Clean install

The machine had no existing installer registration, Program Files directory,
or ListenSphere Start Menu group. A silent elevated install using the default
Controller setup type completed with exit code 0.

Verified:

- Controller executable exists under `Program Files`.
- Sender is absent by default.
- Start Menu contains `聆界` and does not contain `聆界发送端`.
- No desktop shortcut is created by default.
- Exactly one uninstall registration exists.
- Display version is `0.6.0-beta.1`.
- Existing Controller and Sender LocalAppData remained present.

The installed Controller displayed the
`聆界 · ListenSphere Controller` window, remained responsive, accepted a
normal window close, and exited with code 0.

## Sender optional component

The same installer was rerun with the full setup type. It completed with exit
code 0.

Verified:

- Sender executable exists under `Program Files`.
- Start Menu contains `聆界发送端`.
- The shortcut targets the installed Sender executable.
- Sender displayed the `聆界 · ListenSphere Sender` window, remained
  responsive, accepted a normal window close, and exited with code 0.

## Desktop and Start Menu shortcuts

The desktop task is unchecked by default. When explicitly selected, it created
`C:\Users\Public\Desktop\聆界.lnk`, targeting Controller only. The stable
Start Menu group contained exactly the Controller and Sender shortcuts after
the optional Sender installation.

## Reinstall and upgrade behavior

A same-version reinstall simulated the overwrite path. The installer reused
the fixed AppId and left exactly one uninstall registration. It did not create
duplicate Start Menu entries. Both application directories remained valid.

Future versions must retain the same AppId for this behavior.

## Uninstall and user data preservation

The standard elevated uninstaller completed with exit code 0.

Verified after uninstall:

- `C:\Program Files\ListenSphere` was removed.
- The Start Menu group was removed.
- The optional desktop shortcut was removed.
- The installer registry record was removed.
- Controller and Sender LocalAppData directories remained.
- Seven sampled user-data files, including identity, settings, trust, and
  dedicated preservation markers, retained identical SHA-256 hashes across
  reinstall and uninstall.

The installer contains no `[UninstallDelete]` entry and never targets
`%LocalAppData%\ListenSphere`.

## Icon verification

The installer consumes
`assets/branding/windows/ListenSphere.ico`. Extracted 48 px icons from the
installer and Controller executable produced the same rendered SHA-256:

`e769b4fca7466d612e5ca6b3b3453c64be60e46ff57bc97808d6e4c36ad25a69`

Installed Controller and Sender executables both exposed the approved icon.
Start Menu and desktop shortcuts use the respective executable icons.

## Known risks

- The Beta installer is unsigned and may trigger Windows SmartScreen.
- The installed Inno Setup distribution did not include a Simplified Chinese
  message file. Product, component, task, shortcut, and launch labels are
  bilingual/Chinese, while standard wizard messages use English.
- No firewall rule is installed because ListenSphere currently uses dynamic
  TCP/UDP ports. Windows Defender Firewall may show its normal first-run
  consent prompt.
- The P11-C branch currently includes the approved icon prerequisite commit;
  merge that prerequisite to `main` before reviewing the P11-C-only diff.

## Result

P11-C implementation and local acceptance testing passed. It provides a
repeatable unsigned Windows installer build and verified install, optional
component, reinstall, launch, shortcut, uninstall, and user-data-preservation
behavior. Packaging CI, Android packaging, signing, and public release remain
out of scope.
