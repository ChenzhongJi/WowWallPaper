using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;
using WowWallPaper.Models;

namespace WowWallPaper.Services;

/// <summary>
/// Applies still images through the Windows desktop wallpaper API. The original
/// wallpaper is captured before the first change so it can be restored later.
/// </summary>
public sealed class NativeWallpaperService : IDisposable
{
    private IDesktopWallpaper? _desktop;
    private bool _initialized;
    private bool _hasOriginalPosition;
    private DesktopWallpaperPosition _originalPosition = DesktopWallpaperPosition.Fill;
    private string? _originalPath;

    public string? OriginalWallpaper => _originalPath;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            _desktop = DesktopWallpaperCom.TryCreate();
            if (_desktop is not null)
            {
                try
                {
                    _desktop.GetWallpaper(null, out var path);
                    _desktop.GetPosition(out var position);
                    _originalPath = string.IsNullOrWhiteSpace(path) ? null : path;
                    _originalPosition = position;
                    _hasOriginalPosition = true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not read current Windows wallpaper: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Desktop wallpaper COM initialization failed: {ex.Message}");
        }

        _originalPath ??= ReadWallpaperFromRegistry();
        AppLog.Info($"Original Windows wallpaper: {_originalPath ?? "(none)"}");
    }

    public void SetImage(string imagePath, WallpaperFit fit)
    {
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("壁纸文件不存在。", fullPath);
        }

        EnsureDesktop();
        if (_desktop is not null)
        {
            try
            {
                _desktop.SetPosition(MapPosition(fit));
                _desktop.SetWallpaper(null, fullPath);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"IDesktopWallpaper failed; falling back to SystemParametersInfo: {ex.Message}");
            }
        }

        SetImageFallback(fullPath, fit);
    }

    public void RestoreOriginal()
    {
        if (string.IsNullOrWhiteSpace(_originalPath))
        {
            AppLog.Info("No original wallpaper recorded; nothing to restore.");
            return;
        }

        EnsureDesktop();
        if (_desktop is not null && _hasOriginalPosition)
        {
            try
            {
                _desktop.SetPosition(_originalPosition);
                _desktop.SetWallpaper(null, _originalPath);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not restore wallpaper through COM: {ex.Message}");
            }
        }

        try
        {
            SetImageFallback(_originalPath, MapPosition(_originalPosition));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restore the original wallpaper.", ex);
        }
    }

    public void Dispose()
    {
        if (_desktop is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_desktop);
            }
            catch
            {
                // Ignore COM teardown failures during shutdown.
            }

            _desktop = null;
        }
    }

    private void EnsureDesktop()
    {
        if (_desktop is not null)
        {
            return;
        }

        _desktop = DesktopWallpaperCom.TryCreate();
    }

    private static void SetImageFallback(string imagePath, WallpaperFit fit)
    {
        var (style, tile) = fit switch
        {
            WallpaperFit.Fill => ("10", "0"),
            WallpaperFit.Fit => ("6", "0"),
            WallpaperFit.Stretch => ("2", "0"),
            WallpaperFit.Center => ("0", "0"),
            WallpaperFit.Tile => ("0", "1"),
            _ => ("10", "0")
        };

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop", writable: true);
            key?.SetValue("WallpaperStyle", style, RegistryValueKind.String);
            key?.SetValue("TileWallpaper", tile, RegistryValueKind.String);
            key?.SetValue("Wallpaper", imagePath, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // Registry writes can be virtualized or denied by policy; the Win32
            // API below is still worth trying and is the actual wallpaper switch.
            AppLog.Warn($"Could not persist wallpaper style registry values: {ex.Message}");
        }

        if (!NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETDESKWALLPAPER,
                0,
                imagePath,
                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 拒绝了桌面壁纸设置请求。");
        }
    }

    private static string? ReadWallpaperFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: false);
            var path = key?.GetValue("Wallpaper") as string;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static DesktopWallpaperPosition MapPosition(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => DesktopWallpaperPosition.Fill,
        WallpaperFit.Fit => DesktopWallpaperPosition.Fit,
        WallpaperFit.Stretch => DesktopWallpaperPosition.Stretch,
        WallpaperFit.Center => DesktopWallpaperPosition.Center,
        WallpaperFit.Tile => DesktopWallpaperPosition.Tile,
        _ => DesktopWallpaperPosition.Fill
    };

    private static WallpaperFit MapPosition(DesktopWallpaperPosition position) => position switch
    {
        DesktopWallpaperPosition.Center => WallpaperFit.Center,
        DesktopWallpaperPosition.Tile => WallpaperFit.Tile,
        DesktopWallpaperPosition.Stretch => WallpaperFit.Stretch,
        DesktopWallpaperPosition.Fit => WallpaperFit.Fit,
        DesktopWallpaperPosition.Fill or DesktopWallpaperPosition.Span => WallpaperFit.Fill,
        _ => WallpaperFit.Fill
    };
}
