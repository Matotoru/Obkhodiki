namespace ZapretHub.App;

internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.Log)!);
                var info = new FileInfo(AppPaths.Log);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(AppPaths.Log, AppPaths.Log + ".old", overwrite: true);
                }
                File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
