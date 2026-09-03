# Architecture

This note explains the rules the code is expected to keep. It is meant for anyone reviewing or changing GHelperAutoMode. The actual configuration values live in `examples/config.example.json`.

## Components

- `AutomationEngine` samples telemetry, maintains evidence timers, evaluates application rules, and selects a target performance profile.
- `GHelperController` sends and confirms profile requests through G-Helper.
- `KeyboardLightingManager` keeps the selected lighting owner in place, separately from performance-profile decisions.
- `StartupManager` owns the per-user Task Scheduler entry and legacy-startup migration.
- `PowerNotificationWindow` receives display, resume, and session notifications.

## Performance state machine

### Signal-owned promotion evidence

Promotion evidence belongs to the observed signal, not to the current profile. Confirming Silent to Balanced does not reset CPU, GPU, foreground-process, or temperature evidence that could later justify Turbo.

CPU and GPU temperature timers are separate. Evidence is never combined across sensors. A missing or failed provider sample only invalidates the corresponding chain.

### Telemetry quality

GPU telemetry has four quality levels:

- `Fresh`: a new provider measurement
- `IntervalCache`: normal reuse between scheduled provider samples
- `GraceCache`: stale context retained after a provider failure
- `Unavailable`: no usable value

`IntervalCache` may preserve an active evidence timer, but cannot complete a promotion or downshift. Completion requires a fresh sample. `GraceCache` and `Unavailable` break promotion evidence and cannot justify a downshift.

CPU-temperature decisions require current, reliable data. The configured grace period avoids brief provider gaps but does not turn stale data into decision evidence.

### Foreground-process identity

Foreground evidence is tied to a process ID. A process change resets the foreground rolling average and its evidence timers. Consecutive applications, or separate instances of the same executable, cannot combine evidence.

Foreground load is reported as a one-logical-core equivalent. It complements total CPU load on high-core-count systems.

### Coherent sampling time

The engine completes asynchronous GPU sampling before taking the monotonic timestamp used for the rest of a tick. CPU, foreground, mode observation, and evidence updates therefore share one time reference.

### Epoch isolation

Resume, configuration reload, and explicit control-state changes increment an engine epoch. Results from an asynchronous operation started under an older epoch are discarded. The GPU provider also maintains a reset generation, preventing an old `nvidia-smi` continuation from repopulating its internal cache after resume or disposal.

### Transition serialization and preemption

Normal hardware transitions are serialized. A stronger promotion may preempt a pending lower-profile downshift:

- pending Silent while new Balanced or Turbo evidence exists: send Balanced again
- pending Balanced while new Turbo evidence exists: send Turbo again

An explicit tray action has higher priority than an automatic request. AutoMode may send the selected forced profile once more so the user's choice is the latest command sent to G-Helper. Older in-flight commands stay in recent-command history, which prevents a delayed G-Helper write from being mistaken for an outside change.

### Confirmation and retries

Every profile request is confirmed through G-Helper's `performance_mode`. The normal confirmation timeout is five seconds by default. An unconfirmed target can be retried after the regular cooldown.

`LateConfirmationGraceSeconds` is only a classification window for delayed writes. It does not block a required retry for the full grace period.

### External changes

Known external Silent, Balanced, or Turbo changes are temporarily protected from automatic downshifts. Valid evidence may still promote to a higher profile. Custom G-Helper modes are handled conservatively because their relative performance rank is unknown. Absolute manual control is available through the tray's Force modes.

### Downshift clocks

Turbo recovery starts only after Turbo is confirmed. Earlier idle time is not credited.

Balanced-to-Silent qualification starts only when:

1. Balanced is confirmed;
2. any post-Turbo cooling period has completed; and
3. all current low-load and temperature requirements hold.

The post-Turbo cooling timer and Silent timer do not run in parallel.

### Hysteresis

Evidence can survive inside the configured reset band to prevent jitter. A promotion is committed only while the current value is again at or above the entry threshold. A short spike cannot mature silently below the entry threshold and trigger a later unexpected transition.

### Application rules

The first enabled matching rule wins. `MinimumBalanced` and `MinimumTurbo` set a floor while preserving adaptive promotion. `ForceSilent`, `ForceBalanced`, and `ForceTurbo` are absolute while the rule matches.

## Display-power-safe transport

Display state affects command transport, not the performance floor. An active or dimmed display does not force Balanced, and Silent remains valid during normal use.

- `DisplayState.On`: `SendInput` is allowed after modifier-key validation.
- `Off`, `Dimmed`, or `Unknown`: only direct `WM_HOTKEY` messages to G-Helper-owned windows are allowed.
- If direct delivery is unavailable, the request remains deferred.

The non-waking branch is selected before the input guard and before any keyboard API. There is no path from an off, dimmed, or unknown display state to `SendInput` or `keybd_event`.

## Lighting ownership

Lighting management is independent from the performance state machine and never synthesizes keyboard input.

### Windows-owned mode

1. G-Helper is configured with `skip_aura=1`.
2. If G-Helper may hold a direct LampArray handle, it exits through `Global\GHelperApp-Exit` and restarts through its own scheduled task.
3. Windows Dynamic Lighting is enabled at the user root and each discovered device.
4. `ControlledByForegroundApp=0` is enforced at the root and each device.

The order matters: G-Helper releases direct control before Windows reacquires the device. Existing Windows effect and color settings are preserved.

The installed G-Helper revision applies Aura after logon, unlock, and console connection without consulting `skip_aura`. AutoMode therefore waits briefly, asks G-Helper to release the device, and gives it back to Windows after those events and after resume. Repeated events are folded into one update. Changes to G-Helper's Aura mode, colors, speed, or skip flag are picked up during the next check.

### G-Helper-owned modes

Windows Dynamic Lighting is disabled before G-Helper is reloaded. Accent mode writes a static Aura color resolved from the active Windows accent. Manual mode changes only the ownership flag and preserves the existing Aura configuration.

### Concurrency

Only one lighting update runs at a time. A new session or ownership request waits for the active update to finish. A regular check that finds no change does not restart G-Helper or write to the log.

## Startup integrity

AutoMode registers one per-user task with an interactive token and `HighestAvailable` run level, matching G-Helper's startup model. A manual launch compares process integrity levels and requests elevation only when the running G-Helper process is higher.

Startup changes are read back after registration. Owned legacy `HKCU\...\Run` state is migrated. The historical `LaunchGHelper` task is removed only when its action points to GHelperAutoMode.

## Shutdown

Shutdown invalidates the engine epoch, cancels lighting recovery work, detaches notification handlers, stops timers, and disposes telemetry providers. Pending asynchronous results cannot update state after disposal.
