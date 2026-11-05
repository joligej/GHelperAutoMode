using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace GHelperAutoMode;

internal static class StartupSupervisor
{
    public static int Run(string[] args, TimeSpan retryDelay, string? recoveryLogPath = null)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The tray executable could not be resolved.");
        var exitCode = 1;
        for (var attempt = 0; attempt <= 3; attempt++)
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                    startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
                foreach (var argument in args)
                    startInfo.ArgumentList.Add(argument);
                startInfo.ArgumentList.Add("--tray");
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The tray process could not be started.");
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                exitCode = 1;
            }

            if (exitCode == 0)
                return 0;

            SaveRecoveryStatus(exitCode, attempt, recoveryLogPath);
            if (attempt < 3)
                Thread.Sleep(retryDelay);
        }
        return exitCode;
    }

    private static void SaveRecoveryStatus(int exitCode, int attempt, string? recoveryLogPath)
    {
        try
        {
            recoveryLogPath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GHelperAutoMode", "logs", "last-recovery.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryLogPath)!);
            var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            File.WriteAllText(recoveryLogPath,
                $"{timestamp} Tray exit code 0x{exitCode:X8}. Restart attempts used: {attempt}/3.{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recovery must still work when the diagnostic file cannot be written.
        }
    }
}
