namespace WowWallPaper.Models;

public enum WallpaperFit
{
    Fill,
    Fit,
    Stretch,
    Center,
    Tile
}

public static class WallpaperFitExtensions
{
    public static string ToDisplayName(this WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => "填充",
        WallpaperFit.Fit => "适应",
        WallpaperFit.Stretch => "拉伸",
        WallpaperFit.Center => "居中",
        WallpaperFit.Tile => "平铺",
        _ => "填充"
    };

    public static IReadOnlyList<WallpaperFit> ImageFits { get; } =
    [
        WallpaperFit.Fill,
        WallpaperFit.Fit,
        WallpaperFit.Stretch,
        WallpaperFit.Center,
        WallpaperFit.Tile
    ];

    public static IReadOnlyList<WallpaperFit> VideoFits { get; } =
    [
        WallpaperFit.Fill,
        WallpaperFit.Fit,
        WallpaperFit.Stretch
    ];
}
