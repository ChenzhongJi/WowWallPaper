using WowWallPaper.Interop;

namespace WowWallPaper.Infrastructure;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    public static PixelRect FromRect(NativeMethods.RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    public override string ToString() => $"{Width}×{Height} @ {X},{Y}";
}

public sealed record MonitorDescriptor(nint Handle, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);
