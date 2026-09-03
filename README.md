# GHelperAutoMode

GHelperAutoMode sits in the Windows tray and watches what your laptop is doing. It asks [G-Helper](https://github.com/seerge/g-helper) for Silent, Balanced, or Turbo when the measured load and temperatures call for it. It can also keep Windows Dynamic Lighting and G-Helper from fighting over the keyboard backlight.

G-Helper still does the hardware work. AutoMode adds the automatic decisions and, if you enable lighting management, makes sure one lighting system is in charge at a time.

## Replacing Armoury Crate

AutoMode is not a stand-alone replacement for Armoury Crate. [G-Helper is the Armoury Crate alternative](https://github.com/seerge/g-helper); AutoMode is an optional layer on top. Together, they are meant to replace the laptop-control part of Armoury Crate on supported ASUS laptops.

Why use this setup:

- **It stays focused.** G-Helper handles laptop controls, while AutoMode handles automatic profile switching and keyboard-lighting ownership. AutoMode has no account, store, news feed, game library, or cloud service.
- **Profiles follow the workload.** Armoury Crate can link settings to an application through Scenario Profiles. AutoMode can use application rules too, but it can also react to sustained CPU load, GPU load, temperature, and foreground activity.
- **Small spikes do not cause constant switching.** Timers, hysteresis, cooldowns, and fresh-telemetry checks keep profile changes deliberate.
- **A dark screen stays off.** When Windows does not confirm that the display is on, AutoMode avoids normal keyboard-input APIs and uses a non-waking G-Helper route instead.
- **Lighting control is explicit.** You choose Windows, G-Helper with the Windows accent color, G-Helper's existing Aura settings, or no management at all.
- **The setup is easy to inspect.** AutoMode uses a readable JSON config, has public source, and can be built with one PowerShell command.

There are trade-offs. [ASUS describes Armoury Crate](https://www.asus.com/support/faq/1041654/) as a broader device suite: it also covers supported peripherals, Aura Sync and Aura Creator, driver and utility downloads, account features, and other device-specific pages. G-Helper and AutoMode do not promise a one-for-one replacement for all of that. Check your model and any ASUS peripherals you depend on before removing Armoury Crate.

[G-Helper recommends against running its controls alongside Armoury Crate services](https://github.com/seerge/g-helper/wiki/Requirements), because both can change the same settings. A sensible switch looks like this:

1. Set up G-Helper first and check performance modes, GPU mode, battery limits, hotkeys, and any model-specific controls you need.
2. Run AutoMode and test Silent, Balanced, Turbo, startup, sleep/resume, and your chosen lighting mode.
3. If everything works, remove Armoury Crate with the [official ASUS uninstall tool](https://www.asus.com/support/faq/1041654/) and restart Windows.
4. Keep the ASUS System Control Interface installed; G-Helper relies on it. Armoury Crate can be installed again later if your model needs a feature that is not covered.

## Requirements

- Windows 11 x64
- G-Helper installed and configured
- An NVIDIA GPU is optional; NVML or `nvidia-smi` is used when available
- ASUS CPU temperature support depends on the firmware and WMI data exposed by the device
- .NET 10 SDK only when building from source

The self-contained release includes the .NET runtime.

## What AutoMode adds

- Three-level automatic profile selection: Silent, Balanced, and Turbo
- Separate CPU, GPU, foreground-process, and thermal evidence timers
- Deliberate downshifts with hysteresis and fresh-telemetry checks
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
- **Use Silent at low load**: allow Silent during normal active use.
- **Pass through Balanced before Turbo**: optionally spend a short time in Balanced before a normal sustained-load move to Turbo.
- **Keyboard lighting**: select the lighting owner.
- **Save logs**: keep a rotating diagnostic log on disk.
- **Show diagnostics**: show current telemetry, state, transport, startup, and lighting information.

## Adaptive profile policy

The default thresholds live in [`examples/config.example.json`](examples/config.example.json). AutoMode waits for a signal to stay above its threshold; a single brief spike is not enough.

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

Each signal keeps its own timer. Moving into Balanced does not erase a Turbo timer that was already building, and CPU and GPU temperature do not share a timer.

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

Only one system should control the keyboard backlight. The tray menu gives you four choices:

| Mode | Behavior |
| --- | --- |
| **Leave lighting alone** | Do not change Windows or G-Helper lighting settings. This is the default for new and upgraded configs. |
| **Windows controls lighting** | Turn on Windows Dynamic Lighting, tell G-Helper to release Aura, and block foreground apps from taking over the discovered lighting devices. Your existing Windows effect, brightness, speed, and color stay in place. |
| **G-Helper uses the Windows accent color** | Turn off Dynamic Lighting, use G-Helper's static Aura mode, and update it when the Windows accent color changes. |
| **G-Helper keeps its current Aura settings** | Turn off Dynamic Lighting, let G-Helper control the keyboard, and keep its current Aura mode and colors. |

AutoMode checks this choice at startup, after sign-in, unlock, resume, and any outside G-Helper Aura change it notices. A small 30-second registry check keeps Windows in control when that mode is selected; it does not flash Dynamic Lighting off and on. **Apply this lighting choice again** is there for recovery and should not be needed day to day.

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

## What AutoMode does not manage

AutoMode does not directly change fan curves, CPU or GPU power limits, boost settings, GPU mode, overclocking, undervolting, temperature targets, refresh rate, battery charge limits, or sleep policy. Those settings remain in G-Helper, firmware, or Windows.

## References

- [G-Helper repository](https://github.com/seerge/g-helper)
- [G-Helper requirements and Armoury Crate guidance](https://github.com/seerge/g-helper/wiki/Requirements)
- [ASUS Armoury Crate FAQ and uninstall instructions](https://www.asus.com/support/faq/1041654/)
- [G-Helper power-user settings](https://github.com/seerge/g-helper/wiki/Power-user-settings)
- [Microsoft Dynamic Lighting](https://learn.microsoft.com/windows/apps/develop/devices-sensors/lighting-dynamic-lamparray)
- [Microsoft SendInput](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput)
- [Microsoft power setting GUIDs](https://learn.microsoft.com/windows/win32/power/power-setting-guids)
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)

## License

No license has been added. The source is public to read, but reuse and redistribution are not granted automatically.
