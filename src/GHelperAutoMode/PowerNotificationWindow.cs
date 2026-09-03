using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal sealed class PowerNotificationWindow : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int DeviceNotifyWindowHandle = 0;
    private const int WmWtsSessionChange = 0x02B1;
    private const int NotifyForThisSession = 0;
    private const int WtsConsoleConnect = 0x1;
    private const int WtsSessionLogon = 0x5;
    private const int WtsSessionUnlock = 0x8;

    // Microsoft recommends GUID_SESSION_DISPLAY_STATUS for interactive user-mode applications.
    private static readonly Guid GuidSessionDisplayStatus =
        new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    private IntPtr _displayRegistrationHandle;
    private bool _sessionNotificationRegistered;
    private long? _lastResumeNotificationAt;

    public bool DisplayNotificationRegistered => _displayRegistrationHandle != IntPtr.Zero;
    public int DisplayRegistrationError { get; private set; }
    public bool SessionNotificationRegistered => _sessionNotificationRegistered;
    public int SessionRegistrationError { get; private set; }

    public event Action<DisplayState>? DisplayStateChanged;
    public event Action? Resumed;
    public event Action? SessionReady;

    public PowerNotificationWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "GHelperAutoModePowerWindow",
            Parent = new IntPtr(-3) // HWND_MESSAGE
        });

        var guid = GuidSessionDisplayStatus;
        _displayRegistrationHandle = RegisterPowerSettingNotification(
            Handle,
            ref guid,
            DeviceNotifyWindowHandle);

        if (_displayRegistrationHandle == IntPtr.Zero)
            DisplayRegistrationError = Marshal.GetLastWin32Error();

        _sessionNotificationRegistered = WTSRegisterSessionNotification(
            Handle,
            NotifyForThisSession);
        if (!_sessionNotificationRegistered)
            SessionRegistrationError = Marshal.GetLastWin32Error();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPowerBroadcast)
        {
            var eventType = m.WParam.ToInt32();

            if (eventType == PbtPowerSettingChange && m.LParam != IntPtr.Zero)
                HandlePowerSettingChange(m.LParam);
            else if (eventType is PbtApmResumeAutomatic or PbtApmResumeSuspend)
                HandleResume();
        }
        else if (m.Msg == WmWtsSessionChange)
        {
            var reason = m.WParam.ToInt32();
            if (reason is WtsConsoleConnect or WtsSessionLogon or WtsSessionUnlock)
                SessionReady?.Invoke();
        }

        base.WndProc(ref m);
    }

    private void HandleResume()
    {
        var now = MonotonicClock.Now;

        // Windows can emit both resume variants for one physical resume. Treat that pair as
        // one boundary so fresh provider state/evidence is not immediately reset a second
        // time after sampling has already restarted.
        if (_lastResumeNotificationAt.HasValue
            && MonotonicClock.Elapsed(_lastResumeNotificationAt.Value, now) < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastResumeNotificationAt = now;
        Resumed?.Invoke();
    }

    private void HandlePowerSettingChange(IntPtr data)
    {
        try
        {
            var settingGuid = Marshal.PtrToStructure<Guid>(data);
            if (settingGuid != GuidSessionDisplayStatus)
                return;

            var dataLength = Marshal.ReadInt32(data, 16);
            if (dataLength < sizeof(int))
                return;

            var value = Marshal.ReadInt32(data, 20);
            var state = value switch
            {
                0 => DisplayState.Off,
                1 => DisplayState.On,
                2 => DisplayState.Dimmed,
                _ => DisplayState.Unknown
            };

            if (state != DisplayState.Unknown)
                DisplayStateChanged?.Invoke(state);
        }
        catch
        {
            // Ignore malformed/unexpected power messages; automation will remain conservative.
        }
    }

    public void Dispose()
    {
        if (_sessionNotificationRegistered)
        {
            _ = WTSUnRegisterSessionNotification(Handle);
            _sessionNotificationRegistered = false;
        }

        if (_displayRegistrationHandle != IntPtr.Zero)
        {
            _ = UnregisterPowerSettingNotification(_displayRegistrationHandle);
            _displayRegistrationHandle = IntPtr.Zero;
        }

        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient,
        ref Guid PowerSettingGuid,
        int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
