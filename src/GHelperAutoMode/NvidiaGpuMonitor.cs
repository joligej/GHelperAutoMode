using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GHelperAutoMode;

internal sealed class NvidiaGpuMonitor : IDisposable
{
    private NvmlApi? _nvml;
    private readonly string? _nvidiaSmiPath;
    private long? _lastNvmlAttemptAt;
    private GpuTelemetry? _lastGood;
    private long? _lastGoodAt;
    private GpuTelemetry? _lastSmiSample;
    private long? _lastSmiSampleAt;
    private int _consecutiveNvmlFailures;
    private int _resetGeneration;
    private bool _disposed;

    public bool IsAvailable => _nvml is not null || _nvidiaSmiPath is not null;
    public string PreferredProvider => _nvml is not null ? "NVML" : _nvidiaSmiPath is not null ? "nvidia-smi" : "unavailable";

    public NvidiaGpuMonitor()
    {
        _nvml = NvmlApi.TryCreate();
        _lastNvmlAttemptAt = MonotonicClock.Now;
        _nvidiaSmiPath = LocateNvidiaSmi();
    }

    public async Task<GpuTelemetry> SampleAsync(int graceSeconds)
    {
        if (_disposed)
            return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "disposed", "NVIDIA telemetry monitor is disposed.");

        var generation = _resetGeneration;
        var attemptNow = MonotonicClock.Now;
        TryRecoverNvml(attemptNow);

        var sample = SampleNvml(attemptNow);
        if (sample is null || !sample.UtilizationPercent.HasValue)
        {
            var fallback = await SampleNvidiaSmiAsync(generation);
            if (fallback is not null && fallback.UtilizationPercent.HasValue)
                sample = fallback;
            else if (sample is null)
                sample = fallback;
        }

        // Resume may be processed by the WinForms message loop while nvidia-smi is still
        // awaited. The engine rejects the old tick by epoch, and this generation check also
        // prevents that old continuation from repopulating provider caches after reset.
        if (_disposed || generation != _resetGeneration)
        {
            return new GpuTelemetry(
                null,
                null,
                TelemetryQuality.Unavailable,
                "discarded",
                "NVIDIA telemetry sample crossed a resume/dispose boundary and was discarded.");
        }

        // Timestamp successful telemetry at the point the provider actually completed.
        // This matters for nvidia-smi, which can take noticeable time to return.
        var completedAt = MonotonicClock.Now;
        if (sample is not null && sample.UtilizationPercent.HasValue)
        {
            if (sample.Quality == TelemetryQuality.Fresh)
            {
                _lastGood = sample;
                _lastGoodAt = completedAt;
            }

            return sample;
        }

        if (_lastGood is not null
            && _lastGoodAt.HasValue
            && MonotonicClock.Elapsed(_lastGoodAt.Value, completedAt) <= TimeSpan.FromSeconds(Math.Max(0, graceSeconds)))
        {
            return _lastGood with
            {
                Quality = TelemetryQuality.GraceCache,
                Source = _lastGood.Source + " grace cache",
                Error = sample?.Error ?? "Using last valid NVIDIA telemetry sample during grace period."
            };
        }

