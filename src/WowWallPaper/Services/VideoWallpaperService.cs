using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;
using WowWallPaper.Models;
using Application = System.Windows.Application;

namespace WowWallPaper.Services;

public sealed class PlaybackStateChangedEventArgs(PlaybackState state, string? message) : EventArgs
{
    public PlaybackState State { get; } = state;

    public string? Message { get; } = message;
}

/// <summary>
/// Renders video content into the Explorer desktop layer (WorkerW), one child
/// surface per monitor. The engine watches Explorer restarts, monitor changes,
/// session locks and fullscreen applications, then pauses or rebuilds itself.
/// </summary>
public sealed class VideoWallpaperService : IDisposable
{
    private const uint WM_SPAWN_WORKER = 0x052C;
    private readonly Dispatcher _dispatcher;
    private readonly MonitorService _monitorService = new();
    private readonly DispatcherTimer _timer;
    private readonly List<VideoSurface> _surfaces = [];
    private readonly List<MonitorDescriptor> _surfacesMonitors = [];
    private nint _host;
    private nint _shellDefView;
    private bool _isRaisedDesktop;
    private string? _desiredPath;
    private WallpaperFit _fit = WallpaperFit.Fill;
    private bool _muted = true;
    private bool _pauseOnFullscreen = true;
    private bool _userPaused;
    private bool _fullscreenPaused;
    private bool _suspended;
    private bool _permanentFailure;
    private bool _disposed;
    private bool _eventsHooked;
    private int _tick;
    private DateTime _surfacesCreatedUtc;
    private DateTime _lastHostSearchUtc;
    private DateTime _waitingSinceUtc;
    private string? _lastError;
    private PlaybackState _state = PlaybackState.Stopped;
    private HwndSource? _testSurface;
    private DispatcherTimer? _testSurfaceTimer;
    private nint _nativeTestWindow;
    private Window? _wpfTestWindow;

    public VideoWallpaperService()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        HookSystemEvents();
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    public PlaybackState State => _state;

    public string? CurrentPath => _desiredPath;

    public bool IsPaused => _state == PlaybackState.Paused;

    public bool IsRunning => _surfaces.Count > 0 && !_permanentFailure;

    public WallpaperFit CurrentFit => _fit;

    public PlaybackState Start(string videoPath, WallpaperFit fit, bool muted, bool pauseOnFullscreen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = Path.GetFullPath(videoPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("视频文件不存在。", fullPath);
        }

        StopInternal(raiseEvent: false);
        _desiredPath = fullPath;
        _fit = fit is WallpaperFit.Center or WallpaperFit.Tile ? WallpaperFit.Fill : fit;
        _muted = muted;
        _pauseOnFullscreen = pauseOnFullscreen;
        _userPaused = false;
        _fullscreenPaused = false;
        _suspended = false;
        _permanentFailure = false;
        _lastError = null;
        _waitingSinceUtc = DateTime.UtcNow;
        _timer.Start();

        if (!TryEnsureHost())
        {
            SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层…");
            return _state;
        }

        RebuildSurfaces();
        return _state;
    }

    public void Stop() => StopInternal(raiseEvent: true);

    public void SetFit(WallpaperFit fit)
    {
        _fit = fit is WallpaperFit.Center or WallpaperFit.Tile ? WallpaperFit.Fill : fit;
        foreach (var surface in _surfaces)
        {
            surface.Media.Stretch = MapStretch(_fit);
        }
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        foreach (var surface in _surfaces)
        {
            surface.Media.IsMuted = muted || !surface.IsAudioPrimary;
        }
    }

    public void SetPauseOnFullscreen(bool enabled)
    {
        _pauseOnFullscreen = enabled;
        if (!enabled && _fullscreenPaused)
        {
            _fullscreenPaused = false;
            ApplyPlaybackState();
        }
    }

    public void SetPaused(bool paused)
    {
        if (_desiredPath is null)
        {
            return;
        }

        _userPaused = paused;
        ApplyPlaybackState();
    }

    public void TogglePaused() => SetPaused(!_userPaused);

