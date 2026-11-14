using System.Runtime.InteropServices;
using WowWallPaper.Infrastructure;

namespace WowWallPaper.Interop;

public enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5
}

[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] out string? wallpaper);

    void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string? monitorID);

    void GetMonitorDevicePathCount(out uint count);

    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out NativeMethods.RECT displayRect);

    void SetBackgroundColor(uint color);

    void GetBackgroundColor(out uint color);

    void SetPosition(DesktopWallpaperPosition position);

    void GetPosition(out DesktopWallpaperPosition position);
}

internal static class DesktopWallpaperCom
{
    public static readonly Guid Clsid = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    public static IDesktopWallpaper? TryCreate()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(Clsid);
            if (type is null)
            {
                return null;
            }

            return Activator.CreateInstance(type) as IDesktopWallpaper;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Desktop wallpaper COM unavailable: {ex.Message}");
            return null;
        }
    }
}
