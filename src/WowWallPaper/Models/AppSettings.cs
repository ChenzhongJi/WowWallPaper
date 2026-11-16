using WowWallPaper.Infrastructure;

namespace WowWallPaper.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public List<WallpaperItem> Library { get; set; } = [];

    public string? CurrentWallpaperPath { get; set; }

    public WallpaperFit ImageFit { get; set; } = WallpaperFit.Fill;

    public WallpaperFit VideoFit { get; set; } = WallpaperFit.Fill;

    public bool MuteVideo { get; set; } = true;

    public bool PauseOnFullscreen { get; set; } = true;

    public bool AutoRotate { get; set; }

    public int RotationIntervalMinutes { get; set; } = 30;

    public bool Shuffle { get; set; }

    public bool RunAtStartup { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public void Normalize()
    {
        Library ??= [];
        RotationIntervalMinutes = Math.Clamp(RotationIntervalMinutes, 1, 1440);
        if (!Enum.IsDefined(ImageFit))
        {
            ImageFit = WallpaperFit.Fill;
        }

        if (!Enum.IsDefined(VideoFit) || VideoFit is WallpaperFit.Center or WallpaperFit.Tile)
        {
            VideoFit = WallpaperFit.Fill;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<WallpaperItem>(Library.Count);
        foreach (var item in Library)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(item.Path);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            item.Path = fullPath;
            if (!Enum.IsDefined(item.Kind) || item.Kind == WallpaperKind.Image && FileTypes.IsVideo(fullPath))
            {
                var inferred = FileTypes.GetKind(fullPath);
                if (inferred is null)
                {
                    continue;
                }

                item.Kind = inferred.Value;
            }

            normalized.Add(item);
        }

        Library = normalized;

        if (CurrentWallpaperPath is not null)
        {
            try
            {
                CurrentWallpaperPath = Path.GetFullPath(CurrentWallpaperPath);
            }
            catch
            {
                CurrentWallpaperPath = null;
            }

            if (CurrentWallpaperPath is not null &&
                !Library.Any(i => string.Equals(i.Path, CurrentWallpaperPath, StringComparison.OrdinalIgnoreCase)))
            {
                CurrentWallpaperPath = null;
            }
        }
    }
}
