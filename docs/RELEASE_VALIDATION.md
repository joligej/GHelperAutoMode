# Release validation

This is the test record for GHelperAutoMode 5.0.0. It describes what was checked on the development machine and what still depends on the ASUS model, firmware, and G-Helper installation.

## Build environment

- Windows 11 x64, build 10.0.26200.0
- .NET SDK 10.0.400
- Target: `net10.0-windows`, `win-x64`
- WiX Toolset SDK 6.0.2
- WingetCreateCLI 1.12.13.0
- G-Helper 0.279, source revision `5b0029057541bff4cbd025e1a01e0641e11bd64e`

The G-Helper revision was checked at its profile-hotkey, config, Aura, LampArray, Dynamic Lighting, exit-event, and scheduled-task paths. In particular, session events can reapply Aura without consulting `skip_aura`, which is why Windows-owned lighting recovery releases G-Helper before asking Windows to reacquire the device.

## Automated checks

The strict build checks these points before compiling:

- project, assembly, file, and config-schema versions;
- exact example-config parity for thresholds, lighting, telemetry, and logging;
- threshold ordering, hysteresis bands, and downshift ceilings;
- a display state other than `On` cannot reach `SendInput` or `keybd_event`;
- lighting code contains no keyboard-input API;
- Windows-owned lighting enforces foreground-takeover policy and ordered ownership recovery;
- startup uses an interactive Task Scheduler token at `HighestAvailable`, not HKCU Run;
- uninstall has owned-task cleanup, process-specific cross-session events, a UIPI-approved window message, and an exact-path termination fallback;
- the MSI is dual-purpose, has upgrade and uninstall handling, and registers its install location;
- Setup reads `TokenElevation` and passes one explicit MSI scope before Windows Installer starts;
- every maintained repository file appears exactly once in `SOURCE_MANIFEST.sha256` with the right hash.

`dotnet format --verify-no-changes` is run separately for the application source. Release, publish, native-AOT setup, and WiX builds treat warnings as errors. WiX also runs the Windows Installer ICE validation supplied by its build.

## Configuration and runtime checks

An existing schema 7 configuration was opened by v5 and migrated to schema 8. The selected `GHelperWindowsAccent` mode and existing preferences were retained, and a `config.pre-v8.20260903-194717.json` copy was written first.

The settings model/UI parity check found no missing non-schema fields across `AutoModeConfig`, `KeyboardLightingConfig`, `TelemetryConfig`, `LoggingConfig`, `ThresholdConfig`, and `AppRule`. Settings edits use a detached copy; invalid cross-field relationships are rejected before the config is written or the live engine is reloaded.

The form was also opened on an STA thread and every tab was rendered at 980 by 760 pixels. The test confirmed five tabs, four application-rule columns, five existing rules, scrollable long pages, readable labels, and the retained `GHelperWindowsAccent` selection. This render caught and corrected an enum-binding reset before release.

The machine-installed v5 application started against the existing G-Helper installation and relaunched to G-Helper's higher integrity level as intended. A settled ten-second sample of that active instance recorded:

- 0.219 CPU seconds;
- 98.6 MiB working set;
- 39.2 MiB private memory;
- 454 handles.

This is a short idle observation on one machine, not a benchmark. CPU use varies with the configured poll interval, providers, and lighting mode.

## Installer scope and removal

The same token-aware Setup executable was tested twice.

Normal launch:

- exit code 0;
- application installed only to `%LOCALAPPDATA%\Programs\GHelperAutoMode`;
- Start menu shortcut installed only for the current account;
- Installed Apps reported version 5.0.0 and the per-user path;
- existing `config.json` hash unchanged;
- no extracted `GHelperAutoMode-*.msi` remained in `%TEMP%`.

Elevated launch:

- exit code 0;
- application installed only to `C:\Program Files\GHelperAutoMode`;
- Start menu shortcut installed for all users;
- Installed Apps reported version 5.0.0 and the machine path;
- existing `config.json` hash unchanged;
- no extracted MSI remained in `%TEMP%`.

Both uninstall routes returned 0. The running tray process stopped, installed files and the matching shortcut were removed, and the personal config hash stayed unchanged. The per-user test deliberately left the tray at G-Helper's higher integrity level before uninstalling from a normal process; the approved window message still reached the tray, and cleanup completed. A per-user uninstall also removed the existing `GHelperAutoMode_<SID>` login task.

These tests caught and removed an earlier design that tried to change MSI scope after initialization. Windows Installer had already selected its directories at that point, which could create a mixed-scope installation. The released MSI has no late scope-changing custom action; Setup makes the choice before invoking it.

## Final artifacts

The exact values below are filled from the final clean build:

| File | Size | SHA-256 |
| --- | ---: | --- |
| `GHelperAutoMode-5.0.0-win-x64.exe` | 116,385,970 bytes | `F40680ED9EE8CFA569CB792722713A1005A306A762966E7395830E0B0EF1F2E3` |
| `GHelperAutoMode-5.0.0-win-x64-setup.exe` | 39,077,376 bytes | `7A4017FF1E9E572BEE94E2EF1F0AB1E0F442FBC982E381D1B8B785EDECCE6F73` |
| `GHelperAutoMode-5.0.0-win-x64.msi` | 37,552,128 bytes | `E3792637E45A2CE7BA49E7B3C252EBF2C7A7FA8885AC2AEAB7F8DE4B3FF0FF53` |

The MSI ProductCode is `{EDFEB7D7-FF3E-4258-A9A1-B41B51B98DC3}`. The release binaries are unsigned. `SHA256SUMS.txt` is published beside them and also records the final ZIP, whose hash cannot be embedded in a document inside that same archive.

The application hash remained unchanged across clean publishes. WiX assigns package-level build metadata to each newly produced MSI, so the installer and its embedded-setup hash describe the published build rather than a general promise that later rebuilds will be byte-identical.

## Checks that remain hardware-dependent

No registry or process test can prove the final LED color. On a new ASUS model, confirm the selected lighting owner after sign-in, lock/unlock, and sleep/resume. Also confirm Silent, Balanced, and Turbo in G-Helper itself before relying on AutoMode's hotkeys.

For the original display-wake failure, the invariant is stricter than a hardware smoke test: `Off`, `Dimmed`, and `Unknown` select the direct `WM_HOTKEY` route before any input API is reachable. If no G-Helper window accepts that route, the request is deferred rather than converted into a keypress.
