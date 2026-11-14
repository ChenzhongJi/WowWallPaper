using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;
using WowWallPaper.Models;
using WowWallPaper.ViewModels;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WowWallPaper;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer;
    private HwndSource? _source;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        _viewModel.NoticeRequested += OnNoticeRequested;
        _viewModel.ExitRequested += OnExitRequested;
        Activated += OnWindowActivated;
    }

    public event EventHandler? HiddenToTray;

    public bool AllowClose
    {
        get => _allowClose;
        set => _allowClose = value;
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnNoticeRequested(object? sender, NoticeEventArgs e) => ShowToast(e.Message, e.Kind);

    private void OnExitRequested(object? sender, EventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ExitApp();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        foreach (var item in _viewModel.Wallpapers)
        {
            item.RefreshMissingState();
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WindowMessageHook);

        try
        {
            var corner = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                helper.Handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref corner,
                sizeof(int));
            var dark = 1;
            NativeMethods.DwmSetWindowAttribute(
                helper.Handle,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref dark,
                sizeof(int));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"DWM window styling unavailable: {ex.Message}");
        }
    }

    private nint WindowMessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_GETMINMAXINFO)
        {
            return 0;
        }

        try
        {
            var info = NativeMethods.MONITORINFO.Create();
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return 0;
            }

            var minMax = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            var work = info.rcWork;
            var screen = info.rcMonitor;
            minMax.ptMaxPosition.X = Math.Abs(work.Left - screen.Left);
            minMax.ptMaxPosition.Y = Math.Abs(work.Top - screen.Top);
            minMax.ptMaxSize.X = work.Width;
            minMax.ptMaxSize.Y = work.Height;
            minMax.ptMaxTrackSize.X = Math.Max(work.Width, (int)MinWidth);
            minMax.ptMaxTrackSize.Y = Math.Max(work.Height, (int)MinHeight);
            Marshal.StructureToPtr(minMax, lParam, true);
            handled = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"WM_GETMINMAXINFO handling failed: {ex.Message}");
        }

        return 0;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is null)
        {
            return;
        }

        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || Application.Current is not App app || app.IsExiting)
        {
            return;
        }

        if (_viewModel.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        e.Cancel = true;
        Dispatcher.BeginInvoke(app.ExitApp);
    }

    private void HideToTray()
    {
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(_viewModel.SearchText))
        {
            _viewModel.SearchText = string.Empty;
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            AddFilesButton_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            _viewModel.NextCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox && _viewModel.CanControlVideo)
        {
            _viewModel.TogglePause();
            e.Handled = true;
        }
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e) =>
        await AddFilesFromDialogAsync(FileTypes.ImportFilter, "添加壁纸");

    private async void AddImagesButton_Click(object sender, RoutedEventArgs e) =>
        await AddFilesFromDialogAsync(FileTypes.ImageFilter, "添加图片壁纸");

    private async void AddVideosButton_Click(object sender, RoutedEventArgs e) =>
        await AddFilesFromDialogAsync(FileTypes.VideoFilter, "添加视频壁纸");

    private async Task AddFilesFromDialogAsync(string filter, string title)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = filter,
            Title = title,
            CheckFileExists = true,
            DereferenceLinks = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.AddFilesAsync(dialog.FileNames);
        }
    }

    private async void ImportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择包含图片或视频的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        string[] files;
        try
        {
            files = await Task.Run(() => Directory
                .EnumerateFiles(dialog.SelectedPath, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
                })
                .Where(FileTypes.IsSupported)
                .Take(5000)
                .ToArray());
        }
        catch (Exception ex)
        {
            ShowToast("读取文件夹失败：" + ex.Message, NoticeKind.Error);
            return;
        }

        if (files.Length == 0)
        {
            ShowToast("该文件夹中没有可用的图片或视频。", NoticeKind.Info);
            return;
        }

        await _viewModel.AddFilesAsync(files);
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SearchText = string.Empty;

    private async void WallpaperCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WallpaperItemViewModel item })
        {
            return;
        }

        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.ApplyWallpaperAsync(item);
    }

    private void ClearLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "确定要清空壁纸库吗？\n\n这会移除所有壁纸记录，如果当前正在使用视频壁纸，还会停止播放并恢复系统壁纸。原始文件不会被删除。",
            "清空壁纸库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.ClearLibrary();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await _viewModel.AddFilesAsync(paths);
        }
    }

    private void ShowToast(string message, NoticeKind kind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastMessage.Text = message;
        var (glyph, brushKey) = kind switch
        {
            NoticeKind.Success => ("\uE73E", "SuccessBrush"),
            NoticeKind.Error => ("\uE783", "DangerBrush"),
            _ => ("\uE946", "AccentBrush")
        };
        ToastIcon.Text = glyph;
        ToastIcon.Foreground = (Brush)FindResource(brushKey);
        ToastBorder.Visibility = Visibility.Visible;
        ToastBorder.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _toastTimer.Stop();
        _toastTimer.Interval = kind == NoticeKind.Error
            ? TimeSpan.FromSeconds(6.5)
            : TimeSpan.FromSeconds(3.6);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        ToastBorder.BeginAnimation(OpacityProperty, animation);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.NoticeRequested -= OnNoticeRequested;
        _viewModel.ExitRequested -= OnExitRequested;
        Activated -= OnWindowActivated;
        _toastTimer.Stop();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        base.OnClosed(e);
    }
}
