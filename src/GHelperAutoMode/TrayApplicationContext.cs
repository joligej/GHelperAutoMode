using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly ConfigService _configService;
    private readonly FileLogger _logger;
    private readonly PowerNotificationWindow _powerWindow;
    private readonly AutomationEngine _engine;
    private readonly GHelperController _gHelper;
    private readonly KeyboardLightingManager _lightingManager;
    private readonly NotifyIcon _notifyIcon;
    private Icon? _trayIconImage;
    private ControlState? _lastIconControlState;
    private PerformanceMode? _lastIconMode;

    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _silentItem;
    private readonly ToolStripMenuItem _balancedItem;
    private readonly ToolStripMenuItem _turboItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _loggingItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _preferSilentItem;
    private readonly ToolStripMenuItem _stepwiseUpshiftItem;
    private readonly ToolStripMenuItem _lightingMenu;
    private readonly ToolStripMenuItem _lightingUnmanagedItem;
    private readonly ToolStripMenuItem _lightingDynamicItem;
    private readonly ToolStripMenuItem _lightingAccentItem;
    private readonly ToolStripMenuItem _lightingManualItem;
    private readonly ToolStripMenuItem _lightingStatusItem;
    private readonly ToolStripMenuItem _statusItem;

    private EngineStatus? _lastStatus;
    private bool _updatingUi;

    public TrayApplicationContext()
    {
        _ = _dispatcher.Handle;
        _configService = new ConfigService();
        _logger = new FileLogger(_configService);
        _powerWindow = new PowerNotificationWindow();
        _powerWindow.ExitRequested += RequestExit;
        _gHelper = new GHelperController();
        _lightingManager = new KeyboardLightingManager(_configService, _logger, _gHelper);

        if (!_powerWindow.DisplayNotificationRegistered)
        {
            _logger.Warn(
                $"Windows display power notifications could not be registered (Win32 error {_powerWindow.DisplayRegistrationError}); "
                + "display-off automation will remain disabled/conservative until registration succeeds after a restart.");
        }
        if (!_powerWindow.SessionNotificationRegistered)
        {
            _logger.Warn(
                $"Windows session notifications could not be registered (Win32 error {_powerWindow.SessionRegistrationError}); "
                + "AutoMode will still check Dynamic Lighting control regularly.");
        }

        var cpu = new CpuMonitor();
        var cpuTemperature = new AsusCpuTemperatureMonitor();
        var gpu = new NvidiaGpuMonitor();
        _engine = new AutomationEngine(_configService, _logger, cpu, cpuTemperature, gpu, _gHelper);

        _autoItem = MakeControlItem("Auto", ControlState.Auto);
        _silentItem = MakeControlItem("Force Silent", ControlState.ForceSilent);
        _balancedItem = MakeControlItem("Force Balanced", ControlState.ForceBalanced);
        _turboItem = MakeControlItem("Force Turbo", ControlState.ForceTurbo);
        _pauseItem = MakeControlItem("Pause automation", ControlState.Pause);

        _preferSilentItem = new ToolStripMenuItem("Use Silent at low load")
        {
            Checked = _configService.Current.PreferSilentAtLowLoad,
            CheckOnClick = true
        };
        _preferSilentItem.CheckedChanged += (_, _) => ToggleAdaptiveOption(
            _preferSilentItem,
            value => _configService.Current.PreferSilentAtLowLoad = value,
            "Use Silent at low load");

        _stepwiseUpshiftItem = new ToolStripMenuItem("Pass through Balanced before Turbo")
        {
            Checked = _configService.Current.StepwiseAutomaticUpshifts,
            CheckOnClick = true
        };
        _stepwiseUpshiftItem.CheckedChanged += (_, _) => ToggleAdaptiveOption(
            _stepwiseUpshiftItem,
            value => _configService.Current.StepwiseAutomaticUpshifts = value,
            "Pass through Balanced before Turbo");

        var adaptiveMenu = new ToolStripMenuItem("Automatic switching");
        adaptiveMenu.DropDownItems.Add(_preferSilentItem);
        adaptiveMenu.DropDownItems.Add(_stepwiseUpshiftItem);

        _lightingUnmanagedItem = MakeLightingItem(
            "Leave lighting alone",
            KeyboardLightingMode.Unmanaged);
        _lightingDynamicItem = MakeLightingItem(
            "Windows controls lighting",
            KeyboardLightingMode.WindowsDynamicLighting);
        _lightingAccentItem = MakeLightingItem(
            "G-Helper uses the Windows accent color",
            KeyboardLightingMode.GHelperWindowsAccent);
        _lightingManualItem = MakeLightingItem(
            "G-Helper keeps its current Aura settings",
            KeyboardLightingMode.GHelperManual);
        _lightingStatusItem = new ToolStripMenuItem("Status: starting...")
        {
            Enabled = false
        };

        var openDynamicLightingSettingsItem = new ToolStripMenuItem("Open Windows Dynamic Lighting settings...");
        openDynamicLightingSettingsItem.Click += (_, _) => OpenPath("ms-settings:personalization-lighting");

        var reapplyLightingOwnershipItem = new ToolStripMenuItem("Apply this lighting choice again");
        reapplyLightingOwnershipItem.Click += async (_, _) => await ReapplyLightingOwnershipAsync();

        _lightingMenu = new ToolStripMenuItem("Keyboard lighting");
        _lightingMenu.DropDownItems.AddRange([
            _lightingUnmanagedItem,
            _lightingDynamicItem,
            _lightingAccentItem,
            _lightingManualItem,
            new ToolStripSeparator(),
            reapplyLightingOwnershipItem,
            _lightingStatusItem,
            openDynamicLightingSettingsItem
        ]);
        UpdateLightingChecks(_configService.Current.KeyboardLighting.Mode);
        UpdateLightingStatus();

        _loggingItem = new ToolStripMenuItem("Save logs")
        {
            Checked = _configService.Current.Logging.Enabled,
            CheckOnClick = true
        };
        _loggingItem.CheckedChanged += (_, _) => ToggleLogging();

        _startupItem = new ToolStripMenuItem("Run at Windows login (Task Scheduler)")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += (_, _) => ToggleStartup();

        _statusItem = new ToolStripMenuItem("Starting...");
        _statusItem.Click += (_, _) => ShowDiagnostics();

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => ShowSettings();

        var diagnosticsItem = new ToolStripMenuItem("Show diagnostics...");
        diagnosticsItem.Click += (_, _) => ShowDiagnostics();

        var copyDiagnosticsItem = new ToolStripMenuItem("Copy diagnostics");
        copyDiagnosticsItem.Click += (_, _) => CopyDiagnostics();

        var openConfigItem = new ToolStripMenuItem("Open AutoMode config");
        openConfigItem.Click += (_, _) => OpenPath(_configService.ConfigPath);

        var openGHelperConfigItem = new ToolStripMenuItem("Open G-Helper config");
        openGHelperConfigItem.Click += (_, _) => OpenPath(_gHelper.ConfigPath);

        var openLogsItem = new ToolStripMenuItem("Open log folder");
        openLogsItem.Click += (_, _) => OpenPath(_configService.LogDirectory);

        var reloadItem = new ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, _) => ReloadConfig();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.AddRange([_autoItem, _silentItem, _balancedItem, _turboItem, _pauseItem]);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(adaptiveMenu);
        menu.Items.Add(_lightingMenu);
        menu.Items.Add(_loggingItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(copyDiagnosticsItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(openGHelperConfigItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIconImage = TrayIconFactory.Create(ControlState.Auto, _engine.CurrentMode);
        _lastIconControlState = ControlState.Auto;
        _lastIconMode = _engine.CurrentMode;
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "G-Helper Auto Mode",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        _powerWindow.DisplayStateChanged += _engine.SetDisplayState;
        _powerWindow.Resumed += _engine.ResetAfterResume;
        _powerWindow.Resumed += PowerWindowOnResumeForLighting;
        _powerWindow.SessionReady += PowerWindowOnSessionReady;
        _engine.ModeChanged += EngineOnModeChanged;
        _engine.StatusUpdated += EngineOnStatusUpdated;
        _lightingManager.StatusChanged += LightingManagerOnStatusChanged;

        UpdateControlChecks(ControlState.Auto);
        _engine.Start();
        _lightingManager.Start();

        if (!string.IsNullOrWhiteSpace(_configService.LastLoadWarning))
            ShowConfigWarning(_configService.LastLoadWarning);

        if (!string.IsNullOrWhiteSpace(StartupManager.LastMaintenanceWarning))
        {
            _logger.Warn(StartupManager.LastMaintenanceWarning);
            MessageBox.Show(
                StartupManager.LastMaintenanceWarning,
                "G-Helper Auto Mode startup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    public void RequestExit()
    {
        if (_dispatcher.IsDisposed)
            return;

        try
        {
            if (_dispatcher.InvokeRequired)
                _dispatcher.BeginInvoke((Action)ExitThread);
            else
                ExitThread();
        }
        catch (InvalidOperationException)
        {
            // The UI thread is already shutting down.
        }
    }

    private ToolStripMenuItem MakeControlItem(string text, ControlState state)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            _engine.SetControlState(state);
            UpdateControlChecks(state);
            UpdateTrayIcon(state, _engine.CurrentMode);
        };
        return item;
    }

    private ToolStripMenuItem MakeLightingItem(string text, KeyboardLightingMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += async (_, _) => await SetKeyboardLightingModeAsync(mode);
        return item;
    }

    private void UpdateControlChecks(ControlState state)
    {
        _autoItem.Checked = state == ControlState.Auto;
        _silentItem.Checked = state == ControlState.ForceSilent;
        _balancedItem.Checked = state == ControlState.ForceBalanced;
        _turboItem.Checked = state == ControlState.ForceTurbo;
        _pauseItem.Checked = state == ControlState.Pause;
    }

    private void UpdateLightingChecks(KeyboardLightingMode mode)
    {
        _lightingUnmanagedItem.Checked = mode == KeyboardLightingMode.Unmanaged;
        _lightingDynamicItem.Checked = mode == KeyboardLightingMode.WindowsDynamicLighting;
        _lightingAccentItem.Checked = mode == KeyboardLightingMode.GHelperWindowsAccent;
        _lightingManualItem.Checked = mode == KeyboardLightingMode.GHelperManual;
    }

    private async Task SetKeyboardLightingModeAsync(KeyboardLightingMode mode)
    {
        if (_updatingUi)
            return;

        var previous = _configService.Current.KeyboardLighting.Mode;
        try
        {
            _configService.Current.KeyboardLighting.Mode = mode;
            _configService.Save();
            UpdateLightingChecks(mode);
        }
        catch (Exception ex)
        {
            _configService.Current.KeyboardLighting.Mode = previous;
            UpdateLightingChecks(previous);
            MessageBox.Show(
                $"Could not save this lighting choice:\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _lightingMenu.Enabled = false;
        try
        {
            var result = await _lightingManager.ApplyNowAsync(
                "tray selection",
                forceGHelperReload: mode != KeyboardLightingMode.Unmanaged &&
                                    (mode != previous || mode == KeyboardLightingMode.WindowsDynamicLighting),
                forceOwnershipRefresh: mode == KeyboardLightingMode.WindowsDynamicLighting);

            if (!result.Success)
            {
                MessageBox.Show(
                    "Your lighting choice was saved and AutoMode will try again, "
                    + $"but AutoMode could not finish applying it right now:\n\n{result.Message}",
                    "G-Helper Auto Mode",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                _notifyIcon.BalloonTipTitle = "Keyboard lighting";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(2500);
            }
        }
        finally
        {
            _lightingMenu.Enabled = true;
            UpdateLightingStatus();
        }
    }

    private async Task ReapplyLightingOwnershipAsync()
    {
        if (_updatingUi)
            return;

        var mode = _configService.Current.KeyboardLighting.Mode;
        _lightingMenu.Enabled = false;
        try
        {
            var result = await _lightingManager.ApplyNowAsync(
                "manual lighting retry",
                forceGHelperReload: mode != KeyboardLightingMode.Unmanaged,
                forceOwnershipRefresh: mode == KeyboardLightingMode.WindowsDynamicLighting);

            if (!result.Success)
            {
                MessageBox.Show(
                    $"AutoMode could not fully apply this lighting choice:\n\n{result.Message}",
                    "G-Helper Auto Mode",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _notifyIcon.BalloonTipTitle = "Keyboard lighting";
            _notifyIcon.BalloonTipText = result.Message;
            _notifyIcon.ShowBalloonTip(2500);
        }
        finally
        {
            _lightingMenu.Enabled = true;
            UpdateLightingStatus();
        }
    }

    private void PowerWindowOnResumeForLighting() =>
        _lightingManager.QueueOwnershipReassertion("resume lighting check");

    private void PowerWindowOnSessionReady() =>
        _lightingManager.QueueOwnershipReassertion("session lighting check");

    private void LightingManagerOnStatusChanged() => UpdateLightingStatus();

    private void UpdateLightingStatus()
    {
        var lighting = _lightingManager.GetSnapshot();
        _lightingStatusItem.Text = lighting.LastApplyFailed
            ? "Status: could not apply (see diagnostics)"
            : lighting.DesiredMode switch
            {
                KeyboardLightingMode.WindowsDynamicLighting => lighting.DynamicOwnershipHealthy
                    ? $"Status: Windows is in control - {FormatArgb(lighting.EffectiveDynamicLightingArgb)}"
                    : "Status: waiting for Windows",
                KeyboardLightingMode.GHelperWindowsAccent => lighting.WindowsAccentArgb.HasValue
                    ? $"Status: G-Helper accent #{lighting.WindowsAccentArgb.Value & 0x00FFFFFFu:X6}"
                    : "Status: Windows accent unavailable",
                KeyboardLightingMode.GHelperManual => "Status: G-Helper is keeping its Aura settings",
                _ => "Status: lighting left alone"
            };
        _lightingStatusItem.ToolTipText = lighting.LastResult;
    }

    private void ToggleAdaptiveOption(ToolStripMenuItem item, Action<bool> setter, string name)
    {
        if (_updatingUi)
            return;

        var requested = item.Checked;
        try
        {
            setter(requested);
            _configService.Save();
            _engine.ReloadConfig();
            _logger.Info($"{name} -> {requested}.");
        }
        catch (Exception ex)
        {
            _configService.Load();
            _updatingUi = true;
            _preferSilentItem.Checked = _configService.Current.PreferSilentAtLowLoad;
            _stepwiseUpshiftItem.Checked = _configService.Current.StepwiseAutomaticUpshifts;
            _updatingUi = false;

            MessageBox.Show(
                $"Could not save this automatic-switching setting:\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_configService);
        if (form.ShowDialog() != DialogResult.OK)
            return;

        _updatingUi = true;
        try
        {
            _preferSilentItem.Checked = _configService.Current.PreferSilentAtLowLoad;
            _stepwiseUpshiftItem.Checked = _configService.Current.StepwiseAutomaticUpshifts;
            _loggingItem.Checked = _configService.Current.Logging.Enabled;
            UpdateLightingChecks(_configService.Current.KeyboardLighting.Mode);
        }
        finally
        {
            _updatingUi = false;
        }

        _engine.ReloadConfig();
        _lightingManager.ReloadConfig();
        _logger.Info("Settings saved from the settings window.");
    }

    private void ToggleLogging()
    {
        if (_updatingUi)
            return;

        var enabled = _loggingItem.Checked;
        var previous = _configService.Current.Logging.Enabled;

        try
        {
            if (!enabled)
                _logger.Info("Log saving disabled from tray menu.");

            _configService.Current.Logging.Enabled = enabled;
            _configService.Save();

            if (enabled)
                _logger.Info("Log saving enabled from tray menu.");
        }
        catch (Exception ex)
        {
            _configService.Current.Logging.Enabled = previous;
            _updatingUi = true;
            _loggingItem.Checked = previous;
            _updatingUi = false;

            MessageBox.Show(
                $"Could not save the logging setting:\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ToggleStartup()
    {
        if (_updatingUi)
            return;

        try
        {
            StartupManager.SetEnabled(_startupItem.Checked);
            var status = StartupManager.GetStatus();
            _logger.Info($"Windows-login startup changed: {status.Summary}. {status.Detail}");
        }
        catch (Exception ex)
        {
            _updatingUi = true;
            _startupItem.Checked = StartupManager.IsEnabled();
            _updatingUi = false;

            MessageBox.Show(
                $"Could not change Windows-login startup:\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void EngineOnModeChanged(PerformanceMode mode, string reason)
    {
        UpdateTrayIcon(_engine.ControlState, mode);
        _notifyIcon.Text = TrimNotifyText($"G-Helper Auto Mode | {mode}");

        if (_configService.Current.ShowModeChangeNotifications)
        {
            _notifyIcon.BalloonTipTitle = $"Mode: {mode}";
            _notifyIcon.BalloonTipText = reason;
            _notifyIcon.ShowBalloonTip(2500);
        }
    }

    private void EngineOnStatusUpdated(EngineStatus status)
    {
        _lastStatus = status;
        var snapshot = status.Snapshot;
        var cpu = snapshot.CpuPercentAverage.HasValue ? Fmt(snapshot.CpuPercentAverage) + "%" : "n/a";
        var cpuTemp = snapshot.CpuTemperatureC.HasValue ? Fmt(snapshot.CpuTemperatureC) + "C" : "n/a";
        var gpu = snapshot.GpuPercentAverage.HasValue ? Fmt(snapshot.GpuPercentAverage) + "%" : "n/a";
        var gpuTemp = snapshot.GpuTemperatureC.HasValue ? Fmt(snapshot.GpuTemperatureC) + "C" : "n/a";
        var pending = status.PendingMode.HasValue ? $" -> {status.PendingMode.Value}?" : string.Empty;

        var fgCore = snapshot.ForegroundCpuCoreEquivalentAverage.HasValue
            ? " | FG " + Fmt(snapshot.ForegroundCpuCoreEquivalentAverage) + "%core"
            : string.Empty;
        _statusItem.Text = $"{status.ControlState} | {status.CurrentMode}{pending} | CPU {cpu}/{cpuTemp} | GPU {gpu}/{gpuTemp}{fgCore} | {snapshot.DisplayState}";
        UpdateTrayIcon(status.ControlState, status.CurrentMode);
        _notifyIcon.Text = TrimNotifyText($"G-Helper Auto Mode | {status.ControlState} | {status.CurrentMode}");
    }

    private void ReloadConfig()
    {
        _configService.Load();

        _updatingUi = true;
        _loggingItem.Checked = _configService.Current.Logging.Enabled;
        _startupItem.Checked = StartupManager.IsEnabled();
        _preferSilentItem.Checked = _configService.Current.PreferSilentAtLowLoad;
        _stepwiseUpshiftItem.Checked = _configService.Current.StepwiseAutomaticUpshifts;
        UpdateLightingChecks(_configService.Current.KeyboardLighting.Mode);
        _updatingUi = false;

        _engine.ReloadConfig();
        _lightingManager.ReloadConfig();
        _logger.Info("Config reload requested from tray menu.");

        if (!string.IsNullOrWhiteSpace(_configService.LastLoadWarning))
            ShowConfigWarning(_configService.LastLoadWarning);
    }

    private void ShowDiagnostics()
    {
        MessageBox.Show(
            BuildDiagnostics(),
            "G-Helper Auto Mode diagnostics",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(BuildDiagnostics());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy diagnostics:\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        var g = _gHelper.ReadSettings();
        var lighting = _lightingManager.GetSnapshot();
        var status = _lastStatus;

        AppendInvariant(sb, $"G-Helper Auto Mode v{Program.Version}");
        AppendInvariant(sb, $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var startup = StartupManager.GetStatus();
        AppendInvariant(sb, $"Windows-login startup: {startup.Summary}");
        AppendInvariant(sb, $"Startup detail: {startup.Detail}");
        AppendInvariant(sb, $"Process integrity: AutoMode={StartupManager.CurrentProcessIntegrity}; G-Helper={StartupManager.GHelperProcessIntegrity}");
        AppendInvariant(sb, $"Control: {_engine.ControlState}");
        AppendInvariant(sb, $"Current mode: {_engine.CurrentMode}");
        AppendInvariant(sb, $"Config schema: {_configService.Current.SchemaVersion}");
        AppendInvariant(sb, $"Automation enabled: {_configService.Current.AutomationEnabled}");
        AppendInvariant(sb, $"Automation poll / load window: {_configService.Current.PollIntervalMilliseconds}ms / {_configService.Current.Thresholds.LoadAverageSeconds}s");
        AppendInvariant(sb, $"Use Silent at low load: {_configService.Current.PreferSilentAtLowLoad}");
        AppendInvariant(sb, $"Pass through Balanced before Turbo: {_configService.Current.StepwiseAutomaticUpshifts}");
        AppendInvariant(sb, $"Thermal promotion: Balanced={Fmt(_configService.Current.Thresholds.BalancedThermalTempC)}C/{_configService.Current.Thresholds.BalancedThermalSeconds}s; Turbo={Fmt(_configService.Current.Thresholds.TurboThermalTempC)}C/{_configService.Current.Thresholds.TurboThermalSeconds}s");
        AppendInvariant(sb, $"Silent temperature ceilings: CPU={Fmt(_configService.Current.Thresholds.SilentCpuTempMaxC)}C; GPU={Fmt(_configService.Current.Thresholds.SilentGpuTempMaxC)}C");
        AppendInvariant(sb, $"Application rules: {_configService.Current.AppRules.Count} total, {_configService.Current.AppRules.Count(rule => rule.Enabled)} enabled");
        AppendInvariant(sb, $"Keyboard lighting target: {lighting.DesiredMode}");
        AppendInvariant(sb, $"Lighting check / ownership heartbeat / session delay: {_configService.Current.KeyboardLighting.AccentPollIntervalSeconds}s / {_configService.Current.KeyboardLighting.OwnershipHeartbeatSeconds}s / {_configService.Current.KeyboardLighting.SessionRecoveryDelayMilliseconds}ms");
        AppendInvariant(sb, $"Windows Dynamic Lighting enabled: {(lighting.DynamicLightingEnabled.HasValue ? lighting.DynamicLightingEnabled.Value.ToString() : "unknown")}");
        AppendInvariant(sb, $"Foreground-app lighting takeover allowed: {BoolValue(lighting.ForegroundAppControlEnabled)}");
        AppendInvariant(sb, $"Dynamic Lighting uses Windows accent: {BoolValue(lighting.UsesSystemAccentColor)}");
        AppendInvariant(sb, $"Dynamic Lighting effective color: {FormatArgb(lighting.EffectiveDynamicLightingArgb)} ({lighting.DynamicLightingColorSource})");
        AppendInvariant(sb, $"Dynamic Lighting devices owned by Windows: {lighting.WindowsOwnedDeviceCount}/{lighting.DynamicLightingDeviceCount}");
        AppendInvariant(sb, $"Dynamic Lighting ownership healthy: {lighting.DynamicOwnershipHealthy}");
        AppendInvariant(sb, $"Windows accent: {(lighting.WindowsAccentArgb.HasValue ? $"#{lighting.WindowsAccentArgb.Value & 0x00FFFFFFu:X6}" : "unavailable")}");
        AppendInvariant(sb, $"G-Helper lighting: skip_aura={Value(lighting.GHelperSkipAura)}, aura_mode={Value(lighting.GHelperAuraMode)}, aura_color={FormatColor(lighting.GHelperAuraColor)}");
        AppendInvariant(sb, $"Last lighting check: {lighting.LastResult}");
        AppendInvariant(sb, $"Evidence policy: promotion timers are mode-independent; downshift timers are mode-scoped; CPU/GPU thermal clocks are separate");
        AppendInvariant(sb, $"Last decision: {_engine.LastDecision}");
        AppendInvariant(sb, $"Transition progress: {_engine.TransitionProgress}");
        AppendInvariant(sb, $"G-Helper running: {_gHelper.IsGHelperRunning()}");
        AppendInvariant(sb, $"G-Helper top-level windows: {_gHelper.GetGHelperWindowCount()}");
        AppendInvariant(sb, $"Last control request transport: {_gHelper.LastControlTransport}");
        AppendInvariant(sb, $"Last control request time: {(_gHelper.LastControlAt.HasValue ? _gHelper.LastControlAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "never")}");
        AppendInvariant(sb, $"Last control request detail: {_gHelper.LastControlDetail}");
        AppendInvariant(sb, $"Last confirmed automatic mode: {(_gHelper.LastConfirmedMode.HasValue ? _gHelper.LastConfirmedMode.Value.ToString() : "none")}");
        AppendInvariant(sb, $"Last confirmation time: {(_gHelper.LastConfirmationAt.HasValue ? _gHelper.LastConfirmationAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "never")}");
        AppendInvariant(sb, $"Last confirmation detail: {_gHelper.LastConfirmationDetail}");
        AppendInvariant(sb, $"Display-power input policy: keyboard injection only when display state is explicitly On; otherwise non-waking WM_HOTKEY only");
        AppendInvariant(sb, $"Win32 SendInput INPUT size: {GHelperController.SendInputStructSize} bytes");
        AppendInvariant(sb, $"G-Helper config readable: {g.ConfigReadable}");
        AppendInvariant(sb, $"G-Helper hotkeys disabled: {g.SkipHotkeys}");
        AppendInvariant(sb, $"G-Helper modifier_keybind_alt: {g.ModifierKeybindAlt}");
        AppendInvariant(sb, $"G-Helper profile keys: Silent={GHelperController.VirtualKeyName(g.ProfileKeySilent)}, Balanced={GHelperController.VirtualKeyName(g.ProfileKeyBalanced)}, Turbo={GHelperController.VirtualKeyName(g.ProfileKeyTurbo)}");
        AppendInvariant(sb, $"Display notification registered: {_powerWindow.DisplayNotificationRegistered}");
        if (!_powerWindow.DisplayNotificationRegistered)
            AppendInvariant(sb, $"Display registration Win32 error: {_powerWindow.DisplayRegistrationError}");
        AppendInvariant(sb, $"Session notification registered: {_powerWindow.SessionNotificationRegistered}");
        if (!_powerWindow.SessionNotificationRegistered)
            AppendInvariant(sb, $"Session registration Win32 error: {_powerWindow.SessionRegistrationError}");
        AppendInvariant(sb, $"G-Helper disable_power_event: {Value(g.DisablePowerEvent)}");
        AppendInvariant(sb, $"G-Helper screen_auto: {Value(g.ScreenAuto)}");
        AppendInvariant(sb, $"G-Helper gpu_mode: {Value(g.GpuMode)}");
        AppendInvariant(sb, $"NVIDIA fallback interval / timeout / NVML recovery: {_configService.Current.Telemetry.NvidiaSmiPollIntervalSeconds}s / {_configService.Current.Telemetry.NvidiaSmiTimeoutSeconds}s / {_configService.Current.Telemetry.NvmlRecoveryIntervalSeconds}s");
        AppendInvariant(sb, $"Health: {BuildHealthSummary(g)}");

        if (!string.IsNullOrWhiteSpace(g.Error))
            AppendInvariant(sb, $"G-Helper config error: {g.Error}");

        if (status is not null)
        {
            var s = status.Snapshot;
            sb.AppendLine();
            sb.AppendLine("Telemetry");
            var displayText = s.DisplayState == DisplayState.Unknown
                ? "Unknown (not confirmed Off; conservative active-display timing is used)"
                : s.DisplayState.ToString();
            AppendInvariant(sb, $"Display: {displayText}");
            AppendInvariant(sb, $"Foreground: {s.ForegroundProcess} (PID {Value(s.ForegroundProcessId)})");
            AppendInvariant(sb, $"Foreground CPU core-equivalent raw / avg: {Fmt(s.ForegroundCpuCoreEquivalentRaw)}% / {Fmt(s.ForegroundCpuCoreEquivalentAverage)}%");
            AppendInvariant(sb, $"CPU raw / avg: {Fmt(s.CpuPercentRaw)}% / {Fmt(s.CpuPercentAverage)}%");
            AppendInvariant(sb, $"CPU temperature: {(s.CpuTemperatureC.HasValue ? Fmt(s.CpuTemperatureC) + " C" : "n/a")} ({s.CpuTemperatureSource}, {s.CpuTemperatureQuality})");
            if (!string.IsNullOrWhiteSpace(s.CpuTemperatureError))
                AppendInvariant(sb, $"CPU temperature detail: {s.CpuTemperatureError}");
            AppendInvariant(sb, $"GPU raw / avg: {Fmt(s.GpuPercentRaw)}% / {Fmt(s.GpuPercentAverage)}%");
            AppendInvariant(sb, $"GPU temperature: {(s.GpuTemperatureC.HasValue ? Fmt(s.GpuTemperatureC) + " C" : "n/a")}");
            AppendInvariant(sb, $"GPU provider: {s.GpuTelemetrySource} ({s.GpuTelemetryQuality})");
            if (!string.IsNullOrWhiteSpace(s.GpuTelemetryError))
                AppendInvariant(sb, $"GPU telemetry detail: {s.GpuTelemetryError}");
            AppendInvariant(sb, $"Pending mode: {(status.PendingMode.HasValue ? status.PendingMode.Value.ToString() : "none")}");
            AppendInvariant(sb, $"External/manual hold: {(status.ExternalHoldRemaining.HasValue ? status.ExternalHoldRemaining.Value.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s" : "none")}");
        }

        sb.AppendLine();
        AppendInvariant(sb, $"Auto config: {_configService.ConfigPath}");
        AppendInvariant(sb, $"G-Helper config: {_gHelper.ConfigPath}");
        AppendInvariant(sb, $"Log: {_logger.LogPath}");
        return sb.ToString();
    }

    private string BuildHealthSummary(GHelperSettingsSnapshot g)
    {
        var issues = new List<string>();
        if (!_powerWindow.DisplayNotificationRegistered) issues.Add("display-event registration failed");
        if (!_powerWindow.SessionNotificationRegistered) issues.Add("session-event registration failed");
        if (!g.ConfigReadable)
        {
            issues.Add("G-Helper config unreadable");
        }
        else
        {
            if (g.SkipHotkeys) issues.Add("G-Helper hotkeys disabled");
            if (g.ProfileKeySilent is <= 0 or > byte.MaxValue
                || g.ProfileKeyBalanced is <= 0 or > byte.MaxValue
                || g.ProfileKeyTurbo is <= 0 or > byte.MaxValue)
            {
                issues.Add("a profile hotkey is disabled or invalid");
            }
            else if (new[] { g.ProfileKeySilent, g.ProfileKeyBalanced, g.ProfileKeyTurbo }.Distinct().Count() != 3)
            {
                issues.Add("G-Helper profile hotkeys are not unique");
            }
            if (g.DisablePowerEvent != 1) issues.Add("G-Helper AC/battery mode switching is not disabled");
            if (g.ScreenAuto == 1) issues.Add("G-Helper auto refresh rate is enabled");
        }
        var gHelperRunning = _gHelper.IsGHelperRunning();
        if (_configService.Current.RequireGHelperRunning && !gHelperRunning)
        {
            issues.Add("G-Helper not running");
        }
        if (!_configService.Current.AutomationEnabled) issues.Add("automation disabled in config");
        var lighting = _lightingManager.GetSnapshot();
        if (lighting.DesiredMode != KeyboardLightingMode.Unmanaged && lighting.LastApplyFailed)
            issues.Add("keyboard-lighting update failed");
        if (lighting.DesiredMode == KeyboardLightingMode.WindowsDynamicLighting && !lighting.DynamicOwnershipHealthy)
            issues.Add("Windows Dynamic Lighting ownership is not established on every device");
        if (_lastStatus is not null)
        {
            if (_lastStatus.Snapshot.GpuTelemetryQuality is TelemetryQuality.Unavailable or TelemetryQuality.GraceCache
                && !_configService.Current.Thresholds.AllowDowngradeWhenGpuUnavailable)
            {
                issues.Add("NVIDIA telemetry unavailable/degraded");
            }

            if (_lastStatus.Snapshot.CpuTemperatureQuality is TelemetryQuality.Unavailable or TelemetryQuality.GraceCache)
                issues.Add("ASUS CPU temperature unavailable/degraded (load-based promotion still works)");
        }
        return issues.Count == 0 ? "OK" : string.Join("; ", issues);
    }

    private static string Value(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "not set";
    private static string FormatColor(int? value) => value.HasValue
        ? $"#{unchecked((uint)value.Value) & 0x00FFFFFFu:X6} ({value.Value.ToString(CultureInfo.InvariantCulture)})"
        : "not set";
    private static string FormatArgb(uint? value) => value.HasValue
        ? $"#{value.Value & 0x00FFFFFFu:X6}"
        : "unavailable";
    private static string BoolValue(bool? value) => value.HasValue ? value.Value.ToString() : "unknown";
    private static string Fmt(double? value) => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) : "n/a";

    private static void AppendInvariant(StringBuilder sb, FormattableString value) =>
        sb.AppendLine(FormattableString.Invariant(value));

    private void UpdateTrayIcon(ControlState controlState, PerformanceMode mode)
    {
        if (_lastIconControlState == controlState && _lastIconMode == mode)
            return;

        var newIcon = TrayIconFactory.Create(controlState, mode);
        var oldIcon = _trayIconImage;
        _trayIconImage = newIcon;
        _lastIconControlState = controlState;
        _lastIconMode = mode;
        _notifyIcon.Icon = newIcon;
        oldIcon?.Dispose();
    }

    private static void ShowConfigWarning(string warning)
    {
        MessageBox.Show(
            warning,
            "G-Helper Auto Mode config warning",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static string TrimNotifyText(string text) => text.Length <= 63 ? text : text[..63];

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open:\n{path}\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        _logger.Info("Exiting.");
        _notifyIcon.Visible = false;
        _powerWindow.DisplayStateChanged -= _engine.SetDisplayState;
        _powerWindow.Resumed -= _engine.ResetAfterResume;
        _powerWindow.Resumed -= PowerWindowOnResumeForLighting;
        _powerWindow.SessionReady -= PowerWindowOnSessionReady;
        _powerWindow.ExitRequested -= RequestExit;
        _engine.ModeChanged -= EngineOnModeChanged;
        _engine.StatusUpdated -= EngineOnStatusUpdated;
        _lightingManager.StatusChanged -= LightingManagerOnStatusChanged;
        _notifyIcon.Dispose();
        _trayIconImage?.Dispose();
        _lightingManager.Dispose();
        _engine.Dispose();
        _powerWindow.Dispose();
        _dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
