using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using WowWallPaper.Infrastructure;
using WowWallPaper.Models;
using WowWallPaper.Services;

namespace WowWallPaper.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly ThumbnailService _thumbnailService;
    private readonly WallpaperController _controller;
    private readonly WallpaperRotationService _rotation;
    private readonly AppSettings _settings;
    private readonly ICollectionView _wallpapersView;
    private readonly List<FilterOptionViewModel> _filterOptions;
    private FilterOptionViewModel _selectedFilter;
    private WallpaperItemViewModel? _currentWallpaper;
    private string _searchText = string.Empty;
    private string _heroStatusText = "尚未设置壁纸";
    private string _heroStatusDetail = "从壁纸库中选择一张图片或视频，开始打造你的桌面。";
    private string _busyText = string.Empty;
    private string? _lastVideoMessage;
    private string? _runtimeWarning;
    private bool _isBusy;
    private bool _isSettingsVisible;
    private bool _isApplying;
    private bool _initialized;
    private bool _disposed;
    private PlaybackState _videoState = PlaybackState.Stopped;

    public MainViewModel(
        SettingsService settingsService,
        StartupService startupService,
        ThumbnailService thumbnailService)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        _thumbnailService = thumbnailService;
        _settings = settingsService.Load();
        _settings.RunAtStartup = _startupService.IsEnabled();
        _controller = new WallpaperController(_settings);
        _controller.PlaybackStateChanged += OnPlaybackStateChanged;

        _rotation = new WallpaperRotationService();
        _rotation.Tick += OnRotationTick;

        Wallpapers = [];
        foreach (var item in _settings.Library.OrderByDescending(i => i.AddedAt))
        {
            Wallpapers.Add(new WallpaperItemViewModel(item));
        }

        _filterOptions =
        [
            new FilterOptionViewModel(WallpaperFilter.All, "全部壁纸", "\uE8FD"),
            new FilterOptionViewModel(WallpaperFilter.Images, "图片", "\uEB9F"),
            new FilterOptionViewModel(WallpaperFilter.Videos, "视频", "\uE714")
        ];
        _selectedFilter = _filterOptions[0];

        _wallpapersView = CollectionViewSource.GetDefaultView(Wallpapers);
        _wallpapersView.Filter = FilterWallpaper;

        var fitOptions = WallpaperFitExtensions.ImageFits
            .Select(f => new FitOption(f, f.ToDisplayName()))
            .ToList();
        ImageFitOptions = fitOptions;
        VideoFitOptions = WallpaperFitExtensions.VideoFits
            .Select(f => new FitOption(f, f.ToDisplayName()))
            .ToList();
        RotationIntervalOptions =
        [
            new RotationIntervalOption(1, "每 1 分钟"),
            new RotationIntervalOption(5, "每 5 分钟"),
            new RotationIntervalOption(10, "每 10 分钟"),
            new RotationIntervalOption(15, "每 15 分钟"),
            new RotationIntervalOption(30, "每 30 分钟"),
            new RotationIntervalOption(60, "每 1 小时"),
            new RotationIntervalOption(120, "每 2 小时"),
            new RotationIntervalOption(360, "每 6 小时"),
            new RotationIntervalOption(1440, "每 24 小时")
        ];

        ApplyCommand = new AsyncRelayCommand(
            async parameter =>
            {
                if (parameter is WallpaperItemViewModel item)
                {
                    await ApplyWallpaperAsync(item);
                }
            });
        NextCommand = new AsyncRelayCommand(NextWallpaperAsync);
        StopCommand = new RelayCommand(StopWallpaper);
        TogglePauseCommand = new RelayCommand(TogglePause);
        RemoveCommand = new RelayCommand(parameter =>
        {
            if (parameter is WallpaperItemViewModel item)
            {
                RemoveWallpaper(item);
            }
        });
        RevealCommand = new RelayCommand(parameter =>
        {
            if (parameter is WallpaperItemViewModel item)
            {
                RevealInExplorer(item);
            }
        });
        ShowSettingsCommand = new RelayCommand(() => IsSettingsVisible = true);
        ShowLibraryCommand = new RelayCommand(() => IsSettingsVisible = false);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        DismissRuntimeWarningCommand = new RelayCommand(() => SetRuntimeWarning(null));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));

        UpdateCounts();
    }

    public event EventHandler<NoticeEventArgs>? NoticeRequested;

    public event EventHandler? TrayStateChanged;

    public event EventHandler? ExitRequested;

    public ObservableCollection<WallpaperItemViewModel> Wallpapers { get; }

    public ICollectionView WallpapersView => _wallpapersView;

    public IReadOnlyList<FilterOptionViewModel> FilterOptions => _filterOptions;

    public IReadOnlyList<FitOption> ImageFitOptions { get; }

    public IReadOnlyList<FitOption> VideoFitOptions { get; }

    public IReadOnlyList<RotationIntervalOption> RotationIntervalOptions { get; }

    public AsyncRelayCommand ApplyCommand { get; }

    public AsyncRelayCommand NextCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    public RelayCommand ShowLibraryCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand DismissRuntimeWarningCommand { get; }

    public RelayCommand ExitCommand { get; }

    public string VersionText => "WowWallPaper 1.0.0";

    public string DataDirectory => AppPaths.DataDirectory;

    public string LibrarySummary => $"{Wallpapers.Count} 个壁纸  ·  {ImageCount} 张图片  ·  {VideoCount} 个视频";

    public string ResultSummary
    {
        get
        {
            if (Wallpapers.Count == 0)
            {
                return "你的壁纸库还是空的";
            }

            return $"{FilteredCount} 个结果";
        }
    }

    public int ImageCount => Wallpapers.Count(i => i.Kind == WallpaperKind.Image);

    public int VideoCount => Wallpapers.Count(i => i.Kind == WallpaperKind.Video);

    public int FilteredCount => _wallpapersView.Cast<object>().Count();

    public bool HasWallpapers => Wallpapers.Count > 0;

    public bool IsLibraryEmpty => Wallpapers.Count == 0;

    public bool IsFilteredEmpty => HasWallpapers && FilteredCount == 0;

    public bool HasCurrentWallpaper => _currentWallpaper is not null;

    public bool IsVideoActive => _currentWallpaper?.Kind == WallpaperKind.Video;

    public bool IsVideoPaused => IsVideoActive && _videoState == PlaybackState.Paused;

    public bool CanControlVideo => IsVideoActive &&
                                    _videoState is PlaybackState.Playing
                                        or PlaybackState.Paused
                                        or PlaybackState.Starting
                                        or PlaybackState.WaitingForDesktop;

    public bool IsVideoFailed => IsVideoActive && _videoState == PlaybackState.Failed;

    public PlaybackState VideoState => _videoState;

    public string PauseButtonGlyph => IsVideoPaused ? "\uE768" : "\uE769";

    public string PauseButtonLabel => IsVideoPaused ? "继续" : "暂停";

    public string HeroBadgeText => IsVideoActive ? "NOW PLAYING" : "ON DISPLAY";

    public WallpaperItemViewModel? CurrentWallpaper
    {
        get => _currentWallpaper;
        private set
        {
            var previous = _currentWallpaper;
            if (!SetProperty(ref _currentWallpaper, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.IsActive = false;
            }

            if (value is not null)
            {
                value.IsActive = true;
                value.RefreshMissingState();
            }

            OnPropertyChanged(nameof(HasCurrentWallpaper));
            OnPropertyChanged(nameof(IsVideoActive));
            OnPropertyChanged(nameof(IsVideoPaused));
            OnPropertyChanged(nameof(CanControlVideo));
            OnPropertyChanged(nameof(IsVideoFailed));
            OnPropertyChanged(nameof(PauseButtonGlyph));
            OnPropertyChanged(nameof(PauseButtonLabel));
            OnPropertyChanged(nameof(HeroBadgeText));
            UpdateHeroStatus();
            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
            {
                return;
            }

            RefreshFilter();
        }
    }

    public FilterOptionViewModel SelectedFilterOption
    {
        get => _selectedFilter;
        set
        {
            if (value is null || ReferenceEquals(_selectedFilter, value))
            {
                return;
            }

            _selectedFilter = value;
            OnPropertyChanged();
            if (IsSettingsVisible)
            {
                IsSettingsVisible = false;
            }

            RefreshFilter();
        }
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => SetProperty(ref _isSettingsVisible, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string HeroStatusText
    {
        get => _heroStatusText;
        private set => SetProperty(ref _heroStatusText, value);
    }

    public string HeroStatusDetail
    {
        get => _heroStatusDetail;
        private set => SetProperty(ref _heroStatusDetail, value);
    }

    public string? RuntimeWarning => _runtimeWarning;

    public bool HasRuntimeWarning => !string.IsNullOrWhiteSpace(_runtimeWarning);

    public WallpaperFit ImageFit
    {
        get => _settings.ImageFit;
        set
        {
            if (_settings.ImageFit == value)
            {
                return;
            }

            var previous = _settings.ImageFit;
            _settings.ImageFit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageFitSummary));
            SaveSettings();
            try
            {
                _controller.SetImageFit(value);
            }
            catch (Exception ex)
            {
                _settings.ImageFit = previous;
                OnPropertyChanged();
                PostNotice("应用图片缩放方式失败：" + ex.Message, NoticeKind.Error);
            }

            UpdateHeroStatus();
        }
    }

    public WallpaperFit VideoFit
    {
        get => _settings.VideoFit;
        set
        {
            if (_settings.VideoFit == value)
            {
                return;
            }

            _settings.VideoFit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VideoFitSummary));
            SaveSettings();
            _controller.SetVideoFit(value);
            UpdateHeroStatus();
        }
    }

    public bool MuteVideo
    {
        get => _settings.MuteVideo;
        set
        {
            if (_settings.MuteVideo == value)
            {
                return;
            }

            _settings.MuteVideo = value;
            OnPropertyChanged();
            SaveSettings();
            _controller.SetMuted(value);
            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool PauseOnFullscreen
    {
        get => _settings.PauseOnFullscreen;
        set
        {
            if (_settings.PauseOnFullscreen == value)
            {
                return;
            }

            _settings.PauseOnFullscreen = value;
            OnPropertyChanged();
            SaveSettings();
            _controller.SetPauseOnFullscreen(value);
        }
    }

    public bool AutoRotate
    {
        get => _settings.AutoRotate;
        set
        {
            if (_settings.AutoRotate == value)
            {
                return;
            }

            _settings.AutoRotate = value;
            OnPropertyChanged();
            SaveSettings();
            UpdateRotation();
        }
    }

    public int RotationIntervalMinutes
    {
        get => _settings.RotationIntervalMinutes;
        set
        {
            var clamped = Math.Clamp(value, 1, 1440);
            if (_settings.RotationIntervalMinutes == clamped)
            {
                return;
            }

            _settings.RotationIntervalMinutes = clamped;
            OnPropertyChanged();
            SaveSettings();
            UpdateRotation();
        }
    }

    public bool Shuffle
    {
        get => _settings.Shuffle;
        set
        {
            if (_settings.Shuffle == value)
            {
                return;
            }

            _settings.Shuffle = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool RunAtStartup
    {
        get => _settings.RunAtStartup;
        set
        {
            if (_settings.RunAtStartup == value)
            {
                return;
            }

            if (!_startupService.TrySetEnabled(value, out var error))
            {
                PostNotice(error ?? "开机启动设置失败。", NoticeKind.Error);
                return;
            }

            _settings.RunAtStartup = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value)
            {
                return;
            }

            _settings.CloseToTray = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string RotationStatusText => AutoRotate
        ? $"已开启 · 每 {FormatInterval(RotationIntervalMinutes)}轮播"
        : "轮播已关闭";

    public string ImageFitSummary => $"图片：{ImageFit.ToDisplayName()}";

    public string VideoFitSummary => $"视频：{VideoFit.ToDisplayName()}";

    public async Task InitializeAsync(bool applySavedWallpaper = true)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        try
        {
            _controller.Initialize();
        }
        catch (Exception ex)
        {
            AppLog.Error("Wallpaper controller initialization failed.", ex);
            PostNotice("桌面壁纸接口初始化失败：" + ex.Message, NoticeKind.Error);
        }

        foreach (var item in Wallpapers)
        {
            _ = item.LoadThumbnailAsync(_thumbnailService);
        }

        UpdateCounts();
        UpdateRotation();

        if (!string.IsNullOrWhiteSpace(_settings.CurrentWallpaperPath))
        {
            var saved = Wallpapers.FirstOrDefault(w =>
                string.Equals(w.Path, _settings.CurrentWallpaperPath, StringComparison.OrdinalIgnoreCase));

            if (saved is null)
            {
                _settings.CurrentWallpaperPath = null;
                SaveSettings();
            }
            else if (saved.IsMissing)
            {
                _settings.CurrentWallpaperPath = null;
                SaveSettings();
                HeroStatusText = "上次的壁纸文件已丢失";
                HeroStatusDetail = "文件可能被移动或删除，请重新选择一张壁纸。";
                PostNotice("上次使用的壁纸文件已丢失，请重新选择。", NoticeKind.Error);
            }
            else
            {
                if (!applySavedWallpaper)
                {
                    CurrentWallpaper = saved;
                    _videoState = PlaybackState.Stopped;
                    OnPropertyChanged(nameof(VideoState));
                    UpdateHeroStatus();
                }
                else
                {
                    await ApplyWallpaperAsync(saved, showNotice: false, isRestore: true);
                }
            }
        }

        await Task.CompletedTask;
    }

    public async Task<int> AddFilesAsync(IEnumerable<string> paths, bool showNotice = true)
    {
        if (_disposed)
        {
            return 0;
        }

        var existing = new HashSet<string>(Wallpapers.Select(w => w.Path), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<WallpaperItem>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(raw);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(fullPath) || !FileTypes.IsSupported(fullPath) || !existing.Add(fullPath))
            {
                continue;
            }

            var kind = FileTypes.GetKind(fullPath);
            if (kind is null)
            {
                continue;
            }

            candidates.Add(WallpaperItem.Create(fullPath, kind.Value));
        }

        if (candidates.Count == 0)
        {
            if (showNotice)
            {
                PostNotice("没有发现新的图片或视频。文件可能已在壁纸库中，或格式不受支持。", NoticeKind.Info);
            }

            return 0;
        }

        foreach (var item in candidates)
        {
            var viewModel = new WallpaperItemViewModel(item);
            Wallpapers.Insert(0, viewModel);
            _settings.Library.Add(item);
            _ = viewModel.LoadThumbnailAsync(_thumbnailService);
        }

        SaveSettings();
        UpdateCounts();

        if (CurrentWallpaper is null)
        {
            await ApplyWallpaperAsync(Wallpapers[0], showNotice: false);
        }

        if (showNotice)
        {
            PostNotice($"已添加 {candidates.Count} 个壁纸。", NoticeKind.Success);
        }

        return candidates.Count;
    }

    public void RemoveWallpaper(WallpaperItemViewModel item)
    {
        if (!Wallpapers.Contains(item))
        {
            return;
        }

        if (ReferenceEquals(CurrentWallpaper, item))
        {
            StopWallpaper();
        }

        Wallpapers.Remove(item);
        _settings.Library.RemoveAll(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        _thumbnailService.Invalidate(item.Path);
        SaveSettings();
        UpdateCounts();
        PostNotice($"已从壁纸库移除“{item.Title}”。", NoticeKind.Info);
    }

    public void ClearLibrary()
    {
        if (CurrentWallpaper is not null)
        {
            StopWallpaper();
        }

        Wallpapers.Clear();
        _settings.Library.Clear();
        _settings.CurrentWallpaperPath = null;
        SaveSettings();
        UpdateCounts();
        HeroStatusText = "尚未设置壁纸";
        HeroStatusDetail = "从壁纸库中选择一张图片或视频，开始打造你的桌面。";
        PostNotice("壁纸库已清空。", NoticeKind.Info);
    }

    public async Task ApplyWallpaperAsync(
        WallpaperItemViewModel item,
        bool showNotice = true,
        bool isRestore = false)
    {
        if (_disposed || _isApplying)
        {
            return;
        }

        item.RefreshMissingState();
        if (item.IsMissing)
        {
            PostNotice($"“{item.Title}”的文件已丢失，请重新添加。", NoticeKind.Error);
            return;
        }

        if (!File.Exists(item.Path))
        {
            item.RefreshMissingState();
            PostNotice($"“{item.Title}”的文件已丢失，请重新添加。", NoticeKind.Error);
            return;
        }

        _isApplying = true;
        IsBusy = true;
        BusyText = item.IsVideo ? "正在启动动态壁纸…" : "正在应用壁纸…";
        try
        {
            var result = _controller.Apply(item.Model);
            CurrentWallpaper = item;
            _settings.CurrentWallpaperPath = item.Path;
            _videoState = result.State;
            OnPropertyChanged(nameof(VideoState));
            OnPropertyChanged(nameof(IsVideoPaused));
            OnPropertyChanged(nameof(CanControlVideo));
            OnPropertyChanged(nameof(IsVideoFailed));
            UpdateHeroStatus();
            SaveSettings();
            _rotation.Reset();
            TrayStateChanged?.Invoke(this, EventArgs.Empty);

            if (showNotice)
            {
                if (item.IsVideo)
                {
                    var message = result.State == PlaybackState.WaitingForDesktop
                        ? "正在等待 Windows 桌面层，动态壁纸会自动启动。"
                        : $"“{item.Title}”已设为动态壁纸。";
                    PostNotice(message, result.State == PlaybackState.WaitingForDesktop
                        ? NoticeKind.Info
                        : NoticeKind.Success);
                }
                else
                {
                    PostNotice($"“{item.Title}”已设为静态壁纸。", NoticeKind.Success);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not apply wallpaper.", ex);
            if (showNotice || isRestore)
            {
                PostNotice(DescribeWallpaperError(ex), NoticeKind.Error);
            }

            if (isRestore)
            {
                HeroStatusText = "无法恢复上次的壁纸";
                HeroStatusDetail = ex.Message;
            }

            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isApplying = false;
            IsBusy = false;
            BusyText = string.Empty;
        }

        await Task.CompletedTask;
    }

    public void StopWallpaper()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _controller.StopWallpaper();
            CurrentWallpaper = null;
            _settings.CurrentWallpaperPath = null;
            _videoState = PlaybackState.Stopped;
            OnPropertyChanged(nameof(VideoState));
            SaveSettings();
            HeroStatusText = "已恢复系统壁纸";
            HeroStatusDetail = "选择一个壁纸，继续装饰你的桌面。";
            TrayStateChanged?.Invoke(this, EventArgs.Empty);
            PostNotice("已停止壁纸并恢复原来的 Windows 壁纸。", NoticeKind.Info);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not stop wallpaper.", ex);
            PostNotice("停止壁纸失败：" + ex.Message, NoticeKind.Error);
        }
    }

    public void TogglePause()
    {
        if (!CanControlVideo)
        {
            return;
        }

        _controller.TogglePaused();
    }

    public async Task NextWallpaperAsync()
    {
        await NextWallpaperAsync(quiet: false);
    }

    public void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppPaths.DataDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            PostNotice("无法打开数据目录：" + ex.Message, NoticeKind.Error);
        }
    }

    public void NotifySystem(string message, NoticeKind kind = NoticeKind.Info) =>
        PostNotice(message, kind);

    public bool TestDesktopLayer()
    {
        try
        {
            return _controller.ShowDesktopLayerTest();
        }
        catch (Exception ex)
        {
            AppLog.Error("Desktop layer test failed.", ex);
            return false;
        }
    }

    public void SetRuntimeWarning(string? message)
    {
        _runtimeWarning = message;
        OnPropertyChanged(nameof(RuntimeWarning));
        OnPropertyChanged(nameof(HasRuntimeWarning));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.PlaybackStateChanged -= OnPlaybackStateChanged;
        _rotation.Tick -= OnRotationTick;
        _rotation.Dispose();
        SaveSettings();
        _controller.Dispose();
    }

    private async Task NextWallpaperAsync(bool quiet)
    {
        if (_disposed || _isApplying)
        {
            return;
        }

        var candidates = Wallpapers.Where(w => !w.IsMissing && File.Exists(w.Path)).ToList();
        if (candidates.Count == 0)
        {
            if (!quiet)
            {
                PostNotice("壁纸库中没有可用的壁纸。", NoticeKind.Info);
            }

            return;
        }

        WallpaperItemViewModel next;
        if (candidates.Count == 1)
        {
            next = candidates[0];
        }
        else if (Shuffle)
        {
            var options = candidates
                .Where(c => !ReferenceEquals(c, CurrentWallpaper))
                .ToList();
            next = options[Random.Shared.Next(options.Count)];
        }
        else
        {
            var currentIndex = CurrentWallpaper is null ? -1 : candidates.IndexOf(CurrentWallpaper);
            next = candidates[(currentIndex + 1) % candidates.Count];
        }

        await ApplyWallpaperAsync(next, showNotice: !quiet);
    }

    private void OnRotationTick(object? sender, EventArgs e)
    {
        if (_isApplying)
        {
            return;
        }

        _ = NextWallpaperAsync(quiet: true);
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        _videoState = e.State;
        _lastVideoMessage = e.Message;
        OnPropertyChanged(nameof(VideoState));
        OnPropertyChanged(nameof(IsVideoPaused));
        OnPropertyChanged(nameof(CanControlVideo));
        OnPropertyChanged(nameof(IsVideoFailed));
        OnPropertyChanged(nameof(PauseButtonGlyph));
        OnPropertyChanged(nameof(PauseButtonLabel));
        UpdateHeroStatus();
        TrayStateChanged?.Invoke(this, EventArgs.Empty);

        if (e.State == PlaybackState.Failed && IsVideoActive)
        {
            PostNotice(e.Message ?? "动态壁纸播放失败。", NoticeKind.Error);
        }
    }

    private void UpdateHeroStatus()
    {
        if (_currentWallpaper is null)
        {
            HeroStatusText = "尚未设置壁纸";
            HeroStatusDetail = "从壁纸库中选择一张图片或视频，开始打造你的桌面。";
            return;
        }

        if (_currentWallpaper.Kind == WallpaperKind.Image)
        {
            HeroStatusText = "静态壁纸已应用";
            HeroStatusDetail = $"{_currentWallpaper.MetaText} · {ImageFit.ToDisplayName()}";
            return;
        }

        HeroStatusDetail = $"{_currentWallpaper.MetaText} · {VideoFit.ToDisplayName()}";
        HeroStatusText = _videoState switch
        {
            PlaybackState.Playing => "动态壁纸播放中",
            PlaybackState.Paused => _lastVideoMessage ?? "动态壁纸已暂停",
            PlaybackState.Starting => "正在加载动态壁纸…",
            PlaybackState.WaitingForDesktop => "正在连接 Windows 桌面层…",
            PlaybackState.Failed => _lastVideoMessage ?? "动态壁纸启动失败",
            _ => "动态壁纸已停止"
        };
    }

    private void RefreshFilter()
    {
        _wallpapersView.Refresh();
        UpdateCounts();
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    private bool FilterWallpaper(object obj)
    {
        if (obj is not WallpaperItemViewModel item)
        {
            return false;
        }

        var matchesKind = _selectedFilter.Filter switch
        {
            WallpaperFilter.Images => item.Kind == WallpaperKind.Image,
            WallpaperFilter.Videos => item.Kind == WallpaperKind.Video,
            _ => true
        };

        return matchesKind && item.Matches(SearchText);
    }

    private void UpdateCounts()
    {
        var all = Wallpapers.Count;
        var images = Wallpapers.Count(w => w.Kind == WallpaperKind.Image);
        var videos = Wallpapers.Count(w => w.Kind == WallpaperKind.Video);
        _filterOptions[0].Count = all;
        _filterOptions[1].Count = images;
        _filterOptions[2].Count = videos;

        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(VideoCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(HasWallpapers));
        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateRotation()
    {
        _rotation.Configure(AutoRotate, RotationIntervalMinutes);
        OnPropertyChanged(nameof(RotationStatusText));
    }

    private void SaveSettings()
    {
        _settingsService.Save(_settings);
    }

    private void RevealInExplorer(WallpaperItemViewModel item)
    {
        try
        {
            if (!File.Exists(item.Path))
            {
                PostNotice("文件不存在，无法在资源管理器中显示。", NoticeKind.Error);
                item.RefreshMissingState();
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            PostNotice("无法打开资源管理器：" + ex.Message, NoticeKind.Error);
        }
    }

    private void PostNotice(string message, NoticeKind kind) =>
        NoticeRequested?.Invoke(this, new NoticeEventArgs(message, kind));

    private static string DescribeWallpaperError(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        if (current is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == 5 ||
            current is UnauthorizedAccessException ||
            current is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80070005) })
        {
            return "Windows 拒绝修改桌面壁纸。请从资源管理器直接运行 WowWallPaper.exe，" +
                   "不要以管理员身份或受限沙箱运行。";
        }

        return "应用壁纸失败：" + exception.Message;
    }

    private static string FormatInterval(int minutes)
    {
        if (minutes < 60)
        {
            return $"{minutes} 分钟";
        }

        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 24 ? "24 小时" : $"{hours} 小时";
        }

        return $"{minutes} 分钟";
    }
}
