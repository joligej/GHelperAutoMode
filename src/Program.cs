using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\GHelperAutoMode.SingleInstance";
    private const string ExitEventPrefix = @"Global\GHelperAutoMode.ExitRequested.";

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    [STAThread]
    private static void Main(string[] args)
    {
        if (TryHandleUninstallPreparation(args, out var uninstallExitCode))
        {
            Environment.ExitCode = uninstallExitCode;
            return;
        }

        if (StartupManager.TryHandleManagementCommand(args, out var managementExitCode))
        {
            Environment.ExitCode = managementExitCode;
            return;
        }

        var launchedForStartup = args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

        if (IsAnotherInstanceRunning())
        {
            if (!launchedForStartup)
            {
                MessageBox.Show(
                    "GHelperAutoMode is already running in the system tray.",
                    "GHelperAutoMode",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        if (StartupManager.TryRelaunchToMatchGHelper(args, out var elevationError))
            return;

        if (!string.IsNullOrWhiteSpace(elevationError))
        {
            if (!launchedForStartup)
            {
                MessageBox.Show(
                    elevationError,
                    "GHelperAutoMode integrity check",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var createdNew);
        if (!createdNew)
        {
            if (!launchedForStartup)
            {
                MessageBox.Show(
                    "GHelperAutoMode is already running in the system tray.",
                    "GHelperAutoMode",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        StartupManager.MigrateLegacyStartupIfNeeded();

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var context = new TrayApplicationContext();
            using var exitEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ExitEventPrefix + Environment.ProcessId);
            var exitRegistration = ThreadPool.RegisterWaitForSingleObject(
                exitEvent,
                static (state, _) => ((TrayApplicationContext)state!).RequestExit(),
                context,
                Timeout.Infinite,
                executeOnlyOnce: true);
            try
            {
                Application.Run(context);
            }
            finally
            {
                exitRegistration.Unregister(waitObject: null);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"GHelperAutoMode hit an unexpected error and needs to close.\n\n{ex.GetType().Name}: {ex.Message}",
                "GHelperAutoMode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* ownership already released/lost */ }
        }
    }

    private static bool TryHandleUninstallPreparation(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 1 || !string.Equals(args[0], "--prepare-uninstall", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            StartupManager.SetEnabled(false);
        }
        catch
        {
            // MSI owns the installed files, so a stale startup registration must not make
            // the whole uninstall fail. The command still asks any running instance to exit.
            exitCode = 1;
        }

        var instances = FindInstalledInstances();
        foreach (var instance in instances)
        {
            try
            {
                using var exitEvent = EventWaitHandle.OpenExisting(ExitEventPrefix + instance.Id);
                exitEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The process may still be starting, or it may be an older build.
            }
            catch (UnauthorizedAccessException)
            {
                // The window message below handles a higher-integrity instance in this session.
            }
        }

        _ = PowerNotificationWindow.TryRequestExit();

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline
               && (HasRunningInstance(instances) || IsAnotherInstanceRunning()))
        {
            Thread.Sleep(50);
        }

        foreach (var instance in instances)
        {
            try
            {
                if (instance.HasExited)
                    continue;

                instance.Kill(entireProcessTree: false);
                if (!instance.WaitForExit(2000))
                    exitCode = 1;
            }
            catch
            {
                exitCode = 1;
            }
        }

        if (IsAnotherInstanceRunning())
            exitCode = 1;

        foreach (var instance in instances)
            instance.Dispose();

        return true;
    }

    private static List<Process> FindInstalledInstances()
    {
        var instances = new List<Process>();
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return instances;

        executablePath = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcessesByName("GHelperAutoMode"))
        {
            if (process.Id == Environment.ProcessId)
            {
                process.Dispose();
                continue;
            }

            try
            {
                var candidatePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(candidatePath)
                    && string.Equals(
                        Path.GetFullPath(candidatePath),
                        executablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    instances.Add(process);
                    continue;
                }
            }
            catch
            {
                // A lower-integrity helper may not be allowed to inspect the target path.
            }

            process.Dispose();
        }

        return instances;
    }

    private static bool HasRunningInstance(List<Process> instances)
    {
        foreach (var instance in instances)
        {
            try
            {
                if (!instance.HasExited)
                    return true;
            }
            catch
            {
                // A process that disappeared between checks is no longer a blocker.
            }
        }

        return false;
    }

    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            using var existing = Mutex.OpenExisting(SingleInstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A higher-integrity instance can own a mutex the current token cannot open.
            return true;
        }
    }
}
