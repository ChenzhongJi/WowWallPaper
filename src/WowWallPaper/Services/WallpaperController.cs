using WowWallPaper.Infrastructure;
using WowWallPaper.Models;

namespace WowWallPaper.Services;

public sealed record WallpaperApplyResult(bool Applied, PlaybackState State, string? Message);

/// <summary>
/// Coordinates the native image wallpaper path and the video desktop layer.
/// The controller owns the "current wallpaper" state; the view model owns the
/// library and rotation policy.
/// </summary>
public sealed class WallpaperController : IDisposable
{
    private readonly NativeWallpaperService _nativeWallpaper;
    private readonly VlcVideoWallpaperService _videoWallpaper;
    private readonly AppSettings _settings;
    private bool _disposed;

    public WallpaperController(AppSettings settings)
    {
        _settings = settings;
        _nativeWallpaper = new NativeWallpaperService();
        _videoWallpaper = new VlcVideoWallpaperService();
        _videoWallpaper.StateChanged += OnVideoStateChanged;
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public WallpaperItem? Current { get; private set; }

    public PlaybackState VideoState => _videoWallpaper.State;

    public bool IsVideoActive => Current?.Kind == WallpaperKind.Video;

    public bool IsPaused => _videoWallpaper.IsPaused;

    public string? OriginalWallpaper => _nativeWallpaper.OriginalWallpaper;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _nativeWallpaper.Initialize();
    }

    public WallpaperApplyResult Apply(WallpaperItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(item.Path))
        {
            throw new FileNotFoundException("壁纸文件已被移动或删除。", item.Path);
        }

        if (item.Kind == WallpaperKind.Image)
        {
            _nativeWallpaper.SetImage(item.Path, _settings.ImageFit);
            _videoWallpaper.Stop();
            Current = item;
            AppLog.Info($"Applied image wallpaper: {item.Path}");
            return new WallpaperApplyResult(true, PlaybackState.Stopped, "静态壁纸已应用");
        }

        var state = _videoWallpaper.Start(
            item.Path,
            _settings.VideoFit,
            _settings.MuteVideo,
            _settings.PauseOnFullscreen);

        if (state == PlaybackState.Failed)
        {
            _videoWallpaper.Stop();
            throw new InvalidOperationException("无法启动动态壁纸，请确认 Windows 桌面层和视频解码器可用。");
        }

        Current = item;
        AppLog.Info($"Applied video wallpaper: {item.Path} ({state})");
        var message = state == PlaybackState.WaitingForDesktop
            ? "正在等待 Windows 桌面层…"
            : "动态壁纸已启动";
        return new WallpaperApplyResult(true, state, message);
    }

    public void StopWallpaper()
    {
        if (_disposed)
        {
            return;
        }

        _videoWallpaper.Stop();
        _nativeWallpaper.RestoreOriginal();
        Current = null;
        AppLog.Info("Wallpaper stopped; original Windows wallpaper restored.");
    }

    public void SetImageFit(WallpaperFit fit)
    {
        _settings.ImageFit = fit;
        if (Current?.Kind == WallpaperKind.Image)
        {
            _nativeWallpaper.SetImage(Current.Path, fit);
        }
    }

    public void SetVideoFit(WallpaperFit fit)
    {
        _settings.VideoFit = fit;
        _videoWallpaper.SetFit(fit);
    }

    public void SetMuted(bool muted)
    {
        _settings.MuteVideo = muted;
        _videoWallpaper.SetMuted(muted);
    }

    public void SetPauseOnFullscreen(bool enabled)
    {
        _settings.PauseOnFullscreen = enabled;
        _videoWallpaper.SetPauseOnFullscreen(enabled);
    }

    public void SetPaused(bool paused) => _videoWallpaper.SetPaused(paused);

    public void TogglePaused() => _videoWallpaper.TogglePaused();

    public bool ShowDesktopLayerTest() => _videoWallpaper.ShowDesktopLayerTest();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _videoWallpaper.StateChanged -= OnVideoStateChanged;
        _videoWallpaper.Dispose();
        _nativeWallpaper.Dispose();
    }

    private void OnVideoStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        PlaybackStateChanged?.Invoke(this, e);
    }
}
