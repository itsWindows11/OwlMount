using OwlCore.Storage;
using OwlCore.Storage.System.IO;

namespace OwlMount.WinUI.Services;

public sealed class LocalLogService
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SystemFile _logFile;
    private readonly string _logPath;

    public LocalLogService()
    {
        string rootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwlMount",
            "Logs");

        if (!Directory.Exists(rootPath))
            Directory.CreateDirectory(rootPath);

        Directory.CreateDirectory(rootPath);
        _logPath = Path.Combine(rootPath, $"owlmount.log");

        if (!File.Exists(_logPath))
            File.WriteAllBytes(_logPath, []);

        _logFile = new SystemFile(_logPath);
    }

    public string LogPath => _logPath;

    public Task InfoAsync(string message) => WriteAsync("INFO", message);

    public Task WarnAsync(string message) => WriteAsync("WARN", message);

    public Task ErrorAsync(string message, Exception? exception = null)
    {
        string detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        return WriteAsync("ERROR", detail);
    }

    private async Task WriteAsync(string level, string message)
    {
        await _gate.WaitAsync();

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

        using Stream stream = await _logFile.OpenWriteAsync().ConfigureAwait(false);
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(stream, leaveOpen: false);
        await writer.WriteAsync(line).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        _gate.Release();
    }
}
