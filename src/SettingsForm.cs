using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal sealed class SettingsForm : Form
{
    private sealed class SettingsPage
    {
        public required TableLayoutPanel Grid { get; init; }
        public int Row { get; set; }
    }

    private sealed record EnumChoice<T>(T Value, string Label) where T : struct, Enum
    {
        public override string ToString() => Label;
    }

    private readonly ConfigService _configService;
    private readonly AutoModeConfig _settings;
    private readonly List<Action> _writers = [];
    private readonly BindingList<AppRule> _rules;
    private readonly DataGridView _rulesGrid;

    public SettingsForm(ConfigService configService)
    {
        _configService = configService;
        _settings = configService.CreateWorkingCopy();
        _rules = new BindingList<AppRule>(_settings.AppRules);

        Text = $"G-Helper Auto Mode {Program.Version} - Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 680);
        ClientSize = new Size(980, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.Sizable;

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* The window remains usable without a custom icon. */ }

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 7)
        };

        BuildGeneralPage(tabs);
        BuildPerformancePage(tabs);
        BuildThermalPage(tabs);
        _rulesGrid = BuildRulesPage(tabs);
        BuildIntegrationPage(tabs);

        var intro = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(14, 10, 14, 4),
            Text = "Settings are saved to your Windows account and take effect immediately. "
                   + "Recommended defaults are conservative; change thresholds in small steps."
        };

        var saveButton = new Button
        {
            Text = "Save",
            AutoSize = true,
            Padding = new Padding(18, 4, 18, 4)
        };
        saveButton.Click += SaveButtonOnClick;

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Padding = new Padding(18, 4, 18, 4)
        };

        var openConfigButton = new Button
        {
            Text = "Open data folder",
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 4)
        };
        openConfigButton.Click += (_, _) => OpenDataFolder();

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 8),
            WrapContents = false
        };
        footer.Controls.Add(saveButton);
        footer.Controls.Add(cancelButton);
        footer.Controls.Add(openConfigButton);

        Controls.Add(tabs);
        Controls.Add(intro);
        Controls.Add(footer);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void BuildGeneralPage(TabControl tabs)
    {
        var page = AddPage(tabs, "General");
        AddSection(page, "Automatic control");
        AddCheck(page, "Enable automatic switching", _settings.AutomationEnabled,
            value => _settings.AutomationEnabled = value,
            "Starts in Auto after launch. Manual tray overrides still remain available.");
        AddCheck(page, "Require G-Helper to be running", _settings.RequireGHelperRunning,
            value => _settings.RequireGHelperRunning = value,
            "Prevents requests when there is no G-Helper process to receive them.");
        AddCheck(page, "Delay normal hotkeys while a modifier key is held", _settings.InputGuardEnabled,
            value => _settings.InputGuardEnabled = value,
            "Avoids colliding with Ctrl, Shift, Alt, or Win held by the user. Display-off input blocking is always enforced.");
        AddCheck(page, "Show a notification after a confirmed mode change", _settings.ShowModeChangeNotifications,
            value => _settings.ShowModeChangeNotifications = value,
            "Uses a tray balloon only after G-Helper confirms the new profile.");
        AddCheck(page, "Respect profile changes made outside AutoMode", _settings.RespectExternalModeChanges,
            value => _settings.RespectExternalModeChanges = value,
            "Temporarily pauses automatic switching after a manual or external G-Helper change.");
        AddCheck(page, "Use Silent at low load", _settings.PreferSilentAtLowLoad,
            value => _settings.PreferSilentAtLowLoad = value,
            "When disabled, automatic downshifts stop at Balanced.");
        AddCheck(page, "Pass through Balanced before Turbo", _settings.StepwiseAutomaticUpshifts,
            value => _settings.StepwiseAutomaticUpshifts = value,
            "Adds a Balanced settling step even when Turbo evidence is already mature.");

        AddSection(page, "Core timing");
        AddInt(page, "Startup grace period", _settings.StartupGraceSeconds, 0, 300, "seconds",
            value => _settings.StartupGraceSeconds = value,
            "No automatic profile changes are requested while providers warm up.");
        AddInt(page, "Automation poll interval", _settings.PollIntervalMilliseconds, 500, 60_000, "ms",
            value => _settings.PollIntervalMilliseconds = value,
            "Lower values react faster but sample more often.", increment: 100);
        AddInt(page, "External-change hold", _settings.ExternalModeChangeHoldSeconds, 0, 86_400, "seconds",
            value => _settings.ExternalModeChangeHoldSeconds = value,
            "How long Auto waits after another source changes the G-Helper profile.");
        AddInt(page, "Confirmation timeout", _settings.ModeConfirmationTimeoutSeconds, 1, 30, "seconds",
            value => _settings.ModeConfirmationTimeoutSeconds = value,
            "How long a requested profile may remain unconfirmed.");
        AddInt(page, "Late-confirmation grace", _settings.LateConfirmationGraceSeconds, 0, 120, "seconds",
            value => _settings.LateConfirmationGraceSeconds = value,
            "Still recognizes a delayed G-Helper write as AutoMode's own request.");
    }

    private void BuildPerformancePage(TabControl tabs)
    {
        var page = AddPage(tabs, "Performance");
        var t = _settings.Thresholds;

        AddSection(page, "Smoothing");
        AddInt(page, "Load averaging window", t.LoadAverageSeconds, 1, 60, "seconds",
            value => t.LoadAverageSeconds = value,
            "Rolling window used for CPU, GPU, and foreground-process load.");

        AddSection(page, "Silent to Balanced");
        AddDouble(page, "CPU enter threshold", t.BalancedCpuPercent, 1, 99, "%",
            value => t.BalancedCpuPercent = value, "Promote after sustained average CPU load.");
        AddDouble(page, "CPU reset threshold", t.BalancedCpuResetPercent, 0, 98, "%",
            value => t.BalancedCpuResetPercent = value, "Must stay below the enter threshold.");
        AddInt(page, "CPU evidence duration", t.BalancedCpuSeconds, 1, 600, "seconds",
            value => t.BalancedCpuSeconds = value, "Required uninterrupted evidence.");
        AddDouble(page, "GPU enter threshold", t.BalancedGpuPercent, 1, 99, "%",
            value => t.BalancedGpuPercent = value, "Promote after sustained average NVIDIA GPU load.");
        AddDouble(page, "GPU reset threshold", t.BalancedGpuResetPercent, 0, 98, "%",
            value => t.BalancedGpuResetPercent = value, "Must stay below the enter threshold.");
        AddInt(page, "GPU evidence duration", t.BalancedGpuSeconds, 1, 600, "seconds",
            value => t.BalancedGpuSeconds = value, "Required uninterrupted evidence.");
        AddDouble(page, "Foreground enter threshold", t.ForegroundBalancedCoreEquivalentPercent, 1, 6399, "% of one core",
            value => t.ForegroundBalancedCoreEquivalentPercent = value,
            "100% means roughly one fully occupied logical CPU core.");
        AddDouble(page, "Foreground reset threshold", t.ForegroundBalancedCoreEquivalentResetPercent, 0, 6398, "% of one core",
            value => t.ForegroundBalancedCoreEquivalentResetPercent = value, "Must stay below the enter threshold.");
        AddInt(page, "Foreground evidence duration", t.ForegroundBalancedSeconds, 1, 600, "seconds",
            value => t.ForegroundBalancedSeconds = value, "Required uninterrupted foreground load.");

        AddSection(page, "Balanced to Turbo");
        AddDouble(page, "CPU enter threshold", t.TurboCpuPercent, 2, 100, "%",
            value => t.TurboCpuPercent = value, "Must remain above the Balanced CPU threshold.");
        AddDouble(page, "CPU reset threshold", t.TurboCpuResetPercent, 0, 99, "%",
            value => t.TurboCpuResetPercent = value, "Must stay below the Turbo enter threshold.");
        AddInt(page, "CPU evidence duration", t.TurboCpuSeconds, 1, 600, "seconds",
            value => t.TurboCpuSeconds = value, "Required uninterrupted evidence.");
        AddDouble(page, "GPU enter threshold", t.TurboGpuPercent, 2, 100, "%",
            value => t.TurboGpuPercent = value, "Must remain above the Balanced GPU threshold.");
        AddDouble(page, "GPU reset threshold", t.TurboGpuResetPercent, 0, 99, "%",
            value => t.TurboGpuResetPercent = value, "Must stay below the Turbo enter threshold.");
        AddInt(page, "GPU evidence duration", t.TurboGpuSeconds, 1, 600, "seconds",
            value => t.TurboGpuSeconds = value, "Required uninterrupted evidence.");
        AddDouble(page, "Foreground enter threshold", t.ForegroundTurboCoreEquivalentPercent, 2, 6400, "% of one core",
            value => t.ForegroundTurboCoreEquivalentPercent = value,
            "Catches demanding foreground work that does not fill the whole CPU.");
        AddDouble(page, "Foreground reset threshold", t.ForegroundTurboCoreEquivalentResetPercent, 0, 6399, "% of one core",
            value => t.ForegroundTurboCoreEquivalentResetPercent = value, "Must stay below the enter threshold.");
        AddInt(page, "Foreground evidence duration", t.ForegroundTurboSeconds, 1, 600, "seconds",
            value => t.ForegroundTurboSeconds = value, "Required uninterrupted foreground load.");

        AddSection(page, "Fast Turbo path");
        AddDouble(page, "Fast CPU threshold", t.FastTurboCpuPercent, 0, 100, "%",
            value => t.FastTurboCpuPercent = value, "For unmistakably heavy bursts; must be at least the normal Turbo threshold.");
        AddDouble(page, "Fast CPU reset", t.FastTurboCpuResetPercent, 0, 99, "%",
            value => t.FastTurboCpuResetPercent = value, "Resets fast-path evidence below this value.");
        AddInt(page, "Fast CPU duration", t.FastTurboCpuSeconds, 1, 600, "seconds",
            value => t.FastTurboCpuSeconds = value, "Required uninterrupted evidence.");
        AddDouble(page, "Fast GPU threshold", t.FastTurboGpuPercent, 0, 100, "%",
            value => t.FastTurboGpuPercent = value, "For unmistakably heavy bursts; must be at least the normal Turbo threshold.");
        AddDouble(page, "Fast GPU reset", t.FastTurboGpuResetPercent, 0, 99, "%",
            value => t.FastTurboGpuResetPercent = value, "Resets fast-path evidence below this value.");
        AddInt(page, "Fast GPU duration", t.FastTurboGpuSeconds, 1, 600, "seconds",
            value => t.FastTurboGpuSeconds = value, "Required uninterrupted evidence.");
    }

    private void BuildThermalPage(TabControl tabs)
    {
        var page = AddPage(tabs, "Thermals & downshifts");
        var t = _settings.Thresholds;

        AddSection(page, "Temperature promotion");
        AddDouble(page, "Balanced temperature", t.BalancedThermalTempC, 20, 105, "°C",
            value => t.BalancedThermalTempC = value, "CPU or GPU heat can promote to Balanced independently of load.");
        AddInt(page, "Balanced thermal duration", t.BalancedThermalSeconds, 1, 600, "seconds",
            value => t.BalancedThermalSeconds = value, "Required uninterrupted fresh temperature evidence.");
        AddDouble(page, "Turbo temperature", t.TurboThermalTempC, 21, 110, "°C",
            value => t.TurboThermalTempC = value, "Must remain above the Balanced temperature.");
        AddInt(page, "Turbo thermal duration", t.TurboThermalSeconds, 1, 600, "seconds",
            value => t.TurboThermalSeconds = value, "Required uninterrupted fresh temperature evidence.");

        AddSection(page, "Turbo to Balanced");
        AddDouble(page, "CPU load below", t.TurboExitCpuBelowPercent, 0, 100, "%",
            value => t.TurboExitCpuBelowPercent = value, "CPU and GPU must both remain below their exit ceilings.");
        AddDouble(page, "GPU load below", t.TurboExitGpuBelowPercent, 0, 100, "%",
            value => t.TurboExitGpuBelowPercent = value, "Unknown GPU load remains conservative unless allowed below.");
        AddDouble(page, "Maximum temperature", t.TurboExitMaxTempC, 20, 105, "°C",
            value => t.TurboExitMaxTempC = value, "Must remain below the Turbo thermal trigger.");
        AddInt(page, "Low-load duration", t.TurboExitSeconds, 1, 1800, "seconds",
            value => t.TurboExitSeconds = value, "Required uninterrupted cool and quiet period.");
        AddInt(page, "Minimum Turbo residence", t.TurboMinimumSeconds, 0, 3600, "seconds",
            value => t.TurboMinimumSeconds = value, "Prevents an immediate downshift after entering Turbo.");

        AddSection(page, "Balanced to Silent");
        AddDouble(page, "CPU load below", t.SilentCpuBelowPercent, 0, 100, "%",
            value => t.SilentCpuBelowPercent = value, "CPU and GPU must both remain below their Silent ceilings.");
        AddDouble(page, "GPU load below", t.SilentGpuBelowPercent, 0, 100, "%",
            value => t.SilentGpuBelowPercent = value, "Unknown GPU load remains conservative unless allowed below.");
        AddDouble(page, "Maximum CPU temperature", t.SilentCpuTempMaxC, 20, 100, "°C",
            value => t.SilentCpuTempMaxC = value, "Must remain below the Balanced thermal trigger.");
        AddDouble(page, "Maximum GPU temperature", t.SilentGpuTempMaxC, 20, 100, "°C",
            value => t.SilentGpuTempMaxC = value, "Must remain below the Balanced thermal trigger.");
        AddInt(page, "Active-display quiet time", t.ActiveDisplaySilentSeconds, 1, 3600, "seconds",
            value => t.ActiveDisplaySilentSeconds = value, "Quiet period before Silent while the display is on.");
        AddInt(page, "Display-off quiet time", t.DisplayOffSilentSeconds, 1, 3600, "seconds",
            value => t.DisplayOffSilentSeconds = value, "Cannot exceed the active-display quiet time.");

        AddSection(page, "Stability and missing data");
        AddInt(page, "Balanced settling before Turbo", t.BalancedBeforeTurboSeconds, 0, 120, "seconds",
            value => t.BalancedBeforeTurboSeconds = value, "Used when staged automatic upshifts are enabled.");
        AddInt(page, "Balanced residence after Turbo", t.PostTurboBalancedSeconds, 0, 3600, "seconds",
            value => t.PostTurboBalancedSeconds = value, "Prevents dropping straight through Balanced after Turbo.");
        AddInt(page, "Mode-change cooldown", t.ModeChangeCooldownSeconds, 0, 300, "seconds",
            value => t.ModeChangeCooldownSeconds = value, "Minimum spacing between ordinary automatic requests.");
        AddInt(page, "Application-rule debounce", t.AppRuleDebounceSeconds, 0, 30, "seconds",
            value => t.AppRuleDebounceSeconds = value, "Avoids reacting to a fleeting foreground window.");
        AddInt(page, "CPU temperature grace", t.CpuTemperatureGraceSeconds, 0, 60, "seconds",
            value => t.CpuTemperatureGraceSeconds = value, "How briefly the last valid ASUS sensor value may be reported as stale.");
        AddInt(page, "GPU telemetry grace", t.GpuTelemetryGraceSeconds, 0, 120, "seconds",
            value => t.GpuTelemetryGraceSeconds = value, "How briefly the last valid NVIDIA sample may be reported as stale.");
        AddCheck(page, "Allow downshifts when NVIDIA telemetry is unavailable", t.AllowDowngradeWhenGpuUnavailable,
            value => t.AllowDowngradeWhenGpuUnavailable = value,
            "Less conservative; useful only when the system has no NVIDIA GPU/provider.");
    }

    private DataGridView BuildRulesPage(TabControl tabs)
    {
        var tab = new TabPage("Application rules") { Padding = new Padding(12) };
        tabs.TabPages.Add(tab);

        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 54,
            Text = "Rules apply to the foreground process after the debounce period. Wildcards are supported, for example Game*.exe. "
                   + "Minimum rules never force a lower profile. Force rules keep their selected profile while the app is foreground."
        };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            DataSource = _rules
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Enabled",
            DataPropertyName = nameof(AppRule.Enabled),
            Width = 75
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Process",
            DataPropertyName = nameof(AppRule.Process),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 45
        });
        var ruleChoices = Enum.GetValues<AppRuleAction>()
            .Select(value => new EnumChoice<AppRuleAction>(value, FormatEnum(value)))
            .ToList();
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Action",
            DataPropertyName = nameof(AppRule.Action),
            DataSource = ruleChoices,
            DisplayMember = nameof(EnumChoice<AppRuleAction>.Label),
            ValueMember = nameof(EnumChoice<AppRuleAction>.Value),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 35,
            FlatStyle = FlatStyle.Flat
        });
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Apply while display is off",
            DataPropertyName = nameof(AppRule.ApplyWhenDisplayOff),
            Width = 180
        });
        grid.DataError += (_, e) => e.ThrowException = false;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0)
        };
        var add = new Button { Text = "Add rule", AutoSize = true };
        add.Click += (_, _) =>
        {
            _rules.Add(new AppRule { Enabled = true });
            var index = _rules.Count - 1;
            grid.CurrentCell = grid.Rows[index].Cells[1];
            grid.BeginEdit(true);
        };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.SelectedRows.Cast<DataGridViewRow>().OrderByDescending(row => row.Index))
            {
                if (!row.IsNewRow && row.DataBoundItem is AppRule rule)
                    _rules.Remove(rule);
            }
        };
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);

        tab.Controls.Add(grid);
        tab.Controls.Add(buttons);
        tab.Controls.Add(help);
        return grid;
    }

    private void BuildIntegrationPage(TabControl tabs)
    {
        var page = AddPage(tabs, "Lighting & advanced");
        var lighting = _settings.KeyboardLighting;
        var telemetry = _settings.Telemetry;
        var logging = _settings.Logging;

        AddSection(page, "Keyboard lighting");
        AddCombo(page, "Control mode", lighting.Mode, Enum.GetValues<KeyboardLightingMode>(),
            value => lighting.Mode = value,
            "Unmanaged makes no lighting changes; the other modes define Windows/G-Helper ownership.");
        AddInt(page, "Accent and ownership check", lighting.AccentPollIntervalSeconds, 2, 300, "seconds",
            value => lighting.AccentPollIntervalSeconds = value,
            "Timer cadence. Unchanged state is read cheaply and does not restart G-Helper.");
        AddInt(page, "Full ownership heartbeat", lighting.OwnershipHeartbeatSeconds, 5, 3600, "seconds",
            value => lighting.OwnershipHeartbeatSeconds = value,
            "Periodically rewrites Dynamic Lighting ownership to recover from outside changes.");
        AddInt(page, "Session recovery delay", lighting.SessionRecoveryDelayMilliseconds, 0, 10_000, "ms",
            value => lighting.SessionRecoveryDelayMilliseconds = value,
            "Wait after logon/unlock before reconciling ownership.", increment: 100);

        AddSection(page, "G-Helper lighting recovery");
        AddInt(page, "Config read attempts", lighting.GHelperConfigReadAttempts, 1, 10, "attempts",
            value => lighting.GHelperConfigReadAttempts = value, "Handles a brief race while G-Helper writes its JSON file.");
        AddInt(page, "Config read retry delay", lighting.GHelperConfigReadRetryMilliseconds, 0, 1000, "ms",
            value => lighting.GHelperConfigReadRetryMilliseconds = value, "Delay between failed config reads.", increment: 10);
        AddInt(page, "Restart timeout", lighting.GHelperRestartTimeoutSeconds, 2, 30, "seconds",
            value => lighting.GHelperRestartTimeoutSeconds = value, "Maximum wait for G-Helper to stop or start.");
        AddInt(page, "Process check interval", lighting.GHelperProcessPollMilliseconds, 25, 1000, "ms",
            value => lighting.GHelperProcessPollMilliseconds = value, "Polling cadence during a controlled G-Helper restart.", increment: 25);
        AddInt(page, "Scheduled-task start attempts", lighting.GHelperTaskStartAttempts, 1, 10, "attempts",
            value => lighting.GHelperTaskStartAttempts = value, "Retries G-Helper's own per-user task after an ownership handoff.");
        AddInt(page, "Scheduled-task retry delay", lighting.GHelperTaskRetryMilliseconds, 0, 5000, "ms",
            value => lighting.GHelperTaskRetryMilliseconds = value, "Delay between task-start attempts.", increment: 50);

        AddSection(page, "NVIDIA telemetry fallback");
        AddInt(page, "nvidia-smi sample interval", telemetry.NvidiaSmiPollIntervalSeconds, 1, 60, "seconds",
            value => telemetry.NvidiaSmiPollIntervalSeconds = value, "Used only when direct NVML sampling is unavailable.");
        AddInt(page, "nvidia-smi timeout", telemetry.NvidiaSmiTimeoutSeconds, 1, 30, "seconds",
            value => telemetry.NvidiaSmiTimeoutSeconds = value, "Maximum time for one fallback process.");
        AddInt(page, "NVML recovery interval", telemetry.NvmlRecoveryIntervalSeconds, 5, 3600, "seconds",
            value => telemetry.NvmlRecoveryIntervalSeconds = value, "How often a missing NVML provider may be initialized again.");
        AddInt(page, "NVML failures before reset", telemetry.NvmlFailuresBeforeReset, 1, 20, "failures",
            value => telemetry.NvmlFailuresBeforeReset = value, "Drops and recreates a stale NVML handle after this many failures.");

        AddSection(page, "Logging");
        AddCheck(page, "Save log files", logging.Enabled, value => logging.Enabled = value,
            "Logs are written below the current account's LocalAppData folder.");
        AddInt(page, "Maximum log file size", logging.MaxFileSizeMB, 1, 1024, "MB",
            value => logging.MaxFileSizeMB = value, "The active log rotates after reaching this size.");
        AddInt(page, "Rotated log files to keep", logging.KeepFiles, 0, 20, "files",
            value => logging.KeepFiles = value, "Zero removes the previous log during rotation.");
        AddInt(page, "Telemetry log interval", logging.TelemetryIntervalSeconds, 0, 86_400, "seconds",
            value => logging.TelemetryIntervalSeconds = value, "Zero disables periodic telemetry entries while keeping event logs.");
    }

    private static SettingsPage AddPage(TabControl tabs, string title)
    {
        var tab = new TabPage(title) { Padding = new Padding(5) };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 3,
            Padding = new Padding(10, 8, 10, 14),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tab.Controls.Add(grid);
        tabs.TabPages.Add(tab);
        return new SettingsPage { Grid = grid };
    }

    private static void AddSection(SettingsPage page, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(0, page.Row == 0 ? 4 : 18, 0, 6)
        };
        page.Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Grid.Controls.Add(label, 0, page.Row);
        page.Grid.SetColumnSpan(label, 3);
        page.Row++;
    }

    private void AddCheck(SettingsPage page, string label, bool value, Action<bool> writer, string description)
    {
        var check = new CheckBox
        {
            Text = label,
            Checked = value,
            AutoSize = true,
            Margin = new Padding(0, 5, 8, 6)
        };
        var detail = MakeDescription(description);
        page.Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Grid.Controls.Add(check, 0, page.Row);
        page.Grid.SetColumnSpan(check, 2);
        page.Grid.Controls.Add(detail, 2, page.Row);
        page.Row++;
        _writers.Add(() => writer(check.Checked));
    }

    private void AddInt(
        SettingsPage page,
        string label,
        int value,
        int minimum,
        int maximum,
        string unit,
        Action<int> writer,
        string description,
        int increment = 1)
    {
        var control = MakeNumeric(value, minimum, maximum, increment, decimalPlaces: 0);
        AddNumberRow(page, label, control, unit, description);
        _writers.Add(() => writer(decimal.ToInt32(control.Value)));
    }

    private void AddDouble(
        SettingsPage page,
        string label,
        double value,
        double minimum,
        double maximum,
        string unit,
        Action<double> writer,
        string description)
    {
        var control = MakeNumeric((decimal)value, (decimal)minimum, (decimal)maximum, 0.5m, decimalPlaces: 1);
        AddNumberRow(page, label, control, unit, description);
        _writers.Add(() => writer((double)control.Value));
    }

    private void AddCombo<T>(
        SettingsPage page,
        string label,
        T value,
        T[] values,
        Action<T> writer,
        string description) where T : struct, Enum
    {
        var choices = values
            .Select(item => new EnumChoice<T>(item, FormatEnum(item)))
            .ToList();
        var control = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 210,
            Anchor = AnchorStyles.Left
        };
        control.Items.AddRange(choices.Cast<object>().ToArray());
        AddControlRow(page, label, control, description);
        control.SelectedItem = choices.First(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));
        _writers.Add(() =>
        {
            if (control.SelectedItem is EnumChoice<T> selected)
                writer(selected.Value);
        });
    }

    private static NumericUpDown MakeNumeric(
        decimal value,
        decimal minimum,
        decimal maximum,
        decimal increment,
        int decimalPlaces)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Increment = increment,
            DecimalPlaces = decimalPlaces,
            Value = Math.Clamp(value, minimum, maximum),
            ThousandsSeparator = true,
            Width = 125,
            Anchor = AnchorStyles.Left
        };
    }

    private static void AddNumberRow(
        SettingsPage page,
        string label,
        NumericUpDown control,
        string unit,
        string description) =>
        AddControlRow(page, label, control, $"{unit} - {description}");

    private static void AddControlRow(SettingsPage page, string label, Control control, string description)
    {
        var name = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6)
        };
        control.Margin = new Padding(0, 2, 8, 3);
        var detail = MakeDescription(description);
        page.Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Grid.Controls.Add(name, 0, page.Row);
        page.Grid.Controls.Add(control, 1, page.Row);
        page.Grid.Controls.Add(detail, 2, page.Row);
        page.Row++;
    }

    private static Label MakeDescription(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 6, 0, 6)
    };

    private static string FormatEnum<T>(T value) where T : struct, Enum => value switch
    {
        KeyboardLightingMode.Unmanaged => "Leave lighting alone",
        KeyboardLightingMode.WindowsDynamicLighting => "Windows Dynamic Lighting",
        KeyboardLightingMode.GHelperWindowsAccent => "G-Helper: Windows accent",
        KeyboardLightingMode.GHelperManual => "G-Helper: current Aura",
        AppRuleAction.MinimumBalanced => "Minimum Balanced",
        AppRuleAction.MinimumTurbo => "Minimum Turbo",
        AppRuleAction.ForceSilent => "Force Silent",
        AppRuleAction.ForceBalanced => "Force Balanced",
        AppRuleAction.ForceTurbo => "Force Turbo",
        _ => value.ToString()
    };

    private void SaveButtonOnClick(object? sender, EventArgs e)
    {
        try
        {
            _rulesGrid.EndEdit();
            var bindingContext = BindingContext;
            if (bindingContext is not null && bindingContext[_rules] is CurrencyManager manager)
                manager.EndCurrentEdit();

            foreach (var writer in _writers)
                writer();

            _settings.AppRules = _rules.ToList();
            if (!ValidateSettings(out var validationError))
            {
                MessageBox.Show(this, validationError, "Check these settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _configService.ReplaceAndSave(_settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"The settings could not be saved. Nothing in the running configuration was replaced.\n\n{ex.Message}",
                "G-Helper Auto Mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool ValidateSettings(out string error)
    {
        var t = _settings.Thresholds;
        var checks = new (bool Valid, string Message)[]
        {
            (t.BalancedCpuResetPercent < t.BalancedCpuPercent, "Balanced CPU reset must be below its enter threshold."),
            (t.BalancedGpuResetPercent < t.BalancedGpuPercent, "Balanced GPU reset must be below its enter threshold."),
            (t.ForegroundBalancedCoreEquivalentResetPercent < t.ForegroundBalancedCoreEquivalentPercent, "Balanced foreground reset must be below its enter threshold."),
            (t.TurboCpuPercent > t.BalancedCpuPercent, "Turbo CPU threshold must be above the Balanced CPU threshold."),
            (t.TurboGpuPercent > t.BalancedGpuPercent, "Turbo GPU threshold must be above the Balanced GPU threshold."),
            (t.ForegroundTurboCoreEquivalentPercent > t.ForegroundBalancedCoreEquivalentPercent, "Turbo foreground threshold must be above the Balanced foreground threshold."),
            (t.TurboCpuResetPercent < t.TurboCpuPercent, "Turbo CPU reset must be below its enter threshold."),
            (t.TurboGpuResetPercent < t.TurboGpuPercent, "Turbo GPU reset must be below its enter threshold."),
            (t.ForegroundTurboCoreEquivalentResetPercent < t.ForegroundTurboCoreEquivalentPercent, "Turbo foreground reset must be below its enter threshold."),
            (t.FastTurboCpuPercent >= t.TurboCpuPercent, "Fast Turbo CPU threshold must be at least the normal Turbo CPU threshold."),
            (t.FastTurboGpuPercent >= t.TurboGpuPercent, "Fast Turbo GPU threshold must be at least the normal Turbo GPU threshold."),
            (t.FastTurboCpuResetPercent < t.FastTurboCpuPercent, "Fast Turbo CPU reset must be below its threshold."),
            (t.FastTurboGpuResetPercent < t.FastTurboGpuPercent, "Fast Turbo GPU reset must be below its threshold."),
            (t.TurboThermalTempC > t.BalancedThermalTempC, "Turbo temperature must be above the Balanced temperature."),
            (t.TurboExitMaxTempC < t.TurboThermalTempC, "Turbo-exit temperature must be below the Turbo temperature."),
            (t.SilentCpuTempMaxC < t.BalancedThermalTempC, "Silent CPU temperature must be below the Balanced temperature."),
            (t.SilentGpuTempMaxC < t.BalancedThermalTempC, "Silent GPU temperature must be below the Balanced temperature."),
            (t.TurboExitCpuBelowPercent < t.TurboCpuPercent, "Turbo-exit CPU load must be below the Turbo CPU threshold."),
            (t.TurboExitGpuBelowPercent < t.TurboGpuPercent, "Turbo-exit GPU load must be below the Turbo GPU threshold."),
            (t.SilentCpuBelowPercent < t.BalancedCpuPercent, "Silent CPU load must be below the Balanced CPU threshold."),
            (t.SilentGpuBelowPercent < t.BalancedGpuPercent, "Silent GPU load must be below the Balanced GPU threshold."),
            (t.DisplayOffSilentSeconds <= t.ActiveDisplaySilentSeconds, "Display-off quiet time cannot exceed active-display quiet time.")
        };

        foreach (var check in checks)
        {
            if (!check.Valid)
            {
                error = check.Message;
                return false;
            }
        }

        var enabledWithoutProcess = _settings.AppRules.FirstOrDefault(rule => rule.Enabled && string.IsNullOrWhiteSpace(rule.Process));
        if (enabledWithoutProcess is not null)
        {
            error = "Every enabled application rule needs a process name or wildcard.";
            return false;
        }

        var duplicate = _settings.AppRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Process))
            .GroupBy(rule => rule.Process.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            error = $"The application rule '{duplicate.Key}' appears more than once.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _configService.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the data folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
