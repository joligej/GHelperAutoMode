using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GHelperAutoMode;

internal sealed class AutoModeConfig
{
    public int SchemaVersion { get; set; } = 7;
    public bool AutomationEnabled { get; set; } = true;
    public int StartupGraceSeconds { get; set; } = 12;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public bool RequireGHelperRunning { get; set; } = true;
    public bool ShowModeChangeNotifications { get; set; }
    public bool RespectExternalModeChanges { get; set; } = true;
    public int ExternalModeChangeHoldSeconds { get; set; } = 180;
    public int ModeConfirmationTimeoutSeconds { get; set; } = 5;
    public int LateConfirmationGraceSeconds { get; set; } = 20;
    public bool InputGuardEnabled { get; set; } = true;
    public bool PreferSilentAtLowLoad { get; set; } = true;
    public bool StepwiseAutomaticUpshifts { get; set; }
    public KeyboardLightingConfig KeyboardLighting { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public ThresholdConfig Thresholds { get; set; } = new();
    public List<AppRule> AppRules { get; set; } = DefaultAppRules();

    public static List<AppRule> DefaultAppRules() =>
    [
        new() { Process = "Revit.exe", Action = AppRuleAction.MinimumBalanced, Enabled = true },
        new() { Process = "acad.exe", Action = AppRuleAction.MinimumBalanced, Enabled = true },
        new() { Process = "FurMark*.exe", Action = AppRuleAction.MinimumTurbo, Enabled = true },
        new() { Process = "3DMark*.exe", Action = AppRuleAction.MinimumTurbo, Enabled = true },
        new() { Process = "YourGame.exe", Action = AppRuleAction.ForceTurbo, Enabled = false }
    ];
}

internal sealed class KeyboardLightingConfig
{
    // Upgrades never take over lighting until the user explicitly selects a managed mode.
    public KeyboardLightingMode Mode { get; set; } = KeyboardLightingMode.Unmanaged;
    public int AccentPollIntervalSeconds { get; set; } = 5;
}

internal sealed class LoggingConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxFileSizeMB { get; set; } = 5;
    public int KeepFiles { get; set; } = 3;
    public int TelemetryIntervalSeconds { get; set; }
}

internal sealed class ThresholdConfig
{
    public int LoadAverageSeconds { get; set; } = 5;

    // Silent -> Balanced: fast but debounce-protected. Silent is a normal interactive
    // mode; a few seconds of real activity promotes it before responsiveness is lost.
    public double BalancedCpuPercent { get; set; } = 22;
    public double BalancedCpuResetPercent { get; set; } = 15;
    public int BalancedCpuSeconds { get; set; } = 3;
    public double BalancedGpuPercent { get; set; } = 18;
    public double BalancedGpuResetPercent { get; set; } = 10;
    public int BalancedGpuSeconds { get; set; } = 3;

    // 100 means roughly one fully occupied logical CPU core. This catches Revit-style
    // single-thread work even when total CPU usage looks deceptively low.
    public double ForegroundBalancedCoreEquivalentPercent { get; set; } = 40;
    public double ForegroundBalancedCoreEquivalentResetPercent { get; set; } = 25;
    public int ForegroundBalancedSeconds { get; set; } = 2;

    // Thermal pressure is an independent signal. If Silent keeps the machine at 65 C
    // for 10 s it deserves Balanced; 75 C for 10 s is enough evidence for Turbo.
    public double BalancedThermalTempC { get; set; } = 65;
    public int BalancedThermalSeconds { get; set; } = 10;
    public double TurboThermalTempC { get; set; } = 75;
    public int TurboThermalSeconds { get; set; } = 10;

    // Balanced -> Turbo: deliberately earlier than v3. Temperature can also promote
    // independently, so workloads do not need to saturate the whole CPU first.
    public double TurboCpuPercent { get; set; } = 50;
    public double TurboCpuResetPercent { get; set; } = 40;
    public int TurboCpuSeconds { get; set; } = 5;
    public double TurboGpuPercent { get; set; } = 45;
    public double TurboGpuResetPercent { get; set; } = 35;
    public int TurboGpuSeconds { get; set; } = 4;

