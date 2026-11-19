namespace VrcTranslator.Desktop;

internal sealed class ActivityLog(string path)
{
    private readonly object guard = new();

    public string ReadHistory()
    {
        // Take a consistent snapshot even if a writer is about to rotate the files.
        lock (guard)
        {
            string previous = ReadFile(path + ".old");
            string current = ReadFile(path);
            return previous.Length > 0 && current.Length > 0 && !previous.EndsWith('\n')
                ? previous + Environment.NewLine + current
                : previous + current;
        }
    }

    private static string ReadFile(string file)
    {
        try { return File.ReadAllText(file); }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
    }

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