    /// <summary>
    /// Shows a short, unmistakable WPF overlay on the desktop layer. This is a
    /// diagnostic that distinguishes a bad desktop host/z-order from a video
    /// rendering failure.
    /// </summary>
    public bool ShowDesktopLayerTest()
    {
        if (_disposed || !TryEnsureHost())
        {
            return false;
        }

        DisposeTestSurface();
        var monitor = _monitorService.GetMonitors().FirstOrDefault();
        if (monitor is null)
        {
            return false;
        }

        try
        {
            _wpfTestWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = 0,
                Width = 800,
                Height = 600,
                Content = new Grid
                {
                    Background = new SolidColorBrush(Color.FromRgb(0, 225, 255)),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "WPF WINDOW LAYER TEST",
                            Foreground = Brushes.White,
                            FontSize = 36,
                            FontWeight = FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };
            _wpfTestWindow.Show();
            var hwnd = new WindowInteropHelper(_wpfTestWindow).Handle;
            if (hwnd == 0)
            {
                _wpfTestWindow.Close();
                _wpfTestWindow = null;
                return false;
            }

            var screenRect = new NativeMethods.RECT
            {
                Left = monitor.Bounds.X,
                Top = monitor.Bounds.Y,
                Right = monitor.Bounds.Right,
                Bottom = monitor.Bounds.Bottom
            };
            NativeMethods.MapWindowPoints(0, _host, ref screenRect, 2);

            if (_isRaisedDesktop)
            {
                AttachAsRaisedDesktopWindow(hwnd);
            }
            else
            {
                NativeMethods.SetParent(hwnd, _host);
                ApplySurfaceZOrder(hwnd);
            }

            NativeMethods.SetWindowPos(
                hwnd,
                0,
                screenRect.Left,
                screenRect.Top,
                Math.Max(1, screenRect.Width),
                Math.Max(1, screenRect.Height),
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOZORDER);

            _testSurfaceTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _testSurfaceTimer.Tick += (_, _) =>
            {
                _testSurfaceTimer?.Stop();
                DisposeTestSurface();
            };
            _testSurfaceTimer.Start();
            AppLog.Info(
                $"WPF window layer test visible for 5 seconds. hwnd=0x{hwnd:X}, " +
                $"parent=0x{_host:X} class={GetWindowClass(_host)} " +
                $"isVisible={NativeMethods.IsWindowVisible(hwnd)} mode={(_isRaisedDesktop ? "raised-layered" : "classic")}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("WPF desktop layer test creation failed.", ex);
            DisposeTestSurface();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        DisposeTestSurface();
        UnhookSystemEvents();
        StopInternal(raiseEvent: false);
        _state = PlaybackState.Stopped;
    }

    private void StopInternal(bool raiseEvent)
    {
        _desiredPath = null;
        _permanentFailure = false;
        _userPaused = false;
        _fullscreenPaused = false;
        _suspended = false;
        _timer.Stop();
        DisposeSurfaces();
        _host = 0;
        _lastError = null;
        if (raiseEvent)
        {
            SetState(PlaybackState.Stopped, "已停止动态壁纸");
        }
        else
        {
            _state = PlaybackState.Stopped;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _desiredPath is null || _permanentFailure)
        {
            return;
        }

        _tick++;

        if (_pauseOnFullscreen)
        {
            var fullscreen = IsFullscreenAppActive();
            if (fullscreen != _fullscreenPaused)
            {
                _fullscreenPaused = fullscreen;
                AppLog.Info(fullscreen ? "Fullscreen app detected; video wallpaper paused." : "Fullscreen app left; video wallpaper resumed.");
                ApplyPlaybackState();
            }
        }

        if (_tick % 2 != 0)
        {
            return;
        }

        if (!IsHostValid(_host) || !SurfacesAreHealthy() || MonitorsChanged())
        {
            if (!IsHostValid(_host))
            {
                DisposeSurfaces();
                _host = 0;
            }

            if (!TryEnsureHost())
            {
                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                }

                SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层…");

                if ((DateTime.UtcNow - _waitingSinceUtc).TotalSeconds > 20 &&
                    _state is PlaybackState.WaitingForDesktop or PlaybackState.Starting)
                {
                    SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层（请确认资源管理器正在运行）…");
                }

                return;
            }

            RebuildSurfaces();
        }
        else if (_surfaces.Count > 0 &&
                 _surfaces.All(s => !s.Opened && !s.Failed) &&
                 (DateTime.UtcNow - _surfacesCreatedUtc).TotalSeconds > 12)
        {
            Fail("视频加载超时，请尝试其他视频或确认系统视频解码器可用。", permanent: true);
        }
    }

    private bool TryEnsureHost()
    {
        if (IsHostValid(_host))
        {
            return true;
        }

        // Avoid hammering Explorer while it is restarting.
        if ((DateTime.UtcNow - _lastHostSearchUtc).TotalMilliseconds < 1200)
        {
            return false;
        }

        _lastHostSearchUtc = DateTime.UtcNow;
        _host = DesktopHostFinder.FindDesktopHost();
        if (_host == 0)
        {
            _host = 0;
            _isRaisedDesktop = false;
            _shellDefView = 0;
            return false;
        }

        var hostClass = GetWindowClass(_host);
        _isRaisedDesktop = string.Equals(hostClass, "Progman", StringComparison.Ordinal) &&
                           DesktopHostFinder.IsRaisedDesktop(_host);
        _shellDefView = _isRaisedDesktop
            ? NativeMethods.FindWindowEx(_host, 0, "SHELLDLL_DefView", null)
            : 0;

        AppLog.Info(
            $"Desktop WorkerW host acquired: 0x{_host:X} class={hostClass} " +
            $"mode={(_isRaisedDesktop ? "raised-layered" : "classic")} " +
            $"shellDefView=0x{_shellDefView:X}");
        return true;
    }

    private void RebuildSurfaces()
    {
        DisposeSurfaces();
        if (_desiredPath is null || _disposed)
        {
            return;
        }

        if (!IsHostValid(_host) && !TryEnsureHost())
        {
            SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层…");
            return;
        }

        _surfacesCreatedUtc = DateTime.UtcNow;
        foreach (var monitor in _monitorService.GetMonitors())
        {
            try
            {
                var surface = CreateSurface(monitor, _desiredPath, isAudioPrimary: monitor.IsPrimary);
                _surfaces.Add(surface);
                _surfacesMonitors.Add(monitor);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not create video surface for monitor {monitor.Bounds}.", ex);
            }
        }

        if (_surfaces.Count == 0)
        {
            Fail("无法创建桌面视频窗口。请确认 Windows 资源管理器正在运行。", permanent: false);
            SetState(PlaybackState.WaitingForDesktop, "正在等待可用的桌面层…");
            return;
        }

        SetState(PlaybackState.Starting, "正在加载视频壁纸…");
    }

    private VideoSurface CreateSurface(MonitorDescriptor monitor, string path, bool isAudioPrimary)
    {
        var screenRect = new NativeMethods.RECT
        {
            Left = monitor.Bounds.X,
            Top = monitor.Bounds.Y,
            Right = monitor.Bounds.Right,
            Bottom = monitor.Bounds.Bottom
        };
        NativeMethods.MapWindowPoints(0, _host, ref screenRect, 2);

        var parameters = new HwndSourceParameters("WowWallPaperVideoSurface")
        {
            ParentWindow = _host,
            WindowStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE |
                          NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
            ExtendedWindowStyle = NativeMethods.WS_EX_NOACTIVATE |
                                  NativeMethods.WS_EX_TOOLWINDOW,
            PositionX = screenRect.Left,
            PositionY = screenRect.Top,
            Width = Math.Max(1, screenRect.Width),
            Height = Math.Max(1, screenRect.Height)
        };

        var source = new HwndSource(parameters);
        var root = new Grid
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };

        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = MapStretch(_fit),
            IsMuted = _muted || !isAudioPrimary,
            Volume = 0.6,
            SnapsToDevicePixels = true
        };

