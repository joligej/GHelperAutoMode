using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal sealed record KeyboardLightingSnapshot(
    KeyboardLightingMode DesiredMode,
    bool? DynamicLightingEnabled,
    bool? ForegroundAppControlEnabled,
    bool? UsesSystemAccentColor,
    uint? EffectiveDynamicLightingArgb,
    string DynamicLightingColorSource,
    int DynamicLightingDeviceCount,
    int WindowsOwnedDeviceCount,
    bool DynamicOwnershipHealthy,
    uint? WindowsAccentArgb,
    int? GHelperSkipAura,
    int? GHelperAuraMode,
    int? GHelperAuraColor,
    string LastResult,
    bool LastApplyFailed);

internal sealed record KeyboardLightingApplyResult(bool Success, bool Changed, string Message);

internal sealed record DynamicLightingSnapshot(
    bool? Enabled,
    bool? ForegroundAppControlEnabled,
    bool? UsesSystemAccentColor,
    uint? ConfiguredColorArgb,
    uint? EffectiveColorArgb,
    string EffectiveColorSource,
    int DeviceCount,
    int EnabledDeviceCount,
    int WindowsOwnedDeviceCount);

internal readonly record struct GHelperAuraSignature(
    int? SkipAura,
    int? AuraMode,
    int? AuraColor,
    int? AuraColor2,
    int? AuraSpeed);

internal readonly record struct DynamicLightingWriteResult(bool Changed);

/// <summary>
/// Reconciles keyboard-lighting ownership without synthesizing keyboard input.
/// G-Helper is allowed to reload its own Aura settings through its normal startup path;
/// Windows Dynamic Lighting is controlled through the same per-user ownership values G-Helper uses.
/// </summary>
internal sealed class KeyboardLightingManager : IDisposable
{
    private const string LightingRegistryPath = @"Software\Microsoft\Lighting";
    private const string LightingDevicesRegistryPath = @"Software\Microsoft\Lighting\Devices";
    private const string ExplorerAccentRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string DwmRegistryPath = @"Software\Microsoft\Windows\DWM";
    private const string GHelperExitEventName = @"Global\GHelperApp-Exit";
    private const int GHelperStaticAuraMode = 0;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly ConfigService _configService;
    private readonly FileLogger _logger;
    private readonly GHelperController _gHelper;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _started;
    private bool _applyInProgress;
    private bool _ownershipRefreshPending;
    private bool _gHelperReloadPending;
    private bool _disposed;
    private string _lastResult = "Not applied yet.";
    private bool _lastApplyFailed;
    private DateTime _lastOwnershipRefreshUtc = DateTime.MinValue;
    private GHelperAuraSignature? _lastObservedGHelperAura;
    private int _sessionReassertGeneration;

    public event Action? StatusChanged;

    public KeyboardLightingManager(
        ConfigService configService,
        FileLogger logger,
        GHelperController gHelper)
    {
        _configService = configService;
        _logger = logger;
        _gHelper = gHelper;
        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += TimerOnTick;
        UpdateTimerInterval();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;

        _started = true;
        UpdateTimerInterval();
        _timer.Start();
        _ = ApplyNowAsync("startup check");
    }

    public void ReloadConfig()
    {
        UpdateTimerInterval();
        _ = ApplyNowAsync("config reload");
    }

    public async Task<KeyboardLightingApplyResult> ApplyNowAsync(
        string reason,
        bool forceGHelperReload = false,
        bool forceOwnershipRefresh = false)
    {
        if (_disposed)
            return new KeyboardLightingApplyResult(false, false, "Keyboard-lighting manager is disposed.");
        if (_applyInProgress)
        {
            if (forceOwnershipRefresh)
                _ownershipRefreshPending = true;
            if (forceGHelperReload)
                _gHelperReloadPending = true;

            return new KeyboardLightingApplyResult(false, false, "A keyboard-lighting update is already in progress.");
        }

        _applyInProgress = true;
        _timer.Stop();
        StatusChanged?.Invoke();

        try
        {
            var result = await ReconcileAsync(forceGHelperReload, forceOwnershipRefresh);
            _lastResult = result.Message;
            _lastApplyFailed = !result.Success;

            if (!result.Success)
                _logger.Warn($"Keyboard lighting ({reason}): {result.Message}");
            else if (result.Changed || reason != "regular check")
                _logger.Info($"Keyboard lighting ({reason}): {result.Message}");

            return result;
        }
        catch (Exception ex)
        {
            _lastResult = ex.Message;
            _lastApplyFailed = true;
            _logger.Error($"Keyboard lighting ({reason}) failed: {ex}");
            return new KeyboardLightingApplyResult(false, false, ex.Message);
        }
        finally
        {
            _applyInProgress = false;
            UpdateTimerInterval();
            if (_started && !_disposed)
                _timer.Start();
            StatusChanged?.Invoke();

            if ((_ownershipRefreshPending || _gHelperReloadPending) && !_disposed)
            {
                var reloadGHelper = _gHelperReloadPending;
                var refreshOwnership = _ownershipRefreshPending;
                _ownershipRefreshPending = false;
                _gHelperReloadPending = false;
                _ = ApplyNowAsync(
                    "queued ownership check",
                    forceGHelperReload: reloadGHelper,
                    forceOwnershipRefresh: refreshOwnership);
            }
        }
    }

