using System.Diagnostics;

namespace GHelperAutoMode;

internal static class MonotonicClock
{
    public static long Now => Stopwatch.GetTimestamp();

    public static TimeSpan Elapsed(long? start, long end) =>
        start.HasValue ? Elapsed(start.Value, end) : TimeSpan.Zero;

    public static TimeSpan Elapsed(long start, long end)
    {
        if (end <= start)
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds((end - start) / (double)Stopwatch.Frequency);
    }
}

internal sealed class RollingAverage
{
    private readonly Queue<(long Timestamp, double Value)> _samples = new();

    public double? AddAndGetAverage(long now, double? value, TimeSpan window)
    {
        if (value.HasValue)
            _samples.Enqueue((now, value.Value));

        while (_samples.Count > 0 && MonotonicClock.Elapsed(_samples.Peek().Timestamp, now) >= window)
            _samples.Dequeue();

        if (_samples.Count == 0)
            return null;

        return _samples.Average(sample => sample.Value);
    }

    public void Clear() => _samples.Clear();
}
