namespace WowWallPaper.Models;

public enum PlaybackState
{
    Stopped,
    WaitingForDesktop,
    Starting,
    Playing,
    Paused,
    Failed
}

public enum NoticeKind
{
    Info,
    Success,
    Error
}

public sealed record RotationIntervalOption(int Minutes, string Label);