        var surface = new VideoSurface(source, media, monitor, isAudioPrimary);
        media.MediaOpened += (_, _) => OnMediaOpened(surface);
        media.MediaEnded += (_, _) => OnMediaEnded(surface);
        media.MediaFailed += (_, args) => OnMediaFailed(surface, args);
        media.Loaded += (_, _) =>
        {
            if (ShouldPlay)
            {
                TryPlay(surface);
            }
        };

        root.Children.Add(media);
        source.RootVisual = root;
        if (source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Black;
        }

        NativeMethods.SetWindowPos(
            source.Handle,
            ShouldPlaceSurfaceAtBottom() ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        ApplySurfaceZOrder(source.Handle);

        AppLog.Info(
            $"Video surface created hwnd=0x{source.Handle:X} parent=0x{_host:X} " +
            $"class={GetWindowClass(_host)} monitor={monitor.Bounds} " +
            $"isVisible={NativeMethods.IsWindowVisible(source.Handle)} " +
            $"mode={(_isRaisedDesktop ? "raised-layered" : "classic")}");

        media.Source = new Uri(path, UriKind.Absolute);
        if (ShouldPlay)
        {
            TryPlay(surface);
        }

        return surface;
    }

    private bool ShouldPlaceSurfaceAtBottom()
    {
        // A DefView host or a classic Progman host owns the icon view. Its video
        // child must stay below the icons. A dedicated wallpaper WorkerW has no
        // icon view, so its child belongs at the top of that layer.
        return string.Equals(GetWindowClass(_host), "SHELLDLL_DefView", StringComparison.Ordinal)
               || NativeMethods.FindWindowEx(_host, 0, "SHELLDLL_DefView", null) != 0;
    }

