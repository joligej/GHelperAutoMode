using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace GHelperAutoMode;

internal sealed class GHelperController
{
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12; // Alt
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkF16 = 0x7F;
    private const ushort VkF17 = 0x80;
    private const ushort VkF18 = 0x81;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint WmHotkey = 0x0312;

    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfExtendedkey = 0x0001;

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GHelper",
        "config.json");

    public static int SendInputStructSize => Marshal.SizeOf<INPUT>();

    public string LastControlTransport { get; private set; } = "none";
    public string LastControlDetail { get; private set; } = "No mode request has been made yet.";
    public DateTime? LastControlAt { get; private set; }
    public PerformanceMode? LastConfirmedMode { get; private set; }
    public DateTime? LastConfirmationAt { get; private set; }
    public string LastConfirmationDetail { get; private set; } = "No automatic profile request has been confirmed yet.";
    public int LastDetectedProcessCount { get; private set; }
    public int LastDetectedWindowCount { get; private set; }
    private IntPtr _preferredHotkeyWindow;

    public bool IsGHelperRunning()
    {
        var processes = GetGHelperProcesses();
        LastDetectedProcessCount = processes.Count;
        DisposeProcesses(processes);
        return LastDetectedProcessCount > 0;
    }

    public int GetGHelperWindowCount()
    {
        var windows = FindGHelperWindows(out var processCount);
        LastDetectedProcessCount = processCount;
        LastDetectedWindowCount = windows.Count;
        return windows.Count;
    }

    public GHelperSettingsSnapshot ReadSettings()
    {
        Exception? lastError = null;

        // G-Helper can rewrite config.json while we are polling it. A couple of very
        // short retries avoid turning a harmless write race into a failed mode switch.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return ReadSettingsOnce();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                lastError = ex;
                if (attempt < 2)
                    Thread.Sleep(15);
            }
        }

        return new GHelperSettingsSnapshot(
            false, null, false, "control-shift-alt",
            VkF17, VkF18, VkF16,
            null, null, null, lastError?.Message ?? "Unknown G-Helper config read error.");
    }

    private GHelperSettingsSnapshot ReadSettingsOnce()
    {
        if (!File.Exists(ConfigPath))
        {
            return new GHelperSettingsSnapshot(
                false, null, false, "control-shift-alt",
                VkF17, VkF18, VkF16,
                null, null, null,
                "G-Helper config.json was not found.");
        }

        using var stream = new FileStream(
            ConfigPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.SequentialScan);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;

        PerformanceMode? currentMode = null;
        if (root.TryGetProperty("performance_mode", out var modeValue) && modeValue.TryGetInt32(out var mode))
        {
            currentMode = mode switch
            {
                0 => PerformanceMode.Balanced,
                1 => PerformanceMode.Turbo,
                2 => PerformanceMode.Silent,
                3 => PerformanceMode.Custom1,
                4 => PerformanceMode.Custom2,
                _ => null
            };
        }

        var skipHotkeys = ReadInt(root, "skip_hotkeys") == 1;
        var modifier = ReadString(root, "modifier_keybind_alt") ?? "control-shift-alt";

        return new GHelperSettingsSnapshot(
            true,
            currentMode,
            skipHotkeys,
            modifier,
            ReadVirtualKey(root, "keybind_profile_0", VkF17),
            ReadVirtualKey(root, "keybind_profile_1", VkF18),
            ReadVirtualKey(root, "keybind_profile_2", VkF16),
            ReadInt(root, "disable_power_event"),
            ReadInt(root, "screen_auto"),
            ReadInt(root, "gpu_mode"));
    }

    public PerformanceMode? TryReadCurrentMode() => ReadSettings().CurrentMode;

    public ModeRequestResult RequestMode(
        PerformanceMode mode,
        bool inputGuardEnabled,
        DisplayState displayState)
    {
        if (mode is not (PerformanceMode.Silent or PerformanceMode.Balanced or PerformanceMode.Turbo))
            return Fail($"Performance mode {mode} is not an automation target.");

        var settings = ReadSettings();
        if (!settings.ConfigReadable)
            return Fail($"G-Helper config is not readable right now: {settings.Error ?? "unknown error"}");

        if (settings.CurrentMode == mode)
            return Sent("no-op", $"G-Helper already reports {mode}; no input was injected.");

        if (settings.SkipHotkeys)
            return Fail("G-Helper has skip_hotkeys=1, so its profile hotkeys are disabled.");

        var configuredKey = mode switch
        {
            PerformanceMode.Silent => settings.ProfileKeySilent,
            PerformanceMode.Balanced => settings.ProfileKeyBalanced,
            PerformanceMode.Turbo => settings.ProfileKeyTurbo,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        if (configuredKey <= 0 || configuredKey > byte.MaxValue)
            return Fail($"G-Helper profile hotkey for {mode} is disabled or invalid (VK={configuredKey}).");

        var enabledProfileKeys = new[]
        {
            settings.ProfileKeySilent,
            settings.ProfileKeyBalanced,
            settings.ProfileKeyTurbo
        }.Where(key => key > 0 && key <= byte.MaxValue).ToArray();

        if (enabledProfileKeys.Distinct().Count() != enabledProfileKeys.Length)
        {
            return Fail(
                "Two or more G-Helper profile hotkeys use the same virtual key; automatic switching is ambiguous until those shortcuts are unique.");
        }

        var functionKey = (ushort)configuredKey;
        var modifierMask = ParseModifierMask(settings.ModifierKeybindAlt);
        var modifierKeys = ParseModifierKeys(settings.ModifierKeybindAlt);
        var hotkey = DescribeHotkey(modifierKeys, functionKey);

        // SendInput/keybd_event are real synthetic input as far as the Windows power
        // manager is concerned. They can wake an Off display or cancel Dimmed state. When
        // the display is not positively known to be On, deliver WM_HOTKEY directly to
        // G-Helper's private hotkey window instead. If that non-waking path is unavailable,
        // defer the transition; never fall through to a wake-capable keyboard transport.
        if (!displayState.AllowsInputInjection())
        {
            var wakeSafeDirect = TryPostDirectHotkeyToGHelperWindows(modifierMask, functionKey);
            if (wakeSafeDirect.PostedCount > 0)
            {
                return Sent(
                    "WM_HOTKEY (non-waking)",
                    $"Display state is {displayState}; queued {hotkey} directly to "
                    + $"{wakeSafeDirect.PostedCount}/{wakeSafeDirect.WindowCount} G-Helper window candidates. "
                    + "No keyboard input was injected; asynchronous G-Helper confirmation pending.");
            }

            var error = wakeSafeDirect.LastError == 0
                ? "no candidate window was found"
                : $"Win32 error {wakeSafeDirect.LastError}";
            return Deferred(
                $"Display state is {displayState}; wake-capable keyboard input is suppressed. "
                + $"The non-waking WM_HOTKEY route could not be queued ({error}), so the mode change will be retried safely.");
        }

        // Primary transport: inject exactly one documented global G-Helper profile hotkey.
        // This is the most deterministic path because G-Helper itself owns the registered
        // global shortcut. It does not require focus or administrator rights when both
        // programs run at the normal user integrity level.
        if (inputGuardEnabled && IsUserHoldingModifierKeys())
            return Deferred("A Ctrl/Shift/Alt/Win key is currently held; delaying the G-Helper profile shortcut to avoid colliding with user input.");

        var sendInput = TrySendInputHotkey(modifierKeys, functionKey);
        if (sendInput.Success)
        {
            // A successful SendInput call means Windows accepted the complete shortcut.
            // Do not block the WinForms UI thread waiting for G-Helper to persist its config,
            // and never emit a duplicate fallback merely because that persistence is delayed.
            // AutomationEngine owns the longer asynchronous confirmation window.
            return Sent(
                "SendInput",
                $"Sent one {hotkey} shortcut successfully; asynchronous G-Helper confirmation pending. INPUT size={Marshal.SizeOf<INPUT>()} bytes.");
        }

        RecordTransport("SendInput", $"{sendInput.Message} Trying conservative fallbacks because the primary input call itself failed.");

        // Fallback 1 mirrors the legacy keyboard injection helper G-Helper itself still
        // uses in parts of its input stack. It is used only if SendInput itself failed.
        TryLegacyKeybdEventHotkey(modifierKeys, functionKey);
        if (WaitForMode(mode, milliseconds: 750))
        {
            return Sent(
                "keybd_event",
                $"G-Helper confirmed {mode} after legacy keybd_event fallback ({hotkey}).");
        }

        // Fallback 2: direct WM_HOTKEY delivery to G-Helper candidate windows. This is
        // deliberately last so normal switching never sprays messages at multiple windows.
        var direct = TryDirectHotkeyToGHelper(mode, modifierMask, functionKey);
        if (direct.Confirmed)
        {
            return Sent(
                "WM_HOTKEY",
                $"G-Helper confirmed {mode} after direct WM_HOTKEY fallback ({hotkey}); candidate {direct.CandidateIndex}/{direct.WindowCount}.");
        }

        var details = $"{sendInput.Message} Direct WM_HOTKEY tried {direct.PostedCount}/{direct.WindowCount} candidates.";

        return Fail($"G-Helper did not confirm {mode}. {details}");
    }

    private (int WindowCount, int PostedCount, int LastError) TryPostDirectHotkeyToGHelperWindows(
        uint modifiers,
        ushort key)
    {
        var windows = FindGHelperWindows(out var processCount);
        LastDetectedProcessCount = processCount;
        LastDetectedWindowCount = windows.Count;

        if (_preferredHotkeyWindow != IntPtr.Zero && windows.Remove(_preferredHotkeyWindow))
            windows.Insert(0, _preferredHotkeyWindow);
        else if (_preferredHotkeyWindow != IntPtr.Zero)
            _preferredHotkeyWindow = IntPtr.Zero;

        var lParamValue = ((nuint)key << 16) | (nuint)(modifiers & 0xFFFFu);
        var lParam = new IntPtr(unchecked((long)lParamValue));
        var posted = 0;
        var lastError = 0;

        // There is normally one hidden, untitled KeyboardHook window. Posting once to each
        // G-Helper-owned candidate avoids guessing the private HWND while remaining
        // non-waking and idempotent: only the KeyboardHook window handles this message and
        // every profile hotkey selects an explicit target mode.
        foreach (var hwnd in windows)
        {
            if (PostMessage(hwnd, WmHotkey, IntPtr.Zero, lParam))
                posted++;
            else
                lastError = Marshal.GetLastWin32Error();
        }

        return (windows.Count, posted, lastError);
    }

    private (int WindowCount, int PostedCount, int LastError, bool Confirmed, int CandidateIndex) TryDirectHotkeyToGHelper(
        PerformanceMode mode,
        uint modifiers,
        ushort key)
    {
        var windows = FindGHelperWindows(out var processCount);
        LastDetectedProcessCount = processCount;
        LastDetectedWindowCount = windows.Count;

        if (_preferredHotkeyWindow != IntPtr.Zero && windows.Remove(_preferredHotkeyWindow))
            windows.Insert(0, _preferredHotkeyWindow);
        else if (_preferredHotkeyWindow != IntPtr.Zero)
            _preferredHotkeyWindow = IntPtr.Zero;

        var lParamValue = ((nuint)key << 16) | (nuint)(modifiers & 0xFFFFu);
        var lParam = new IntPtr(unchecked((long)lParamValue));
        var posted = 0;
        var lastError = 0;

        for (var i = 0; i < windows.Count; i++)
        {
            var hwnd = windows[i];
            if (!PostMessage(hwnd, WmHotkey, IntPtr.Zero, lParam))
            {
                lastError = Marshal.GetLastWin32Error();
                continue;
            }

            posted++;
            var verificationMilliseconds = hwnd == _preferredHotkeyWindow || i == 0 ? 650 : 250;
            if (WaitForMode(mode, verificationMilliseconds))
            {
                _preferredHotkeyWindow = hwnd;
                return (windows.Count, posted, lastError, true, i + 1);
            }
        }

        // Allow a slightly slow G-Helper config write to settle after the final fallback.
        // The requested hotkey selects an explicit profile; waiting briefly avoids declaring
        // failure while G-Helper is still persisting the already-delivered command.
        if (posted > 0 && WaitForMode(mode, milliseconds: 500))
            return (windows.Count, posted, lastError, true, Math.Max(1, posted));

        return (windows.Count, posted, lastError, false, 0);
    }

    private static List<IntPtr> FindGHelperWindows(out int processCount)
    {
        var processes = GetGHelperProcesses();
        processCount = processes.Count;
        var processIds = processes.Select(p => (uint)p.Id).ToHashSet();
        DisposeProcesses(processes);

        var windows = new List<IntPtr>();
        if (processIds.Count == 0)
            return windows;

        var enumerated = EnumWindows((hwnd, lParam) =>
        {
            var threadId = GetWindowThreadProcessId(hwnd, out var processId);
            if (threadId != 0 && processIds.Contains(processId))
                windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        if (!enumerated)
            return [];

        // G-Helper's KeyboardHook uses a hidden NativeWindow with no title. Prefer
        // candidates with those characteristics before ordinary settings/forms.
        return windows
            .Distinct()
            .OrderBy(hwnd => IsWindowVisible(hwnd) ? 1 : 0)
            .ThenBy(hwnd => GetWindowTextLength(hwnd) > 0 ? 1 : 0)
            .ToList();
    }

    private static List<Process> GetGHelperProcesses()
    {
        try
        {
            return Process.GetProcessesByName("GHelper").ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
            process.Dispose();
    }

    private bool WaitForMode(PerformanceMode mode, int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        do
        {
            if (TryReadCurrentMode() == mode)
                return true;

            Thread.Sleep(30);
        }
        while (Environment.TickCount64 < until);

        return TryReadCurrentMode() == mode;
    }

    private void TryLegacyKeybdEventHotkey(List<ushort> modifiers, ushort functionKey)
    {
        try
        {
            foreach (var modifier in modifiers)
                keybd_event((byte)modifier, 0, LegacyFlags(modifier), UIntPtr.Zero);

            keybd_event((byte)functionKey, 0, LegacyFlags(functionKey), UIntPtr.Zero);
            Thread.Sleep(2);
            keybd_event((byte)functionKey, 0, LegacyFlags(functionKey) | KeyeventfKeyup, UIntPtr.Zero);

            for (var i = modifiers.Count - 1; i >= 0; i--)
                keybd_event((byte)modifiers[i], 0, LegacyFlags(modifiers[i]) | KeyeventfKeyup, UIntPtr.Zero);

            RecordTransport("keybd_event", $"Emitted {DescribeHotkey(modifiers, functionKey)} using G-Helper-style keybd_event fallback.");
        }
        catch (Exception ex)
        {
            BestEffortReleaseLegacyKeys(modifiers, functionKey);
            RecordTransport("keybd_event", $"Legacy keyboard fallback failed: {ex.Message}");
        }
    }

    private (bool Success, string Message) TrySendInputHotkey(List<ushort> modifiers, ushort functionKey)
    {
        var inputSize = Marshal.SizeOf<INPUT>();
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;
        if (inputSize != expectedSize)
        {
            return (false,
                $"Internal Win32 INPUT layout is invalid: {inputSize} bytes, expected {expectedSize}. "
                + "Refusing SendInput instead of issuing malformed input.");
        }

        var inputs = new List<INPUT>();
        inputs.AddRange(modifiers.Select(KeyDown));
        inputs.Add(KeyDown(functionKey));
        inputs.Add(KeyUp(functionKey));

        for (var i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(KeyUp(modifiers[i]));

        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, inputSize);
        if (sent != (uint)array.Length)
        {
            var error = Marshal.GetLastWin32Error();
            BestEffortReleaseInjectedKeys(modifiers, functionKey);

            var detail = error == 0
                ? "Windows returned no error code; input injection may have been blocked by UIPI/integrity-level differences."
                : $"Win32 error {error}.";
            return (false, $"SendInput sent {sent}/{array.Length} events. {detail}");
        }

        RecordTransport("SendInput", $"Emitted {DescribeHotkey(modifiers, functionKey)} using SendInput.");
        return (true, $"Sent {DescribeHotkey(modifiers, functionKey)}.");
    }


    public void RecordConfirmation(PerformanceMode mode, string reason)
    {
        LastConfirmedMode = mode;
        LastConfirmationAt = DateTime.Now;
        LastConfirmationDetail = $"G-Helper confirmed {mode}. Reason: {reason}";
    }

    private ModeRequestResult Sent(string transport, string message)
    {
        RecordTransport(transport, message);
        return ModeRequestResult.Sent(message);
    }

    private ModeRequestResult Fail(string message)
    {
        RecordTransport("failed", message);
        return ModeRequestResult.Failed(message);
    }

    private ModeRequestResult Deferred(string message)
    {
        RecordTransport("deferred", message);
        return ModeRequestResult.Deferred(message);
    }

    private void RecordTransport(string transport, string detail)
    {
        LastControlTransport = transport;
        LastControlDetail = detail;
        LastControlAt = DateTime.Now;
    }

    private static void BestEffortReleaseLegacyKeys(List<ushort> modifiers, ushort functionKey)
    {
        try
        {
            keybd_event((byte)functionKey, 0, LegacyFlags(functionKey) | KeyeventfKeyup, UIntPtr.Zero);
            for (var i = modifiers.Count - 1; i >= 0; i--)
                keybd_event((byte)modifiers[i], 0, LegacyFlags(modifiers[i]) | KeyeventfKeyup, UIntPtr.Zero);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void BestEffortReleaseInjectedKeys(List<ushort> modifiers, ushort functionKey)
    {
        try
        {
            var releases = new List<INPUT> { KeyUp(functionKey) };
            for (var i = modifiers.Count - 1; i >= 0; i--)
                releases.Add(KeyUp(modifiers[i]));

            var array = releases.ToArray();
            var released = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
            if (released == 0)
                return;
        }
        catch
        {
            // Cleanup is strictly best effort after a failed/partial SendInput call.
        }
    }

    private static string DescribeHotkey(List<ushort> modifiers, ushort key)
    {
        var parts = modifiers.Select(ModifierKeyName).Append(VirtualKeyName(key));
        return string.Join("+", parts);
    }

    private static string ModifierKeyName(ushort key) => key switch
    {
        VkControl => "Ctrl",
        VkShift => "Shift",
        VkMenu => "Alt",
        VkLWin or VkRWin => "Win",
        _ => VirtualKeyName(key)
    };

    public static string VirtualKeyName(int key)
    {
        if (key <= 0 || key > byte.MaxValue)
            return $"disabled/invalid ({key})";

        return VirtualKeyName((ushort)key);
    }

    private static string VirtualKeyName(ushort key)
    {
        if (key >= 0x70 && key <= 0x87)
            return $"F{key - 0x6F}";

        if ((key is >= 0x30 and <= 0x39) || (key is >= 0x41 and <= 0x5A))
            return new string((char)key, 1);

        return $"VK_0x{key:X2}";
    }

    private static uint ParseModifierMask(string? value)
    {
        var effectiveValue = string.IsNullOrWhiteSpace(value) ? "control-shift-alt" : value;
        var tokens = effectiveValue
            .Split(['-', '+', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        uint mask = 0;
        foreach (var token in tokens)
        {
            mask |= token.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "shift" => ModShift,
                "alt" or "menu" => ModAlt,
                "win" or "windows" => ModWin,
                _ => 0u
            };
        }

        return mask;
    }

    private static List<ushort> ParseModifierKeys(string? value)
    {
        var result = new List<ushort>();
        var effectiveValue = string.IsNullOrWhiteSpace(value) ? "control-shift-alt" : value;
        var tokens = effectiveValue
            .Split(['-', '+', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            ushort? key = token.ToLowerInvariant() switch
            {
                "ctrl" or "control" => VkControl,
                "shift" => VkShift,
                "alt" or "menu" => VkMenu,
                "win" or "windows" => VkLWin,
                _ => null
            };

            if (key.HasValue && !result.Contains(key.Value))
                result.Add(key.Value);
        }

        return result;
    }

    private static bool IsUserHoldingModifierKeys() =>
        IsKeyDown(VkControl)
        || IsKeyDown(VkShift)
        || IsKeyDown(VkMenu)
        || IsKeyDown(VkLWin)
        || IsKeyDown(VkRWin);

    private static bool IsKeyDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static int? ReadInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => value,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    private static int ReadVirtualKey(JsonElement root, string property, ushort defaultValue)
    {
        var value = ReadInt(root, property);
        return value ?? defaultValue;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return null;

        return element.GetString();
    }

    private static INPUT KeyDown(ushort key) => new()
    {
        Type = InputKeyboard,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = key,
                DwFlags = IsExtendedKey(key) ? KeyeventfExtendedkey : 0
            }
        }
    };

    private static INPUT KeyUp(ushort key) => new()
    {
        Type = InputKeyboard,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = key,
                DwFlags = KeyeventfKeyup | (IsExtendedKey(key) ? KeyeventfExtendedkey : 0)
            }
        }
    };


    private static uint LegacyFlags(ushort key) => IsExtendedKey(key) ? KeyeventfExtendedkey : 0u;

    private static bool IsExtendedKey(ushort key) => key is
        VkLWin or VkRWin
        or 0x21 or 0x22 or 0x23 or 0x24 // Page Up/Down, End, Home
        or 0x25 or 0x26 or 0x27 or 0x28 // Arrow keys
        or 0x2D or 0x2E                 // Insert, Delete
        or 0x6F                         // Numpad Divide
        or 0xA3 or 0xA5;                // Right Ctrl, Right Alt

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mi;

        [FieldOffset(0)]
        public KEYBDINPUT Ki;

        [FieldOffset(0)]
        public HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }
}
