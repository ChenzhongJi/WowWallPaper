using WowWallPaper.Models;

namespace WowWallPaper.Infrastructure;

public static class FileTypes
{
    public static IReadOnlySet<string> ImageExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    public static IReadOnlySet<string> VideoExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mpg", ".mpeg", ".mkv", ".webm"
    };

    public const string ImageFilter =
        "图片文件|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有文件|*.*";

    public const string VideoFilter =
        "视频文件|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mpg;*.mpeg;*.mkv;*.webm|所有文件|*.*";

    public const string ImportFilter =
        "所有支持的壁纸|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mpg;*.mpeg;*.mkv;*.webm|图片文件|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|视频文件|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mpg;*.mpeg;*.mkv;*.webm|所有文件|*.*";

    public static string ExtensionOf(string path) => Path.GetExtension(path).ToLowerInvariant();

    public static bool IsImage(string path) => ImageExtensions.Contains(ExtensionOf(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(ExtensionOf(path));

    public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);

    public static WallpaperKind? GetKind(string path)
    {
        if (IsImage(path))
        {
            return WallpaperKind.Image;
        }

        return IsVideo(path) ? WallpaperKind.Video : null;
    }

    public static string KindLabel(WallpaperKind kind) => kind == WallpaperKind.Video ? "视频" : "图片";

    public static string ExtensionLabel(string path) =>
        ExtensionOf(path).TrimStart('.').ToUpperInvariant();
}