    // Fast path for unmistakable bursts/benchmarks.
    public double FastTurboCpuPercent { get; set; } = 75;
    public double FastTurboCpuResetPercent { get; set; } = 60;
    public int FastTurboCpuSeconds { get; set; } = 2;
    public double FastTurboGpuPercent { get; set; } = 80;
    public double FastTurboGpuResetPercent { get; set; } = 65;
    public int FastTurboGpuSeconds { get; set; } = 2;

    public double ForegroundTurboCoreEquivalentPercent { get; set; } = 70;
    public double ForegroundTurboCoreEquivalentResetPercent { get; set; } = 45;
    public int ForegroundTurboSeconds { get; set; } = 3;

    // Turbo -> Balanced requires both low load and enough thermal headroom.
    public double TurboExitCpuBelowPercent { get; set; } = 35;
    public double TurboExitGpuBelowPercent { get; set; } = 35;
    public double TurboExitMaxTempC { get; set; } = 68;
    public int TurboExitSeconds { get; set; } = 25;

    // Balanced -> Silent is intentionally stricter and slower than either upshift.
    public double SilentCpuBelowPercent { get; set; } = 12;
    public double SilentGpuBelowPercent { get; set; } = 8;
    public double SilentCpuTempMaxC { get; set; } = 60;
    public double SilentGpuTempMaxC { get; set; } = 62;
    public int ActiveDisplaySilentSeconds { get; set; } = 60;
    public int DisplayOffSilentSeconds { get; set; } = 20;

    // Smoothing / anti-flap timings. Upshifts are quick; downshifts are staged.
    public int BalancedBeforeTurboSeconds { get; set; } = 3;
    public int TurboMinimumSeconds { get; set; } = 30;
    public int PostTurboBalancedSeconds { get; set; } = 30;
    public int ModeChangeCooldownSeconds { get; set; } = 3;
    public int AppRuleDebounceSeconds { get; set; } = 1;

    public int CpuTemperatureGraceSeconds { get; set; } = 5;
    public int GpuTelemetryGraceSeconds { get; set; } = 10;

    // Conservative default: unknown GPU load is never interpreted as idle.
    public bool AllowDowngradeWhenGpuUnavailable { get; set; }
}

internal sealed class AppRule
{
    public string Process { get; set; } = string.Empty;
    public AppRuleAction Action { get; set; } = AppRuleAction.MinimumBalanced;
    public bool Enabled { get; set; } = true;

    // Foreground-app rules are normally ignored once the display is off; actual load still controls the mode.
    public bool ApplyWhenDisplayOff { get; set; }
}

internal sealed class ConfigService
{
    private const int CurrentSchemaVersion = 7;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GHelperAutoMode");

    public string ConfigPath => Path.Combine(BaseDirectory, "config.json");
    public string LogDirectory => Path.Combine(BaseDirectory, "logs");

    public AutoModeConfig Current { get; private set; } = new();
    public string? LastLoadWarning { get; private set; }

