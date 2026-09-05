# Release validation

This is the test record for GHelperAutoMode 5.0.1. It separates checks completed for the published build from behavior that still depends on the ASUS model, firmware, and G-Helper installation.

## Build environment

- Windows 11 x64, build 10.0.26200.0
- .NET SDK 10.0.400, pinned in `global.json`
- Target: `net10.0-windows`, `win-x64`
- WiX Toolset SDK 6.0.2
- Windows Package Manager 1.29.290 locally; Microsoft's validation runner used 1.29.250
- G-Helper 0.279, source revision `5b0029057541bff4cbd025e1a01e0641e11bd64e`

The G-Helper revision was checked at its profile-hotkey, config, Aura, LampArray, Dynamic Lighting, exit-event, and scheduled-task paths. Session events can reapply Aura without consulting `skip_aura`, which is why Windows-owned lighting recovery releases G-Helper before asking Windows to reacquire the device.

## Automated checks

The strict build verifies these points before compiling:

- project, assembly, file, changelog, and config-schema versions agree;
- exact example-config parity for thresholds, lighting, telemetry, and logging;
- threshold ordering, hysteresis bands, and downshift ceilings;
- a display state other than `On` cannot reach `SendInput` or `keybd_event`;
- lighting code contains no keyboard-input API;
- Windows-owned lighting enforces foreground-takeover policy and ordered ownership recovery;
- startup uses an interactive Task Scheduler token at `HighestAvailable`, not HKCU Run;
- uninstall has owned-task cleanup, process-specific cross-session events, a UIPI-approved window message, and an exact-path termination fallback;
- the MSI is dual-purpose, has major-upgrade and uninstall handling, keeps a stable UpgradeCode, and does not pin one ProductCode across releases;
- Setup reads `TokenElevation` and passes one explicit MSI scope before Windows Installer starts;
- every maintained repository file appears exactly once in `SOURCE_MANIFEST.sha256` with the right hash.

The application was also built with all available .NET analyzers enabled, code-style enforcement, and warnings treated as errors. `dotnet format --verify-no-changes` runs separately. Release, publish, native-AOT Setup, and WiX builds treat warnings as errors; WiX also runs Windows Installer ICE validation. The public CI workflow repeats the strict application build, formatting check, and installer build on a clean Windows runner.

## Configuration and runtime

The settings-model parity check covers `AutoModeConfig`, `KeyboardLightingConfig`, `TelemetryConfig`, `LoggingConfig`, `ThresholdConfig`, and `AppRule`. Settings edits use a detached copy; invalid cross-field relationships are rejected before the config is written or the live engine is reloaded.

The form and runtime implementation did not change between 5.0.0 and 5.0.1; this patch only changes repository layout, release checks, and installer metadata. The 5.0.0 form test opened the settings window on an STA thread and rendered every tab at 980 by 760 pixels. It confirmed five tabs, four application-rule columns, five existing rules, scrollable long pages, readable labels, and retained lighting selection.

The final 5.0.1 executable started from `dist\self-contained` with the existing schema 8 configuration and registered login task. A settled ten-second sample recorded:

- 0.328 CPU seconds;
- 103.2 MiB working set;
- 46.9 MiB private memory;
- 447 handles.

This is a short idle observation on one machine, not a benchmark. CPU use varies with the configured poll interval, providers, and lighting mode. The configuration hash remained `C3EE21D22EE5DCACE815EA6E7AB56FDCFCC9258CF5DD9B8688BFD9FE97B04C7A` throughout the installer tests.

## Installer scope, upgrade, and removal

The 5.0.1 MSI has ProductCode `{3CE5B0F1-1FBD-4E9E-BCC1-D8E33845C9BA}` and retains UpgradeCode `{C3E10D0F-8FBD-4C20-BDBF-9E7D0F919F1A}` from 5.0.0. An actual per-user 5.0.0 installation was upgraded with the 5.0.1 MSI. Windows Installer returned 0, removed the old ProductCode, registered exactly one 5.0.1 entry, retained the per-user install directory, and installed an executable with file version 5.0.1.0.

