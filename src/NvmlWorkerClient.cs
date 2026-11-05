using System.Diagnostics;
using System.Text.Json;

namespace GHelperAutoMode;

internal sealed class NvmlWorkerClient : IDisposable
{
    private Process? _process;
    private bool _disposed;

    public async Task<GpuTelemetry> SampleAsync(int timeoutSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is null)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The telemetry worker executable could not be resolved.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("--nvml-worker");
            _process = Process.Start(startInfo)
                ?? throw new IOException("The NVIDIA telemetry worker could not be started.");
        }

        var process = _process;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await process.StandardInput.WriteLineAsync("sample".AsMemory(), timeout.Token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (line is null)
            throw new IOException("The NVIDIA telemetry worker exited. Using nvidia-smi for this session.");
        return JsonSerializer.Deserialize<GpuTelemetry>(line)
            ?? throw new IOException("The NVIDIA telemetry worker returned an empty sample.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var process = _process;
        _process = null;
        if (process is null)
            return;
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
                process.Kill();
            process.WaitForExit(1000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Resume/disposal can race an already completed worker exit.
        }
        finally
        {
            process.Dispose();
        }
    }
}
