using System.Threading;
using System.Windows.Forms;

namespace GHelperAutoMode;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\GHelperAutoMode.SingleInstance";

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    [STAThread]
    private static void Main(string[] args)
    {
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
            Application.Run(new TrayApplicationContext());
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
