using System.Globalization;

namespace GHelperAutoMode;

internal sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly ConfigService _configService;

    public string LogPath => Path.Combine(_configService.LogDirectory, "automode.log");

    public FileLogger(ConfigService configService)
    {
        _configService = configService;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        if (!_configService.Current.Logging.Enabled)
            return;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_configService.LogDirectory);
                RotateIfNeeded();
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                File.AppendAllText(
                    LogPath,
                    $"{timestamp} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break automation.
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(LogPath))
            return;

        var maxBytes = (long)_configService.Current.Logging.MaxFileSizeMB * 1024 * 1024;
        if (new FileInfo(LogPath).Length < maxBytes)
            return;

        var keep = _configService.Current.Logging.KeepFiles;
        if (keep <= 0)
        {
            File.Delete(LogPath);
            return;
        }

        for (var i = keep; i >= 1; i--)
        {
            var source = i == 1 ? LogPath : $"{LogPath}.{i - 1}";
            var destination = $"{LogPath}.{i}";

            if (!File.Exists(source))
                continue;

            if (File.Exists(destination))
                File.Delete(destination);

            File.Move(source, destination);
        }
    }
}
