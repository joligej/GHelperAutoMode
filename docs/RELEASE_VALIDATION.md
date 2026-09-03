# Release validation

This document records the release checks for GHelperAutoMode 4.4.2. `SOURCE_MANIFEST.sha256` identifies the source and documentation included in the build.

## Validation scope

The release process checks:

- configuration schema and example parity;
- version metadata;
- performance-threshold relationships;
- display-power-safe command routing;
- Dynamic Lighting ownership recovery;
- Task Scheduler and process-integrity behavior;
- compiler and analyzer output;
- single-file publish contents;
- source-manifest coverage and hashes.

Visual keyboard behavior after physical lock, unlock, sleep, resume, or firmware events still requires observation on the target device. Registry and process state cannot prove the final LED output by themselves.

## G-Helper compatibility

The installed G-Helper 0.279 executable reports source revision `5b0029057541bff4cbd025e1a01e0641e11bd64e`. The following files from that revision were reviewed:

- `app/Input/InputDispatcher.cs`
- `app/Program.cs`
- `app/Settings.cs`
- `app/USB/Aura.cs`
- `app/USB/AsusLampArray.cs`
- `app/Helpers/DynamicLightingHelper.cs`

Relevant findings:

- normal startup honors `skip_aura=1`;
- session logon, unlock, and console connection can call `Aura.ApplyAura()` without checking `skip_aura`;
- Aura controls can apply settings directly;
- a clean G-Helper restart with `skip_aura=1` releases this direct path;
- G-Helper writes Windows Lighting values at both root and device level;
- Dynamic Lighting registry colors use `AABBGGRR`, while G-Helper `aura_color` uses ARGB;
- `Global\GHelperApp-Exit` and `GHelper_<SID>` remain the supported stop and restart mechanisms.

## Static safeguards

The build preflight verifies that:

- the example uses schema 7 and defaults lighting to `Unmanaged`;
- project, assembly, and file versions are 4.4.2;
- every `ThresholdConfig` property exists in the example and no unknown threshold is present;
- entry and reset thresholds form valid hysteresis bands;
- the non-waking display branch precedes normal `SendInput` use;
- `KeyboardLightingManager` contains no input-synthesis API;
- Windows-owned lighting enforces `ControlledByForegroundApp`;
- session recovery releases G-Helper before Windows reacquires ownership;
- startup uses an interactive Task Scheduler token and `HighestAvailable` run level;
- startup is not registered through a new HKCU Run value;
- every maintained repository file is present once in `SOURCE_MANIFEST.sha256` and matches its hash.

## Build command

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1 -Clean -SelfContained -Strict
dotnet format GHelperAutoMode.csproj --verify-no-changes --severity warn --no-restore
```

Validation environment:

- Windows 11 x64, build 10.0.26200
- .NET SDK 10.0.400
- target framework `net10.0-windows`
- runtime identifier `win-x64`

## Build result

- Source-manifest preflight: passed
- Release build: passed
- Compiler warnings: 0
- Compiler errors: 0
- Self-contained single-file publish: passed
- Formatter and analyzer verification: passed
- FileVersion: 4.4.2.0
- ProductVersion: 4.4.2
- Executable size: 116,336,818 bytes
- Executable SHA-256: `F9D278C02DB276368B32291F44CFA411D786F2F8C6194091784408BF05721BEC`
- Clean-publish reproducibility: identical executable hash from the repository root and an isolated committed-source copy

## Target-machine checks

The previous 4.4.0 runtime validation established:

- the SID-scoped task used the expected executable, `--startup`, interactive logon, and Highest run level;
- Windows-owned mode set `AmbientLightingEnabled=1` and `ControlledByForegroundApp=0` at root and device level;
- one discovered ASUS Dynamic Lighting device was aligned;
- the effective system-accent color resolved to `#0A7EA5`;
- an injected ownership-policy drift was corrected within one polling interval without disabling Dynamic Lighting or restarting G-Helper;
- display state `Unknown` used direct non-waking `WM_HOTKEY` delivery for profile changes;
- the final runtime emitted no new warnings or errors.

The 4.4.2 build was then started through the existing SID-scoped task. The check confirmed:

- the task still points to `dist\self-contained\GHelperAutoMode.exe --startup` and reports a running instance;
- AutoMode and G-Helper are both running;
- schema 7 and the selected `GHelperWindowsAccent` mode were preserved;
- G-Helper reports `skip_aura=0` and Windows Dynamic Lighting remains disabled, as required for the selected owner;
- the user's disabled logging preference was preserved.

Logging was already disabled, so the runtime check used process, task, configuration, and registry state. Version 4.4.2 changes documentation and user-facing wording; it does not change the performance or lighting policy.