The token-aware 5.0.1 Setup executable was then tested twice:

- a scheduled task with `RunLevel Limited` returned 0 and installed only to `%LOCALAPPDATA%\Programs\GHelperAutoMode`;
- an elevated launch returned 0 and installed only to `C:\Program Files\GHelperAutoMode`.

The raw MSI was also installed directly in both scopes. Every uninstall returned 0, removed the installed files and matching registration, and left no user or machine installation behind. Setup left no extracted MSI in `%TEMP%`. The personal configuration hash was unchanged.

## WinGet manifest and Microsoft validation

The initial 5.0.0 submission is [microsoft/winget-pkgs#429318](https://github.com/microsoft/winget-pkgs/pull/429318). Its schema, URLs, policy, catalog-content, installer-scan, and CLA checks passed. Microsoft's user-scope installation check failed before launching Windows Installer because the manifest declared `ElevationRequirement: elevationProhibited` while the validation command itself ran from an administrator context. The WinGet 1.29.250 log returned `0x8A150056`: “The installer cannot be run from an administrator context.”

The 5.0.1 manifest removes that optional declaration from the user entry. The MSI still receives `ALLUSERS=2 MSIINSTALLPERUSER=1`; a matching elevated-context test installed to the per-user directory, and the MSI log identified it as a dual-mode package installed per-user. The machine entry retains `ElevationRequirement: elevationRequired` and `ALLUSERS=1`.

The final three-file schema 1.12 manifest points at the exact release MSI listed below and passes `winget validate` without warnings. An elevated local WinGet reproduction selected the corrected user entry, downloaded and verified the published 5.0.0 MSI, and reached the expected `ALLUSERS=2 MSIINSTALLPERUSER=1` launch. This machine's attachment policy then displayed an interactive Open File security warning for the unsigned downloaded MSI, so the unattended run was stopped; the same MSI and arguments were validated directly as described above. Microsoft's clean-runner result remains the authoritative community-submission test. G-Helper is not declared as a package dependency, avoiding a second package-managed copy over an existing standalone setup.

## Final artifacts

The final application and installer values are:

| File | Size | SHA-256 |
| --- | ---: | --- |
| `GHelperAutoMode-5.0.1-win-x64.exe` | 116,385,970 bytes | `9D7DAF309F842129E7FB87BC483049818CC3BC0277687164BB65096BD2502C12` |
| `GHelperAutoMode-5.0.1-win-x64-setup.exe` | 39,085,568 bytes | `22ED9675F5DE0676BC155F27495815CF3E8DEE56FFFBB4A46C5763D95D5CDE00` |
| `GHelperAutoMode-5.0.1-win-x64.msi` | 37,560,320 bytes | `A1E2F5D170A900B2A16AC343A676639957916671A102F0D446ECA3031213BF24` |

Release binaries are unsigned. `SHA256SUMS.txt` is published beside them and also records the portable ZIP, whose hash cannot be embedded in a document inside that same archive. The application binary is deterministic for the same source and toolchain. WiX assigns package-level metadata to a newly produced MSI, so installer hashes identify the published build rather than promising that later rebuilds will be byte-identical.

The portable ZIP contains exactly the seven files from `dist\self-contained`, with matching hashes and no PDB. A Microsoft Defender custom scan of the complete release directory found no threats.

## Checks that remain hardware-dependent

No registry or process test can prove the final LED color. On a new ASUS model, confirm the selected lighting owner after sign-in, lock/unlock, and sleep/resume. Also confirm Silent, Balanced, and Turbo in G-Helper before relying on AutoMode's hotkeys.

For the original display-wake failure, the invariant is stricter than a hardware smoke test: `Off`, `Dimmed`, and `Unknown` select the direct `WM_HOTKEY` route before any input API is reachable. If no G-Helper window accepts that route, the request is deferred rather than converted into a keypress.
