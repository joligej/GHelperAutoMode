# Changelog

## 4.4.1 - Repository and documentation maintenance

- Organized C# sources, assets, scripts, examples, and technical documents into dedicated directories.
- Rewrote all project documentation in English and shortened repetitive release text.
- Updated build, publish, startup-management, and source-manifest paths for the new layout.
- Replaced decorative ownership separators in tray labels with plain English punctuation.
- Kept generated output outside Git while preserving the existing `dist/self-contained` live-installation path.
- Fixed repository text files to LF and disabled Git-derived informational-version suffixes.
- Compiled the release without portable debug metadata before bundling, keeping the single-file binary identical across clean source paths.
- No performance or lighting policy changed in this release.

## 4.4.0 - Deterministic Dynamic Lighting ownership

- Enforced `ControlledByForegroundApp=0` at the Windows Lighting root and every discovered device in Windows-owned mode.
- Accounted for G-Helper 0.279 applying Aura after logon, unlock, and console connection even with `skip_aura=1`.
- Added coalesced session notifications and a delayed G-Helper release followed by Windows reacquisition.
- Detected out-of-band G-Helper Aura mode, color, speed, and ownership changes.
- Corrected transition order between Windows-owned and G-Helper-owned lighting.
- Added a 30-second ownership heartbeat and a manual reapply command without toggling Dynamic Lighting off and on.
- Reported device alignment, takeover policy, effective color source, and converted RGB value in diagnostics.
- Removed warning spam from unchanged periodic checks and avoided unnecessary G-Helper reloads.

## 4.3.1 - Startup integrity

- Replaced the legacy HKCU Run entry with a per-user Task Scheduler definition using an interactive token and `HighestAvailable`.
- Compared AutoMode and G-Helper process integrity before requesting elevation.
- Migrated owned legacy startup entries and limited historical-task removal to tasks that target GHelperAutoMode.
- Added startup read-back verification and integrity details to diagnostics.
- Unified tray and PowerShell startup-management behavior.

## 4.3.0 - Keyboard-lighting modes

- Added Unmanaged, Windows Dynamic Lighting, G-Helper Windows-accent, and G-Helper manual modes.
- Added atomic G-Helper configuration updates with a one-time recovery copy.
- Relaunched a previously running G-Helper instance through its per-user scheduled task.
- Added schema 7 with an opt-in `Unmanaged` lighting default.
- Added lighting state and color information to diagnostics.

## 4.2.2 - Display-power-safe profile transport

- Restricted `SendInput` and `keybd_event` to a positively reported `On` display state.
- Used direct `WM_HOTKEY` delivery for `Off`, `Dimmed`, and `Unknown` states.
- Deferred requests when no non-waking target was available.
- Prevented stale pre-resume GPU fallback operations from restoring old cache state.
- Coalesced duplicate resume broadcasts and tightened shutdown ordering.
- Added source-manifest and release-preflight checks.

## 4.2.1 - Diagnostics

- Separated the last sent request from the last G-Helper-confirmed mode.
- Reported an unobserved display state as `Unknown`.
- Confirmed the normal Silent, Balanced, and Turbo transition paths on the target system.

## 4.2.0 - Adaptive-policy hardening

- Added schema 6 migration while preserving existing application rules.
- Set the default polling interval to one second.
- Kept Silent available during active display use.
- Made mature Turbo evidence direct by default, with optional staged promotion.
- Added explicit `Fresh`, `IntervalCache`, `GraceCache`, and `Unavailable` telemetry quality.
- Required fresh provider data to complete GPU promotions and automatic downshifts.
- Tied foreground evidence to process ID and aligned tick timing around asynchronous sampling.
- Added epoch isolation for stale asynchronous work.
- Allowed stronger promotions and explicit user actions to supersede pending lower requests.
- Separated confirmation timeout from late-write classification.
- Strengthened hysteresis, temperature, and downshift invariants.
- Parsed G-Helper JSON with comment and trailing-comma support.
- Applied warnings-as-errors to strict build and publish operations.

## 4.1.0 - Evidence handling

- Made promotion evidence independent of the active profile.
- Separated CPU and GPU temperature timers.
- Serialized profile transitions and scoped downshift evidence to the confirmed mode.
- Preserved valid low-load evidence across display-state changes.

## 4.0.0 - Thermal adaptation

- Corrected the x64 Windows `INPUT` structure used by `SendInput`.
- Added read-only ASUS ACPI CPU-temperature telemetry.
- Added temperature-based Balanced and Turbo promotion.
- Adopted fast promotion and gradual downshift behavior.

## 3.0.0 - Three-profile controller

- Made Silent a normal active-display profile.
- Added separate Balanced and Turbo load paths.
- Added foreground one-core-equivalent CPU telemetry.
- Added initial CAD and benchmark application rules.

## 2.x

- Added G-Helper hotkey discovery, mode confirmation, NVIDIA telemetry, display events, logging, diagnostics, manual overrides, startup integration, and single-instance handling.
