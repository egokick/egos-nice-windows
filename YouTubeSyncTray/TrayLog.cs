namespace YouTubeSyncTray;

internal static class TrayLog
{
    private static readonly object SyncRoot = new();

    public static void Write(YoutubeSyncPaths paths, string message)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsPath);
            var logPath = Path.Combine(paths.LogsPath, "tray-sync.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
        }
    }
}
