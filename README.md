# GHelperAutoMode

GHelperAutoMode is a tray companion for [G-Helper](https://github.com/seerge/g-helper). It watches sustained CPU load, NVIDIA GPU load, foreground activity, and temperatures, then asks G-Helper to use Silent, Balanced, or Turbo. Short spikes are filtered out, and every change is confirmed against G-Helper's config.

It also settles a common keyboard-backlight problem: Windows Dynamic Lighting and G-Helper both trying to control the same device. Lighting management is optional and starts in **Leave lighting alone** mode.

G-Helper still talks to the laptop hardware. AutoMode only decides when to change profile and, if asked, which lighting system should own the keyboard.

## Before removing Armoury Crate

G-Helper is the actual Armoury Crate alternative; AutoMode adds automatic profile switching. On a supported ASUS laptop, the pair can cover the laptop-control parts most people use without running the Armoury Crate services, account features, store, news feed, or game library.

This is not a one-for-one replacement. Armoury Crate also supports some ASUS peripherals, Aura Creator, driver downloads, and model-specific pages. Set up G-Helper first and check performance modes, GPU mode, battery limits, hotkeys, and any device-specific controls you need. Then test AutoMode through a sleep/resume cycle before removing Armoury Crate with the [official ASUS uninstall tool](https://www.asus.com/support/faq/1041654/).

Do not remove the ASUS System Control Interface. G-Helper relies on it, and the two projects should not be run alongside active Armoury Crate control services. See the [G-Helper requirements](https://github.com/seerge/g-helper/wiki/Requirements) for the current model and service guidance.

## Requirements

- Windows 11 x64
- G-Helper installed and configured
- An NVIDIA GPU only if you want GPU telemetry; AutoMode uses NVML and falls back to `nvidia-smi`
- ASUS System Control Interface/ATKACPI support for CPU-temperature readings

The release builds include their .NET runtime. The .NET 10 SDK is only needed to build the project yourself.

## Install

Download the latest release from [GitHub Releases](https://github.com/joligej/GHelperAutoMode/releases/latest). For most people, `GHelperAutoMode-5.0.0-win-x64-setup.exe` is the right file.

- Open Setup normally to install for the current Windows account under `%LOCALAPPDATA%\Programs`.
- Run the same Setup file as administrator to install for every account under `C:\Program Files`.

Setup checks its real process token before starting Windows Installer. It does not ask for elevation on its own, so a normal launch stays per-user and a launch through Windows `sudo` or **Run as administrator** becomes per-machine.

The raw MSI is included for managed deployments. It defaults to the current user:

```powershell
# Current user
msiexec /i .\GHelperAutoMode-5.0.0-win-x64.msi ALLUSERS=2 MSIINSTALLPERUSER=1

# All users; run this command from an elevated terminal
msiexec /i .\GHelperAutoMode-5.0.0-win-x64.msi ALLUSERS=1
```

The portable ZIP is still available. Extract it to a directory you intend to keep, then run `GHelperAutoMode.exe`.

### WinGet

The WinGet package ID is `joligej.GHelperAutoMode`. Its community manifest is submitted after the v5 release because WinGet needs the final public download URL and SHA-256 hash.

Once Microsoft has merged that submission, install for the current account from a normal terminal:

```powershell
winget install --id joligej.GHelperAutoMode -e
```

Run the same command through Windows `sudo`, or from an administrator terminal, for a machine-wide installation:

```powershell
sudo winget install --id joligej.GHelperAutoMode -e
```

WinGet downloads the token-aware Setup executable. The package intentionally does not pretend that `--scope` can change a running process token.

### Startup and uninstall

The installer does not silently add a login task. Choose **Run at Windows login (Task Scheduler)** from the tray menu when you want it. The task is called `GHelperAutoMode_<your Windows SID>` and appears in Task Scheduler rather than Task Manager's Startup apps page. Windows may ask for approval when the task is created or removed; later logins do not need another prompt.

Uninstall from **Installed apps** or with `winget uninstall joligej.GHelperAutoMode`. Windows Installer closes the tray process even when AutoMode has elevated itself to match G-Helper, removes AutoMode's task for the current account, then removes the program and Start menu shortcut. Your config and logs are left in place for a later reinstall.

## Using AutoMode

Double-click the tray icon to open Settings. The tray menu also has quick controls for:

- automatic, paused, or forced Silent/Balanced/Turbo operation;
- allowing Silent during ordinary low-load use;
- optionally passing through Balanced before a normal Turbo promotion;
- keyboard-lighting ownership;
- login startup, logs, and live diagnostics.

Settings contains every policy value used by the engine: load and temperature thresholds, evidence timers, downshift rules, application rules, NVIDIA fallback timing, lighting recovery, and log rotation. The form edits a working copy. Invalid threshold ladders are rejected, **Cancel** changes nothing, and a successful save atomically replaces `config.json` before the engine reloads it.

### Default profile policy

Each signal has its own timer. A signal must remain above the configured threshold for the full duration; CPU, GPU, foreground, and temperature evidence are not mixed together.

Balanced defaults:

| Signal | Threshold | Time |
| --- | ---: | ---: |
| Average CPU | 22% | 3 s |
| Average GPU | 18% | 3 s |
| Foreground process, one-core equivalent | 40% | 2 s |
| CPU or GPU temperature | 65 C | 10 s |

Turbo defaults:

| Signal | Threshold | Time |
| --- | ---: | ---: |
| Average CPU | 50% | 5 s |
| Average GPU | 45% | 4 s |
| Fast CPU path | 75% | 2 s |
| Fast GPU path | 80% | 2 s |
| Foreground process, one-core equivalent | 70% | 3 s |
| CPU or GPU temperature | 75 C | 10 s |

Turbo stays active for at least 30 seconds and needs 25 continuous seconds below its exit limits before returning to Balanced. After Turbo, Balanced gets a 30-second cooling period. Silent then needs 60 seconds of low load with the display on, or 20 seconds with it off. All of these numbers can be changed in Settings.

Application rules are checked from top to bottom; the first enabled wildcard match wins. A rule can set a minimum profile or force an exact profile. Display-off rules are opt-in.

## Display-off safety

Windows reports display power through `GUID_SESSION_DISPLAY_STATUS`. AutoMode only uses normal `SendInput` hotkeys when the state is positively `On`. For `Off`, `Dimmed`, or `Unknown`, it posts `WM_HOTKEY` directly to G-Helper-owned windows. If no safe target exists, the profile request waits instead of falling back to keyboard input.

That separation is what prevents an automatic profile change from waking a screen that has just powered down.

## Keyboard lighting

The lighting menu has four modes:

| Choice | What happens |
| --- | --- |
| **Leave lighting alone** | AutoMode does not touch Dynamic Lighting or G-Helper Aura. This is the default. |
| **Windows controls lighting** | G-Helper releases Aura, Dynamic Lighting stays enabled, and foreground-app takeover is disabled for the discovered devices. Existing Windows effect, brightness, speed, and color are preserved. |
| **G-Helper uses the Windows accent color** | Dynamic Lighting is disabled. G-Helper uses static Aura and only reloads when the resolved Windows accent actually changes. |
| **G-Helper keeps its current Aura settings** | Dynamic Lighting is disabled and the current G-Helper Aura mode and colors are left intact. |

AutoMode reconciles ownership after startup, logon, unlock, resume, and outside changes to G-Helper's Aura config. The ordinary check runs every five seconds by default and only reads a small JSON file and a few registry values. Windows-owned mode also has a 30-second heartbeat. An unchanged check does not restart G-Helper, rewrite the config, or emit a log entry.

The first managed G-Helper lighting change makes this recovery copy:

```text
%APPDATA%\GHelper\config.before-GHelperAutoMode-lighting.json
```

## Files written outside the install directory

Runtime data belongs to the signed-in account, even when the program itself is installed for the whole machine:

```text
%LOCALAPPDATA%\GHelperAutoMode\config.json
%LOCALAPPDATA%\GHelperAutoMode\logs\automode.log
```

The current config schema is 8. Upgrades keep existing choices and write a timestamped copy before a schema migration. Invalid JSON is copied to `config.invalid.<timestamp>.json` before defaults are restored. Logs rotate at 5 MB by default and keep three old files.

The repository and install directory do not receive runtime state. The setup bootstrapper extracts its embedded MSI to `%TEMP%` only for the duration of the install and deletes it when Windows Installer exits.

## Build it yourself

Open PowerShell in the repository root. A clean self-contained build is:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1 -Clean -SelfContained -Strict
```

Output:

```text
dist\self-contained\GHelperAutoMode.exe
```

Build the MSI and token-aware Setup executable after that publish:

```powershell
.\scripts\build-installer.ps1 -SkipAppBuild -Strict
```

Or rebuild everything in one pass:

```powershell
.\scripts\build-installer.ps1 -Clean -Strict
```

Installer output:

```text
dist\installer\GHelperAutoMode-5.0.0-win-x64.msi
dist\installer\GHelperAutoMode-5.0.0-win-x64-setup.exe
```

The build requires the .NET 10 SDK. WiX Toolset 6.0.2 is pinned by the installer project and restored automatically. Strict builds treat warnings as errors and run the Windows Installer ICE checks. Release files are unsigned because this project does not have a code-signing certificate; compare downloads with `SHA256SUMS.txt` before running them.

The implementation notes are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and the checks recorded for the current release are in [docs/RELEASE_VALIDATION.md](docs/RELEASE_VALIDATION.md).

## Limits

AutoMode does not directly change fan curves, CPU or GPU power limits, boost, GPU mode, overclocking, undervolting, refresh rate, battery charge limits, or sleep policy. Those settings remain in G-Helper, Windows, or firmware.

It is built and tested around one ASUS/G-Helper setup, so diagnostics matter on other models. In particular, CPU-temperature access and keyboard-lighting ownership depend on what the firmware and ASUS interfaces expose.

## License

No license has been added. The source is public to read, but reuse and redistribution are not granted automatically.
