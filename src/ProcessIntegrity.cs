using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GHelperAutoMode;

internal enum ProcessIntegrityLevel
{
    Unknown = 0,
    Untrusted = 1,
    Low = 2,
    Medium = 3,
    MediumPlus = 4,
    High = 5,
    System = 6,
    Protected = 7
}

internal static class ProcessIntegrity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private const int SecurityMandatoryUntrustedRid = 0x00000000;
    private const int SecurityMandatoryLowRid = 0x00001000;
    private const int SecurityMandatoryMediumRid = 0x00002000;
    private const int SecurityMandatoryMediumPlusRid = 0x00002100;
    private const int SecurityMandatoryHighRid = 0x00003000;
    private const int SecurityMandatorySystemRid = 0x00004000;
    private const int SecurityMandatoryProtectedProcessRid = 0x00005000;

    public static string DescribeCurrent()
    {
        var level = GetForProcessId(Environment.ProcessId);
        return level == ProcessIntegrityLevel.Unknown ? "unknown" : level.ToString();
    }

    public static string DescribeHighest(string processName)
    {
        var (found, level) = FindHighest(processName);
        if (!found)
            return "not running";
        return level == ProcessIntegrityLevel.Unknown ? "running; integrity unknown" : level.ToString();
    }

    public static bool IsElevationNeededFor(string processName, out string detail)
    {
        var current = GetForProcessId(Environment.ProcessId);
        var (found, target) = FindHighest(processName);
        detail = !found
            ? $"{processName} is not running"
            : $"AutoMode={current}; {processName}={target}";

        return found &&
               current != ProcessIntegrityLevel.Unknown &&
               target != ProcessIntegrityLevel.Unknown &&
               target > current;
    }

    private static (bool Found, ProcessIntegrityLevel Level) FindHighest(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return (false, ProcessIntegrityLevel.Unknown);
        }

        if (processes.Length == 0)
            return (false, ProcessIntegrityLevel.Unknown);

        var highest = ProcessIntegrityLevel.Unknown;
        foreach (var process in processes)
        {
            using (process)
            {
                ProcessIntegrityLevel level;
                try
                {
                    level = GetForProcessId(process.Id);
                }
                catch
                {
                    level = ProcessIntegrityLevel.Unknown;
                }

                if (level > highest)
                    highest = level;
            }
        }

        return (true, highest);
    }

    private static ProcessIntegrityLevel GetForProcessId(int processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (processHandle == IntPtr.Zero)
            return ProcessIntegrityLevel.Unknown;

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
                return ProcessIntegrityLevel.Unknown;

            try
            {
                _ = GetTokenInformation(tokenHandle, TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredLength);
                if (requiredLength <= 0)
                    return ProcessIntegrityLevel.Unknown;

                var buffer = Marshal.AllocHGlobal(requiredLength);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenIntegrityLevel, buffer, requiredLength, out _))
                        return ProcessIntegrityLevel.Unknown;

                    var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                    if (label.Label.Sid == IntPtr.Zero)
                        return ProcessIntegrityLevel.Unknown;

                    var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
                    if (countPointer == IntPtr.Zero)
                        return ProcessIntegrityLevel.Unknown;

                    var count = Marshal.ReadByte(countPointer);
                    if (count == 0)
                        return ProcessIntegrityLevel.Unknown;

                    var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                    if (ridPointer == IntPtr.Zero)
                        return ProcessIntegrityLevel.Unknown;

                    return ClassifyRid(Marshal.ReadInt32(ridPointer));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static ProcessIntegrityLevel ClassifyRid(int rid) => rid switch
    {
        >= SecurityMandatoryProtectedProcessRid => ProcessIntegrityLevel.Protected,
        >= SecurityMandatorySystemRid => ProcessIntegrityLevel.System,
        >= SecurityMandatoryHighRid => ProcessIntegrityLevel.High,
        >= SecurityMandatoryMediumPlusRid => ProcessIntegrityLevel.MediumPlus,
        >= SecurityMandatoryMediumRid => ProcessIntegrityLevel.Medium,
        >= SecurityMandatoryLowRid => ProcessIntegrityLevel.Low,
        >= SecurityMandatoryUntrustedRid => ProcessIntegrityLevel.Untrusted,
        _ => ProcessIntegrityLevel.Unknown
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