        return sample ?? new GpuTelemetry(
            null, null, TelemetryQuality.Unavailable, "unavailable", "No NVIDIA telemetry provider is available.");
    }

    private void TryRecoverNvml(long now)
    {
        if (_nvml is not null)
            return;

        if (_lastNvmlAttemptAt.HasValue
            && MonotonicClock.Elapsed(_lastNvmlAttemptAt.Value, now) < TimeSpan.FromSeconds(60))
            return;

        _lastNvmlAttemptAt = now;
        _nvml = NvmlApi.TryCreate();
    }

    private GpuTelemetry? SampleNvml(long now)
    {
        if (_nvml is null)
            return null;

        try
        {
            var sample = _nvml.Sample();
            if (sample.UtilizationPercent.HasValue)
            {
                _consecutiveNvmlFailures = 0;
                return sample;
            }

            RegisterNvmlFailure(now);
            return sample;
        }
        catch (Exception ex)
        {
            RegisterNvmlFailure(now);
            return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "NVML", ex.Message);
        }
    }

    private void RegisterNvmlFailure(long now)
    {
        _consecutiveNvmlFailures++;
        if (_consecutiveNvmlFailures < 3 || _nvml is null)
            return;

        // Driver resets / resume can invalidate existing NVML handles. Drop the stale
        // instance after repeated failures and allow a clean reinitialization later.
        try { _nvml.Dispose(); } catch { /* best effort */ }
        _nvml = null;
        _lastNvmlAttemptAt = now;
        _consecutiveNvmlFailures = 0;
    }

    public void ResetAfterResume()
    {
        _resetGeneration++;
        try { _nvml?.Dispose(); } catch { /* best effort */ }
        _nvml = null;
        _lastNvmlAttemptAt = null;
        _consecutiveNvmlFailures = 0;
        _lastGood = null;
        _lastGoodAt = null;
        _lastSmiSample = null;
        _lastSmiSampleAt = null;
    }

    private async Task<GpuTelemetry?> SampleNvidiaSmiAsync(int generation)
    {
        if (_nvidiaSmiPath is null)
            return null;

        var now = MonotonicClock.Now;

        // nvidia-smi is intentionally only a fallback. Avoid spawning a process every
        // automation tick; a 4-second cadence still gives the fast-GPU path a fresh
        // confirmation sample while keeping fallback overhead negligible.
        if (_lastSmiSample is not null
            && _lastSmiSampleAt.HasValue
            && MonotonicClock.Elapsed(_lastSmiSampleAt.Value, now) < TimeSpan.FromSeconds(4))
        {
            return _lastSmiSample with
            {
                Quality = TelemetryQuality.IntervalCache,
                Source = "nvidia-smi interval cache",
                Error = null
            };
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _nvidiaSmiPath,
                    Arguments = "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };

            if (!process.Start())
                return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "nvidia-smi", "Failed to start nvidia-smi.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { await process.WaitForExitAsync(); } catch { /* best effort */ }
                try { await Task.WhenAll(outputTask, errorTask); } catch { /* best effort */ }
                return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "nvidia-smi", "nvidia-smi timed out.");
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "nvidia-smi", string.IsNullOrWhiteSpace(error) ? $"Exit code {process.ExitCode}." : error.Trim());

            var rows = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseSmiRow)
                .Where(row => row.HasValue)
                .Select(row => row!.Value)
                .ToArray();

            if (rows.Length == 0)
                return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "nvidia-smi", "No parsable GPU telemetry returned.");

            var validTemperatures = rows
                .Select(row => row.Temperature)
                .Where(value => !double.IsNaN(value))
                .ToArray();

            var sample = new GpuTelemetry(
                Math.Clamp(rows.Max(row => row.Utilization), 0, 100),
                validTemperatures.Length > 0 ? validTemperatures.Max() : (double?)null,
                TelemetryQuality.Fresh,
                "nvidia-smi");

            if (!_disposed && generation == _resetGeneration)
            {
                _lastSmiSample = sample;
                _lastSmiSampleAt = MonotonicClock.Now;
            }
            return sample;
        }
        catch (Exception ex)
        {
            return new GpuTelemetry(null, null, TelemetryQuality.Unavailable, "nvidia-smi", ex.Message);
        }
    }

    private static (double Utilization, double Temperature)? ParseSmiRow(string row)
    {
        var parts = row.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return null;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var utilization))
            return null;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
            temperature = double.NaN;

        return (utilization, temperature);
    }

    private static string? LocateNvidiaSmi()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvidia-smi.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(
            path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim().Trim('"'), "nvidia-smi.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _resetGeneration++;
        _nvml?.Dispose();
        _nvml = null;
        GC.SuppressFinalize(this);
    }

    private sealed class NvmlApi : IDisposable
    {
        private const int NvmlSuccess = 0;
        private const uint NvmlTemperatureGpu = 0;

        private readonly IntPtr _library;
        private readonly NvmlShutdownDelegate _shutdown;
        private readonly NvmlDeviceGetUtilizationRatesDelegate _getUtilization;
        private readonly NvmlDeviceGetTemperatureDelegate _getTemperature;
        private readonly IntPtr[] _devices;
        private bool _disposed;

        private NvmlApi(
            IntPtr library,
            NvmlShutdownDelegate shutdown,
            NvmlDeviceGetUtilizationRatesDelegate getUtilization,
            NvmlDeviceGetTemperatureDelegate getTemperature,
            IntPtr[] devices)
        {
            _library = library;
            _shutdown = shutdown;
            _getUtilization = getUtilization;
            _getTemperature = getTemperature;
            _devices = devices;
        }

        public static NvmlApi? TryCreate()
        {
            IntPtr library = IntPtr.Zero;
            NvmlShutdownDelegate? shutdown = null;

            try
            {
                foreach (var candidate in NvmlCandidates())
                {
                    try
                    {
                        if (File.Exists(candidate))
                        {
                            library = NativeLibrary.Load(candidate);
                            break;
                        }
                    }
                    catch
                    {
                        // Try next candidate.
                    }
                }

                if (library == IntPtr.Zero)
                    return null;

                var init = GetDelegateAny<NvmlInitV2Delegate>(library, "nvmlInit_v2", "nvmlInit");
                shutdown = GetDelegate<NvmlShutdownDelegate>(library, "nvmlShutdown");
                var getCount = GetDelegateAny<NvmlDeviceGetCountV2Delegate>(library, "nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
                var getHandle = GetDelegateAny<NvmlDeviceGetHandleByIndexV2Delegate>(library, "nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
                var getUtilization = GetDelegate<NvmlDeviceGetUtilizationRatesDelegate>(library, "nvmlDeviceGetUtilizationRates");
                var getTemperature = GetDelegate<NvmlDeviceGetTemperatureDelegate>(library, "nvmlDeviceGetTemperature");

                if (init() != NvmlSuccess)
                    throw new InvalidOperationException("nvmlInit_v2 failed.");

                uint count = 0;
                if (getCount(ref count) != NvmlSuccess || count == 0)
                    throw new InvalidOperationException("NVML returned no NVIDIA devices.");

                var devices = new List<IntPtr>();
                for (uint i = 0; i < count; i++)
                {
                    var handle = IntPtr.Zero;
                    if (getHandle(i, ref handle) == NvmlSuccess && handle != IntPtr.Zero)
                        devices.Add(handle);
                }

                if (devices.Count == 0)
                    throw new InvalidOperationException("NVML could not obtain any device handles.");

                return new NvmlApi(library, shutdown, getUtilization, getTemperature, devices.ToArray());
            }
            catch
            {
                try { shutdown?.Invoke(); } catch { /* best effort */ }
                if (library != IntPtr.Zero)
                {
                    try { NativeLibrary.Free(library); } catch { /* best effort */ }
                }
                return null;
            }
        }

        public GpuTelemetry Sample()
        {
            double? maxUtilization = null;
            double? maxTemperature = null;

            foreach (var device in _devices)
            {
                var utilization = new NvmlUtilization();
                if (_getUtilization(device, ref utilization) == NvmlSuccess)
                {
                    var value = Math.Clamp((double)utilization.Gpu, 0, 100);
                    maxUtilization = !maxUtilization.HasValue ? value : Math.Max(maxUtilization.Value, value);
                }

                uint temperature = 0;
                if (_getTemperature(device, NvmlTemperatureGpu, ref temperature) == NvmlSuccess)
                {
                    var value = (double)temperature;
                    maxTemperature = !maxTemperature.HasValue ? value : Math.Max(maxTemperature.Value, value);
                }
            }

            if (!maxUtilization.HasValue)
                return new GpuTelemetry(null, maxTemperature, TelemetryQuality.Unavailable, "NVML", "NVML did not return GPU utilization.");

            return new GpuTelemetry(maxUtilization, maxTemperature, TelemetryQuality.Fresh, "NVML");
        }

        private static IEnumerable<string> NvmlCandidates()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvml.dll");
            yield return Path.Combine(
                Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files",
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll");
        }

        private static T GetDelegate<T>(IntPtr library, string exportName) where T : Delegate
        {
            var export = NativeLibrary.GetExport(library, exportName);
            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        private static T GetDelegateAny<T>(IntPtr library, params string[] exportNames) where T : Delegate
        {
            foreach (var exportName in exportNames)
            {
                if (NativeLibrary.TryGetExport(library, exportName, out var export))
                    return Marshal.GetDelegateForFunctionPointer<T>(export);
            }

            throw new EntryPointNotFoundException($"None of the expected NVML exports were found: {string.Join(", ", exportNames)}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            try { _shutdown(); } catch { /* best effort */ }
            try { NativeLibrary.Free(_library); } catch { /* best effort */ }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlInitV2Delegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetCountV2Delegate(ref uint deviceCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetHandleByIndexV2Delegate(uint index, ref IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetUtilizationRatesDelegate(IntPtr device, ref NvmlUtilization utilization);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetTemperatureDelegate(IntPtr device, uint sensorType, ref uint temperature);
    }
}