    public KeyboardLightingSnapshot GetSnapshot()
    {
        var desired = _configService.Current.KeyboardLighting.Mode;
        uint? accent = TryGetWindowsAccentArgb(out var color) ? color : null;
        var dynamicLighting = ReadDynamicLightingSnapshot(accent);
        var gHelperLighting = TryReadGHelperLighting();
        var ownershipHealthy = desired == KeyboardLightingMode.WindowsDynamicLighting &&
                               dynamicLighting.Enabled == true &&
                               dynamicLighting.ForegroundAppControlEnabled == false &&
                               dynamicLighting.EnabledDeviceCount == dynamicLighting.DeviceCount &&
                               dynamicLighting.WindowsOwnedDeviceCount == dynamicLighting.DeviceCount;

        return new KeyboardLightingSnapshot(
            desired,
            dynamicLighting.Enabled,
            dynamicLighting.ForegroundAppControlEnabled,
            dynamicLighting.UsesSystemAccentColor,
            dynamicLighting.EffectiveColorArgb,
            dynamicLighting.EffectiveColorSource,
            dynamicLighting.DeviceCount,
            dynamicLighting.WindowsOwnedDeviceCount,
            ownershipHealthy,
            accent,
            gHelperLighting.SkipAura,
            gHelperLighting.AuraMode,
            gHelperLighting.AuraColor,
            _applyInProgress ? "Applying..." : _lastResult,
            _lastApplyFailed);
    }

    private async void TimerOnTick(object? sender, EventArgs e)
    {
        if (_configService.Current.KeyboardLighting.Mode == KeyboardLightingMode.Unmanaged)
            return;

        await ApplyNowAsync("regular check");
    }

    public void QueueOwnershipReassertion(string reason)
    {
        if (_disposed || _configService.Current.KeyboardLighting.Mode != KeyboardLightingMode.WindowsDynamicLighting)
            return;

        var generation = Interlocked.Increment(ref _sessionReassertGeneration);
        _ = ReassertOwnershipAfterDelayAsync(reason, generation);
    }

