namespace GHelperAutoMode;

internal enum PerformanceMode
{
    Unknown = -1,
    Balanced = 0,
    Turbo = 1,
    Silent = 2,
    Custom1 = 3,
    Custom2 = 4
}

internal enum ControlState
{
    Auto,
    ForceSilent,
    ForceBalanced,
    ForceTurbo,
    Pause
}

internal enum KeyboardLightingMode
{
    Unmanaged,
    WindowsDynamicLighting,
    GHelperWindowsAccent,
    GHelperManual
}

internal enum DisplayState
{
    Unknown = -1,
    Off = 0,
    On = 1,
    Dimmed = 2
}

internal static class DisplayStateExtensions
{
    /// <summary>
    /// Synthetic keyboard input is allowed only while Windows has positively reported the
    /// session display as fully on. Input can wake an Off display, cancel Dimmed state, or
    /// wake a display whose state has not yet been delivered after startup/resume.
    /// </summary>
    public static bool AllowsInputInjection(this DisplayState state) => state == DisplayState.On;
}

internal enum AppRuleAction
{
    MinimumBalanced,
    MinimumTurbo,
    ForceSilent,
    ForceBalanced,
    ForceTurbo
}

internal enum ModeRequestStatus
{
    Sent,
    Deferred,
    Failed
}

/// <summary>
/// Describes how trustworthy a telemetry value is for continuous-evidence timers.
/// IntervalCache is intentional short reuse between normal provider polls; GraceCache
/// means the provider actually failed and an old value is shown only as context.
/// </summary>
internal enum TelemetryQuality
{
    Unavailable,
    Fresh,
    IntervalCache,
    GraceCache
}

internal static class TelemetryQualityExtensions
{
    public static bool PreservesContinuity(this TelemetryQuality quality) =>
        quality is TelemetryQuality.Fresh or TelemetryQuality.IntervalCache;

    public static bool IsAffirmative(this TelemetryQuality quality) =>
        quality is TelemetryQuality.Fresh or TelemetryQuality.IntervalCache;
}

internal sealed record ModeRequestResult(ModeRequestStatus Status, string Message)
{
    public static ModeRequestResult Sent(string message = "Hotkey sent") => new(ModeRequestStatus.Sent, message);
    public static ModeRequestResult Deferred(string message) => new(ModeRequestStatus.Deferred, message);
    public static ModeRequestResult Failed(string message) => new(ModeRequestStatus.Failed, message);
}

internal sealed record CpuTemperatureTelemetry(
    double? TemperatureC,
    TelemetryQuality Quality,
    string Source,
    string? Error = null);

internal sealed record GpuTelemetry(
    double? UtilizationPercent,
    double? TemperatureC,
    TelemetryQuality Quality,
    string Source,
    string? Error = null);

internal sealed record TelemetrySnapshot(
    DateTime Timestamp,
    double? CpuPercentRaw,
    double? CpuPercentAverage,
    double? CpuTemperatureC,
    TelemetryQuality CpuTemperatureQuality,
    string CpuTemperatureSource,
    string? CpuTemperatureError,
    double? GpuPercentRaw,
    double? GpuPercentAverage,
    double? GpuTemperatureC,
    TelemetryQuality GpuTelemetryQuality,
    string GpuTelemetrySource,
    string? GpuTelemetryError,
    string ForegroundProcess,
    int? ForegroundProcessId,
    double? ForegroundCpuCoreEquivalentRaw,
    double? ForegroundCpuCoreEquivalentAverage,
    DisplayState DisplayState);

internal sealed record Decision(PerformanceMode? TargetMode, string Reason, bool IgnoreCooldown = false);

internal sealed record EngineStatus(
    TelemetrySnapshot Snapshot,
    ControlState ControlState,
    PerformanceMode CurrentMode,
    PerformanceMode? PendingMode,
    string LastDecision,
    string TransitionProgress,
    TimeSpan? ExternalHoldRemaining,
    bool GHelperRunning);

internal sealed record GHelperSettingsSnapshot(
    bool ConfigReadable,
    PerformanceMode? CurrentMode,
    bool SkipHotkeys,
    string ModifierKeybindAlt,
    int ProfileKeyBalanced,
    int ProfileKeyTurbo,
    int ProfileKeySilent,
    int? DisablePowerEvent,
    int? ScreenAuto,
    int? GpuMode,
    string? Error = null);
