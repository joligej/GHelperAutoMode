using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace GHelperAutoMode.Tests;

internal static class Program
{
    private const string WorkerMode = "GHELPER_TEST_WORKER_MODE";
    private const string ChildExitCode = "GHELPER_TEST_CHILD_EXIT";
    private const string ChildCounter = "GHELPER_TEST_CHILD_COUNTER";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--nvml-worker"))
            return RunWorker();
        if (args.Contains("--tray"))
            return RunSupervisorChild();

        var root = Path.Combine(Path.GetTempPath(), "GHelperAutoMode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestConfiguration(Path.Combine(root, "config"));
            TestWindowsResources(Path.Combine(root, "ui"));
            TestSupervisor(root);
            RunUiAsync(() => RunAsyncTests(args.Contains("--gpu")));
            Console.WriteLine("All regression checks passed.");
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerMode, null);
            Environment.SetEnvironmentVariable(ChildExitCode, null);
            Environment.SetEnvironmentVariable(ChildCounter, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestConfiguration(string directory)
    {
        var service = new ConfigService(directory);
        var before = File.ReadAllBytes(service.ConfigPath);
        var requested = service.CreateWorkingCopy();
        requested.AutomationEnabled = !service.Current.AutomationEnabled;
        using (var locked = new FileStream(service.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ThrowsFileAccessError(() => service.ReplaceAndSave(requested));
            Require(service.Current.AutomationEnabled != requested.AutomationEnabled, "Failed save replaced the live configuration.");
        }
        Require(before.SequenceEqual(File.ReadAllBytes(service.ConfigPath)), "Failed save changed the configuration file.");

        using (var locked = new FileStream(service.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
            ThrowsFileAccessError(() => service.Load());
        Require(before.SequenceEqual(File.ReadAllBytes(service.ConfigPath)), "Read failure overwrote the configuration.");
        Require(!Directory.EnumerateFiles(directory, "config.invalid.*").Any(), "Read failure was treated as invalid JSON.");

        File.WriteAllText(service.ConfigPath, "null");
        service.Load();
        Require(Directory.EnumerateFiles(directory, "config.invalid.*").Count() == 1, "Invalid JSON was not backed up.");
        Require(service.Current.SchemaVersion == 8, "Defaults did not restore the current schema.");
        Require(!File.Exists(service.ConfigPath + ".tmp"), "Temporary configuration file was left behind.");
        var legacy = service.CreateWorkingCopy();
        legacy.SchemaVersion = 7;
        legacy.AutomationEnabled = false;
        legacy.Thresholds.BalancedCpuPercent = 26;
        var previousJson = JsonSerializer.Serialize(legacy);
        File.WriteAllText(service.ConfigPath, previousJson);
        service.Load();
        var migrationBackup = Directory.EnumerateFiles(directory, "config.pre-v8.*.json").Single();
        Require(File.ReadAllText(migrationBackup) == previousJson, "Migration did not preserve the original settings.");
        Require(!service.Current.AutomationEnabled && service.Current.Thresholds.BalancedCpuPercent == 26,
            "Migration changed existing user choices.");
        Console.WriteLine("PASS configuration saves, read failures, and invalid JSON recovery.");
    }

    private static void TestWindowsResources(string directory)
    {
        var service = new ConfigService(directory);
        service.Current.AutomationEnabled = false;
        service.Current.KeyboardLighting.Mode = KeyboardLightingMode.Unmanaged;
        service.Save();
        for (var i = 0; i < 15; i++)
        {
            using var window = new PowerNotificationWindow();
            window.Dispose();
            window.Dispose();
            using var form = new SettingsForm(service);
            form.CreateControl();
            form.Dispose();
            using var tray = new TrayApplicationContext(service);
            tray.Dispose();
            tray.Dispose();
        }
        Require(service.Current.KeyboardLighting.Mode == KeyboardLightingMode.Unmanaged, "Resource tests changed lighting ownership.");
        Console.WriteLine("PASS repeated power-window, settings, and tray disposal.");
    }

    private static void TestSupervisor(string directory)
    {
        var counter = Path.Combine(directory, "child-count.txt");
        var recovery = Path.Combine(directory, "last-recovery.txt");
        Environment.SetEnvironmentVariable(ChildCounter, counter);
        Environment.SetEnvironmentVariable(ChildExitCode, "23");
        File.WriteAllText(counter, "0");
        Require(StartupSupervisor.Run([], TimeSpan.Zero, recovery) == 23, "Supervisor lost the failure exit code.");
        Require(File.ReadAllText(counter) == "4", "Supervisor did not stop after three retries.");
        Require(File.ReadAllText(recovery).Contains("3/3", StringComparison.Ordinal), "Recovery status did not record the retry limit.");

        Environment.SetEnvironmentVariable(ChildExitCode, "0");
        File.WriteAllText(counter, "0");
        Require(StartupSupervisor.Run([], TimeSpan.Zero, recovery) == 0, "Supervisor rejected a normal exit.");
        Require(File.ReadAllText(counter) == "1", "Supervisor restarted a normal tray exit.");
        Console.WriteLine("PASS supervisor retry limits and normal exit.");
    }

    private static int RunSupervisorChild()
    {
        var path = Environment.GetEnvironmentVariable(ChildCounter)!;
        var count = int.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture) + 1;
        File.WriteAllText(path, count.ToString(CultureInfo.InvariantCulture));
        return int.Parse(Environment.GetEnvironmentVariable(ChildExitCode)!, CultureInfo.InvariantCulture);
    }

    private static int RunWorker()
    {
        switch (Environment.GetEnvironmentVariable(WorkerMode))
        {
            case "exit":
                return 23;
            case "hang":
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return 24;
            case "fake":
                using (var input = new StreamReader(Console.OpenStandardInput()))
                using (var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true })
                {
                    while (input.ReadLine() is not null)
                        output.WriteLine(JsonSerializer.Serialize(new GpuTelemetry(45, 55, TelemetryQuality.Fresh, "NVML")));
                }
                return 0;
            default:
                return NvidiaGpuMonitor.RunNvmlWorker();
        }
    }

    private static async Task RunAsyncTests(bool realGpu)
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var settings = new TelemetryConfig { NvidiaSmiTimeoutSeconds = 1 };
        Environment.SetEnvironmentVariable(WorkerMode, "fake");
        using var monitor = new NvidiaGpuMonitor();
        var sample = await monitor.SampleAsync(10, settings).ConfigureAwait(true);
        Require(Environment.CurrentManagedThreadId == uiThread, "Telemetry continuation left the UI thread.");
        Require(sample.UtilizationPercent == 45 && sample.TemperatureC == 55, "Worker sample was not returned.");
        for (var i = 0; i < 10; i++)
        {
            var pending = monitor.SampleAsync(10, settings);
            monitor.ResetAfterResume();
            var stale = await pending.ConfigureAwait(true);
            Require(stale.Quality == TelemetryQuality.Unavailable, "A stale sample survived resume.");
        }
        var fresh = await monitor.SampleAsync(10, settings).ConfigureAwait(true);
        Require(fresh.UtilizationPercent == 45, "Worker did not recover after resume.");

        foreach (var mode in new[] { "exit", "hang" })
        {
            Environment.SetEnvironmentVariable(WorkerMode, mode);
            using var failing = new NvidiaGpuMonitor();
            var watch = Stopwatch.StartNew();
            var fallback = await failing.SampleAsync(0, settings).ConfigureAwait(true);
            Require(failing.PreferredProvider != "NVML", "Worker failure did not disable the native provider.");
            Require(fallback.UtilizationPercent.HasValue || fallback.Quality == TelemetryQuality.Unavailable,
                "Missing fallback telemetry was treated as a valid measurement.");
            Require(watch.Elapsed < TimeSpan.FromSeconds(6), "Worker failure exceeded its timeout.");
            failing.ResetAfterResume();
            var afterReset = await failing.SampleAsync(0, settings).ConfigureAwait(true);
            Require(failing.PreferredProvider != "NVML", "Resume retried a failed native worker in the same session.");
            Require(afterReset.UtilizationPercent.HasValue || afterReset.Quality == TelemetryQuality.Unavailable,
                "Missing telemetry was treated as valid after resume.");
        }
        Console.WriteLine("PASS telemetry worker samples, resume races, exit, and timeout.");

        if (!realGpu)
            return;
        Environment.SetEnvironmentVariable(WorkerMode, null);
        using var hardware = new NvidiaGpuMonitor();
        for (var i = 0; i < 40; i++)
        {
            var actual = await hardware.SampleAsync(10, new TelemetryConfig()).ConfigureAwait(true);
            Require(actual.UtilizationPercent.HasValue && actual.TemperatureC.HasValue, "NVIDIA hardware sample is missing.");
        }
        var field = typeof(NvidiaGpuMonitor).GetField("_nvml", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var worker = field.GetValue(hardware);
        Require(worker is not null, "Native worker was unavailable before the termination test.");
        var processField = typeof(NvmlWorkerClient).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var process = (Process)processField.GetValue(worker)!;
        process.Kill();
        await process.WaitForExitAsync().ConfigureAwait(true);
        var safe = await hardware.SampleAsync(10, new TelemetryConfig()).ConfigureAwait(true);
        Require(safe.Source == "nvidia-smi" && safe.UtilizationPercent.HasValue, "Native worker termination did not return smi telemetry.");
        Console.WriteLine("PASS 40 hardware samples and native-worker termination.");
    }

    private static void ThrowsFileAccessError(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected a file access error.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The test message-loop boundary captures any async failure and rethrows it after the loop exits.")]
    private static void RunUiAsync(Func<Task> action)
    {
        Exception? failure = null;
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        dispatcher.BeginInvoke(async () =>
        {
            try { await action().ConfigureAwait(true); }
            catch (Exception ex) { failure = ex; }
            finally { Application.ExitThread(); }
        });
        Application.Run();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
