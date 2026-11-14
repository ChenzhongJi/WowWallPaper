using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;
using WowWallPaper.Models;

namespace WowWallPaper.Services;

/// <summary>
/// Produces 16:9-friendly preview images for both pictures and videos.
/// Shell thumbnails are used for videos and for image formats WPF cannot decode;
/// results are cached under LocalAppData so scrolling the library stays cheap.
/// </summary>
public sealed class ThumbnailService
{
    private const int MaxCacheDimension = 960;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public async Task<BitmapSource?> GetThumbnailAsync(string path, WallpaperKind kind)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var cacheFile = BuildCachePath(path);
        if (cacheFile is not null)
        {
            var cached = LoadCachedBitmap(cacheFile);
            if (cached is not null)
            {
                return cached;
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cacheFile is not null)
            {
                var cached = LoadCachedBitmap(cacheFile);
                if (cached is not null)
                {
                    return cached;
                }
            }

            var bitmap = await Task.Run(() => RunOnStaThread(() => CreateThumbnail(path, kind))).ConfigureAwait(false);
            if (bitmap is null)
            {
                return null;
            }

            if (cacheFile is not null)
            {
                TrySaveCache(bitmap, cacheFile);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Thumbnail generation failed for '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(string path)
    {
        try
        {
            var cacheFile = BuildCachePath(path);
            if (cacheFile is not null && File.Exists(cacheFile))
            {
                File.Delete(cacheFile);
            }
        }
        catch
        {
            // Cache cleanup is best-effort.
        }
    }

    private static BitmapSource? CreateThumbnail(string path, WallpaperKind kind)
    {
        BitmapSource? source = null;
        if (kind == WallpaperKind.Image)
        {
            source = TryDecodeImage(path);
        }

        if (source is null)
        {
            source = TryShellThumbnail(path);
        }

        if (source is null)
        {
            return null;
        }

        return ScaleDown(source);
    }

    private static BitmapSource? TryDecodeImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 1280;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryShellThumbnail(string path)
    {
        try
        {
            var iid = ShellThumbnailNative.IidShellItemImageFactory;
            ShellThumbnailNative.SHCreateItemFromParsingName(path, 0, ref iid, out var factory);
            try
            {
                var hr = factory.GetImage(
                    new SIZE(MaxCacheDimension, MaxCacheDimension * 9 / 16),
                    SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk | SIIGBF.ScaleUp,
                    out var hBitmap);
                if (hr != 0 || hBitmap == 0)
                {
                    return null;
                }

                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        0,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    ShellThumbnailNative.DeleteObject(hBitmap);
                }
            }
            finally
            {
                if (factory is not null)
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource ScaleDown(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return source;
        }

        var scale = Math.Min(1.0, MaxCacheDimension / (double)Math.Max(width, height));
        if (scale >= 0.999)
        {
            return source;
        }

        var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static BitmapSource? LoadCachedBitmap(string cacheFile)
    {
        try
        {
            if (!File.Exists(cacheFile) || new FileInfo(cacheFile).Length == 0)
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(cacheFile, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySaveCache(BitmapSource bitmap, string cacheFile)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ThumbnailDirectory);
            var temp = cacheFile + ".tmp";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }

            File.Move(temp, cacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Thumbnail cache write failed: {ex.Message}");
        }
    }

    private static string? BuildCachePath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var identity = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            var name = Convert.ToHexString(hash)[..32].ToLowerInvariant();
            return Path.Combine(AppPaths.ThumbnailDirectory, name + ".png");
        }
        catch
        {
            return null;
        }
    }

    private static T? RunOnStaThread<T>(Func<T?> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "WowWallPaper.Thumbnail"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("Thumbnail extraction failed.", failure);
        }

        return result;
    }
}
