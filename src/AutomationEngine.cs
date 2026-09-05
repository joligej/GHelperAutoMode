using System.Globalization;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal sealed class AutomationEngine : IDisposable
{
    private sealed record PendingModeRequest(PerformanceMode Mode, string Reason, long RequestedAt);

    private readonly ConfigService _configService;
    private readonly FileLogger _logger;
    private readonly CpuMonitor _cpuMonitor;
    private readonly AsusCpuTemperatureMonitor _cpuTemperatureMonitor;
    private readonly NvidiaGpuMonitor _gpuMonitor;
    private readonly ForegroundAppMonitor _foregroundMonitor = new();
    private readonly GHelperController _gHelper;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly RollingAverage _cpuAverage = new();
    private readonly RollingAverage _gpuAverage = new();
    private readonly RollingAverage _foregroundCpuAverage = new();

    private bool _tickRunning;
    private bool _disposed;
    private int _stateEpoch;
    private DisplayState _displayState = DisplayState.Unknown;
    private PerformanceMode _currentMode = PerformanceMode.Unknown;
    private ControlState _controlState = ControlState.Auto;

    private long _startedAt;
    private long? _balancedEnteredAt;
    private long? _turboEnteredAt;
    private long? _lastTurboExitAt;
    private long? _lastModeRequestAt;
    private long? _externalModeChangeAt;
    private long? _lastTelemetryLogAt;

    private long? _balancedCpuSince;
    private long? _balancedGpuSince;
    private long? _foregroundBalancedSince;
    // Promotion evidence is tied to the physical signal that produced it, not to the
    // current performance mode. CPU/GPU thermal clocks are deliberately separate so
    // ten seconds cannot be assembled from five hot CPU seconds plus five hot GPU seconds.
    private long? _balancedCpuThermalSince;
    private long? _balancedGpuThermalSince;
    private long? _turboCpuThermalSince;
    private long? _turboGpuThermalSince;
    private long? _turboCpuSince;
    private long? _turboGpuSince;
    private long? _fastTurboCpuSince;
    private long? _fastTurboGpuSince;
    private long? _foregroundTurboSince;
    private long? _turboExitLowSince;
    private long? _silentEligibleSince;

    private string? _candidateRuleKey;
    private long? _candidateRuleSince;
    private int? _foregroundAverageProcessId;

    private PendingModeRequest? _pendingRequest;
    // When an explicit Force choice supersedes a different already-sent request while the
    // requested Force mode still equals our currently observed mode, we must reassert it once.
    // Otherwise the older in-flight request could land after the user's click.
    private PerformanceMode? _manualReassertTarget;
    private readonly Dictionary<PerformanceMode, (long RequestedAt, string Reason)> _recentAutomaticCommands = new();
    private bool _warnedGHelperMissing;
    private long? _lastRequestProblemAt;
    private string? _lastRequestProblemMessage;
    private string _lastDecision = "Starting";
    private string _transitionProgress = "Telemetry warming up";

    public event Action<PerformanceMode, string>? ModeChanged;
    public event Action<EngineStatus>? StatusUpdated;

    public ControlState ControlState => _controlState;
    public PerformanceMode CurrentMode => _currentMode;
    public string LastDecision => _lastDecision;
    public string TransitionProgress => _transitionProgress;

    public AutomationEngine(
        ConfigService configService,
        FileLogger logger,
        CpuMonitor cpuMonitor,
        AsusCpuTemperatureMonitor cpuTemperatureMonitor,
        NvidiaGpuMonitor gpuMonitor,
        GHelperController gHelper)
    {
        _configService = configService;
        _logger = logger;
        _cpuMonitor = cpuMonitor;
        _cpuTemperatureMonitor = cpuTemperatureMonitor;
        _gpuMonitor = gpuMonitor;
        _gHelper = gHelper;

        _timer = new System.Windows.Forms.Timer
        {
            Interval = _configService.Current.PollIntervalMilliseconds
        };
        _timer.Tick += TimerOnTick;
    }

    public void Start()
    {
        _startedAt = MonotonicClock.Now;
        var gHelperSettings = _gHelper.ReadSettings();
        _currentMode = gHelperSettings.CurrentMode ?? PerformanceMode.Unknown;
        _balancedEnteredAt = _currentMode == PerformanceMode.Balanced ? _startedAt : null;
        _turboEnteredAt = _currentMode == PerformanceMode.Turbo ? _startedAt : null;

        _logger.Info($"Starting v{Program.Version}. Current G-Helper mode={_currentMode}; GPU telemetry provider={_gpuMonitor.PreferredProvider}.");

        if (!gHelperSettings.ConfigReadable)
            _logger.Warn($"G-Helper config is not readable at startup: {gHelperSettings.Error ?? "unknown error"}");

        if (gHelperSettings.SkipHotkeys)
            _logger.Warn("G-Helper skip_hotkeys=1: automatic mode switching cannot work until G-Helper hotkeys are enabled.");

        if (gHelperSettings.DisablePowerEvent != 1)
            _logger.Warn("G-Helper disable_power_event is not 1; G-Helper may still change performance mode on AC/battery events.");

        if (gHelperSettings.ScreenAuto == 1)
            _logger.Warn("G-Helper screen_auto=1; refresh-rate automation is still enabled in G-Helper.");

        _timer.Start();
    }

    public void SetDisplayState(DisplayState state)
    {
        if (_disposed)
            return;

        if (_displayState == state)
            return;

        _displayState = state;
        // Do not erase an already-running quiet-evidence clock merely because the display
        // changed state. The target duration changes (active vs. off), but evidence of an
        // otherwise continuously cool/quiet system remains valid.
        _logger.Info($"Display state -> {state}.");
    }

    public void ResetAfterResume()
    {
        if (_disposed)
            return;

        var now = MonotonicClock.Now;
        ResetThresholdTimers(clearAverages: true);
        _cpuMonitor.Reset();
        _cpuTemperatureMonitor.ResetAfterResume();
        _foregroundMonitor.Reset();
        _gpuMonitor.ResetAfterResume();
        _displayState = DisplayState.Unknown;
        _startedAt = now;
        _pendingRequest = null;
        ClearRecentAutomaticCommands();
        _lastModeRequestAt = null;
        _externalModeChangeAt = null;
        _lastTurboExitAt = null;
        _lastTelemetryLogAt = null;
        _stateEpoch++;

        var resumedMode = _gHelper.TryReadCurrentMode();
        if (resumedMode.HasValue && resumedMode.Value != PerformanceMode.Unknown)
            _currentMode = resumedMode.Value;

        _balancedEnteredAt = _currentMode == PerformanceMode.Balanced ? now : null;
        _turboEnteredAt = _currentMode == PerformanceMode.Turbo ? now : null;
        _logger.Info($"System resume detected; telemetry providers and automation timers reset. Current G-Helper mode={_currentMode}.");
    }

    public void SetControlState(ControlState state)
    {
        var changed = _controlState != state;
        _controlState = state;
        _externalModeChangeAt = null;

        if (changed)
        {
            // A user control-state change is an epoch boundary. Any async telemetry tick
            // started under the previous policy must not inject evidence or decisions after
            // the click has already changed the user's intent. A request that was already
            // sent cannot be unsent, so keep its recent-command fingerprint for late
            // classification, but release serialization so an explicit Force command can
            // take effect immediately. If the user's Force target equals the currently
            // observed mode while a DIFFERENT request was already in flight, remember that
            // we owe the user one explicit reassertion of the chosen profile.
            var forceTarget = ForcedTargetFor(state);
            _manualReassertTarget = forceTarget.HasValue
                && _pendingRequest is not null
                && _pendingRequest.Mode != forceTarget.Value
                ? forceTarget
                : null;
            _pendingRequest = null;
            _stateEpoch++;
            _logger.Info($"Control state -> {state}.");
        }

        switch (state)
        {
            case ControlState.ForceSilent:
                TryManualOverrideRequest(PerformanceMode.Silent, "Manual override: Force Silent");
                break;
            case ControlState.ForceBalanced:
                TryManualOverrideRequest(PerformanceMode.Balanced, "Manual override: Force Balanced");
                break;
            case ControlState.ForceTurbo:
                TryManualOverrideRequest(PerformanceMode.Turbo, "Manual override: Force Turbo");
                break;
            case ControlState.Auto:
                _manualReassertTarget = null;
                if (changed)
                    ResetThresholdTimers(clearAverages: false);
                break;
            case ControlState.Pause:
                _manualReassertTarget = null;
                break;
        }
    }

    public void ReloadConfig()
    {
        _timer.Interval = _configService.Current.PollIntervalMilliseconds;
        ResetThresholdTimers(clearAverages: true);
        _foregroundMonitor.Reset();
        // The already-sent request cannot be cancelled. Drop pending serialization but keep
        // the recent-command fingerprint so a delayed G-Helper config write is still
        // recognized as ours rather than as a manual external override.
        _pendingRequest = null;
        _stateEpoch++;
        _logger.Info("Configuration reloaded.");
    }

    private async void TimerOnTick(object? sender, EventArgs e)
    {
        if (_disposed || _tickRunning)
            return;

        _tickRunning = true;
        try
        {
            var tickEpoch = _stateEpoch;

            // GPU fallback sampling may be asynchronous and can take up to its timeout.
            // Finish it first, then take every other sample against one coherent monotonic
            // timestamp. This avoids inflating foreground CPU when a slow nvidia-smi call
            // makes the old tick timestamp lag behind the actual process-time read.
            var gpuTelemetry = await _gpuMonitor.SampleAsync(
                _configService.Current.Thresholds.GpuTelemetryGraceSeconds,
                _configService.Current.Telemetry);
            if (tickEpoch != _stateEpoch)
                return;

            var monotonicNow = MonotonicClock.Now;
            ObserveGHelperMode(monotonicNow);

            var cpuRaw = _cpuMonitor.Sample();
            var cpuTemperature = _cpuTemperatureMonitor.Sample(_configService.Current.Thresholds.CpuTemperatureGraceSeconds);
            var foreground = _foregroundMonitor.Sample(monotonicNow);

            if (_foregroundAverageProcessId != foreground.ProcessId)
            {
                _foregroundCpuAverage.Clear();
                _foregroundAverageProcessId = foreground.ProcessId;
                // Foreground evidence belongs to one process identity, not merely a process
                // name. Two distinct Revit.exe instances cannot splice their CPU evidence.
                _foregroundBalancedSince = null;
                _foregroundTurboSince = null;
            }

            var averageWindow = TimeSpan.FromSeconds(_configService.Current.Thresholds.LoadAverageSeconds);
            var cpuAverage = _cpuAverage.AddAndGetAverage(monotonicNow, cpuRaw, averageWindow);
            var gpuAverage = _gpuAverage.AddAndGetAverage(
                monotonicNow,
                gpuTelemetry.Quality == TelemetryQuality.Fresh ? gpuTelemetry.UtilizationPercent : null,
                averageWindow);
            var foregroundCpuAverage = _foregroundCpuAverage.AddAndGetAverage(
                monotonicNow,
                foreground.CoreEquivalentCpuPercent,
                averageWindow);

            var snapshot = new TelemetrySnapshot(
                DateTime.Now,
                cpuRaw,
                cpuAverage,
                cpuTemperature.TemperatureC,
                cpuTemperature.Quality,
                cpuTemperature.Source,
                cpuTemperature.Error,
                gpuTelemetry.UtilizationPercent,
                gpuAverage,
                double.IsNaN(gpuTelemetry.TemperatureC ?? double.NaN) ? null : gpuTelemetry.TemperatureC,
                gpuTelemetry.Quality,
                gpuTelemetry.Source,
                gpuTelemetry.Error,
                foreground.ProcessName,
                foreground.ProcessId,
                foreground.CoreEquivalentCpuPercent,
                foregroundCpuAverage,
                _displayState);

            LogTelemetryIfConfigured(snapshot, monotonicNow);

            if (_controlState != ControlState.Pause)
                EnforceManualOverride();

            if (_controlState == ControlState.Auto && _configService.Current.AutomationEnabled)
            {
                var decision = Decide(snapshot, monotonicNow);
                if (IsExternalHoldActive(monotonicNow, out var remaining))
                {
                    // A temporary external/manual hold protects the user's chosen mode
                    // against automatic DOWNshifts, but must never suppress a justified
                    // performance promotion. Permanent exact control belongs to Force mode.
                    if (decision.TargetMode.HasValue && IsHigherMode(decision.TargetMode.Value, _currentMode))
                    {
                        _externalModeChangeAt = null;
                        _lastDecision = decision.Reason + " (performance upshift overrides temporary manual hold)";
                        RequestMode(decision.TargetMode.Value, _lastDecision, ignoreCooldown: true);
                    }
                    else
                    {
                        _lastDecision = $"External/manual G-Helper mode protected from downshift for {Math.Ceiling(remaining.TotalSeconds)}s more";
                        _transitionProgress += "; manual hold blocks downshift only";
                    }
                }
                else
                {
                    _lastDecision = decision.Reason;
                    if (decision.TargetMode.HasValue)
                        RequestMode(decision.TargetMode.Value, decision.Reason, decision.IgnoreCooldown);
                }
            }
            else if (_controlState == ControlState.Pause)
            {
                _lastDecision = "Automation paused";
                _transitionProgress = "Paused";
            }
            else if (_controlState == ControlState.Auto && !_configService.Current.AutomationEnabled)
            {
                _lastDecision = "Automation disabled in config";
                _transitionProgress = "Disabled";
            }

            StatusUpdated?.Invoke(new EngineStatus(
                snapshot,
                _controlState,
                _currentMode,
                _pendingRequest?.Mode,
                _lastDecision,
                _transitionProgress,
                GetExternalHoldRemaining(monotonicNow),
                _gHelper.IsGHelperRunning()));
        }
        catch (Exception ex)
        {
            _logger.Error($"Tick failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private Decision Decide(TelemetrySnapshot s, long now)
    {
        var config = _configService.Current;
        var t = config.Thresholds;
        var rawRule = FindAppRule(s.ForegroundProcess, s.DisplayState);
        var rule = StabilizeAppRule(rawRule, s.ForegroundProcess, s.ForegroundProcessId, now, t.AppRuleDebounceSeconds);

        if (rule is not null)
        {
            switch (rule.Action)
            {
                case AppRuleAction.ForceSilent:
                    _transitionProgress = $"Foreground rule {rule.Process} forces Silent";
                    return new Decision(PerformanceMode.Silent, $"Foreground rule {rule.Process}: Force Silent", IgnoreCooldown: true);
                case AppRuleAction.ForceBalanced:
                    _transitionProgress = $"Foreground rule {rule.Process} forces Balanced";
                    return new Decision(PerformanceMode.Balanced, $"Foreground rule {rule.Process}: Force Balanced", IgnoreCooldown: true);
                case AppRuleAction.ForceTurbo:
                    _transitionProgress = $"Foreground rule {rule.Process} forces Turbo";
                    return new Decision(PerformanceMode.Turbo, $"Foreground rule {rule.Process}: Force Turbo", IgnoreCooldown: true);
            }
        }

        var minimumMode = rule?.Action switch
        {
            AppRuleAction.MinimumTurbo => PerformanceMode.Turbo,
            AppRuleAction.MinimumBalanced => PerformanceMode.Balanced,
            _ => PerformanceMode.Silent
        };

        // Activity timers. A many-core CPU can hide single-thread bottlenecks in total
        // utilization, so foreground one-core-equivalent activity is tracked separately.
        UpdateAboveHysteresis(
            ref _balancedCpuSince,
            s.CpuPercentRaw.HasValue ? s.CpuPercentAverage : null,
            t.BalancedCpuPercent,
            t.BalancedCpuResetPercent,
            now);
        UpdateQualityAwareAboveHysteresis(ref _balancedGpuSince, s.GpuPercentAverage, s.GpuTelemetryQuality, t.BalancedGpuPercent, t.BalancedGpuResetPercent, now);
        UpdateAboveHysteresis(
            ref _foregroundBalancedSince,
            s.ForegroundCpuCoreEquivalentAverage,
            t.ForegroundBalancedCoreEquivalentPercent,
            t.ForegroundBalancedCoreEquivalentResetPercent,
            now);

        UpdateAboveHysteresis(
            ref _turboCpuSince,
            s.CpuPercentRaw.HasValue ? s.CpuPercentAverage : null,
            t.TurboCpuPercent,
            t.TurboCpuResetPercent,
            now);
        UpdateQualityAwareAboveHysteresis(ref _turboGpuSince, s.GpuPercentAverage, s.GpuTelemetryQuality, t.TurboGpuPercent, t.TurboGpuResetPercent, now);
        UpdateAboveHysteresis(ref _fastTurboCpuSince, s.CpuPercentRaw, t.FastTurboCpuPercent, t.FastTurboCpuResetPercent, now);
        UpdateQualityAwareAboveHysteresis(
            ref _fastTurboGpuSince,
            s.GpuPercentRaw,
            s.GpuTelemetryQuality,
            t.FastTurboGpuPercent,
            t.FastTurboGpuResetPercent,
            now);

        var foregroundCanEscalate = !string.IsNullOrWhiteSpace(s.ForegroundProcess);
        UpdateAboveHysteresis(
            ref _foregroundTurboSince,
            foregroundCanEscalate ? s.ForegroundCpuCoreEquivalentAverage : null,
            t.ForegroundTurboCoreEquivalentPercent,
            t.ForegroundTurboCoreEquivalentResetPercent,
            now);

        // Thermal promotion evidence is mode-independent and sensor-specific. The
        // configured Balanced and Turbo clocks run in parallel from the instant their
        // own threshold is first observed. A Silent -> Balanced transition therefore
        // cannot restart the Turbo clock. CPU and GPU clocks are separate, so evidence is never
        // accidentally spliced across two different physical sensors.
        UpdateQualityAwareSustainedAbove(
            ref _balancedCpuThermalSince,
            s.CpuTemperatureC,
            s.CpuTemperatureQuality,
            t.BalancedThermalTempC,
            now);
        UpdateQualityAwareSustainedAbove(
            ref _balancedGpuThermalSince,
            s.GpuTemperatureC,
            s.GpuTelemetryQuality,
            t.BalancedThermalTempC,
            now);
        UpdateQualityAwareSustainedAbove(
            ref _turboCpuThermalSince,
            s.CpuTemperatureC,
            s.CpuTemperatureQuality,
            t.TurboThermalTempC,
            now);
        UpdateQualityAwareSustainedAbove(
            ref _turboGpuThermalSince,
            s.GpuTemperatureC,
            s.GpuTelemetryQuality,
            t.TurboThermalTempC,
            now);

        var balancedCpuThermal = SustainedAboveWithQuality(
            s.CpuTemperatureC, s.CpuTemperatureQuality, _balancedCpuThermalSince,
            t.BalancedThermalTempC, t.BalancedThermalSeconds, now);
        var balancedGpuThermal = SustainedAboveWithQuality(
            s.GpuTemperatureC, s.GpuTelemetryQuality, _balancedGpuThermalSince,
            t.BalancedThermalTempC, t.BalancedThermalSeconds, now);
        var turboCpuThermal = SustainedAboveWithQuality(
            s.CpuTemperatureC, s.CpuTemperatureQuality, _turboCpuThermalSince,
            t.TurboThermalTempC, t.TurboThermalSeconds, now);
        var turboGpuThermal = SustainedAboveWithQuality(
            s.GpuTemperatureC, s.GpuTelemetryQuality, _turboGpuThermalSince,
            t.TurboThermalTempC, t.TurboThermalSeconds, now);
        var balancedThermal = balancedCpuThermal || balancedGpuThermal;
        var turboThermal = turboCpuThermal || turboGpuThermal;

        // Hysteresis may preserve an already-running evidence clock through a small dip,
        // but a promotion is only committed on a CURRENT affirmative sample at/above its
        // enter threshold. That prevents latent evidence from firing while the machine is
        // merely sitting in the reset band. GPU completion additionally requires Fresh data.
        var balancedByActivity =
            HysteresisReady(s.CpuPercentAverage, _balancedCpuSince, t.BalancedCpuPercent, t.BalancedCpuSeconds, now)
            || QualityAwareHysteresisReady(s.GpuPercentAverage, s.GpuTelemetryQuality, _balancedGpuSince, t.BalancedGpuPercent, t.BalancedGpuSeconds, now)
            || HysteresisReady(s.ForegroundCpuCoreEquivalentAverage, _foregroundBalancedSince, t.ForegroundBalancedCoreEquivalentPercent, t.ForegroundBalancedSeconds, now)
            || balancedThermal;

        var normalTurbo =
            HysteresisReady(s.CpuPercentAverage, _turboCpuSince, t.TurboCpuPercent, t.TurboCpuSeconds, now)
            || QualityAwareHysteresisReady(s.GpuPercentAverage, s.GpuTelemetryQuality, _turboGpuSince, t.TurboGpuPercent, t.TurboGpuSeconds, now);
        var fastTurbo =
            HysteresisReady(s.CpuPercentRaw, _fastTurboCpuSince, t.FastTurboCpuPercent, t.FastTurboCpuSeconds, now)
            || QualityAwareHysteresisReady(s.GpuPercentRaw, s.GpuTelemetryQuality, _fastTurboGpuSince, t.FastTurboGpuPercent, t.FastTurboGpuSeconds, now);
        var foregroundTurbo = foregroundCanEscalate
            && HysteresisReady(s.ForegroundCpuCoreEquivalentAverage, _foregroundTurboSince, t.ForegroundTurboCoreEquivalentPercent, t.ForegroundTurboSeconds, now);
        var wantsTurbo = minimumMode == PerformanceMode.Turbo || normalTurbo || fastTurbo || foregroundTurbo || turboThermal;

        // Gradual Turbo -> Balanced: low load alone is not enough while the chassis is
        // still hot. Only Fresh/IntervalCache telemetry is affirmative; GraceCache is
        // intentionally context-only and can never manufacture a downshift.
        var observedThermal = ObservedReliableHottestTemperature(s);
        var gpuTelemetryAffirmative = s.GpuTelemetryQuality.IsAffirmative();
        var gpuKnownOrAllowed = (gpuTelemetryAffirmative && s.GpuPercentAverage.HasValue)
            || t.AllowDowngradeWhenGpuUnavailable;
        var cpuThermalCoolForTurboExit = s.CpuTemperatureQuality.IsAffirmative()
            && s.CpuTemperatureC.HasValue
            && s.CpuTemperatureC.Value <= t.TurboExitMaxTempC;
        var gpuThermalCoolForTurboExit = (s.GpuTelemetryQuality.IsAffirmative()
                && s.GpuTemperatureC.HasValue
                && s.GpuTemperatureC.Value <= t.TurboExitMaxTempC)
            || (t.AllowDowngradeWhenGpuUnavailable && !s.GpuTelemetryQuality.IsAffirmative());
        var thermalCoolForTurboExit = cpuThermalCoolForTurboExit && gpuThermalCoolForTurboExit;

        // Downshift evidence is intentionally MODE-SCOPED, unlike promotion evidence.
        // Time spent idle before entering Turbo is not evidence of recovery *from* Turbo,
        // and time spent quiet in Silent is not pre-credit for a later Balanced -> Silent
        // transition. This prevents stale downshift clocks from causing rapid reversals.
        var turboMinimumSatisfied = _currentMode == PerformanceMode.Turbo
            && MonotonicClock.Elapsed(_turboEnteredAt, now) >= TimeSpan.FromSeconds(t.TurboMinimumSeconds);
        var turboExitEnter = turboMinimumSatisfied
            && s.CpuPercentRaw.HasValue
            && s.CpuPercentAverage.HasValue
            && s.CpuPercentAverage.Value < t.TurboExitCpuBelowPercent
            && gpuKnownOrAllowed
            && (!s.GpuPercentAverage.HasValue || s.GpuPercentAverage.Value < t.TurboExitGpuBelowPercent)
            && thermalCoolForTurboExit;
        UpdateStrictCondition(ref _turboExitLowSince, turboExitEnter, now);

        var postTurboCoolingActive = _currentMode == PerformanceMode.Balanced
            && _lastTurboExitAt.HasValue
            && MonotonicClock.Elapsed(_lastTurboExitAt.Value, now) < TimeSpan.FromSeconds(t.PostTurboBalancedSeconds);

        // Balanced -> Silent is stricter than every promotion path. Require one truly
        // continuous cool/quiet qualification window after any mandatory post-Turbo
        // cooling interval. Missing temperature is never interpreted as cool.
        var cpuTempSafeForSilent = s.CpuTemperatureQuality.IsAffirmative()
            && s.CpuTemperatureC.HasValue
            && s.CpuTemperatureC.Value <= t.SilentCpuTempMaxC;
        var gpuTempSafeForSilent = (s.GpuTelemetryQuality.IsAffirmative()
                && s.GpuTemperatureC.HasValue
                && s.GpuTemperatureC.Value <= t.SilentGpuTempMaxC)
            || (t.AllowDowngradeWhenGpuUnavailable && !s.GpuTelemetryQuality.IsAffirmative());

        // A downshift may never race an unresolved promotion. This matters especially on
        // many-core CPUs where one foreground process can be busy while total CPU remains
        // low enough to otherwise satisfy the Silent thresholds. A freshly matched app rule
        // also blocks the downshift during its short debounce window.
        var appPromotionCandidate = rawRule is not null
            && rawRule.Action is not AppRuleAction.ForceSilent;
        var promotionEvidenceActive = appPromotionCandidate || HasPromotionEvidence();

        var silentEnter = _currentMode == PerformanceMode.Balanced
            && !postTurboCoolingActive
            && !promotionEvidenceActive
            && config.PreferSilentAtLowLoad
            && minimumMode == PerformanceMode.Silent
            && s.CpuPercentRaw.HasValue
            && s.CpuPercentAverage.HasValue
            && s.CpuPercentAverage.Value < t.SilentCpuBelowPercent
            && gpuKnownOrAllowed
            && (!s.GpuPercentAverage.HasValue || s.GpuPercentAverage.Value < t.SilentGpuBelowPercent)
            && cpuTempSafeForSilent
            && gpuTempSafeForSilent;
        UpdateStrictCondition(ref _silentEligibleSince, silentEnter, now);

        // IntervalCache may preserve a normal nvidia-smi continuity window, but the actual
        // lowering decision waits for a fresh final GPU sample (unless the user explicitly
        // opted into downgrading without GPU telemetry). CPU temperature must also be fresh.
        var gpuFreshForDownshift = s.GpuTelemetryQuality == TelemetryQuality.Fresh
            || (t.AllowDowngradeWhenGpuUnavailable && !s.GpuTelemetryQuality.IsAffirmative());
        var cpuFreshForDownshift = s.CpuTemperatureQuality == TelemetryQuality.Fresh;
        var turboExitReady = turboExitEnter
            && cpuFreshForDownshift
            && gpuFreshForDownshift
            && MonotonicClock.Elapsed(_turboExitLowSince, now) >= TimeSpan.FromSeconds(t.TurboExitSeconds);
        var silentSeconds = s.DisplayState == DisplayState.Off
            ? t.DisplayOffSilentSeconds
            : t.ActiveDisplaySilentSeconds;
        var silentReady = silentEnter
            && cpuFreshForDownshift
            && gpuFreshForDownshift
            && MonotonicClock.Elapsed(_silentEligibleSince, now) >= TimeSpan.FromSeconds(silentSeconds);

        _transitionProgress = BuildTransitionProgress(rule, now, t, silentSeconds);

        // A downshift hotkey may already be in flight when new load appears. Do not wait
        // for the lower profile to land only to switch back up again. Reassert the current
        // stronger profile as soon as evidence relevant to that rung appears; RequestMode
        // is allowed to preempt a lower pending target for exactly this case.
        if (_currentMode == PerformanceMode.Balanced
            && _pendingRequest?.Mode == PerformanceMode.Silent
            && promotionEvidenceActive)
        {
            return new Decision(
                PerformanceMode.Balanced,
                "New promotion evidence supersedes pending Balanced -> Silent downshift",
                IgnoreCooldown: true);
        }

        var rawTurboRuleCandidate = rawRule?.Action is AppRuleAction.MinimumTurbo or AppRuleAction.ForceTurbo;
        if (_currentMode == PerformanceMode.Turbo
            && _pendingRequest?.Mode == PerformanceMode.Balanced
            && (rawTurboRuleCandidate || HasTurboPromotionEvidence()))
        {
            return new Decision(
                PerformanceMode.Turbo,
                "New Turbo evidence supersedes pending Turbo -> Balanced downshift",
                IgnoreCooldown: true);
        }

        // Mature Turbo thermal evidence already shows that the current profile lacks
        // headroom. Honour it directly, even from Silent, rather than adding another
        // stepwise delay.
        if (turboThermal && _currentMode != PerformanceMode.Turbo)
        {
            return new Decision(
                PerformanceMode.Turbo,
                ThermalReason(s, t.TurboThermalTempC, t.TurboThermalSeconds, "Turbo"),
                IgnoreCooldown: true);
        }

        // Turbo is sticky. It may only fall one rung at a time.
        if (_currentMode == PerformanceMode.Turbo)
        {
            if (wantsTurbo)
                return new Decision(null, TurboReason(s, rule, normalTurbo, fastTurbo, foregroundTurbo, turboThermal));

            var turboAge = MonotonicClock.Elapsed(_turboEnteredAt, now);
            if (turboAge < TimeSpan.FromSeconds(t.TurboMinimumSeconds))
                return new Decision(null, $"Turbo minimum residence ({Math.Ceiling((TimeSpan.FromSeconds(t.TurboMinimumSeconds) - turboAge).TotalSeconds)}s remaining)");

            if (!turboExitReady)
                return new Decision(null, $"Turbo: waiting for low load and thermal recovery (hottest={FmtTemp(observedThermal)})");

            return new Decision(PerformanceMode.Balanced, "Turbo workload/temperature recovered; gradual step-down to Balanced");
        }

        // Explicit app policy is authoritative after debounce.
        if (minimumMode == PerformanceMode.Turbo && _currentMode != PerformanceMode.Turbo)
        {
            return new Decision(
                PerformanceMode.Turbo,
                TurboReason(s, rule, normalTurbo, fastTurbo, foregroundTurbo, turboThermal),
                IgnoreCooldown: true);
        }

        // The fast path is intentionally direct: a burst at the configured fast threshold
        // has already been debounced and should not lose more performance to an extra
        // Silent -> Balanced -> Turbo staging delay.
        if (fastTurbo && _currentMode != PerformanceMode.Turbo)
        {
            return new Decision(
                PerformanceMode.Turbo,
                TurboReason(s, rule, normalTurbo, fastTurbo, foregroundTurbo, turboThermal),
                IgnoreCooldown: true);
        }

        // Normal telemetry-based promotion is direct by default once its own sustained
        // evidence has matured. Users can explicitly opt into a staged Balanced hop; thermal
        // and fast paths above are always direct because they already contain strong evidence.
        if (wantsTurbo)
        {
            if (_currentMode is PerformanceMode.Silent or PerformanceMode.Unknown)
            {
                if (!config.StepwiseAutomaticUpshifts)
                {
                    return new Decision(
                        PerformanceMode.Turbo,
                        TurboReason(s, rule, normalTurbo, fastTurbo, foregroundTurbo, turboThermal),
                        IgnoreCooldown: true);
                }

                return new Decision(
                    PerformanceMode.Balanced,
                    $"Heavy workload detected; controlled ramp stage 1/2 ({TurboReason(s, rule, normalTurbo, fastTurbo, foregroundTurbo, turboThermal)})",
                    IgnoreCooldown: true);
            }

            if (_currentMode == PerformanceMode.Balanced)
            {
                var balancedAge = MonotonicClock.Elapsed(_balancedEnteredAt, now);
                if (balancedAge < TimeSpan.FromSeconds(t.BalancedBeforeTurboSeconds))
                    return new Decision(null, "Heavy workload detected; brief Balanced settling period before Turbo");

                return new Decision(
                    PerformanceMode.Turbo,
                    TurboReason(s, rule, normalTurbo, fastTurbo, foregroundTurbo, turboThermal),
                    IgnoreCooldown: fastTurbo);
            }
        }

        // Startup grace suppresses only downshifts.
        if (MonotonicClock.Elapsed(_startedAt, now) < TimeSpan.FromSeconds(config.StartupGraceSeconds))
            return new Decision(null, $"Startup grace ({config.StartupGraceSeconds}s): observing before any downshift");

        if (_currentMode == PerformanceMode.Silent)
        {
            if (minimumMode == PerformanceMode.Balanced)
            {
                return new Decision(
                    PerformanceMode.Balanced,
                    $"Foreground rule {rule!.Process}: minimum Balanced",
                    IgnoreCooldown: true);
            }

            if (balancedByActivity)
            {
                var reason = balancedThermal
                    ? ThermalReason(s, t.BalancedThermalTempC, t.BalancedThermalSeconds, "Balanced")
                    : $"Moderate sustained activity (CPU avg={Fmt(s.CpuPercentAverage)}%, GPU avg={Fmt(s.GpuPercentAverage)}%, foreground={Fmt(s.ForegroundCpuCoreEquivalentAverage)}%core)";
                return new Decision(
                    PerformanceMode.Balanced,
                    $"Silent -> Balanced: {reason}",
                    IgnoreCooldown: true);
            }

            return new Decision(null, "Silent remains appropriate: load and temperature are below promotion thresholds");
        }

        if (_currentMode == PerformanceMode.Balanced)
        {
            if (minimumMode == PerformanceMode.Balanced)
                return new Decision(null, $"Foreground rule {rule!.Process}: holding at least Balanced");

            if (postTurboCoolingActive)
            {
                // Cooling and Silent qualification are sequential, never concurrent.
                var coolingRemaining = TimeSpan.FromSeconds(t.PostTurboBalancedSeconds)
                    - MonotonicClock.Elapsed(_lastTurboExitAt!.Value, now);
                return new Decision(null, $"Post-Turbo Balanced cooling ({Math.Ceiling(coolingRemaining.TotalSeconds)}s remaining); Silent timer paused");
            }

            if (silentReady)
            {
                var context = s.DisplayState switch
                {
                    DisplayState.Off => "display off",
                    DisplayState.On or DisplayState.Dimmed => "display active",
                    _ => "display state not yet observed (conservative active-display timing)"
                };
                return new Decision(
                    PerformanceMode.Silent,
                    $"Sustained cool/light workload with {context}; Balanced -> Silent after {silentSeconds}s "
                    + $"(CPU avg={Fmt(s.CpuPercentAverage)}%, CPU temp={FmtTemp(s.CpuTemperatureC)}, "
                    + $"GPU avg={Fmt(s.GpuPercentAverage)}%, GPU temp={FmtTemp(s.GpuTemperatureC)})");
            }

            return new Decision(null, "Balanced remains appropriate: no Turbo trigger and not yet cool/quiet enough for Silent");
        }

        if (_currentMode == PerformanceMode.Unknown)
        {
            if (minimumMode == PerformanceMode.Balanced || balancedByActivity)
                return new Decision(PerformanceMode.Balanced, "Current mode unknown; activity/app/thermal signal indicates Balanced", IgnoreCooldown: true);

            if (silentReady)
                return new Decision(PerformanceMode.Silent, "Current mode unknown; sustained cool low load indicates Silent", IgnoreCooldown: true);

            return new Decision(PerformanceMode.Balanced, "Current G-Helper mode unknown; temporary safe baseline Balanced");
        }

        if (_currentMode is PerformanceMode.Custom1 or PerformanceMode.Custom2)
        {
            if (minimumMode == PerformanceMode.Balanced || balancedByActivity)
                return new Decision(PerformanceMode.Balanced, $"External/custom mode {_currentMode}: adaptive activity indicates Balanced", IgnoreCooldown: true);

            if (silentReady)
                return new Decision(PerformanceMode.Silent, $"External/custom mode {_currentMode}: sustained cool low load indicates Silent", IgnoreCooldown: true);

            return new Decision(PerformanceMode.Balanced, $"External/custom mode {_currentMode}: re-entering adaptive control through Balanced");
        }

        return new Decision(null, $"Mode {_currentMode} is not an adaptive target");
    }

    private AppRule? StabilizeAppRule(AppRule? rawRule, string foregroundProcess, int? foregroundProcessId, long now, int debounceSeconds)
    {
        if (rawRule is null)
        {
            _candidateRuleKey = null;
            _candidateRuleSince = null;
            return null;
        }

        var key = $"{rawRule.Process}|{rawRule.Action}|{foregroundProcess}|{foregroundProcessId?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
        if (!string.Equals(_candidateRuleKey, key, StringComparison.OrdinalIgnoreCase))
        {
            _candidateRuleKey = key;
            _candidateRuleSince = now;
            return debounceSeconds == 0 ? rawRule : null;
        }

        return MonotonicClock.Elapsed(_candidateRuleSince, now) >= TimeSpan.FromSeconds(debounceSeconds)
            ? rawRule
            : null;
    }

    private string BuildTransitionProgress(AppRule? rule, long now, ThresholdConfig t, int silentSeconds)
    {
        static string Timer(long? since, long nowValue, int targetSeconds) =>
            $"{Math.Min(targetSeconds, (int)Math.Floor(MonotonicClock.Elapsed(since, nowValue).TotalSeconds))}/{targetSeconds}s";

        var ruleText = rule is null ? "none" : $"{rule.Process}:{rule.Action}";
        return $"rule={ruleText}; "
            + $"to Balanced CPU {Timer(_balancedCpuSince, now, t.BalancedCpuSeconds)}, GPU {Timer(_balancedGpuSince, now, t.BalancedGpuSeconds)}, "
            + $"foreground {Timer(_foregroundBalancedSince, now, t.ForegroundBalancedSeconds)}, "
            + $"thermal CPU {Timer(_balancedCpuThermalSince, now, t.BalancedThermalSeconds)}, GPU {Timer(_balancedGpuThermalSince, now, t.BalancedThermalSeconds)}; "
            + $"to Turbo CPU {Timer(_turboCpuSince, now, t.TurboCpuSeconds)}, GPU {Timer(_turboGpuSince, now, t.TurboGpuSeconds)}, "
            + $"fast CPU {Timer(_fastTurboCpuSince, now, t.FastTurboCpuSeconds)}, fast GPU {Timer(_fastTurboGpuSince, now, t.FastTurboGpuSeconds)}, "
            + $"foreground {Timer(_foregroundTurboSince, now, t.ForegroundTurboSeconds)}, "
            + $"thermal CPU {Timer(_turboCpuThermalSince, now, t.TurboThermalSeconds)}, GPU {Timer(_turboGpuThermalSince, now, t.TurboThermalSeconds)}; "
            + $"Turbo-exit {Timer(_turboExitLowSince, now, t.TurboExitSeconds)}; to Silent {Timer(_silentEligibleSince, now, silentSeconds)}";
    }

    private static string TurboReason(
        TelemetrySnapshot s,
        AppRule? rule,
        bool normalTurbo,
        bool fastTurbo,
        bool foregroundTurbo,
        bool thermalTurbo)
    {
        if (rule?.Action == AppRuleAction.MinimumTurbo)
            return $"Foreground rule {rule.Process}: minimum Turbo";
        if (thermalTurbo)
            return $"Sustained thermal pressure (CPU={FmtTemp(s.CpuTemperatureC)}, GPU={FmtTemp(s.GpuTemperatureC)})";
        if (fastTurbo)
            return $"Immediate heavy load (CPU raw={Fmt(s.CpuPercentRaw)}%, GPU raw={Fmt(s.GpuPercentRaw)}%)";
        if (foregroundTurbo)
            return $"Foreground {s.ForegroundProcess} is CPU-bound ({Fmt(s.ForegroundCpuCoreEquivalentAverage)}% of one-core equivalent average)";
        if (normalTurbo)
            return $"Sustained heavy load (CPU avg={Fmt(s.CpuPercentAverage)}%, GPU avg={Fmt(s.GpuPercentAverage)}%)";
        return "Turbo requested";
    }

    private static string ThermalReason(TelemetrySnapshot s, double thresholdC, int seconds, string target) =>
        $"temperature >= {thresholdC:0}C for {seconds}s -> {target} "
        + $"(CPU={FmtTemp(s.CpuTemperatureC)}, GPU={FmtTemp(s.GpuTemperatureC)})";

    private static bool SustainedAboveWithQuality(
        double? value,
        TelemetryQuality quality,
        long? since,
        double threshold,
        int seconds,
        long now) =>
        quality == TelemetryQuality.Fresh
        && value.HasValue
        && value.Value >= threshold
        && MonotonicClock.Elapsed(since, now) >= TimeSpan.FromSeconds(seconds);

    private static double? ObservedReliableHottestTemperature(TelemetrySnapshot s)
    {
        double? hottest = null;

        if (s.CpuTemperatureQuality.IsAffirmative() && s.CpuTemperatureC.HasValue)
            hottest = s.CpuTemperatureC.Value;

        if (s.GpuTelemetryQuality.IsAffirmative() && s.GpuTemperatureC.HasValue)
            hottest = !hottest.HasValue ? s.GpuTemperatureC.Value : Math.Max(hottest.Value, s.GpuTemperatureC.Value);

        return hottest;
    }

    private bool HasPromotionEvidence() =>
        _balancedCpuSince.HasValue
        || _balancedGpuSince.HasValue
        || _foregroundBalancedSince.HasValue
        || _balancedCpuThermalSince.HasValue
        || _balancedGpuThermalSince.HasValue
        || _turboCpuSince.HasValue
        || _turboGpuSince.HasValue
        || _fastTurboCpuSince.HasValue
        || _fastTurboGpuSince.HasValue
        || _foregroundTurboSince.HasValue
        || _turboCpuThermalSince.HasValue
        || _turboGpuThermalSince.HasValue;

    private bool HasTurboPromotionEvidence() =>
        _turboCpuSince.HasValue
        || _turboGpuSince.HasValue
        || _fastTurboCpuSince.HasValue
        || _fastTurboGpuSince.HasValue
        || _foregroundTurboSince.HasValue
        || _turboCpuThermalSince.HasValue
        || _turboGpuThermalSince.HasValue;

    private void ObserveGHelperMode(long now)
    {
        var settings = _gHelper.ReadSettings();
        var observed = settings.CurrentMode;

        if (_pendingRequest is not null)
        {
            if (observed.HasValue && observed.Value == _pendingRequest.Mode)
            {
                ConfirmMode(observed.Value, _pendingRequest.Reason, now);
                _recentAutomaticCommands.Remove(observed.Value);
                _pendingRequest = null;
                return;
            }

            if (MonotonicClock.Elapsed(_pendingRequest.RequestedAt, now)
                >= TimeSpan.FromSeconds(_configService.Current.ModeConfirmationTimeoutSeconds))
            {
                LogRequestProblem(
                    $"Mode request {_pendingRequest.Mode} was not confirmed within {_configService.Current.ModeConfirmationTimeoutSeconds}s.",
                    now);
                _pendingRequest = null;
            }
            else
            {
                return;
            }
        }

        PruneRecentAutomaticCommands(now);

        if (observed.HasValue
            && observed.Value != PerformanceMode.Unknown
            && observed.Value != _currentMode
            && _recentAutomaticCommands.TryGetValue(observed.Value, out var recent))
        {
            // More than one exact profile request can legitimately be in the late-write
            // window over time (for example Balanced times out, then Turbo is requested,
            // then G-Helper persists the older Balanced write). Recognize any recent
            // automatic target instead of misclassifying that delayed write as a manual
            // user override.
            ConfirmMode(observed.Value, recent.Reason + " (late G-Helper confirmation)", now);
            _recentAutomaticCommands.Remove(observed.Value);
            return;
        }

        if (!observed.HasValue || observed.Value == PerformanceMode.Unknown || observed.Value == _currentMode)
            return;

        var previous = _currentMode;
        ApplyObservedModeTransition(previous, observed.Value, now);
        ClearRecentAutomaticCommands();

        if (previous == PerformanceMode.Unknown)
        {
            _logger.Info($"Adopted current G-Helper mode: {_currentMode}.");
            return;
        }

        _logger.Info($"Observed external G-Helper mode change: {previous} -> {_currentMode}.");
        if (_controlState == ControlState.Auto
            && _configService.Current.RespectExternalModeChanges
            && _configService.Current.ExternalModeChangeHoldSeconds > 0)
        {
            _externalModeChangeAt = now;
            _logger.Info($"Auto switching held for {_configService.Current.ExternalModeChangeHoldSeconds}s to respect external/manual mode change.");
        }
    }

    private void ConfirmMode(PerformanceMode mode, string reason, long now)
    {
        var previous = _currentMode;
        ApplyObservedModeTransition(previous, mode, now);
        _logger.Info($"MODE CONFIRMED: {previous} -> {mode}. Reason: {reason}");
        _gHelper.RecordConfirmation(mode, reason);
        ModeChanged?.Invoke(mode, reason);
    }

    private void ApplyObservedModeTransition(PerformanceMode previous, PerformanceMode mode, long now)
    {
        _currentMode = mode;

        // IMPORTANT INVARIANT: never reset promotion-evidence clocks here. CPU/GPU load,
        // foreground load and both thermal ladders represent continuous physical evidence
        // and must survive Silent -> Balanced -> Turbo transitions. Only downshift clocks
        // are mode-scoped and reset at their relevant boundary.
        if (mode == PerformanceMode.Balanced && previous != PerformanceMode.Balanced)
        {
            _balancedEnteredAt = now;
            _silentEligibleSince = null;
        }
        else if (mode != PerformanceMode.Balanced)
        {
            _balancedEnteredAt = null;
        }

        if (mode == PerformanceMode.Turbo && previous != PerformanceMode.Turbo)
        {
            _turboEnteredAt = now;
            _turboExitLowSince = null;
            _silentEligibleSince = null;
            _lastTurboExitAt = null;
        }
        else if (mode != PerformanceMode.Turbo)
        {
            _turboEnteredAt = null;
        }

        if (previous == PerformanceMode.Turbo && mode != PerformanceMode.Turbo)
        {
            _lastTurboExitAt = now;
            _turboExitLowSince = null;
            _silentEligibleSince = null;
        }

        if (mode == PerformanceMode.Silent)
        {
            _turboExitLowSince = null;
            _silentEligibleSince = null;
        }
    }

    private void EnforceManualOverride()
    {
        var desired = ForcedTargetFor(_controlState);
        if (desired.HasValue)
            TryManualOverrideRequest(desired.Value, $"Manual override: {_controlState}");
    }

    private static PerformanceMode? ForcedTargetFor(ControlState state) => state switch
    {
        ControlState.ForceSilent => PerformanceMode.Silent,
        ControlState.ForceBalanced => PerformanceMode.Balanced,
        ControlState.ForceTurbo => PerformanceMode.Turbo,
        _ => null
    };

    private void TryManualOverrideRequest(PerformanceMode desired, string reason)
    {
        var forceReassert = _manualReassertTarget == desired;
        if (RequestMode(desired, reason, ignoreCooldown: true, forceSendIfCurrent: forceReassert)
            && forceReassert)
        {
            _manualReassertTarget = null;
        }
    }

    private AppRule? FindAppRule(string foregroundProcess, DisplayState displayState)
    {
        if (string.IsNullOrWhiteSpace(foregroundProcess))
            return null;

        return _configService.Current.AppRules.FirstOrDefault(rule =>
            rule.Enabled
            && (displayState != DisplayState.Off || rule.ApplyWhenDisplayOff)
            && !string.IsNullOrWhiteSpace(rule.Process)
            && ProcessMatches(rule.Process, foregroundProcess));
    }

    private bool RequestMode(PerformanceMode mode, string reason, bool ignoreCooldown = false, bool forceSendIfCurrent = false)
    {
        if (mode is not (PerformanceMode.Silent or PerformanceMode.Balanced or PerformanceMode.Turbo))
            return false;

        if (_pendingRequest?.Mode == mode)
            return true;

        var preemptingLowerPending = _pendingRequest is not null
            && IsHigherMode(mode, _pendingRequest.Mode);

        if (_pendingRequest is not null && !preemptingLowerPending)
        {
            // One ordinary hardware/profile transition at a time. Stronger evidence keeps
            // accumulating, but equal/lower requests wait for the current transition.
            _lastDecision = $"{mode} request deferred while {_pendingRequest.Mode} confirmation is pending";
            return false;
        }

        var reassertCurrentToCancelDownshift = preemptingLowerPending && _currentMode == mode;
        if (preemptingLowerPending)
        {
            _logger.Info($"Preempting pending {_pendingRequest!.Mode} request with stronger {mode} request.");
            _pendingRequest = null;
        }

        if (_currentMode == mode && !reassertCurrentToCancelDownshift && !forceSendIfCurrent)
            return true;

        var now = MonotonicClock.Now;

        var cooldown = TimeSpan.FromSeconds(_configService.Current.Thresholds.ModeChangeCooldownSeconds);
        if (!ignoreCooldown
            && _lastModeRequestAt.HasValue
            && MonotonicClock.Elapsed(_lastModeRequestAt.Value, now) < cooldown)
        {
            return false;
        }

        if (_configService.Current.RequireGHelperRunning && !_gHelper.IsGHelperRunning())
        {
            if (!_warnedGHelperMissing)
            {
                _logger.Warn("G-Helper is not running; mode switch skipped.");
                _warnedGHelperMissing = true;
            }
            return false;
        }

        _warnedGHelperMissing = false;
        var result = _gHelper.RequestMode(
            mode,
            _configService.Current.InputGuardEnabled,
            _displayState);

        if (result.Status == ModeRequestStatus.Deferred)
        {
            _lastDecision = $"Mode change deferred: {result.Message}";
            return false;
        }

        if (result.Status == ModeRequestStatus.Failed)
        {
            LogRequestProblem($"Mode request {mode} failed. Reason: {reason}. {result.Message}", now);
            return false;
        }

        _pendingRequest = new PendingModeRequest(mode, reason, now);
        _recentAutomaticCommands[mode] = (now, reason);
        _lastModeRequestAt = now;
        _logger.Info($"MODE REQUEST -> {mode}. Reason: {reason}. {result.Message}");
        return true;
    }

    private void PruneRecentAutomaticCommands(long now)
    {
        var window = LateConfirmationWindow();
        foreach (var mode in _recentAutomaticCommands
                     .Where(pair => MonotonicClock.Elapsed(pair.Value.RequestedAt, now) > window)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _recentAutomaticCommands.Remove(mode);
        }
    }

    private void ClearRecentAutomaticCommands() => _recentAutomaticCommands.Clear();

    private void LogRequestProblem(string message, long now)
    {
        var sameProblem = string.Equals(message, _lastRequestProblemMessage, StringComparison.Ordinal);
        var recentlyLogged = _lastRequestProblemAt.HasValue
            && MonotonicClock.Elapsed(_lastRequestProblemAt.Value, now) < TimeSpan.FromSeconds(60);

        if (sameProblem && recentlyLogged)
            return;

        _lastRequestProblemMessage = message;
        _lastRequestProblemAt = now;
        _logger.Warn(message);
    }

    private TimeSpan LateConfirmationWindow() => TimeSpan.FromSeconds(
        _configService.Current.ModeConfirmationTimeoutSeconds
        + _configService.Current.LateConfirmationGraceSeconds);

    private bool IsExternalHoldActive(long now, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_externalModeChangeAt.HasValue || !_configService.Current.RespectExternalModeChanges)
            return false;

        var hold = TimeSpan.FromSeconds(_configService.Current.ExternalModeChangeHoldSeconds);
        var elapsed = MonotonicClock.Elapsed(_externalModeChangeAt.Value, now);
        if (elapsed >= hold)
        {
            _externalModeChangeAt = null;
            _logger.Info("External/manual mode hold expired; Auto control resumed.");
            return false;
        }

        remaining = hold - elapsed;
        return true;
    }

    private TimeSpan? GetExternalHoldRemaining(long now)
    {
        if (!_externalModeChangeAt.HasValue || !_configService.Current.RespectExternalModeChanges)
            return null;

        var hold = TimeSpan.FromSeconds(_configService.Current.ExternalModeChangeHoldSeconds);
        var elapsed = MonotonicClock.Elapsed(_externalModeChangeAt.Value, now);
        return elapsed < hold ? hold - elapsed : null;
    }

    private void LogTelemetryIfConfigured(TelemetrySnapshot snapshot, long now)
    {
        var interval = _configService.Current.Logging.TelemetryIntervalSeconds;
        if (interval <= 0 || !_configService.Current.Logging.Enabled)
            return;

        if (_lastTelemetryLogAt.HasValue
            && MonotonicClock.Elapsed(_lastTelemetryLogAt.Value, now) < TimeSpan.FromSeconds(interval))
            return;

        _lastTelemetryLogAt = now;
        _logger.Info(
            $"TELEMETRY CPU={Fmt(snapshot.CpuPercentRaw)}% avg={Fmt(snapshot.CpuPercentAverage)}% temp={FmtTemp(snapshot.CpuTemperatureC)} "
            + $"tempSource={snapshot.CpuTemperatureSource} tempQuality={snapshot.CpuTemperatureQuality}; "
            + $"GPU={Fmt(snapshot.GpuPercentRaw)}% avg={Fmt(snapshot.GpuPercentAverage)}% temp={FmtTemp(snapshot.GpuTemperatureC)} "
            + $"source={snapshot.GpuTelemetrySource} quality={snapshot.GpuTelemetryQuality}; "
            + $"foreground={snapshot.ForegroundProcess} pid={snapshot.ForegroundProcessId?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} fgCore={Fmt(snapshot.ForegroundCpuCoreEquivalentRaw)}% avg={Fmt(snapshot.ForegroundCpuCoreEquivalentAverage)}%; "
            + $"display={snapshot.DisplayState}.");
    }

    private static bool IsHigherMode(PerformanceMode candidate, PerformanceMode current) =>
        (current is PerformanceMode.Silent or PerformanceMode.Balanced or PerformanceMode.Turbo)
        && (candidate is PerformanceMode.Silent or PerformanceMode.Balanced or PerformanceMode.Turbo)
        && ModeRank(candidate) > ModeRank(current);

    private static int ModeRank(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Silent => 0,
        PerformanceMode.Balanced => 1,
        PerformanceMode.Turbo => 2,
        _ => -1
    };

    private static bool HysteresisReady(
        double? value,
        long? since,
        double enterAtOrAbove,
        int seconds,
        long now) =>
        value.HasValue
        && value.Value >= enterAtOrAbove
        && MonotonicClock.Elapsed(since, now) >= TimeSpan.FromSeconds(seconds);

    private static bool QualityAwareHysteresisReady(
        double? value,
        TelemetryQuality quality,
        long? since,
        double enterAtOrAbove,
        int seconds,
        long now) =>
        quality == TelemetryQuality.Fresh
        && HysteresisReady(value, since, enterAtOrAbove, seconds, now);

    private static void UpdateAboveHysteresis(ref long? since, double? value, double enterAtOrAbove, double resetBelow, long now)
    {
        if (!value.HasValue)
        {
            since = null;
            return;
        }

        if (value.Value >= enterAtOrAbove)
            since ??= now;
        else if (value.Value < resetBelow)
            since = null;
    }

    private static void UpdateQualityAwareAboveHysteresis(
        ref long? since,
        double? value,
        TelemetryQuality quality,
        double enterAtOrAbove,
        double resetBelow,
        long now)
    {
        // Fresh data may start or reset evidence. Intentional short provider caching
        // preserves an already-running clock without adding duplicate rolling-average
        // samples. A provider failure (GraceCache/Unavailable) breaks continuity.
        if (quality is TelemetryQuality.GraceCache or TelemetryQuality.Unavailable || !value.HasValue)
        {
            since = null;
            return;
        }

        if (quality == TelemetryQuality.IntervalCache)
            return;

        if (value.Value >= enterAtOrAbove)
            since ??= now;
        else if (value.Value < resetBelow)
            since = null;
    }

    private static void UpdateQualityAwareSustainedAbove(
        ref long? since,
        double? value,
        TelemetryQuality quality,
        double threshold,
        long now)
    {
        // Thermal thresholds mean continuous physical evidence. A normal nvidia-smi
        // interval cache may bridge the known short gap between provider polls, but a
        // real telemetry failure or missing field breaks the run immediately.
        if (quality is TelemetryQuality.GraceCache or TelemetryQuality.Unavailable || !value.HasValue)
        {
            since = null;
            return;
        }

        if (quality == TelemetryQuality.IntervalCache)
            return;

        if (value.Value >= threshold)
            since ??= now;
        else
            since = null;
    }

    private static void UpdateStrictCondition(ref long? since, bool condition, long now)
    {
        if (condition)
            since ??= now;
        else
            since = null;
    }

    private void ResetThresholdTimers(bool clearAverages)
    {
        // This is a session/control-boundary reset only (resume, config reload, explicit
        // return to Auto). Normal Silent/Balanced/Turbo transitions MUST NOT call this:
        // promotion evidence has to survive intermediate mode changes.
        _balancedCpuSince = null;
        _balancedGpuSince = null;
        _foregroundBalancedSince = null;
        _balancedCpuThermalSince = null;
        _balancedGpuThermalSince = null;
        _turboCpuThermalSince = null;
        _turboGpuThermalSince = null;
        _turboCpuSince = null;
        _turboGpuSince = null;
        _fastTurboCpuSince = null;
        _fastTurboGpuSince = null;
        _foregroundTurboSince = null;
        _turboExitLowSince = null;
        _silentEligibleSince = null;
        _candidateRuleKey = null;
        _candidateRuleSince = null;

        if (clearAverages)
        {
            _cpuAverage.Clear();
            _gpuAverage.Clear();
            _foregroundCpuAverage.Clear();
            _foregroundAverageProcessId = null;
        }
    }

    private static bool ProcessMatches(string pattern, string foregroundProcess)
    {
        try
        {
            return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                NormalizeProcess(pattern),
                NormalizeProcess(foregroundProcess),
                ignoreCase: true);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProcess(string process)
    {
        var value = process.Trim().Replace('/', '\\');
        var separator = value.LastIndexOf('\\');
        if (separator >= 0 && separator + 1 < value.Length)
            value = value[(separator + 1)..];

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    private static string Fmt(double? value) => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) : "n/a";
    private static string FmtTemp(double? value) => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) + "C" : "n/a";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stateEpoch++;
        _timer.Stop();
        _timer.Tick -= TimerOnTick;
        _timer.Dispose();
        _cpuTemperatureMonitor.Dispose();
        _gpuMonitor.Dispose();
        GC.SuppressFinalize(this);
    }
}
