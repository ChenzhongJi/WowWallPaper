namespace WowWallPaper.Infrastructure;

public static class AppPaths
{
    private const string AppFolderName = "WowWallPaper";
    private const string PortableFolderName = "WowWallPaperData";
    private static readonly string ResolvedDataDirectory;
    private static readonly string? ResolvedFallbackReason;
    private static readonly bool ResolvedIsPortable;

    static AppPaths()
    {
        ResolveDataDirectory(out ResolvedDataDirectory, out ResolvedFallbackReason, out ResolvedIsPortable);
    }

    public static string DataDirectory => ResolvedDataDirectory;

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string ThumbnailDirectory => Path.Combine(DataDirectory, "thumbnails");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LogFile => Path.Combine(LogDirectory, "app.log");

    /// <summary>True when the normal per-user data directory was not writable.</summary>
    public static bool IsPortable => ResolvedIsPortable;

    /// <summary>Human-readable explanation when a fallback directory is in use.</summary>
    public static string? FallbackReason => ResolvedFallbackReason;

    public static string? ExecutablePath => Environment.ProcessPath;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static void ResolveDataDirectory(
        out string selected,
        out string? fallbackReason,
        out bool isPortable)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var overrideValue = Environment.GetEnvironmentVariable("WOWWALLPAPER_DATA_DIR");
        var hasOverride = !string.IsNullOrWhiteSpace(overrideValue);
        var preferred = hasOverride
            ? Path.GetFullPath(overrideValue!)
            : Path.Combine(localAppData, AppFolderName);

        var candidates = new List<string> { preferred };
        var userDirectory = Path.Combine(localAppData, AppFolderName);
        if (!SamePath(preferred, userDirectory))
        {
            candidates.Add(userDirectory);
        }

        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            candidates.Add(Path.Combine(executableDirectory, PortableFolderName));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), PortableFolderName));

        var failures = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryPrepare(candidate, out var error))
            {
                selected = candidate;
                var usingRequestedPath = SamePath(candidate, preferred);
                isPortable = !SamePath(candidate, userDirectory) && !(hasOverride && usingRequestedPath);
                fallbackReason = SamePath(candidate, preferred) && (hasOverride || SamePath(preferred, userDirectory))
                    ? null
                    : $"系统数据目录不可写，已切换到便携目录：{candidate}";
                return;
            }

            failures.Add($"{candidate}: {error}");
        }

        // Keep the requested path so the startup error can name it precisely.
        selected = preferred;
        isPortable = false;
        fallbackReason = failures.Count == 0
            ? null
            : "没有可写的数据目录。" + string.Join(" | ", failures);
    }

    private static bool TryPrepare(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            try
            {
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }
            }
            finally
            {
                try
                {
                    File.Delete(probe);
                }
                catch
                {
                    // The probe file is harmless if an antivirus briefly holds it.
                }
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