    private void ApplyRaisedDesktopLayer(nint hwnd)
    {
        try
        {
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hwnd,
                NativeMethods.GWL_EXSTYLE,
                new nint(exStyle | NativeMethods.WS_EX_LAYERED));
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);

            var insertAfter = _shellDefView != 0 ? _shellDefView : NativeMethods.HWND_BOTTOM;
            NativeMethods.SetWindowPos(
                hwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            // Keep the wallpaper WorkerW below our layered window; otherwise it
            // paints over the video even though the video is below DefView.
            var workerW = NativeMethods.FindWindowEx(_host, 0, "WorkerW", null);
            if (workerW != 0)
            {
                NativeMethods.SetWindowPos(
                    workerW,
                    NativeMethods.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            AppLog.Info(
                $"Raised desktop window layered below DefView: hwnd=0x{hwnd:X} " +
                $"defView=0x{_shellDefView:X} workerW=0x{workerW:X} " +
                $"exStyle=0x{NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE):X}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not apply raised-desktop child window style.", ex);
        }
    }

    private void AttachAsRaisedDesktopWindow(nint hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GWL_STYLE,
            new nint(style | NativeMethods.WS_CHILD));

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            new nint(exStyle | NativeMethods.WS_EX_LAYERED));
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        NativeMethods.SetParent(hwnd, _host);
        ApplyRaisedDesktopLayer(hwnd);
    }

    private void ApplySurfaceZOrder(nint hwnd)
    {
        if (_isRaisedDesktop)
        {
            ApplyRaisedDesktopLayer(hwnd);
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            ShouldPlaceSurfaceAtBottom() ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnMediaOpened(VideoSurface surface)
    {
        if (_disposed || !_surfaces.Contains(surface))
        {
            return;
        }

        surface.Opened = true;
        AppLog.Info($"Video surface opened on {surface.Monitor.Bounds}.");
        ApplyPlaybackState();
    }

    private void OnMediaEnded(VideoSurface surface)
    {
        if (_disposed || !_surfaces.Contains(surface) || !surface.Opened)
        {
            return;
        }

        try
        {
            surface.Media.Position = TimeSpan.Zero;
            if (ShouldPlay)
            {
                surface.Media.Play();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not loop video wallpaper: {ex.Message}");
        }
    }

    private void OnMediaFailed(VideoSurface surface, ExceptionRoutedEventArgs args)
    {
        surface.Failed = true;
        var detail = args.ErrorException?.Message ?? "未知解码错误";
        AppLog.Warn($"Video surface failed on {surface.Monitor.Bounds}: {detail}");

        if (_surfaces.Count > 0 && _surfaces.All(s => s.Failed))
        {
            Fail("无法播放此视频。请确认文件未损坏，且系统支持该视频编码（推荐 H.264 MP4）。", permanent: true);
        }
    }

    private void ApplyPlaybackState()
    {
        if (_disposed || _desiredPath is null)
        {
            return;
        }

        foreach (var surface in _surfaces)
        {
            if (!surface.Opened || surface.Failed)
            {
                continue;
            }

            try
            {
                if (ShouldPlay)
                {
                    if (surface.Media.NaturalDuration.HasTimeSpan &&
                        surface.Media.Position >= surface.Media.NaturalDuration.TimeSpan)
                    {
                        surface.Media.Position = TimeSpan.Zero;
                    }

                    surface.Media.Play();
                }
                else
                {
                    surface.Media.Pause();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Video playback transition failed: {ex.Message}");
            }
        }

        if (_surfaces.Any(s => s.Failed) && _surfaces.All(s => s.Failed))
        {
            return;
        }

        if (_surfaces.Count == 0)
        {
            return;
        }

        if (!_surfaces.Any(s => s.Opened))
        {
        SetState(PlaybackState.Starting, "正在加载视频壁纸…");
            return;
        }

        if (ShouldPlay)
        {
            SetState(PlaybackState.Playing, "动态壁纸播放中");
        }
        else
        {
            var reason = _suspended
                ? "系统已锁定，动态壁纸暂停"
                : _fullscreenPaused
                    ? "全屏应用运行中，动态壁纸暂停"
                    : "动态壁纸已暂停";
            SetState(PlaybackState.Paused, reason);
        }
    }

    private void TryPlay(VideoSurface surface)
    {
        try
        {
            surface.Media.Play();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not start video playback: {ex.Message}");
        }
    }

    private bool ShouldPlay =>
        _desiredPath is not null &&
        !_userPaused &&
        !_fullscreenPaused &&
        !_suspended &&
        !_permanentFailure;

    private void Fail(string message, bool permanent)
    {
        _lastError = message;
        _permanentFailure = permanent;
        if (permanent)
        {
            _timer.Stop();
            _dispatcher.BeginInvoke(() =>
            {
                if (_desiredPath is not null && _permanentFailure)
                {
                    DisposeSurfaces();
                }
            });
        }

        SetState(PlaybackState.Failed, message);
    }

    private void SetState(PlaybackState state, string? message)
    {
        if (_state == state && string.Equals(_lastError, message, StringComparison.Ordinal))
        {
            return;
        }

        _state = state;
        _lastError = message;
        AppLog.Info($"Video state: {state}" + (message is null ? string.Empty : $" ({message})"));
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(state, message));
    }

    private HwndSource? CreateTestSurface(MonitorDescriptor monitor)
    {
        var screenRect = new NativeMethods.RECT
        {
            Left = monitor.Bounds.X,
            Top = monitor.Bounds.Y,
            Right = monitor.Bounds.Right,
            Bottom = monitor.Bounds.Bottom
        };
        NativeMethods.MapWindowPoints(0, _host, ref screenRect, 2);

        var parameters = new HwndSourceParameters("WowWallPaperLayerTest")
        {
            ParentWindow = _host,
            WindowStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE |
                          NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
            ExtendedWindowStyle = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW,
            PositionX = screenRect.Left,
            PositionY = screenRect.Top,
            Width = Math.Max(1, screenRect.Width),
            Height = Math.Max(1, screenRect.Height)
        };

        var source = new HwndSource(parameters);
        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(0, 225, 255)),
            ClipToBounds = true
        };
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = "DESKTOP LAYER TEST",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Bold,
            FontSize = 38,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = "如果你能看到这个画面，桌面层窗口是可见的",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 22,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        root.Children.Add(content);
        source.RootVisual = root;
        if (source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Color.FromRgb(0, 225, 255);
        }

        NativeMethods.SetWindowPos(
            source.Handle,
            ShouldPlaceSurfaceAtBottom() ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        return source;
    }

    private void DisposeTestSurface()
    {
        if (_testSurfaceTimer is not null)
        {
            _testSurfaceTimer.Stop();
            _testSurfaceTimer = null;
        }

        if (_nativeTestWindow != 0)
        {
            NativeOverlayWindow.Destroy(_nativeTestWindow);
            _nativeTestWindow = 0;
        }

        if (_wpfTestWindow is not null)
        {
            try
            {
                _wpfTestWindow.Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"WPF test window cleanup failed: {ex.Message}");
            }

            _wpfTestWindow = null;
        }

        if (_testSurface is null)
        {
            return;
        }

        try
        {
            _testSurface.RootVisual = null;
            _testSurface.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Desktop layer test surface cleanup failed: {ex.Message}");
        }
        finally
        {
            _testSurface = null;
        }
    }

    private void DisposeSurfaces()
    {
        foreach (var surface in _surfaces)
        {
            try
            {
                surface.Media.Stop();
                surface.Media.Source = null;
                surface.Media.Close();
            }
            catch
            {
                // The media stack may already be shutting down.
            }

            try
            {
                surface.Source.RootVisual = null;
                surface.Source.Dispose();
            }
            catch
            {
                // Explorer may have destroyed the parent window first.
            }
        }

        _surfaces.Clear();
        _surfacesMonitors.Clear();
    }

    private bool SurfacesAreHealthy()
    {
        if (_surfaces.Count == 0)
        {
            return false;
        }

        foreach (var surface in _surfaces)
        {
            if (surface.Source.Handle == 0 ||
                !NativeMethods.IsWindow(surface.Source.Handle) ||
                !NativeMethods.IsWindowVisible(surface.Source.Handle))
            {
                return false;
            }
        }

        return true;
    }

    private bool MonitorsChanged()
    {
        var monitors = _monitorService.GetMonitors();
        if (monitors.Count != _surfacesMonitors.Count)
        {
            return true;
        }

        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Handle != _surfacesMonitors[i].Handle ||
                monitors[i].Bounds != _surfacesMonitors[i].Bounds)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHostValid(nint host) =>
        host != 0 && NativeMethods.IsWindow(host);

    private static bool IsFullscreenAppActive()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0 || !NativeMethods.IsWindowVisible(foreground) || NativeMethods.IsIconic(foreground))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        var className = GetWindowClass(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
            "MultitaskingViewFrame" or "ForegroundStaging")
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(foreground, out var windowRect))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return false;
        }

        var info = NativeMethods.MONITORINFO.Create();
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var monitorWidth = info.rcMonitor.Width;
        var monitorHeight = info.rcMonitor.Height;
        if (monitorWidth <= 0 || monitorHeight <= 0)
        {
            return false;
        }

        var coveredWidth = Math.Min(windowRect.Right, info.rcMonitor.Right) - Math.Max(windowRect.Left, info.rcMonitor.Left);
        var coveredHeight = Math.Min(windowRect.Bottom, info.rcMonitor.Bottom) - Math.Max(windowRect.Top, info.rcMonitor.Top);
        if (coveredWidth <= 0 || coveredHeight <= 0)
        {
            return false;
        }

        var coverage = (double)coveredWidth * coveredHeight / ((double)monitorWidth * monitorHeight);
        return coverage >= 0.97;
    }

    private static string GetWindowClass(nint hwnd)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(256);
            var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Stretch MapStretch(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => Stretch.UniformToFill,
        WallpaperFit.Fit => Stretch.Uniform,
        WallpaperFit.Stretch => Stretch.Fill,
        _ => Stretch.UniformToFill
    };

    private void HookSystemEvents()
    {
        if (_eventsHooked)
        {
            return;
        }

        _eventsHooked = true;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void UnhookSystemEvents()
    {
        if (!_eventsHooked)
        {
            return;
        }

        _eventsHooked = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var locked = e.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect;

        var unlocked = e.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect;

        if (!locked && !unlocked)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _desiredPath is null)
            {
                return;
            }

            _suspended = locked;
            AppLog.Info(locked ? "Session locked; pausing video wallpaper." : "Session unlocked; resuming video wallpaper.");
            ApplyPlaybackState();
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Suspend or PowerModes.Resume))
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _desiredPath is null)
            {
                return;
            }

            _suspended = e.Mode == PowerModes.Suspend;
            ApplyPlaybackState();
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _desiredPath is null)
            {
                return;
            }

            DisposeSurfaces();
            _host = 0;
            _tick = 1;
        });
    }

    private sealed class VideoSurface(HwndSource source, MediaElement media, MonitorDescriptor monitor, bool isAudioPrimary)
    {
        public HwndSource Source { get; } = source;

        public MediaElement Media { get; } = media;

        public MonitorDescriptor Monitor { get; } = monitor;

        public bool IsAudioPrimary { get; } = isAudioPrimary;

        public bool Opened { get; set; }

        public bool Failed { get; set; }
    }
}

