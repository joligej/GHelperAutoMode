using System.Runtime.InteropServices;

namespace GHelperAutoMode;

internal sealed class CpuMonitor
{
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;


    public void Reset()
    {
        _previousIdle = null;
        _previousKernel = null;
        _previousUser = null;
    }

    public double? Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);

        if (_previousIdle is null || _previousKernel is null || _previousUser is null)
        {
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            return null;
        }

        // GetSystemTimes is cumulative. If the counters ever move backwards (for example
        // around an unusual clock/provider reset), discard this interval rather than letting
        // unsigned subtraction manufacture a giant utilization spike.
        if (idleValue < _previousIdle.Value
            || kernelValue < _previousKernel.Value
            || userValue < _previousUser.Value)
        {
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            return null;
        }

        var idleDelta = idleValue - _previousIdle.Value;
        var kernelDelta = kernelValue - _previousKernel.Value;
        var userDelta = userValue - _previousUser.Value;

        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;

        var total = kernelDelta + userDelta;
        if (total == 0)
            return 0;

        if (idleDelta >= total)
            return 0;

        var busy = total - idleDelta;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static ulong ToUInt64(FILETIME fileTime) =>
        ((ulong)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }
}