    public ConfigService()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogDirectory);
        Load();
    }

    public AutoModeConfig Load()
    {
        LastLoadWarning = null;

        if (!File.Exists(ConfigPath))
        {
            Current = new AutoModeConfig();
            Normalize(Current);
            Save();
            return Current;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            Current = JsonSerializer.Deserialize<AutoModeConfig>(json, _jsonOptions) ?? new AutoModeConfig();
            var migrated = Migrate(Current);
            Normalize(Current);

            if (migrated)
            {
                BackupPreMigrationConfig(json);
                Save();
                LastLoadWarning = "GHelperAutoMode upgraded its configuration to schema 7. Keyboard lighting remains unmanaged until you explicitly select a mode. The previous config was backed up in the same folder.";
            }

            return Current;
        }
        catch (Exception ex)
        {
            var backup = Path.Combine(
                BaseDirectory,
                $"config.invalid.{TimestampForFile()}.json");

            try { File.Copy(ConfigPath, backup, overwrite: true); } catch { /* best effort */ }

            LastLoadWarning = $"config.json was invalid and defaults were restored. Backup: {backup}. Error: {ex.Message}";
            Current = new AutoModeConfig();
            Normalize(Current);
            Save();
            return Current;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(BaseDirectory);
        Normalize(Current);

        var json = JsonSerializer.Serialize(Current, _jsonOptions);
        var tempPath = ConfigPath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best effort only; the original save exception remains authoritative.
            }
        }
    }

    private static bool Migrate(AutoModeConfig config)
    {
        if (config.SchemaVersion >= CurrentSchemaVersion)
            return false;

        if (config.SchemaVersion < 6)
        {
            // v6 deliberately installed the final recommended threshold set. Keep this
            // migration stage isolated so a normal v6 -> v7 upgrade never resets tuning.
            config.Thresholds = new ThresholdConfig();
            config.PollIntervalMilliseconds = 1000;
            config.StepwiseAutomaticUpshifts = false;
            if (config.StartupGraceSeconds == 20) config.StartupGraceSeconds = 12;
            if (config.ExternalModeChangeHoldSeconds == 300) config.ExternalModeChangeHoldSeconds = 180;

            // Preserve every existing rule exactly, including deliberately disabled/customized
            // seed rules. Only add a recommended hint when no rule for that process exists at all.
            EnsureRule(config.AppRules, "Revit.exe", AppRuleAction.MinimumBalanced, enabled: true);
            EnsureRule(config.AppRules, "acad.exe", AppRuleAction.MinimumBalanced, enabled: true);
            EnsureRule(config.AppRules, "FurMark*.exe", AppRuleAction.MinimumTurbo, enabled: true);
            EnsureRule(config.AppRules, "3DMark*.exe", AppRuleAction.MinimumTurbo, enabled: true);
        }

        // v7 adds opt-in keyboard-lighting ownership. The property initializer supplies
        // Unmanaged when an older JSON file has no KeyboardLighting object.
        config.KeyboardLighting ??= new KeyboardLightingConfig();

        config.SchemaVersion = CurrentSchemaVersion;
        return true;
    }

    private static void EnsureRule(List<AppRule>? rules, string process, AppRuleAction action, bool enabled)
    {
        if (rules is null)
            return;

        if (rules.Any(r => string.Equals(r.Process, process, StringComparison.OrdinalIgnoreCase)))
            return;

        rules.Add(new AppRule { Process = process, Action = action, Enabled = enabled });
    }

    private void BackupPreMigrationConfig(string json)
    {
        try
        {
            var backup = Path.Combine(BaseDirectory, $"config.pre-v{CurrentSchemaVersion}.{TimestampForFile()}.json");
            File.WriteAllText(backup, json);
        }
        catch
        {
            // Migration itself must not fail merely because a backup could not be written.
        }
    }

    private static void Normalize(AutoModeConfig config)
    {
        config.SchemaVersion = CurrentSchemaVersion;
        config.PollIntervalMilliseconds = Math.Clamp(config.PollIntervalMilliseconds, 500, 60_000);
        config.StartupGraceSeconds = Math.Clamp(config.StartupGraceSeconds, 0, 300);
        config.ExternalModeChangeHoldSeconds = Math.Clamp(config.ExternalModeChangeHoldSeconds, 0, 86_400);
        config.ModeConfirmationTimeoutSeconds = Math.Clamp(config.ModeConfirmationTimeoutSeconds, 1, 30);
        config.LateConfirmationGraceSeconds = Math.Clamp(config.LateConfirmationGraceSeconds, 0, 120);

        config.Logging ??= new LoggingConfig();
        config.KeyboardLighting ??= new KeyboardLightingConfig();
        config.Thresholds ??= new ThresholdConfig();
        config.AppRules ??= AutoModeConfig.DefaultAppRules();
        config.AppRules = config.AppRules.Where(rule => rule is not null).ToList();

        config.Logging.MaxFileSizeMB = Math.Clamp(config.Logging.MaxFileSizeMB, 1, 1024);
        config.Logging.KeepFiles = Math.Clamp(config.Logging.KeepFiles, 0, 20);
        config.Logging.TelemetryIntervalSeconds = Math.Clamp(config.Logging.TelemetryIntervalSeconds, 0, 86_400);
        if (!Enum.IsDefined(config.KeyboardLighting.Mode))
            config.KeyboardLighting.Mode = KeyboardLightingMode.Unmanaged;
        config.KeyboardLighting.AccentPollIntervalSeconds = Math.Clamp(
            config.KeyboardLighting.AccentPollIntervalSeconds,
            2,
            300);

        var t = config.Thresholds;
        t.LoadAverageSeconds = Math.Clamp(t.LoadAverageSeconds, 1, 60);

        // Keep promotion hysteresis non-degenerate even after hand edits. Automatic tiers
        // must have a real reset band; Turbo must remain a distinct rung above Balanced.
        t.BalancedCpuPercent = Math.Clamp(t.BalancedCpuPercent, 1, 99);
        t.BalancedCpuResetPercent = Math.Min(ClampPercent(t.BalancedCpuResetPercent), t.BalancedCpuPercent - 1);
        t.BalancedGpuPercent = Math.Clamp(t.BalancedGpuPercent, 1, 99);
        t.BalancedGpuResetPercent = Math.Min(ClampPercent(t.BalancedGpuResetPercent), t.BalancedGpuPercent - 1);
        t.ForegroundBalancedCoreEquivalentPercent = Math.Clamp(t.ForegroundBalancedCoreEquivalentPercent, 1, 6399);
        t.ForegroundBalancedCoreEquivalentResetPercent = Math.Min(
            Math.Clamp(t.ForegroundBalancedCoreEquivalentResetPercent, 0, 6400),
            t.ForegroundBalancedCoreEquivalentPercent - 1);

        t.BalancedThermalTempC = Math.Clamp(t.BalancedThermalTempC, 20, 105);
        t.TurboThermalTempC = Math.Clamp(
            t.TurboThermalTempC,
            Math.Min(110, t.BalancedThermalTempC + 1),
            110);

        t.TurboCpuPercent = Math.Clamp(Math.Max(ClampPercent(t.TurboCpuPercent), t.BalancedCpuPercent + 1), 2, 100);
        t.TurboCpuResetPercent = Math.Min(ClampPercent(t.TurboCpuResetPercent), t.TurboCpuPercent - 1);
        t.TurboGpuPercent = Math.Clamp(Math.Max(ClampPercent(t.TurboGpuPercent), t.BalancedGpuPercent + 1), 2, 100);
        t.TurboGpuResetPercent = Math.Min(ClampPercent(t.TurboGpuResetPercent), t.TurboGpuPercent - 1);

        t.FastTurboCpuPercent = Math.Max(ClampPercent(t.FastTurboCpuPercent), t.TurboCpuPercent);
        t.FastTurboCpuResetPercent = Math.Min(ClampPercent(t.FastTurboCpuResetPercent), Math.Max(0, t.FastTurboCpuPercent - 1));
        t.FastTurboGpuPercent = Math.Max(ClampPercent(t.FastTurboGpuPercent), t.TurboGpuPercent);
        t.FastTurboGpuResetPercent = Math.Min(ClampPercent(t.FastTurboGpuResetPercent), Math.Max(0, t.FastTurboGpuPercent - 1));

        t.ForegroundTurboCoreEquivalentPercent = Math.Clamp(
            Math.Max(t.ForegroundTurboCoreEquivalentPercent, t.ForegroundBalancedCoreEquivalentPercent + 1),
            2,
            6400);
        t.ForegroundTurboCoreEquivalentResetPercent = Math.Min(
            Math.Clamp(t.ForegroundTurboCoreEquivalentResetPercent, 0, 6400),
            t.ForegroundTurboCoreEquivalentPercent - 1);

        // Keep the automatic downshift bands below their corresponding promotion
        // thresholds even after hand-edited config values are normalized.
        t.TurboExitCpuBelowPercent = Math.Min(ClampPercent(t.TurboExitCpuBelowPercent), Math.Max(0, t.TurboCpuPercent - 1));
        t.TurboExitGpuBelowPercent = Math.Min(ClampPercent(t.TurboExitGpuBelowPercent), Math.Max(0, t.TurboGpuPercent - 1));
        t.TurboExitMaxTempC = Math.Min(Math.Clamp(t.TurboExitMaxTempC, 20, 105), Math.Max(20, t.TurboThermalTempC - 1));

        t.SilentCpuBelowPercent = Math.Min(ClampPercent(t.SilentCpuBelowPercent), Math.Max(0, t.BalancedCpuPercent - 1));
        t.SilentGpuBelowPercent = Math.Min(ClampPercent(t.SilentGpuBelowPercent), Math.Max(0, t.BalancedGpuPercent - 1));
        t.SilentCpuTempMaxC = Math.Min(Math.Clamp(t.SilentCpuTempMaxC, 20, 100), Math.Max(20, t.BalancedThermalTempC - 1));
        t.SilentGpuTempMaxC = Math.Min(Math.Clamp(t.SilentGpuTempMaxC, 20, 100), Math.Max(20, t.BalancedThermalTempC - 1));

        t.BalancedCpuSeconds = Math.Clamp(t.BalancedCpuSeconds, 1, 600);
        t.BalancedGpuSeconds = Math.Clamp(t.BalancedGpuSeconds, 1, 600);
        t.ForegroundBalancedSeconds = Math.Clamp(t.ForegroundBalancedSeconds, 1, 600);
        t.BalancedThermalSeconds = Math.Clamp(t.BalancedThermalSeconds, 1, 600);
        t.TurboThermalSeconds = Math.Clamp(t.TurboThermalSeconds, 1, 600);
        t.TurboCpuSeconds = Math.Clamp(t.TurboCpuSeconds, 1, 600);
        t.TurboGpuSeconds = Math.Clamp(t.TurboGpuSeconds, 1, 600);
        t.FastTurboCpuSeconds = Math.Clamp(t.FastTurboCpuSeconds, 1, 600);
        t.FastTurboGpuSeconds = Math.Clamp(t.FastTurboGpuSeconds, 1, 600);
        t.ForegroundTurboSeconds = Math.Clamp(t.ForegroundTurboSeconds, 1, 600);
        t.TurboExitSeconds = Math.Clamp(t.TurboExitSeconds, 1, 1800);
        t.ActiveDisplaySilentSeconds = Math.Clamp(t.ActiveDisplaySilentSeconds, 1, 3600);
        t.DisplayOffSilentSeconds = Math.Min(
            Math.Clamp(t.DisplayOffSilentSeconds, 1, 3600),
            t.ActiveDisplaySilentSeconds);
        t.BalancedBeforeTurboSeconds = Math.Clamp(t.BalancedBeforeTurboSeconds, 0, 120);
        t.TurboMinimumSeconds = Math.Clamp(t.TurboMinimumSeconds, 0, 3600);
        t.PostTurboBalancedSeconds = Math.Clamp(t.PostTurboBalancedSeconds, 0, 3600);
        t.ModeChangeCooldownSeconds = Math.Clamp(t.ModeChangeCooldownSeconds, 0, 300);
        t.AppRuleDebounceSeconds = Math.Clamp(t.AppRuleDebounceSeconds, 0, 30);
        t.CpuTemperatureGraceSeconds = Math.Clamp(t.CpuTemperatureGraceSeconds, 0, 60);
        t.GpuTelemetryGraceSeconds = Math.Clamp(t.GpuTelemetryGraceSeconds, 0, 120);

        foreach (var rule in config.AppRules)
        {
            rule.Process = rule.Process?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(rule.Action))
            {
                rule.Action = AppRuleAction.MinimumBalanced;
                rule.Enabled = false;
            }
        }
    }

    private static string TimestampForFile() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
