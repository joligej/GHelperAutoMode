using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace GHelperAutoMode;

/// <summary>
/// Reads the ASUS firmware CPU temperature endpoint through the same ATKACPI device
/// interface used by G-Helper. This avoids adding a heavyweight hardware-monitor
/// dependency and normally works at standard user integrity on supported ASUS laptops.
/// </summary>
internal sealed class AsusCpuTemperatureMonitor : IDisposable
{
    private const string DevicePath = @"\\.\ATKACPI";
    private const uint ControlCode = 0x0022240C;
    private const uint Dsts = 0x53545344;
    private const uint TempCpu = 0x00120094;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private SafeFileHandle? _handle;
    private long? _lastGoodAt;
    private double? _lastGood;
    private string? _lastError;

    public string Provider => _handle is { IsInvalid: false, IsClosed: false } ? "ASUS ACPI" : "ASUS ACPI (reconnecting)";
    public string? LastError => _lastError;

    public CpuTemperatureTelemetry Sample(int graceSeconds = 5)
    {
        try
        {
            EnsureOpen();
            if (_handle is null || _handle.IsInvalid || _handle.IsClosed)
                throw new InvalidOperationException("ATKACPI device handle is unavailable.");

            var value = ReadDeviceValue(TempCpu);
            if (value is > 0 and < 125)
            {
                var completedAt = MonotonicClock.Now;
                _lastGood = value;
                _lastGoodAt = completedAt;
                _lastError = null;
                return new CpuTemperatureTelemetry(value, TelemetryQuality.Fresh, "ASUS ACPI");
            }

            throw new InvalidOperationException($"ASUS CPU temperature endpoint returned invalid value {value}.");
        }
        catch (Exception ex)
        {
            var failedAt = MonotonicClock.Now;
            _lastError = ex.Message;
            DropHandle();

            if (_lastGood.HasValue
                && _lastGoodAt.HasValue
                && MonotonicClock.Elapsed(_lastGoodAt.Value, failedAt) <= TimeSpan.FromSeconds(Math.Max(0, graceSeconds)))
            {
                return new CpuTemperatureTelemetry(
                    _lastGood,
                    TelemetryQuality.GraceCache,
                    "ASUS ACPI grace cache",
                    $"Using last valid CPU temperature during short telemetry grace period. {ex.Message}");
            }

            return new CpuTemperatureTelemetry(null, TelemetryQuality.Unavailable, "ASUS ACPI unavailable", ex.Message);
        }
    }

    public void ResetAfterResume()
    {
        DropHandle();
        _lastGood = null;
        _lastGoodAt = null;
        _lastError = null;
    }

    private void EnsureOpen()
    {
        if (_handle is { IsInvalid: false, IsClosed: false })
            return;

        _handle?.Dispose();
        _handle = CreateFile(
            DevicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            _handle = null;
            throw new InvalidOperationException($"Could not open {DevicePath}; Win32 error {error}.");
        }
    }

    private int ReadDeviceValue(uint deviceId)
    {
        if (_handle is null)
            throw new InvalidOperationException("ATKACPI is not open.");

        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);

        var acpiBuffer = new byte[8 + args.Length];
        BitConverter.GetBytes(Dsts).CopyTo(acpiBuffer, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(acpiBuffer, 4);
        Array.Copy(args, 0, acpiBuffer, 8, args.Length);

        var output = new byte[16];
        if (!DeviceIoControl(
                _handle,
                ControlCode,
                acpiBuffer,
                (uint)acpiBuffer.Length,
                output,
                (uint)output.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"ATKACPI DeviceIoControl failed with Win32 error {error}.");
        }

        if (bytesReturned < sizeof(int))
            throw new InvalidOperationException($"ATKACPI returned only {bytesReturned} bytes.");

        // ASUS DSTS scalar values are returned with bit 16 set. G-Helper applies
        // the same 65536 offset when reading this endpoint.
        return BitConverter.ToInt32(output, 0) - 65536;
    }

    private void DropHandle()
    {
        try { _handle?.Dispose(); } catch { /* best effort */ }
        _handle = null;
    }

    public void Dispose()
    {
        DropHandle();
        GC.SuppressFinalize(this);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