internal static class DesktopHostFinder
{
    public static nint FindDesktopHost()
    {
        // A diagnostic escape hatch for integration tests and unusual shells.
        // Not part of the normal user flow.
        var overrideHost = Environment.GetEnvironmentVariable("WOWWALLPAPER_HOST_HWND");
        if (!string.IsNullOrWhiteSpace(overrideHost) &&
            ulong.TryParse(overrideHost, out var parsedHost) &&
            NativeMethods.IsWindow(new nint((long)parsedHost)))
        {
            return new nint((long)parsedHost);
        }

        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == 0)
        {
            progman = NativeMethods.GetShellWindow();
        }

        if (progman != 0)
        {
            // Ask Explorer to create its wallpaper WorkerW. On Windows 11 the
            // 0xD/0x1 variant also forces the raised-desktop wallpaper layer.
            NativeMethods.SendMessageTimeout(
                progman,
                0x052C,
                new nint(0xD),
                new nint(1),
                NativeMethods.SMTO_ABORTIFHUNG,
                1000,
                out _);
            NativeMethods.SendMessageTimeout(
                progman,
                0x052C,
                0,
                0,
                NativeMethods.SMTO_ABORTIFHUNG,
                1000,
                out _);
        }

        if (progman != 0 && IsRaisedDesktop(progman))
        {
            // Windows 11 "raised desktop": Progman has no redirection bitmap and
            // the shell view is a layered child. The wallpaper window must be a
            // layered child of Progman, z-ordered beneath SHELLDLL_DefView and
            // above the WorkerW spawned by the message above.
            return progman;
        }

