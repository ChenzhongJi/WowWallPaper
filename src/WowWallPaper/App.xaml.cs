using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;
using WowWallPaper.Models;
using WowWallPaper.Services;
using WowWallPaper.ViewModels;
using Application = System.Windows.Application;

namespace WowWallPaper;

public partial class App : Application
{
    private const string MutexName = @"Local\WowWallPaper.SingleInstance";
    private const string ActivateEventName = @"Local\WowWallPaper.Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activationWait;
    private TrayIconService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private bool _ownsMutex;
    private bool _trayHintShown;
    private bool _cleanupDone;
    private bool _isStartupLaunch;
    private bool _showSettingsForScreenshot;
    private string? _screenshotPath;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        ParseArguments(e.Args);
        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "WowWallPaper 无法创建本地数据目录：\n\n" + ex.Message,
                "无法启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        AppLog.Info($"WowWallPaper starting. Startup={_isStartupLaunch}, Screenshot={_screenshotPath ?? "(none)"}");

        if (!AcquireSingleInstance())
        {
            if (!_isStartupLaunch)
            {
                SignalExistingInstance();
            }

            Shutdown();
            return;
        }

        CreateActivationListener();

        try
        {
            _viewModel = new MainViewModel(new SettingsService(), new StartupService(), new ThumbnailService());
            _viewModel.TrayStateChanged += (_, _) => UpdateTray();
            _mainWindow = new MainWindow(_viewModel);
            _mainWindow.HiddenToTray += OnHiddenToTray;
            _mainWindow.AllowClose = false;

            _tray = new TrayIconService();
            _tray.OpenRequested += ShowMainWindow;
            _tray.NextRequested += () => _ = _viewModel.NextWallpaperAsync();
            _tray.TogglePauseRequested += () => _viewModel.TogglePause();
            _tray.MuteToggleRequested += () => _viewModel.MuteVideo = !_viewModel.MuteVideo;
            _tray.StopRequested += () => _viewModel.StopWallpaper();
            _tray.ExitRequested += ExitApp;
            UpdateTray();

            var restrictedIntegrity = TokenSecurity.IsLowIntegrity;
            if (restrictedIntegrity)
            {
                AppLog.Warn(
                    $"Low-integrity process detected ({TokenSecurity.IntegrityLevelLabel}); " +
                    "Windows desktop wallpaper APIs are unavailable.");
                _viewModel.SetRuntimeWarning(
                    "当前运行在受限沙箱（低完整性），Windows 会拒绝修改桌面壁纸。" +
                    "请从托盘退出本程序，然后在 Windows 资源管理器中直接双击 WowWallPaper.exe；" +
                    "不要从聊天文件预览或受限沙箱启动。");
            }

            if (!_isStartupLaunch || _screenshotPath is not null)
            {
                _mainWindow.Show();
            }

            if (AppPaths.FallbackReason is not null)
            {
                AppLog.Warn(AppPaths.FallbackReason);
                if (!restrictedIntegrity && !_isStartupLaunch)
                {
                    _viewModel.NotifySystem(
                        "系统数据目录不可写，已使用便携数据目录：" + AppPaths.DataDirectory,
                        NoticeKind.Info);
                }
            }

            _ = InitializeAndMaybeCaptureAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Application startup failed.", ex);
            System.Windows.MessageBox.Show(
                "WowWallPaper 启动失败：\n\n" + ex.Message,
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApp();
        }
    }

    public void ExitApp()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        AppLog.Info("WowWallPaper exiting.");
        try
        {
            if (_mainWindow is not null)
            {
                _mainWindow.AllowClose = true;
                if (_mainWindow.IsVisible)
                {
                    _mainWindow.Close();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Closing main window failed: {ex.Message}");
        }

        Cleanup();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (!IsExiting)
        {
            ExitApp();
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        base.OnExit(e);
    }

    private async Task InitializeAndMaybeCaptureAsync()
    {
        if (_viewModel is null || _mainWindow is null)
        {
            return;
        }

        try
        {
            await _viewModel.InitializeAsync(applySavedWallpaper: _screenshotPath is null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Wallpaper initialization failed.", ex);
        }

        if (_screenshotPath is null)
        {
            return;
        }

        if (_showSettingsForScreenshot)
        {
            _viewModel.IsSettingsVisible = true;
        }

        try
        {
            await Task.Delay(2200);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            CaptureWindow(_screenshotPath);
            AppLog.Info($"Screenshot written to {_screenshotPath}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Screenshot capture failed.", ex);
        }
        finally
        {
            if (_screenshotPath is null)
            {
                try
                {
                    _viewModel.StopWallpaper();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not restore wallpaper after screenshot: {ex.Message}");
                }
            }

            _ = Dispatcher.BeginInvoke(new Action(ExitApp));
        }
    }

    private void CaptureWindow(string path)
    {
        if (_mainWindow?.Content is not FrameworkElement content)
        {
            return;
        }

        _mainWindow.UpdateLayout();
        content.UpdateLayout();
        var transform = PresentationSource.FromVisual(_mainWindow)?.CompositionTarget?.TransformToDevice
                        ?? Matrix.Identity;
        var width = Math.Max(1, (int)Math.Round(content.ActualWidth * transform.M11));
        var height = Math.Max(1, (int)Math.Round(content.ActualHeight * transform.M22));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96 * transform.M11,
            96 * transform.M22,
            PixelFormats.Pbgra32);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }

    private void ParseArguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--startup":
                    _isStartupLaunch = true;
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    _screenshotPath = args[++i];
                    break;
                case "--settings":
                    _showSettingsForScreenshot = true;
                    break;
            }
        }
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Single-instance mutex unavailable: {ex.Message}");
            return true;
        }
    }

    private void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(80);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not signal the running instance: {ex.Message}");
                return;
            }
        }
    }

    private void CreateActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Single-instance activation listener unavailable: {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (IsExiting || _mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowFromTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _tray.UpdateState(
            _viewModel.CanControlVideo,
            _viewModel.IsVideoPaused,
            _viewModel.MuteVideo,
            _viewModel.HasCurrentWallpaper,
            _viewModel.HasWallpapers);
    }

    private void OnHiddenToTray(object? sender, EventArgs e)
    {
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _tray?.ShowBalloon(
            "WowWallPaper 仍在运行",
            "壁纸已隐藏到系统托盘。双击托盘图标可以随时打开。");
    }

    private void Cleanup()
    {
        if (_cleanupDone)
        {
            return;
        }

        _cleanupDone = true;

        try
        {
            _activationWait?.Unregister(null);
            _activationWait = null;
            _activateEvent?.Dispose();
            _activateEvent = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Activation listener cleanup failed: {ex.Message}");
        }

        try
        {
            _viewModel?.Dispose();
            _viewModel = null;
        }
        catch (Exception ex)
        {
            AppLog.Error("View model cleanup failed.", ex);
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Tray cleanup failed: {ex.Message}");
        }

        try
        {
            if (_ownsMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            _ownsMutex = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Single-instance mutex cleanup failed: {ex.Message}");
        }

        AppLog.Info("Cleanup complete.");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        System.Windows.MessageBox.Show(
            "WowWallPaper 遇到了一个错误，将尝试安全退出。\n\n" + e.Exception.Message,
            "WowWallPaper",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        ExitApp();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("Unhandled domain exception.", exception);
        }
    }
}
