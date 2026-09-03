# GHelperAutoMode

GHelperAutoMode is a Windows tray companion for [G-Helper](https://github.com/seerge/g-helper). It selects the Silent, Balanced, or Turbo performance profile from system load, temperature, foreground activity, display state, and optional application rules. It can also assign keyboard-lighting control to either Windows Dynamic Lighting or G-Helper.

The application does not replace G-Helper. G-Helper continues to apply hardware profiles; AutoMode only decides which profile to request and, when enabled, which lighting system owns the keyboard.

## Requirements

- Windows 11 x64
- G-Helper installed and configured
- An NVIDIA GPU is optional; NVML or `nvidia-smi` is used when available
- ASUS CPU temperature support depends on the firmware and WMI data exposed by the device
- .NET 10 SDK only when building from source

The self-contained release includes the .NET runtime.

## Main features

- Three-level automatic profile selection: Silent, Balanced, and Turbo
- Separate CPU, GPU, foreground-process, and thermal evidence timers
- Conservative downshifts with hysteresis and fresh-telemetry requirements
- Per-application minimum or forced profile rules
- Display-off-safe G-Helper commands that do not inject keyboard input
- Optional Windows Dynamic Lighting ownership
- Optional G-Helper static lighting synchronized to the Windows accent color
- Per-user startup through Task Scheduler at the same integrity level as G-Helper

## Installation

[Download the latest release](https://github.com/joligej/GHelperAutoMode/releases/latest) or build it from source. Extract the release package and keep the executable in a stable directory. The local build output is:

```text
dist\self-contained\GHelperAutoMode.exe
```

Run the executable normally. Use **Run at Windows login (Task Scheduler)** from the tray menu to enable automatic startup. Windows may request administrator approval while creating or removing the task. Later logins do not require another prompt.

The startup task is named `GHelperAutoMode_<SID>`. It appears in Task Scheduler, not in the Startup apps page of Task Manager. It starts after interactive logon or console connection with `HighestAvailable` privileges.

## Tray controls

- **Auto**: use the adaptive policy.
- **Force Silent**, **Force Balanced**, **Force Turbo**: hold an exact profile.
- **Pause automation**: stop issuing automatic profile changes.
- **Prefer Silent at low load**: allow Silent during normal active use.
- **Stage sustained Turbo via Balanced**: optionally insert a short Balanced step before sustained Turbo promotion.
- **Keyboard lighting**: select the lighting owner.
- **Logging enabled**: enable or disable persistent logging.
- **Show diagnostics**: show current telemetry, state, transport, startup, and lighting information.

## Adaptive profile policy

Default thresholds are stored in [`examples/config.example.json`](examples/config.example.json). The values below describe the supplied defaults.

### Promotion to Balanced

Balanced is requested when any enabled path remains above its threshold for the required duration:

| Signal | Threshold | Duration |
| --- | ---: | ---: |
| Average CPU load | 22% | 3 s |
| Average GPU load | 18% | 3 s |
| Foreground process, one-core equivalent | 40% | 2 s |
| CPU temperature | 65 C | 10 s |
| GPU temperature | 65 C | 10 s |

### Promotion to Turbo

| Signal | Threshold | Duration |
| --- | ---: | ---: |
| Average CPU load | 50% | 5 s |
| Average GPU load | 45% | 4 s |
| Fast CPU load | 75% | 2 s |
| Fast GPU load | 80% | 2 s |
| Foreground process, one-core equivalent | 70% | 3 s |
| CPU temperature | 75 C | 10 s |
| GPU temperature | 75 C | 10 s |

Promotion evidence belongs to its signal rather than the current profile. Entering Balanced does not reset an already-running Turbo timer. CPU and GPU temperature timers are independent.

With `StepwiseAutomaticUpshifts=false`, mature Turbo evidence can move directly from Silent to Turbo. The optional staged mode inserts the configured `BalancedBeforeTurboSeconds` delay for normal sustained-load promotion; fast, thermal, and application-rule Turbo requests remain direct.

### Downshifts

Turbo remains active for at least 30 seconds. It then requires 25 continuous seconds below the configured load and temperature limits before returning to Balanced. The final decision requires current CPU temperature data and a fresh GPU sample when GPU telemetry is available.

After Turbo, Balanced remains active for a 30-second cooling period. Silent then requires 60 continuous seconds of low load with an active display, or 20 seconds when the display is off. Active promotion evidence blocks a downshift.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the state-machine invariants.

## Application rules

Rules are evaluated in order; the first enabled match wins. `Process` supports simple wildcards.

Available actions:

- `MinimumBalanced`
- `MinimumTurbo`
- `ForceSilent`
- `ForceBalanced`
- `ForceTurbo`

Minimum actions remain part of the adaptive policy. Force actions are absolute while the rule matches. Rules do not apply with the display off unless `ApplyWhenDisplayOff` is enabled.

## Display-power safety

Windows `GUID_SESSION_DISPLAY_STATUS` supplies the display state.

- `On`: the normal G-Helper hotkey route may use `SendInput`.
- `Off`, `Dimmed`, or `Unknown`: AutoMode only posts `WM_HOTKEY` directly to windows owned by G-Helper.
- If no safe target window is available, the request is deferred. It never falls back to a keyboard API.

This separation prevents profile changes from waking a powered-off display. The input guard also delays normal `SendInput` requests while Ctrl, Shift, Alt, or Win is physically held.

## Keyboard lighting

The tray menu provides four mutually exclusive modes:

| Mode | Behavior |
| --- | --- |
| **Do not manage lighting** | Leave Windows and G-Helper lighting settings unchanged. This is the default for new and migrated configurations. |
| **Windows owns - Dynamic Lighting** | Enable Windows Dynamic Lighting, set G-Helper `skip_aura=1`, and disable foreground-app takeover at the Windows Lighting root and each discovered device. Existing Windows effect, brightness, speed, accent, and color settings are preserved. |
| **G-Helper owns - Windows accent color** | Disable Dynamic Lighting, select G-Helper static Aura mode, and synchronize `aura_color` with the current Windows accent. |
| **G-Helper owns - existing manual Aura** | Disable Dynamic Lighting, set `skip_aura=0`, and preserve the existing G-Helper Aura mode and colors. |

Ownership is reconciled automatically at startup, after logon, console connection, unlock, and resume, and when an out-of-band G-Helper Aura change is detected. A 30-second registry heartbeat refreshes Windows ownership without toggling Dynamic Lighting off and on. **Re-apply selected ownership now** is a recovery command and is not required during normal use.

Lighting checks run at the configured interval, five seconds by default. An unchanged check only reads a small JSON file and a few registry values. It does not restart G-Helper, write a log entry, or toggle lighting. In Windows-owned mode, Windows applies the selected color. In G-Helper accent mode, G-Helper is reloaded only when the resolved accent value actually changes.

The first managed G-Helper lighting change creates this recovery copy:

```text
%APPDATA%\GHelper\config.before-GHelperAutoMode-lighting.json
```

## Configuration and data

AutoMode stores runtime data outside the repository:

```text
%LOCALAPPDATA%\GHelperAutoMode\config.json
%LOCALAPPDATA%\GHelperAutoMode\logs\automode.log
```

The current configuration schema is 7. Upgrades preserve existing thresholds, preferences, and application rules. A pre-migration copy is stored next to `config.json`. Invalid JSON is copied to `config.invalid.<timestamp>.json` before defaults are restored.

The default log rotates at 5 MB and retains three old files. Set `TelemetryIntervalSeconds` to `0` to suppress periodic telemetry records while keeping state changes and warnings.

## Repository layout

```text
GHelperAutoMode/
|-- assets/                  Application icon
|-- docs/                    Architecture and release validation
|-- examples/                Example configuration
|-- scripts/                 Build and startup-management scripts
|-- src/GHelperAutoMode/     C# source code
|-- CHANGELOG.md
|-- GHelperAutoMode.csproj
|-- README.md
`-- SOURCE_MANIFEST.sha256
```

`dist/`, `bin/`, and `obj/` are generated and ignored by Git. The live installation may continue to run from `dist/self-contained`; it is not committed to the repository.

## Building from source

Open PowerShell in the repository root and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1 -Clean -SelfContained -Strict
```

The script requires a .NET 10 SDK. It validates the schema, versions, example-config parity, control-path safety, startup invariants, lighting ownership code, and every hash in `SOURCE_MANIFEST.sha256` before building. `-Strict` treats build and publish warnings as errors.

Output:

```text
dist\self-contained\GHelperAutoMode.exe
```

Manual startup-management scripts:

```powershell
.\scripts\install-startup.ps1 -ExePath .\dist\self-contained\GHelperAutoMode.exe
.\scripts\uninstall-startup.ps1
```

## Diagnostics checklist

After a new build, verify:

1. Diagnostics report display and session notification registration.
2. Force Silent, Balanced, and Turbo each receive G-Helper confirmation.
3. Auto returns through Turbo, Balanced, and Silent as load falls.
4. With the display off, profile requests use `WM_HOTKEY (non-waking)`.
5. Windows-owned lighting reports foreground takeover disabled and all devices aligned.
6. Lock/unlock and sleep/resume return lighting to the selected owner.

Build and target-machine checks are recorded in [`docs/RELEASE_VALIDATION.md`](docs/RELEASE_VALIDATION.md).

## Scope

AutoMode does not directly change ASUS fan curves, CPU or GPU power limits, boost settings, GPU mode, overclocking, undervolting, temperature targets, display refresh rate, battery charge limits, or sleep policy.

## References

- [G-Helper repository](https://github.com/seerge/g-helper)
- [G-Helper power-user settings](https://github.com/seerge/g-helper/wiki/Power-user-settings)
- [Microsoft Dynamic Lighting](https://learn.microsoft.com/windows/apps/develop/devices-sensors/lighting-dynamic-lamparray)
- [Microsoft SendInput](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput)
- [Microsoft power setting GUIDs](https://learn.microsoft.com/windows/win32/power/power-setting-guids)
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)
