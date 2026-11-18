namespace VrcTranslator.Desktop;

internal sealed class ActivityLog(string path)
{
    private readonly object guard = new();

    public void Write(string message)
    {
        lock (guard)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Log write failed: 0x{error.HResult:X8}");
            }
        }
    }
}
