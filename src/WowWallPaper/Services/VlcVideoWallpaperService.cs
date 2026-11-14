using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;
using WowWallPaper.Models;
using VlcLib = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace WowWallPaper.Services;

/// <summary>
/// Video wallpaper renderer backed by LibVLC. Video is rendered directly into
/// plain native windows; on Windows 11 raised desktops those windows are layered
/// children of Progman, below SHELLDLL_DefView, so icons stay above the video.
/// </summary>
public sealed class VlcVideoWallpaperService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MonitorService _monitorService = new();
    private readonly DispatcherTimer _timer;
    private readonly List<Surface> _surfaces = [];
    private readonly List<MonitorDescriptor> _surfaceMonitors = [];
    private VlcLib? _libVlc;
    private string? _path;
    private WallpaperFit _fit = WallpaperFit.Fill;
    private bool _muted = true;
    private bool _pauseOnFullscreen = true;
    private bool _userPaused;
    private bool _fullscreenPaused;
    private bool _suspended;
    private bool _permanentFailure;
    private bool _disposed;
    private bool _eventsHooked;
    private nint _host;
    private nint _shellDefView;
    private nint _workerW;
    private bool _raisedDesktop;
    private PlaybackState _state = PlaybackState.Stopped;
    private string? _message;
    private DateTime _waitingSinceUtc;
    private DateTime _lastHostSearchUtc;
    private int _tick;

    public VlcVideoWallpaperService()
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

    public string? CurrentPath => _path;

    public bool IsPaused => _state == PlaybackState.Paused;

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
        _path = fullPath;
        _fit = fit is WallpaperFit.Center or WallpaperFit.Tile ? WallpaperFit.Fill : fit;
        _muted = muted;
        _pauseOnFullscreen = pauseOnFullscreen;
        _userPaused = false;
        _fullscreenPaused = false;
        _suspended = false;
        _permanentFailure = false;
        _message = null;
        _waitingSinceUtc = DateTime.UtcNow;
        _timer.Start();

        if (!TryEnsureHost())
        {
            SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层…");
            return _state;
        }

        CreateSurfaces();
        return _state;
    }

    public void Stop() => StopInternal(raiseEvent: true);

    public void SetFit(WallpaperFit fit)
    {
        _fit = fit is WallpaperFit.Center or WallpaperFit.Tile ? WallpaperFit.Fill : fit;
        foreach (var surface in _surfaces)
        {
            ApplyAspectRatio(surface);
        }
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        foreach (var surface in _surfaces)
        {
            try
            {
                surface.Player.Mute = muted || !surface.IsAudioPrimary;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not update VLC mute state: {ex.Message}");
            }
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
        if (_path is null)
        {
            return;
        }

        _userPaused = paused;
        ApplyPlaybackState();
    }

    public void TogglePaused() => SetPaused(!_userPaused);

    public bool ShowDesktopLayerTest()
    {
        // The native video surface itself is now the reliable layer. Keep this
        // method for the UI diagnostic by briefly showing a green native window.
        if (_disposed || !TryEnsureHost())
        {
            return false;
        }

        try
        {
            DisposeTestSurface();
            var monitor = _monitorService.GetMonitors().FirstOrDefault();
            if (monitor is null)
            {
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
            _testWindow = NativeOverlayWindow.Create(
                _host,
                screenRect.Left,
                screenRect.Top,
                screenRect.Width,
                screenRect.Height,
                topMost: false);

            if (_testWindow == 0)
            {
                return false;
            }

            ApplyZOrder(_testWindow, isTestWindow: true);
            _testTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _testTimer.Tick += (_, _) =>
            {
                _testTimer?.Stop();
                DisposeTestSurface();
            };
            _testTimer.Start();
            AppLog.Info(
                $"Desktop layer test window: hwnd=0x{_testWindow:X} parent=0x{_host:X} " +
                $"mode={(_raisedDesktop ? "raised-layered" : "classic")}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Desktop layer test failed.", ex);
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
        UnhookSystemEvents();
        DisposeTestSurface();
        StopInternal(raiseEvent: false);
        try
        {
            _libVlc?.Dispose();
        }
        catch
        {
            // Ignore native teardown failures.
        }

        _libVlc = null;
    }

    private nint _testWindow;
    private DispatcherTimer? _testTimer;

    private void StopInternal(bool raiseEvent)
    {
        _path = null;
        _permanentFailure = false;
        _userPaused = false;
        _fullscreenPaused = false;
        _suspended = false;
        _timer.Stop();
        DisposeSurfaces();
        _host = 0;
        _shellDefView = 0;
        _workerW = 0;
        _raisedDesktop = false;
        _message = null;
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
        if (_disposed || _path is null || _permanentFailure)
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

        if (!IsHostValid() || !SurfacesAreHealthy() || MonitorsChanged())
        {
            if (!IsHostValid())
            {
                DisposeSurfaces();
                _host = 0;
            }

            if (!TryEnsureHost())
            {
                SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层…");
                if ((DateTime.UtcNow - _waitingSinceUtc).TotalSeconds > 20)
                {
                    SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层（请确认资源管理器正在运行）…");
                }

                return;
            }

            CreateSurfaces();
        }
        else if (_surfaces.Count > 0 &&
                 _surfaces.All(s => !s.Opened && !s.Failed) &&
                 (DateTime.UtcNow - _surfacesCreatedUtc).TotalSeconds > 12)
        {
            Fail("视频加载超时，请尝试其他视频。", permanent: true);
        }
    }

    private bool TryEnsureHost()
    {
        if (IsHostValid())
        {
            return true;
        }

        if ((DateTime.UtcNow - _lastHostSearchUtc).TotalMilliseconds < 1200)
        {
            return false;
        }

        _lastHostSearchUtc = DateTime.UtcNow;
        _host = DesktopHostFinder.FindDesktopHost();
        if (_host == 0)
        {
            _raisedDesktop = false;
            _shellDefView = 0;
            _workerW = 0;
            return false;
        }

        var hostClass = GetWindowClass(_host);
        _raisedDesktop = string.Equals(hostClass, "Progman", StringComparison.Ordinal) &&
                         DesktopHostFinder.IsRaisedDesktop(_host);
        _shellDefView = _raisedDesktop
            ? NativeMethods.FindWindowEx(_host, 0, "SHELLDLL_DefView", null)
            : 0;
        _workerW = _raisedDesktop
            ? NativeMethods.FindWindowEx(_host, 0, "WorkerW", null)
            : 0;

        AppLog.Info(
            $"VLC desktop host acquired: 0x{_host:X} class={hostClass} " +
            $"mode={(_raisedDesktop ? "raised-layered" : "classic")} " +
            $"defView=0x{_shellDefView:X} workerW=0x{_workerW:X}");
        return true;
    }

    private DateTime _surfacesCreatedUtc;

    private void CreateSurfaces()
    {
        DisposeSurfaces();
        if (_path is null || _disposed)
        {
            return;
        }

        if (!IsHostValid() && !TryEnsureHost())
        {
            SetState(PlaybackState.WaitingForDesktop, "正在等待 Windows 桌面层…");
            return;
        }

        try
        {
            EnsureLibVlc();
        }
        catch (Exception ex)
        {
            Fail("VLC 初始化失败：" + ex.Message, permanent: true);
            return;
        }

        _surfacesCreatedUtc = DateTime.UtcNow;
        foreach (var monitor in _monitorService.GetMonitors())
        {
            try
            {
                var surface = CreateSurface(monitor);
                _surfaces.Add(surface);
                _surfaceMonitors.Add(monitor);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not create VLC surface for monitor {monitor.Bounds}.", ex);
            }
        }

        if (_surfaces.Count == 0)
        {
            SetState(PlaybackState.WaitingForDesktop, "无法创建桌面视频窗口。请确认资源管理器正在运行。");
            return;
        }

        SetState(PlaybackState.Starting, "正在加载视频壁纸…");
    }

    private Surface CreateSurface(MonitorDescriptor monitor)
    {
        var screenRect = new NativeMethods.RECT
        {
            Left = monitor.Bounds.X,
            Top = monitor.Bounds.Y,
            Right = monitor.Bounds.Right,
            Bottom = monitor.Bounds.Bottom
        };
        NativeMethods.MapWindowPoints(0, _host, ref screenRect, 2);

        var hwnd = NativeVideoWindow.Create(
            _host,
            screenRect.Left,
            screenRect.Top,
            screenRect.Width,
            screenRect.Height,
            layered: _raisedDesktop);
        if (hwnd == 0)
        {
            throw new InvalidOperationException("Windows 拒绝了桌面视频窗口创建。");
        }

        ApplyZOrder(hwnd, isTestWindow: false);

        var player = new VlcMediaPlayer(_libVlc!)
        {
            Hwnd = hwnd,
            EnableHardwareDecoding = true,
            Mute = _muted || _surfaces.Count > 0
        };
        var surface = new Surface(monitor, hwnd, player, _surfaces.Count == 0);
        player.Playing += (_, _) => _dispatcher.BeginInvoke(new Action(() => OnPlaying(surface)));
        player.EncounteredError += (_, _) => _dispatcher.BeginInvoke(new Action(() => OnError(surface)));
        player.EndReached += (_, _) => _dispatcher.BeginInvoke(new Action(() => OnEndReached(surface)));
        ApplyAspectRatio(surface);
        surface.Media = CreateMedia();
        if (!player.Play(surface.Media))
        {
            surface.Failed = true;
            throw new InvalidOperationException("VLC 无法开始播放该文件。");
        }

        AppLog.Info(
            $"VLC surface created hwnd=0x{hwnd:X} parent=0x{_host:X} monitor={monitor.Bounds} " +
            $"raised={_raisedDesktop} audio={surface.IsAudioPrimary}");
        return surface;
    }

    private VlcMedia CreateMedia() =>
        new(_libVlc!, new Uri(_path!, UriKind.Absolute));

    private void OnPlaying(Surface surface)
    {
        if (_disposed || !_surfaces.Contains(surface))
        {
            return;
        }

        surface.Opened = true;
        AppLog.Info($"VLC playing on {surface.Monitor.Bounds}.");
        ApplyPlaybackState();
    }

    private void OnError(Surface surface)
    {
        if (_disposed || !_surfaces.Contains(surface))
        {
            return;
        }

        surface.Failed = true;
        AppLog.Warn($"VLC playback error on {surface.Monitor.Bounds}.");
        if (_surfaces.Count > 0 && _surfaces.All(s => s.Failed))
        {
            Fail("无法播放此视频。请确认编码受支持（推荐 H.264 MP4）。", permanent: true);
        }
    }

    private void OnEndReached(Surface surface)
    {
        if (_disposed || !_surfaces.Contains(surface) || _path is null)
        {
            return;
        }

        try
        {
            surface.Media?.Dispose();
            surface.Media = CreateMedia();
            surface.Player.Play(surface.Media);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not loop VLC video: {ex.Message}");
        }
    }

    private void ApplyPlaybackState()
    {
        if (_disposed || _path is null)
        {
            return;
        }

        var play = ShouldPlay;
        foreach (var surface in _surfaces)
        {
            try
            {
                surface.Player.SetPause(!play);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"VLC playback transition failed: {ex.Message}");
            }
        }

        if (_surfaces.Count == 0)
        {
            return;
        }

        if (_surfaces.All(s => s.Failed))
        {
            return;
        }

        if (!_surfaces.Any(s => s.Opened))
        {
            SetState(PlaybackState.Starting, "正在加载视频壁纸…");
            return;
        }

        if (play)
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

    private bool ShouldPlay =>
        _path is not null &&
        !_userPaused &&
        !_fullscreenPaused &&
        !_suspended &&
        !_permanentFailure;

    private void ApplyAspectRatio(Surface surface)
    {
        try
        {
            surface.Player.AspectRatio = _fit == WallpaperFit.Fit
                ? null
                : $"{surface.Monitor.Bounds.Width}:{surface.Monitor.Bounds.Height}";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not apply VLC aspect ratio: {ex.Message}");
        }
    }

    private void ApplyZOrder(nint hwnd, bool isTestWindow)
    {
        if (_raisedDesktop)
        {
            var insertAfter = _shellDefView != 0 ? _shellDefView : NativeMethods.HWND_BOTTOM;
            NativeMethods.SetWindowPos(
                hwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            if (_workerW != 0)
            {
                NativeMethods.SetWindowPos(
                    _workerW,
                    NativeMethods.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            return;
        }

        var bottom = string.Equals(GetWindowClass(_host), "SHELLDLL_DefView", StringComparison.Ordinal) ||
                     NativeMethods.FindWindowEx(_host, 0, "SHELLDLL_DefView", null) != 0;
        NativeMethods.SetWindowPos(
            hwnd,
            bottom ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private bool IsHostValid() => _host != 0 && NativeMethods.IsWindow(_host);

    private bool SurfacesAreHealthy()
    {
        if (_surfaces.Count == 0)
        {
            return false;
        }

        return _surfaces.All(s => s.Hwnd != 0 &&
                                  NativeMethods.IsWindow(s.Hwnd) &&
                                  NativeMethods.IsWindowVisible(s.Hwnd));
    }

    private bool MonitorsChanged()
    {
        var monitors = _monitorService.GetMonitors();
        if (monitors.Count != _surfaceMonitors.Count)
        {
            return true;
        }

        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Handle != _surfaceMonitors[i].Handle ||
                monitors[i].Bounds != _surfaceMonitors[i].Bounds)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureLibVlc()
    {
        if (_libVlc is not null)
        {
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        var nativeDir = Path.Combine(baseDir, "libvlc", "win-x64");
        Core.Initialize(Directory.Exists(nativeDir) ? nativeDir : baseDir);
        _libVlc = new VlcLib(
            "--no-video-title-show",
            "--no-osd",
            "--quiet");
    }

    private void Fail(string message, bool permanent)
    {
        _message = message;
        _permanentFailure = permanent;
        if (permanent)
        {
            _timer.Stop();
        }

        SetState(PlaybackState.Failed, message);
    }

    private void SetState(PlaybackState state, string? message)
    {
        if (_state == state && string.Equals(_message, message, StringComparison.Ordinal))
        {
            return;
        }

        _state = state;
        _message = message;
        AppLog.Info($"VLC state: {state}" + (message is null ? string.Empty : $" ({message})"));
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(state, message));
    }

    private void DisposeSurfaces()
    {
        foreach (var surface in _surfaces)
        {
            try
            {
                surface.Player.Stop();
                surface.Player.Dispose();
            }
            catch
            {
                // VLC teardown can race with its event thread.
            }

            try
            {
                surface.Media?.Dispose();
            }
            catch
            {
                // Ignore.
            }

            NativeVideoWindow.Destroy(surface.Hwnd);
        }

        _surfaces.Clear();
        _surfaceMonitors.Clear();
    }

    private void DisposeTestSurface()
    {
        if (_testTimer is not null)
        {
            _testTimer.Stop();
            _testTimer = null;
        }

        if (_testWindow != 0)
        {
            NativeOverlayWindow.Destroy(_testWindow);
            _testWindow = 0;
        }
    }

    private void HookSystemEvents()
    {
        if (_eventsHooked)
        {
            return;
        }

        _eventsHooked = true;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // DisplaySettingsChanged is intentionally not used: some remote/virtual
        // display drivers raise it repeatedly without a topology change, which
        // would tear down a working video surface. MonitorsChanged() in the
        // health timer detects real topology changes instead.
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

        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _path is null)
            {
                return;
            }

            _suspended = locked;
            ApplyPlaybackState();
        }));
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Suspend or PowerModes.Resume))
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _path is null)
            {
                return;
            }

            _suspended = e.Mode == PowerModes.Suspend;
            ApplyPlaybackState();
        }));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _path is null)
            {
                return;
            }

            DisposeSurfaces();
            _host = 0;
            _tick = 1;
        }));
    }

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

        var buffer = new System.Text.StringBuilder(256);
        var length = NativeMethods.GetClassName(foreground, buffer, buffer.Capacity);
        var className = length > 0 ? buffer.ToString() : string.Empty;
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
        var info = NativeMethods.MONITORINFO.Create();
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var coveredWidth = Math.Min(windowRect.Right, info.rcMonitor.Right) - Math.Max(windowRect.Left, info.rcMonitor.Left);
        var coveredHeight = Math.Min(windowRect.Bottom, info.rcMonitor.Bottom) - Math.Max(windowRect.Top, info.rcMonitor.Top);
        if (coveredWidth <= 0 || coveredHeight <= 0)
        {
            return false;
        }

        var coverage = (double)coveredWidth * coveredHeight /
                       ((double)info.rcMonitor.Width * info.rcMonitor.Height);
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

    private sealed class Surface(MonitorDescriptor monitor, nint hwnd, VlcMediaPlayer player, bool isAudioPrimary)
    {
        public MonitorDescriptor Monitor { get; } = monitor;

        public nint Hwnd { get; } = hwnd;

        public VlcMediaPlayer Player { get; } = player;

        public bool IsAudioPrimary { get; } = isAudioPrimary;

        public VlcMedia? Media { get; set; }

        public bool Opened { get; set; }

        public bool Failed { get; set; }
    }
}
