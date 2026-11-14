using WowWallPaper.Infrastructure;
using WowWallPaper.Models;

namespace WowWallPaper.ViewModels;

public sealed class FilterOptionViewModel(WallpaperFilter filter, string label, string glyph) : ObservableObject
{
    private int _count;

    public WallpaperFilter Filter { get; } = filter;

    public string Label { get; } = label;

    public string Glyph { get; } = glyph;

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}

public sealed record FitOption(WallpaperFit Fit, string Label);

public sealed record NoticeEventArgs(string Message, NoticeKind Kind);
