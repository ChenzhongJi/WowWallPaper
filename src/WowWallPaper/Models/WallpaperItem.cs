namespace WowWallPaper.Models;

public sealed class WallpaperItem
{
    public string Path { get; set; } = string.Empty;

    public WallpaperKind Kind { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public static WallpaperItem Create(string path, WallpaperKind kind) => new()
    {
        Path = System.IO.Path.GetFullPath(path),
        Kind = kind,
        AddedAt = DateTimeOffset.Now
    };
}
