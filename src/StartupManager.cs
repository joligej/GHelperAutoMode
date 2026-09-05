using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GHelperAutoMode;

internal sealed record StartupStatus(
    string TaskName,
    bool TaskSchedulerReadable,
    bool TaskExists,
    bool TaskEnabled,
    bool TaskMatchesExecutable,
    bool TaskUsesHighestAvailable,
    bool TaskUsesInteractiveToken,
    bool LegacyRunEntryPresent,
    bool LegacyRunEntryOwned,
    bool LegacyTaskPresent,
    bool LegacyTaskOwned,
    string Detail)
{
    public bool IsEnabled =>
        TaskSchedulerReadable &&
        TaskExists &&
        TaskEnabled &&
        TaskMatchesExecutable &&
        TaskUsesHighestAvailable &&
        TaskUsesInteractiveToken;

    public string Summary
    {
        get
        {
            if (!TaskSchedulerReadable)
                return "Task Scheduler status unavailable";

            if (IsEnabled)
                return $"enabled via Task Scheduler ({TaskName}; InteractiveToken; HighestAvailable)";

            if (TaskExists)
                return $"disabled or invalid Task Scheduler definition ({TaskName})";

            if (LegacyRunEntryPresent)
                return LegacyRunEntryOwned
                    ? "legacy HKCU Run entry present (migration required)"
                    : "unexpected HKCU Run entry present";

            return "disabled";
        }
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GHelperAutoMode";
    private const string LegacyTaskName = "LaunchGHelper";
    private const string StartupArgument = "--startup";
    private const string ManagementArgument = "--manage-startup";

    private const int TaskTriggerLogon = 9;
    private const int TaskTriggerSessionStateChange = 11;
    private const int TaskActionExecute = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskConsoleConnect = 1;

    public static string? LastMaintenanceWarning { get; private set; }

    public static string CurrentProcessIntegrity => ProcessIntegrity.DescribeCurrent();

    public static string GHelperProcessIntegrity => ProcessIntegrity.DescribeHighest("GHelper");

    public static bool IsEnabled() => GetStatus().IsEnabled;

    public static StartupStatus GetStatus()
    {
        var executable = GetExecutablePath(requirePublishedExecutable: false);
        var taskName = GetTaskName();
        var legacyRun = ReadLegacyRunEntry();
        var detailParts = new List<string>();

        var taskExists = false;
        var taskSchedulerReadable = false;
        var taskEnabled = false;
        var taskMatchesExecutable = false;
        var taskUsesHighest = false;
        var taskUsesInteractive = false;
        var legacyTaskPresent = false;
        var legacyTaskOwned = false;

        object? service = null;
        object? folder = null;
        try
        {
            service = CreateTaskService();
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");

            if (TryGetRegisteredTask(folder, taskName, out var registeredTask))
            {
                taskExists = true;
                try
                {
                    dynamic task = registeredTask!;
                    taskEnabled = Convert.ToBoolean(task.Enabled);
                    dynamic definition = task.Definition;
                    dynamic principal = definition.Principal;
                    taskUsesHighest = Convert.ToInt32(principal.RunLevel) == TaskRunLevelHighest;
                    taskUsesInteractive = Convert.ToInt32(principal.LogonType) == TaskLogonInteractiveToken;
                    taskMatchesExecutable = TaskHasExpectedAction(definition, executable);
                    ReleaseComObject(principal);
                    ReleaseComObject(definition);
                }
                finally
                {
                    ReleaseComObject(registeredTask);
                }
            }

            if (TryGetRegisteredTask(folder, LegacyTaskName, out var legacyTask))
            {
                legacyTaskPresent = true;
                try
                {
                    dynamic definition = ((dynamic)legacyTask!).Definition;
                    legacyTaskOwned = TaskHasOwnedAutoModeAction(definition);
                    ReleaseComObject(definition);
                }
                finally
                {
                    ReleaseComObject(legacyTask);
                }
            }

            taskSchedulerReadable = true;
        }
        catch (Exception ex)
        {
            detailParts.Add($"Task Scheduler query failed: {FriendlyException(ex)}");
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }

        if (taskExists && !taskMatchesExecutable)
            detailParts.Add("task action does not target the current executable");
        if (taskExists && !taskUsesHighest)
            detailParts.Add("task is not configured for HighestAvailable");
        if (taskExists && !taskUsesInteractive)
            detailParts.Add("task does not use an interactive user token");
        if (legacyRun.Present)
            detailParts.Add(legacyRun.Owned ? "legacy Run entry remains" : "Run entry has unexpected content");
        if (legacyTaskPresent)
            detailParts.Add(legacyTaskOwned ? "owned legacy LaunchGHelper task remains" : "unrelated LaunchGHelper task was preserved");

        return new StartupStatus(
            taskName,
            taskSchedulerReadable,
            taskExists,
            taskEnabled,
            taskMatchesExecutable,
            taskUsesHighest,
            taskUsesInteractive,
            legacyRun.Present,
            legacyRun.Owned,
            legacyTaskPresent,
            legacyTaskOwned,
            detailParts.Count == 0 ? "no startup residue detected" : string.Join("; ", detailParts));
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            ApplyStartupState(enabled);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            RunElevatedManagementHelper(enabled);
        }

        var status = GetStatus();
        if (enabled && !status.IsEnabled)
            throw new InvalidOperationException($"The startup task could not be verified. {status.Detail}");
        if (!enabled && (status.TaskExists || status.LegacyRunEntryOwned || status.LegacyTaskOwned))
            throw new InvalidOperationException($"Owned startup entries remain after disabling startup. {status.Detail}");
    }

    public static bool TryHandleManagementCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], ManagementArgument, StringComparison.OrdinalIgnoreCase))
            return false;

        if (args.Length != 2 ||
            (!string.Equals(args[1], "enable", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(args[1], "disable", StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = 2;
            return true;
        }

        try
        {
            var enabled = string.Equals(args[1], "enable", StringComparison.OrdinalIgnoreCase);
            ApplyStartupState(enabled);
            var status = GetStatus();
            if ((enabled && !status.IsEnabled) ||
                (!enabled && (status.TaskExists || status.LegacyRunEntryOwned || status.LegacyTaskOwned)))
            {
                exitCode = 3;
            }
        }
        catch
        {
            exitCode = 1;
        }

        return true;
    }

    public static void MigrateLegacyStartupIfNeeded()
    {
        LastMaintenanceWarning = null;
        try
        {
            var status = GetStatus();
            if (status.LegacyRunEntryOwned)
            {
                ApplyStartupState(enabled: true);
                return;
            }

            if (status.IsEnabled && (status.LegacyTaskOwned || status.LegacyRunEntryOwned))
            {
                ApplyStartupState(enabled: true);
                return;
            }

            if (!status.TaskExists && status.LegacyTaskOwned)
                DeleteOwnedLegacyTaskOnly();
        }
        catch (Exception ex)
        {
            LastMaintenanceWarning =
                "Windows-login startup could not be migrated or cleaned automatically. " + FriendlyException(ex);
        }
    }

    public static bool TryRelaunchToMatchGHelper(string[] args, out string? error)
    {
        error = null;
        if (!ProcessIntegrity.IsElevationNeededFor("GHelper", out _))
            return false;

        var executable = GetExecutablePath(requirePublishedExecutable: false);
        if (IsDotnetHost(executable))
        {
            error = "G-Helper runs at a higher integrity level, but automatic elevation is unavailable under dotnet.exe.";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in args)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Elevation was cancelled. AutoMode was not started because it could not safely control the elevated G-Helper process.";
            return false;
        }
        catch (Exception ex)
        {
            error = "Could not restart AutoMode at G-Helper's integrity level. " + FriendlyException(ex);
            return false;
        }
    }

    private static void ApplyStartupState(bool enabled)
    {
        var executable = GetExecutablePath(requirePublishedExecutable: enabled);
        var taskName = GetTaskName();
        object? service = null;
        object? folder = null;
        object? definition = null;
        object? registrationInfo = null;
        object? principal = null;
        object? settings = null;
        object? triggers = null;
        object? logonTrigger = null;
        object? connectTrigger = null;
        object? actions = null;
        object? action = null;

        try
        {
            service = CreateTaskService();
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");

            if (enabled)
            {
                definition = ((dynamic)service).NewTask(0);
                dynamic taskDefinition = definition;

                registrationInfo = taskDefinition.RegistrationInfo;
                ((dynamic)registrationInfo).Description =
                    "GHelperAutoMode Auto Start - matches G-Helper integrity without waking the display";
                ((dynamic)registrationInfo).Author = "GHelperAutoMode";

                var sid = GetCurrentSid();
                var account = WindowsIdentity.GetCurrent().Name;

                principal = taskDefinition.Principal;
                ((dynamic)principal).Id = "Author";
                ((dynamic)principal).UserId = sid;
                ((dynamic)principal).LogonType = TaskLogonInteractiveToken;
                ((dynamic)principal).RunLevel = TaskRunLevelHighest;

                settings = taskDefinition.Settings;
                ((dynamic)settings).Enabled = true;
                ((dynamic)settings).StartWhenAvailable = true;
                ((dynamic)settings).DisallowStartIfOnBatteries = false;
                ((dynamic)settings).StopIfGoingOnBatteries = false;
                ((dynamic)settings).ExecutionTimeLimit = "PT0S";
                ((dynamic)settings).MultipleInstances = TaskInstancesIgnoreNew;

                triggers = taskDefinition.Triggers;
                logonTrigger = ((dynamic)triggers).Create(TaskTriggerLogon);
                ((dynamic)logonTrigger).Id = "UserLogon";
                ((dynamic)logonTrigger).UserId = account;
                ((dynamic)logonTrigger).Delay = "PT3S";
                ((dynamic)logonTrigger).Enabled = true;

                connectTrigger = ((dynamic)triggers).Create(TaskTriggerSessionStateChange);
                ((dynamic)connectTrigger).Id = "ConsoleConnect";
                ((dynamic)connectTrigger).UserId = account;
                ((dynamic)connectTrigger).StateChange = TaskConsoleConnect;
                ((dynamic)connectTrigger).Delay = "PT3S";
                ((dynamic)connectTrigger).Enabled = true;

                actions = taskDefinition.Actions;
                action = ((dynamic)actions).Create(TaskActionExecute);
                ((dynamic)action).Path = executable;
                ((dynamic)action).Arguments = StartupArgument;
                ((dynamic)action).WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;

                var registeredTask = ((dynamic)folder).RegisterTaskDefinition(
                    taskName,
                    taskDefinition,
                    TaskCreateOrUpdate,
                    sid,
                    null,
                    TaskLogonInteractiveToken,
                    null);
                ReleaseComObject(registeredTask);
            }
            else
            {
                DeleteTaskIfPresent(folder, taskName);
            }

            RemoveOwnedLegacyRunEntry();
            DeleteOwnedLegacyTask(folder);
        }
        finally
        {
            ReleaseComObject(action);
            ReleaseComObject(actions);
            ReleaseComObject(connectTrigger);
            ReleaseComObject(logonTrigger);
            ReleaseComObject(triggers);
            ReleaseComObject(settings);
            ReleaseComObject(principal);
            ReleaseComObject(registrationInfo);
            ReleaseComObject(definition);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static void DeleteOwnedLegacyTaskOnly()
    {
        object? service = null;
        object? folder = null;
        try
        {
            service = CreateTaskService();
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            DeleteOwnedLegacyTask(folder);
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static void DeleteOwnedLegacyTask(object folder)
    {
        if (!TryGetRegisteredTask(folder, LegacyTaskName, out var task))
            return;

        try
        {
            dynamic definition = ((dynamic)task!).Definition;
            try
            {
                if (TaskHasOwnedAutoModeAction(definition))
                    ((dynamic)folder).DeleteTask(LegacyTaskName, 0);
            }
            finally
            {
                ReleaseComObject(definition);
            }
        }
        finally
        {
            ReleaseComObject(task);
        }
    }

    private static void DeleteTaskIfPresent(object folder, string taskName)
    {
        if (!TryGetRegisteredTask(folder, taskName, out var task))
            return;

        ReleaseComObject(task);
        ((dynamic)folder).DeleteTask(taskName, 0);
    }

    private static bool TryGetRegisteredTask(object folder, string taskName, out object? task)
    {
        try
        {
            task = ((dynamic)folder).GetTask(taskName);
            return task is not null;
        }
        catch (Exception ex) when (IsTaskNotFound(ex))
        {
            task = null;
            return false;
        }
    }

    private static bool TaskHasExpectedAction(object definition, string expectedExecutable)
    {
        dynamic actions = ((dynamic)definition).Actions;
        try
        {
            if (Convert.ToInt32(actions.Count) != 1)
                return false;

            dynamic action = actions.Item(1);
            try
            {
                return Convert.ToInt32(action.Type) == TaskActionExecute &&
                       PathsEqual(Convert.ToString(action.Path), expectedExecutable) &&
                       string.Equals(
                           Convert.ToString(action.Arguments)?.Trim(),
                           StartupArgument,
                           StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                ReleaseComObject(action);
            }
        }
        finally
        {
            ReleaseComObject(actions);
        }
    }

    private static bool TaskHasOwnedAutoModeAction(object definition)
    {
        dynamic actions = ((dynamic)definition).Actions;
        try
        {
            var count = Convert.ToInt32(actions.Count);
            for (var i = 1; i <= count; i++)
            {
                dynamic action = actions.Item(i);
                try
                {
                    if (Convert.ToInt32(action.Type) == TaskActionExecute &&
                        IsAutoModeExecutable(Convert.ToString(action.Path)))
                    {
                        return true;
                    }
                }
                finally
                {
                    ReleaseComObject(action);
                }
            }

            return false;
        }
        finally
        {
            ReleaseComObject(actions);
        }
    }

    private static (bool Present, bool Owned, string? Value) ReadLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return string.IsNullOrWhiteSpace(value)
                ? (false, false, null)
                : (true, IsOwnedStartupCommand(value), value);
        }
        catch
        {
            return (false, false, null);
        }
    }

    private static void RemoveOwnedLegacyRunEntry()
    {
        var entry = ReadLegacyRunEntry();
        if (!entry.Present)
            return;
        if (!entry.Owned)
            throw new InvalidOperationException(
                $"The {ValueName} HKCU Run value contains an unexpected command and was preserved: {entry.Value}");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user Run registry key.");
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool IsOwnedStartupCommand(string command)
    {
        var trimmed = command.Trim();
        string executable;
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1)
                return false;
            executable = trimmed[1..closingQuote];
        }
        else
        {
            var separator = trimmed.IndexOf(' ');
            executable = separator < 0 ? trimmed : trimmed[..separator];
        }

        return IsAutoModeExecutable(executable);
    }

    private static bool IsAutoModeExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(
            Path.GetFileName(path.Trim().Trim('"')),
            "GHelperAutoMode.exe",
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim().Trim('"')),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static object CreateTaskService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new InvalidOperationException("Windows Task Scheduler COM service is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create the Windows Task Scheduler service.");
    }

    private static string GetTaskName() => $"GHelperAutoMode_{GetCurrentSid()}";

    private static string GetCurrentSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Could not determine the current Windows user SID.");

    private static string GetExecutablePath(bool requirePublishedExecutable)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the executable path.");

        if (requirePublishedExecutable && IsDotnetHost(executable))
        {
            throw new InvalidOperationException(
                "Startup cannot be enabled while running through dotnet.exe. Build/publish the app and run GHelperAutoMode.exe first.");
        }

        return executable;
    }

    private static bool IsDotnetHost(string executable) =>
        string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static void RunElevatedManagementHelper(bool enabled)
    {
        var executable = GetExecutablePath(requirePublishedExecutable: true);
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(ManagementArgument);
            startInfo.ArgumentList.Add(enabled ? "enable" : "disable");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the elevated startup helper.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The elevated startup helper failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator approval was cancelled; startup was not changed.", ex);
        }
    }

    private static bool IsAccessDenied(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
                return true;
            if (current is Win32Exception win32 && win32.NativeErrorCode == 5)
                return true;
            if (current is COMException com && (uint)com.HResult == 0x80070005)
                return true;
        }

        return false;
    }

    private static bool IsTaskNotFound(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if ((uint)current.HResult is 0x80070002 or 0x8004130F)
                return true;
        }

        return false;
    }

    private static string FriendlyException(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
            current = current.InnerException;
        return $"{current.GetType().Name}: {current.Message}";
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
