# Release validation

This records the checks for version 5.0.3 on 5 November 2026. The local machine is an ASUS ROG Zephyrus G16 running Windows 11 x64, with an NVIDIA GPU and G-Helper 0.288. The build uses .NET SDK 10.0.403 and WiX SDK 6.0.2. `global.json` pins the 10.0.400 SDK feature band and accepts patch updates.

## Analyzer review

The previous working tree produced 169 warnings with `AnalysisMode=All`. Each warning was reviewed against its call site.

| Rule | Before | Resolution |
| --- | ---: | --- |
| CA1031, broad catches | 59 | Narrowed 52 catches or removed redundant wrappers. Kept seven documented application, timer, and COM error boundaries. |
| CA1303, localization | 14 | Retained English UI text. `.editorconfig` scopes this exception to the two UI files. |
| CA1307, string comparison | 1 | Made the startup-command comparison ordinal. |
| CA1308, casing | 2 | Changed modifier-token normalization and matching to invariant uppercase. |
| CA2000, local disposal | 3 | Transfer menu-item ownership directly to its parent collection. |
| CA2007, async context | 36 | Explicitly preserve the UI context in application state machines. Worker-pipe I/O uses `ConfigureAwait(false)`. |
| CA2213, field disposal | 23 | Dispose owned fields through the standard `Dispose(bool)` path, including partial tray construction. |
| CA2216, native lifetime | 1 | Own the power-notification registration through a `SafeHandle`. |
| CA5392, DLL search | 30 | Restrict Windows DLL imports to System32 at assembly level. |

The resulting application builds with the full analyzer set, code-style enforcement, and warnings-as-errors enabled in the project. A clean build must report zero warnings and zero errors. The seven CA1031 annotations explain the recovery or diagnostic boundary at the method itself. There is no project-wide exception for broad catches, disposal, async behavior, or DLL loading.

The boundaries retained under CA1031 are `Program.Main`, `AutomationEngine.TimerOnTick`, `KeyboardLightingManager.ApplyNowAsync`, and four Task Scheduler entry points that turn late-bound COM failures into diagnostics or command exit codes.

The regression project uses the same analyzer and warnings-as-errors policy. Its English console output has a scoped localization exception. Its message-loop boundary captures async test failures and rethrows them after the loop exits.

## Repeatable checks

Run the normal build and regression suite from PowerShell.

```powershell
.\scripts\build.ps1 -SelfContained -Strict
dotnet format GHelperAutoMode.csproj --verify-no-changes --no-restore
dotnet run --project tests/GHelperAutoMode.Tests -c Release
.\scripts\build-installer.ps1 -SkipAppBuild -Strict
```

The regression executable uses temporary configuration directories, fake worker processes, and local recovery files. Its standard run covers these cases.

- A failed atomic save preserves both the file and the live configuration.
- A settings-file read error propagates without replacing the file or creating an invalid-JSON backup.
- Invalid JSON, including a `null` root, gets a recovery copy before defaults are saved.
- Schema migration preserves the original file in a backup and keeps existing settings.
- Fifteen repeated power-window, settings-form, and tray disposal cycles tolerate explicit and implicit disposal.
- Telemetry continuations return to the WinForms UI thread.
- Resume discards in-flight worker results and permits fresh sampling.
- Worker exit and timeout disable that native provider for the current session.
- The supervisor retries a failed child three times and stops immediately after a normal child exit.

For a machine with NVIDIA hardware, add `--gpu`.

```powershell
dotnet run --project tests/GHelperAutoMode.Tests -c Release -- --gpu
```

That run also checks 40 real load-and-temperature samples and forcibly ends the native worker. The parent must stay alive and return a valid `nvidia-smi` fallback sample. Both the standard and hardware runs passed locally. CI runs the hardware-independent suite on Windows alongside the strict build, formatting check, and installer build.

## Startup and recovery

The local 5.0.2 machine installation was tested before this analyzer review. Both G-Helper and AutoMode ran at High integrity. Terminating the GPU worker preserved the tray. Terminating the tray caused its waiting parent to restart it after the configured minute. The settings hash was unchanged across the installation and recovery tests.

Windows Task Scheduler's `RestartOnFailure` handles failed start conditions and action launches. AutoMode therefore also supervises the tray itself to handle an abnormal exit after launch. Normal tray exit ends the parent. The task remains running while the parent waits for the tray.

A full Windows reboot remains a separate manual check. Hardware-dependent checks include final LED color and ownership after logon, unlock, and sleep/resume, plus confirmation of Silent, Balanced, and Turbo on the installed G-Helper version.

## Installer and artifacts

The installer retains the dual-purpose MSI scope selection and stable UpgradeCode `{C3E10D0F-8FBD-4C20-BDBF-9E7D0F919F1A}`. WiX generates the ProductCode for each build. Setup chooses the scope using its actual elevation token. The per-user MSI properties are `ALLUSERS=2 MSIINSTALLPERUSER=1`. The machine property is `ALLUSERS=1`.

The release contains the standalone application, portable ZIP, MSI, setup executable, and `SHA256SUMS.txt`. Checksums identify the exact uploaded files. The portable ZIP is compared with the publish directory, including documentation and the source manifest. Release binaries are unsigned.

## WinGet history

Microsoft's original submission checks passed for 5.0.1 after the conflicting user-scope elevation declaration was removed. Review later requested a product privacy policy and a `PrivacyUrl`, and removal of redundant `DisplayVersion` metadata. The submission closed as stale on 23 October 2026. The product privacy policy is now included in the repository and release. [Original review](https://github.com/microsoft/winget-pkgs/pull/429318) · [Privacy policy](../PRIVACY.md)