        nint defViewHost = 0;
        nint defViewWindow = 0;
        nint workerBehindIcons = 0;
        NativeMethods.EnumWindows((top, _) =>
        {
            var defView = NativeMethods.FindWindowEx(top, 0, "SHELLDLL_DefView", null);
            if (defView == 0)
            {
                return true;
            }

            defViewHost = top;
            defViewWindow = defView;
            var worker = NativeMethods.FindWindowEx(0, top, "WorkerW", null);
            if (worker != 0)
            {
                workerBehindIcons = worker;
            }

            return false;
        }, 0);

        if (workerBehindIcons != 0)
        {
            return workerBehindIcons;
        }

        if (defViewWindow != 0 && IsUsableDesktopWindow(defViewWindow))
        {
            // On some Windows 11 24H2 shells the visible wallpaper background is
            // painted by SHELLDLL_DefView (a layered window), while the sibling
            // WorkerW wallpaper layer is fully covered. Hosting the video as a
            // child of DefView, below its icon list view, keeps icons above it.
            return defViewWindow;
        }

        if (defViewHost != 0)
        {
            var innerWorker = FindLargeVisibleWorker(defViewHost);
            if (innerWorker != 0)
            {
                return innerWorker;
            }

            return defViewHost;
        }

        // Desktop icons may be hidden. Find the largest empty WorkerW, if any.
        nint bestWorker = 0;
        long bestArea = 0;
        NativeMethods.EnumWindows((top, _) =>
        {
            if (NativeMethods.FindWindowEx(top, 0, "SHELLDLL_DefView", null) != 0)
            {
                return true;
            }

            var className = GetClassName(top);
            if (!string.Equals(className, "WorkerW", StringComparison.Ordinal))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(top, out var rect))
            {
                return true;
            }

            var area = (long)Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
            if (area > bestArea)
            {
                bestArea = area;
                bestWorker = top;
            }

            return true;
        }, 0);

        if (bestWorker != 0)
        {
            return bestWorker;
        }

        return progman;
    }

    private static bool IsUsableDesktopWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindowVisible(hwnd) ||
            !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var virtualWidth = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var virtualHeight = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        var area = (long)Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
        return area >= (long)virtualWidth * virtualHeight / 4;
    }

    internal static bool IsRaisedDesktop(nint progman)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(progman, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (exStyle & NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0;
    }

    private static nint FindLargeVisibleWorker(nint root)
    {        var virtualWidth = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var virtualHeight = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        var virtualArea = (long)virtualWidth * virtualHeight;
        nint best = 0;
        long bestArea = 0;

        NativeMethods.EnumChildWindows(root, (child, _) =>
        {
            if (!string.Equals(GetClassName(child), "WorkerW", StringComparison.Ordinal))
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(child) ||
                NativeMethods.FindWindowEx(child, 0, "SHELLDLL_DefView", null) != 0)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(child, out var rect))
            {
                return true;
            }

            var area = (long)Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
            // Ignore the tiny hidden helper WorkerW windows some shells create.
            if (area < virtualArea / 4 || area <= bestArea)
            {
                return true;
            }

            best = child;
            bestArea = area;
            return true;
        }, 0);

        return best;
    }

    private static string GetClassName(nint hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }
}