    private async Task ReassertOwnershipAfterDelayAsync(string reason, int generation)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(_configService.Current.KeyboardLighting.SessionRecoveryDelayMilliseconds),
                _lifetimeCancellation.Token);
            if (_disposed || generation != Volatile.Read(ref _sessionReassertGeneration))
                return;

            // The installed G-Helper applies Aura unconditionally after session unlock/logon.
            // A controlled reload with skip_aura=1 releases its direct LampArray/HID claim.
            await ApplyNowAsync(
                reason,
                forceGHelperReload: true,
                forceOwnershipRefresh: true);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private async Task<KeyboardLightingApplyResult> ReconcileAsync(
        bool forceGHelperReload,
        bool forceOwnershipRefresh)
    {
        var mode = _configService.Current.KeyboardLighting.Mode;
        if (mode == KeyboardLightingMode.Unmanaged)
        {
            return new KeyboardLightingApplyResult(
                true,
                false,
                "Lighting left alone. Windows and G-Helper settings were not changed.");
        }

        var beforeAura = TryReadGHelperLighting();
        var gHelperAuraChanged = _lastObservedGHelperAura.HasValue &&
                                 _lastObservedGHelperAura.Value != beforeAura;
        int? auraMode = null;
        int? auraColor = null;
        var windowsOwns = mode == KeyboardLightingMode.WindowsDynamicLighting;
        var skipAura = windowsOwns ? 1 : 0;

        if (mode == KeyboardLightingMode.GHelperWindowsAccent)
        {
            if (!TryGetWindowsAccentArgb(out var accentArgb))
                return new KeyboardLightingApplyResult(false, false, "Windows did not return an accent color that G-Helper can use.");

            auraMode = GHelperStaticAuraMode;
            auraColor = unchecked((int)accentArgb);
        }

        DynamicLightingWriteResult registryResult;
        KeyboardLightingApplyResult gHelperResult;

        if (windowsOwns)
        {
            // First stop/reconfigure G-Helper so its direct HID/LampArray handle is released;
            // then let Windows acquire the device with foreground takeover disabled.
            var releaseGHelperLighting = forceGHelperReload ||
                                         gHelperAuraChanged ||
                                         !_lastObservedGHelperAura.HasValue;
            gHelperResult = await EnsureGHelperSettingsAsync(
                skipAura,
                auraMode,
                auraColor,
                releaseGHelperLighting);
            if (!gHelperResult.Success)
                return gHelperResult;

            var heartbeatDue = DateTime.UtcNow - _lastOwnershipRefreshUtc >= TimeSpan.FromSeconds(
                _configService.Current.KeyboardLighting.OwnershipHeartbeatSeconds);
            var refreshOwnership = forceOwnershipRefresh ||
                                   releaseGHelperLighting ||
                                   gHelperResult.Changed ||
                                   gHelperAuraChanged ||
                                   heartbeatDue;
            registryResult = SetDynamicLightingState(
                enabled: true,
                enforceWindowsOwnership: true,
                forceRefresh: refreshOwnership);
            if (refreshOwnership)
                _lastOwnershipRefreshUtc = DateTime.UtcNow;
        }
        else
        {
            // Windows must release the LampArray before G-Helper is restarted/applies Aura.
            registryResult = SetDynamicLightingState(
                enabled: false,
                enforceWindowsOwnership: false,
                forceRefresh: false);
            gHelperResult = await EnsureGHelperSettingsAsync(
                skipAura,
                auraMode,
                auraColor,
                forceGHelperReload);
            if (!gHelperResult.Success)
            {
                return new KeyboardLightingApplyResult(
                    false,
                    registryResult.Changed || gHelperResult.Changed,
                    $"Dynamic Lighting was disabled, but {gHelperResult.Message}");
            }
        }

        _lastObservedGHelperAura = TryReadGHelperLighting();
        var state = ReadDynamicLightingSnapshot(
            TryGetWindowsAccentArgb(out var currentAccent) ? currentAccent : null);
        var changed = registryResult.Changed || gHelperResult.Changed;
        var owner = windowsOwns
            ? $"Windows controls the lighting; app takeover is off; "
              + $"{state.WindowsOwnedDeviceCount}/{state.DeviceCount} device(s) aligned; "
              + $"effective color {FormatNullableArgb(state.EffectiveColorArgb)} ({state.EffectiveColorSource})"
            : mode == KeyboardLightingMode.GHelperWindowsAccent
                ? $"G-Helper controls the lighting with Windows accent {FormatArgb(unchecked((uint)auraColor!.Value))}"
                : "G-Helper controls the lighting and keeps its current Aura settings";
        var suffix = gHelperResult.Message.Length == 0 ? string.Empty : $" {gHelperResult.Message}";

        return new KeyboardLightingApplyResult(
            true,
            changed,
            $"{owner}; Dynamic Lighting is {(windowsOwns ? "enabled" : "disabled")}.{suffix}".Trim());
    }

    private async Task<KeyboardLightingApplyResult> EnsureGHelperSettingsAsync(
        int skipAura,
        int? auraMode,
        int? auraColor,
        bool forceReload)
    {
        if (!File.Exists(_gHelper.ConfigPath))
        {
            return new KeyboardLightingApplyResult(
                false,
                false,
                $"G-Helper config was not found: {_gHelper.ConfigPath}");
        }

        var current = await ReadGHelperConfigAsync();
        var configNeedsChange = ApplyDesiredGHelperValues(current, skipAura, auraMode, auraColor);
        var wasRunning = IsGHelperRunning(out var executablePath);

        if (!configNeedsChange && !(forceReload && wasRunning))
            return new KeyboardLightingApplyResult(true, false, string.Empty);

        if (wasRunning && !await StopGHelperAsync())
        {
            return new KeyboardLightingApplyResult(
                false,
                false,
                "Could not stop G-Helper cleanly, so its config was not touched.");
        }

        var wroteConfig = false;
        try
        {
            // Re-read only after G-Helper has exited so a final G-Helper save cannot race us.
            current = await ReadGHelperConfigAsync();
            wroteConfig = ApplyDesiredGHelperValues(current, skipAura, auraMode, auraColor);
            if (wroteConfig)
                WriteGHelperConfigAtomically(current);
        }
        catch
        {
            if (wasRunning)
                await StartGHelperAsync(executablePath);
            throw;
        }

        if (!wasRunning)
        {
            var detail = wroteConfig
                ? "G-Helper was not running; the setting will apply on its next launch."
                : string.Empty;
            return new KeyboardLightingApplyResult(true, wroteConfig, detail);
        }

        var restarted = await StartGHelperAsync(executablePath);
        if (!restarted)
        {
            return new KeyboardLightingApplyResult(
                false,
                wroteConfig,
                "G-Helper config was saved, but G-Helper could not be restarted. Start G-Helper manually.");
        }

        return new KeyboardLightingApplyResult(
            true,
            wroteConfig || forceReload,
            "G-Helper restarted so the new lighting setting can take effect.");
    }

    private static bool ApplyDesiredGHelperValues(
        JsonObject root,
        int skipAura,
        int? auraMode,
        int? auraColor)
    {
        var changed = SetIntIfDifferent(root, "skip_aura", skipAura);
        if (auraMode.HasValue)
            changed |= SetIntIfDifferent(root, "aura_mode", auraMode.Value);
        if (auraColor.HasValue)
            changed |= SetIntIfDifferent(root, "aura_color", auraColor.Value);
        return changed;
    }

    private async Task<JsonObject> ReadGHelperConfigAsync()
    {
        Exception? lastError = null;
        var settings = _configService.Current.KeyboardLighting;
        for (var attempt = 0; attempt < settings.GHelperConfigReadAttempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    _gHelper.ConfigPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                var node = await JsonNode.ParseAsync(
                    stream,
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                return node as JsonObject
                    ?? throw new JsonException("G-Helper config root is not a JSON object.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                lastError = ex;
                if (attempt + 1 < settings.GHelperConfigReadAttempts)
                    await Task.Delay(settings.GHelperConfigReadRetryMilliseconds);
            }
        }

        throw new IOException("Could not read G-Helper config after three attempts.", lastError);
    }

    private void WriteGHelperConfigAtomically(JsonObject root)
    {
        var directory = Path.GetDirectoryName(_gHelper.ConfigPath)
            ?? throw new InvalidOperationException("G-Helper config directory could not be resolved.");
        var backupPath = Path.Combine(directory, "config.before-GHelperAutoMode-lighting.json");
        if (!File.Exists(backupPath))
            File.Copy(_gHelper.ConfigPath, backupPath, overwrite: false);

        var tempPath = _gHelper.ConfigPath + $".automode.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(tempPath, root.ToJsonString(WriteOptions));
            File.Move(tempPath, _gHelper.ConfigPath, overwrite: true);
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
                // Preserve the original write exception; a stale temp file is harmless.
            }
        }
    }

    private static bool SetIntIfDifferent(JsonObject root, string name, int value)
    {
        if (TryGetInt(root, name) == value)
            return false;

        root[name] = value;
        return true;
    }

    private static int? TryGetInt(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is not JsonValue jsonValue)
            return null;
        return jsonValue.TryGetValue<int>(out var value) ? value : null;
    }

    private GHelperAuraSignature TryReadGHelperLighting()
    {
        try
        {
            if (!File.Exists(_gHelper.ConfigPath))
                return new GHelperAuraSignature(null, null, null, null, null);

            using var stream = new FileStream(
                _gHelper.ConfigPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var root = document.RootElement;
            return new GHelperAuraSignature(
                ReadInt(root, "skip_aura"),
                ReadInt(root, "aura_mode"),
                ReadInt(root, "aura_color"),
                ReadInt(root, "aura_color2"),
                ReadInt(root, "aura_speed"));
        }
        catch
        {
            return new GHelperAuraSignature(null, null, null, null, null);
        }
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static DynamicLightingWriteResult SetDynamicLightingState(
        bool enabled,
        bool enforceWindowsOwnership,
        bool forceRefresh)
    {
        var desired = enabled ? 1 : 0;
        var changed = false;

        using (var lighting = Registry.CurrentUser.CreateSubKey(LightingRegistryPath, writable: true))
        {
            if (lighting is null)
                throw new InvalidOperationException("Windows Dynamic Lighting registry key could not be opened.");
            changed |= SetRegistryDword(lighting, "AmbientLightingEnabled", desired, forceRefresh);
            if (enforceWindowsOwnership)
                changed |= SetRegistryDword(lighting, "ControlledByForegroundApp", 0, forceRefresh);
        }

        using var devices = Registry.CurrentUser.OpenSubKey(LightingDevicesRegistryPath, writable: true);
        if (devices is null)
            return new DynamicLightingWriteResult(changed);

        foreach (var deviceName in devices.GetSubKeyNames())
        {
            using var device = devices.OpenSubKey(deviceName, writable: true);
            if (device is null)
                continue;

            if (enabled || device.GetValue("AmbientLightingEnabled") is not null)
                changed |= SetRegistryDword(device, "AmbientLightingEnabled", desired, forceRefresh);
            if (enforceWindowsOwnership)
                changed |= SetRegistryDword(device, "ControlledByForegroundApp", 0, forceRefresh);
        }

        return new DynamicLightingWriteResult(changed);
    }

    private static bool SetRegistryDword(RegistryKey key, string name, int value, bool forceWrite)
    {
        var current = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var changed = current is not int currentInt || currentInt != value;
        if (!changed && !forceWrite)
            return false;

        key.SetValue(name, value, RegistryValueKind.DWord);
        return changed;
    }

    private static DynamicLightingSnapshot ReadDynamicLightingSnapshot(uint? windowsAccentArgb)
    {
        try
        {
            using var lighting = Registry.CurrentUser.OpenSubKey(LightingRegistryPath, writable: false);
            if (lighting is null)
            {
                return new DynamicLightingSnapshot(
                    null, null, null, null, null, "unavailable", 0, 0, 0);
            }

            var enabled = ReadRegistryBool(lighting, "AmbientLightingEnabled");
            var foregroundControl = ReadRegistryBool(lighting, "ControlledByForegroundApp");
            var useAccent = ReadRegistryBool(lighting, "UseSystemAccentColor");
            var configuredColor = ReadDynamicLightingColor(lighting);
            var deviceCount = 0;
            var enabledDevices = 0;
            var windowsOwnedDevices = 0;

            using var devices = Registry.CurrentUser.OpenSubKey(LightingDevicesRegistryPath, writable: false);
            if (devices is not null)
            {
                foreach (var deviceName in devices.GetSubKeyNames())
                {
                    using var device = devices.OpenSubKey(deviceName, writable: false);
                    if (device is null)
                        continue;

                    deviceCount++;
                    if (ReadRegistryBool(device, "AmbientLightingEnabled") == true)
                        enabledDevices++;
                    if (ReadRegistryBool(device, "ControlledByForegroundApp") == false)
                        windowsOwnedDevices++;
                }
            }

            var effectiveColor = useAccent == true && windowsAccentArgb.HasValue
                ? windowsAccentArgb
                : configuredColor;
            var colorSource = useAccent == true
                ? windowsAccentArgb.HasValue ? "Windows accent" : "Windows accent unavailable"
                : configuredColor.HasValue ? "Dynamic Lighting effect color" : "not exposed";

            return new DynamicLightingSnapshot(
                enabled,
                foregroundControl,
                useAccent,
                configuredColor,
                effectiveColor,
                colorSource,
                deviceCount,
                enabledDevices,
                windowsOwnedDevices);
        }
        catch
        {
            return new DynamicLightingSnapshot(
                null, null, null, null, null, "unavailable", 0, 0, 0);
        }
    }

    private static bool? ReadRegistryBool(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int value
            ? value != 0
            : null;

    private static uint? ReadDynamicLightingColor(RegistryKey key)
    {
        if (key.GetValue("Color", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int colorAbgr)
            return null;

        var raw = unchecked((uint)colorAbgr);
        return 0xFF000000u
               | ((raw & 0x000000FFu) << 16)
               | (raw & 0x0000FF00u)
               | ((raw & 0x00FF0000u) >> 16);
    }

    private static bool TryGetWindowsAccentArgb(out uint color)
    {
        color = 0;

        try
        {
            // Explorer keeps the active accent and its light/dark variants as eight RGBA
            // entries. Entry 3 is the actual Accent value returned by Windows UISettings.
            using var accent = Registry.CurrentUser.OpenSubKey(ExplorerAccentRegistryPath, writable: false);
            if (accent?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 16)
            {
                color = 0xFF000000u
                    | ((uint)palette[12] << 16)
                    | ((uint)palette[13] << 8)
                    | palette[14];
                return true;
            }
        }
        catch
        {
            // Continue with documented DWM and compatibility fallbacks.
        }

        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(DwmRegistryPath, writable: false);
            if (dwm?.GetValue("ColorizationColor") is int colorization)
            {
                color = 0xFF000000u | (unchecked((uint)colorization) & 0x00FFFFFFu);
                return true;
            }

            // AccentColor is stored as ABGR/COLORREF-style bytes on current Windows builds.
            if (dwm?.GetValue("AccentColor") is int accentAbgr)
            {
                var raw = unchecked((uint)accentAbgr);
                color = 0xFF000000u
                    | ((raw & 0x000000FFu) << 16)
                    | (raw & 0x0000FF00u)
                    | ((raw & 0x00FF0000u) >> 16);
                return true;
            }
        }
        catch
        {
            // Continue with the documented API fallback.
        }

        var result = DwmGetColorizationColor(out var rawColor, out _);
        if (result < 0)
            return false;

        // LEDs have no alpha channel. Normalize to opaque while preserving DWM's RGB bytes.
        color = 0xFF000000u | (rawColor & 0x00FFFFFFu);
        return true;
    }

    private static string FormatArgb(uint color) => $"#{color & 0x00FFFFFFu:X6}";

    private static string FormatNullableArgb(uint? color) =>
        color.HasValue ? FormatArgb(color.Value) : "unavailable";

    private static bool IsGHelperRunning(out string? executablePath)
    {
        executablePath = null;
        var processes = Process.GetProcessesByName("GHelper");
        try
        {
            foreach (var process in processes)
            {
                if (executablePath is not null)
                    continue;

                try { executablePath = process.MainModule?.FileName; }
                catch { /* An elevated G-Helper may hide its module path. */ }
            }
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private async Task<bool> StopGHelperAsync()
    {
        if (!IsGHelperRunning(out _))
            return true;

        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(GHelperExitEventName);
            exitEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var settings = _configService.Current.KeyboardLighting;
        var deadline = DateTime.UtcNow.AddSeconds(settings.GHelperRestartTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsGHelperRunning(out _))
                return true;
            await Task.Delay(settings.GHelperProcessPollMilliseconds);
        }

        return !IsGHelperRunning(out _);
    }

    private async Task<bool> StartGHelperAsync(string? fallbackExecutablePath)
    {
        var settings = _configService.Current.KeyboardLighting;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            for (var attempt = 0; attempt < settings.GHelperTaskStartAttempts; attempt++)
            {
                try
                {
                    using var task = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "schtasks.exe",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    task.StartInfo.ArgumentList.Add("/Run");
                    task.StartInfo.ArgumentList.Add("/TN");
                    task.StartInfo.ArgumentList.Add($@"\GHelper_{sid}");
                    if (task.Start())
                    {
                        await task.WaitForExitAsync();
                        if (task.ExitCode == 0 && await WaitForGHelperStartAsync())
                            return true;
                    }
                }
                catch
                {
                    // Retry briefly: Task Scheduler can still be retiring the old instance.
                }

                if (IsGHelperRunning(out _))
                    return true;
                if (attempt + 1 < settings.GHelperTaskStartAttempts)
                    await Task.Delay(settings.GHelperTaskRetryMilliseconds);
            }
        }

        fallbackExecutablePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "GHelper.exe");
        if (!File.Exists(fallbackExecutablePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fallbackExecutablePath,
                UseShellExecute = true
            });
            return await WaitForGHelperStartAsync();
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForGHelperStartAsync()
    {
        var settings = _configService.Current.KeyboardLighting;
        var deadline = DateTime.UtcNow.AddSeconds(settings.GHelperRestartTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsGHelperRunning(out _))
                return true;
            await Task.Delay(settings.GHelperProcessPollMilliseconds);
        }
        return IsGHelperRunning(out _);
    }

    private void UpdateTimerInterval()
    {
        var seconds = Math.Clamp(
            _configService.Current.KeyboardLighting.AccentPollIntervalSeconds,
            2,
            300);
        _timer.Interval = checked(seconds * 1000);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _timer.Stop();
        _timer.Tick -= TimerOnTick;
        _timer.Dispose();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(
        out uint pcrColorization,
        [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);
}
