using WowWallPaper.Infrastructure;
using WowWallPaper.Interop;

namespace WowWallPaper.Services;

public sealed class MonitorService
{
    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint handle, nint _, ref NativeMethods.RECT rect, nint _) =>
        {
            var info = NativeMethods.MONITORINFO.Create();
            if (!NativeMethods.GetMonitorInfo(handle, ref info))
            {
                return true;
            }

            var bounds = PixelRect.FromRect(info.rcMonitor);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return true;
            }

            monitors.Add(new MonitorDescriptor(
                handle,
                bounds,
                PixelRect.FromRect(info.rcWork),
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));

            return true;
        }, 0);

        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .ToList();
    }

    public PixelRect GetVirtualBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new PixelRect(x, y, width, height);
    }
}
