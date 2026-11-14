using System.Windows.Media;
using WowWallPaper.Infrastructure;
using WowWallPaper.Models;
using WowWallPaper.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace WowWallPaper.ViewModels;

public sealed class WallpaperItemViewModel : ObservableObject
{
    private readonly HashSet<string> _thumbnailAttempted = new(StringComparer.Ordinal);
    private ImageSource? _thumbnail;
    private bool _isActive;
    private bool _isMissing;
    private bool _isThumbnailLoading;

    public WallpaperItemViewModel(WallpaperItem model)
    {
        Model = model;
        _isMissing = !File.Exists(model.Path);
        PlaceholderBrush = CreatePlaceholderBrush(model.Path);
    }

    public WallpaperItem Model { get; }

    public string Path => Model.Path;

    public WallpaperKind Kind => Model.Kind;

    public bool IsVideo => Kind == WallpaperKind.Video;

    public string Title
    {
        get
        {
            var title = System.IO.Path.GetFileNameWithoutExtension(Model.Path);
            return string.IsNullOrWhiteSpace(title) ? "未命名壁纸" : title;
        }
    }

    public string FileName => System.IO.Path.GetFileName(Model.Path);

    public string Directory => System.IO.Path.GetDirectoryName(Model.Path) ?? string.Empty;

    public string KindLabel => FileTypes.KindLabel(Kind);

    public string ExtensionLabel => FileTypes.ExtensionLabel(Model.Path);

    public string MetaText => IsMissing ? "文件已丢失" : $"{KindLabel} · {ExtensionLabel} · {FileSizeLabel}";

    public string FileSizeLabel
    {
        get
        {
            try
            {
                var bytes = new FileInfo(Model.Path).Length;
                return bytes switch
                {
                    >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
                    >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
                    >= 1024 => $"{bytes / 1024d:0} KB",
                    _ => $"{bytes} B"
                };
            }
            catch
            {
                return "未知大小";
            }
        }
    }

    public string AddedLabel
    {
        get
        {
            var local = Model.AddedAt.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            if (local.Date == today)
            {
                return $"今天 {local:HH:mm}";
            }

            if (local.Date == today.AddDays(-1))
            {
                return $"昨天 {local:HH:mm}";
            }

            return local.Year == DateTimeOffset.Now.Year
                ? local.ToString("M月d日")
                : local.ToString("yyyy年M月d日");
        }
    }

    public Brush PlaceholderBrush { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool IsMissing
    {
        get => _isMissing;
        private set
        {
            if (!SetProperty(ref _isMissing, value))
            {
                return;
            }

            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(FileSizeLabel));
        }
    }

    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        private set => SetProperty(ref _isThumbnailLoading, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || Directory.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    public void RefreshMissingState()
    {
        IsMissing = !File.Exists(Model.Path);
    }

    public async Task LoadThumbnailAsync(ThumbnailService service)
    {
        if (IsMissing || _thumbnail is not null || !_thumbnailAttempted.Add(Path))
        {
            return;
        }

        IsThumbnailLoading = true;
        try
        {
            var thumbnail = await service.GetThumbnailAsync(Path, Kind).ConfigureAwait(true);
            if (thumbnail is not null)
            {
                Thumbnail = thumbnail;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not load thumbnail for '{Path}': {ex.Message}");
        }
        finally
        {
            IsThumbnailLoading = false;
        }
    }

    private static Brush CreatePlaceholderBrush(string path)
    {
        var hash = 17;
        foreach (var c in path)
        {
            hash = unchecked(hash * 31 + c);
        }

        var hue = Math.Abs(hash % 360);
        var from = HslToColor(hue, 0.55, 0.22);
        var to = HslToColor((hue + 48) % 360, 0.60, 0.38);
        var brush = new LinearGradientBrush(from, to, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color HslToColor(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
