using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GHelperAutoMode;

internal sealed record ForegroundActivity(
    string ProcessName,
    int? ProcessId,
    double? CoreEquivalentCpuPercent);

internal sealed class ForegroundAppMonitor
{
    private int? _previousProcessId;
    private TimeSpan? _previousProcessorTime;
    private long? _previousSampleAt;

    public ForegroundActivity Sample(long now)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Reset();
                return new ForegroundActivity(string.Empty, null, null);
            }

            var threadId = GetWindowThreadProcessId(hwnd, out var processId);
            if (threadId == 0 || processId == 0)
            {
                Reset();
                return new ForegroundActivity(string.Empty, null, null);
            }

            using var process = Process.GetProcessById(unchecked((int)processId));
            var processName = process.ProcessName;
            var processorTime = process.TotalProcessorTime;
            double? coreEquivalent = null;

            if (_previousProcessId == process.Id
                && _previousProcessorTime.HasValue
                && _previousSampleAt.HasValue)
            {
                var wall = MonotonicClock.Elapsed(_previousSampleAt.Value, now).TotalSeconds;
                var cpu = (processorTime - _previousProcessorTime.Value).TotalSeconds;
                if (wall > 0 && cpu >= 0)
                {
                    // 100% means approximately one logical CPU core fully occupied.
                    // Multi-threaded foreground work may legitimately exceed 100%.
                    var max = Math.Max(100, Environment.ProcessorCount * 100.0);
                    coreEquivalent = Math.Clamp(cpu / wall * 100.0, 0, max);
                }
            }

            _previousProcessId = process.Id;
            _previousProcessorTime = processorTime;
            _previousSampleAt = now;
            return new ForegroundActivity(processName, process.Id, coreEquivalent);
        }
        catch
        {
            Reset();
            return new ForegroundActivity(string.Empty, null, null);
        }
    }

    public void Reset()
    {
        _previousProcessId = null;
        _previousProcessorTime = null;
        _previousSampleAt = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
