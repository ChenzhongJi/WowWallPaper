using System.Text;

namespace WowWallPaper.Infrastructure;

public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxSize = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                RotateIfNeeded();
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);
                if (exception is not null)
                {
                    builder.AppendLine().Append(exception);
                }

                File.AppendAllText(AppPaths.LogFile, builder.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be able to take down the wallpaper engine.
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(AppPaths.LogFile);
        if (!info.Exists || info.Length < MaxSize)
        {
            return;
        }

        var previous = AppPaths.LogFile + ".old";
        File.Delete(previous);
        File.Move(AppPaths.LogFile, previous);
    }
}
