using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GHelperAutoMode.Setup;

internal static partial class Program
{
    private const string InstallerResource = "GHelperAutoMode.Installer.msi";
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const uint MbError = 0x00000010;

    public static int Main(string[] args)
    {
        var quiet = args.Any(IsQuietArgument);
        var temporaryMsi = Path.Combine(
            Path.GetTempPath(),
            $"GHelperAutoMode-{Guid.NewGuid():N}.msi");

        try
        {
            ExtractInstaller(temporaryMsi);

            var elevated = IsProcessElevated();
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                UseShellExecute = false,
                CreateNoWindow = quiet
            };

            startInfo.ArgumentList.Add("/i");
            startInfo.ArgumentList.Add(temporaryMsi);
            startInfo.ArgumentList.Add(elevated ? "ALLUSERS=1" : "ALLUSERS=2");
            if (!elevated)
                startInfo.ArgumentList.Add("MSIINSTALLPERUSER=1");
            startInfo.ArgumentList.Add(quiet ? "/qn" : "/passive");
            startInfo.ArgumentList.Add("/norestart");

            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Installer could not be started.");
            installer.WaitForExit();

            if (installer.ExitCode != 0 && installer.ExitCode != 3010 && !quiet)
                ShowError($"Setup did not complete. Windows Installer returned error {installer.ExitCode}.");

            return installer.ExitCode;
        }
        catch (Exception ex)
        {
            if (!quiet)
                ShowError($"Setup could not start. {ex.Message}");
            return ex is Win32Exception win32 && win32.NativeErrorCode != 0
                ? win32.NativeErrorCode
                : 1;
        }
        finally
        {
            try { File.Delete(temporaryMsi); }
            catch { /* Windows Installer may still be releasing its source handle. */ }
        }
    }

    private static bool IsQuietArgument(string value) =>
        value.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("/qn", StringComparison.OrdinalIgnoreCase);

    private static void ExtractInstaller(string destination)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(InstallerResource)
            ?? throw new InvalidOperationException("The embedded Windows Installer package is missing.");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static bool IsProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var elevation = new TokenElevationValue();
            var size = Marshal.SizeOf<TokenElevationValue>();
            if (!GetTokenInformation(token, TokenElevation, ref elevation, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static void ShowError(string message) =>
        MessageBox(IntPtr.Zero, message, "G-Helper Auto Mode Setup", MbError);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationValue
    {
        public int TokenIsElevated;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        ref TokenElevationValue tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr window, string text, string caption, uint type);
}
