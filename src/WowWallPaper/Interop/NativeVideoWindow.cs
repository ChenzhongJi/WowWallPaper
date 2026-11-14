using System.Runtime.InteropServices;

namespace WowWallPaper.Interop;

/// <summary>
/// A plain native child window used as a LibVLC video output target. It has no
/// WPF content and paints only a black background until VLC attaches.
/// </summary>
internal static class NativeVideoWindow
{
    private const string ClassName = "WowWallPaperVideoSurface";
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const int CsHRedraw = 0x0002;
    private const int CsVRedraw = 0x0001;

    private static readonly WindowProc Proc = WindowProcImpl;
    private static readonly object Gate = new();
    private static bool _registered;
    private static nint _blackBrush;

    public static nint Create(nint parent, int x, int y, int width, int height, bool layered)
    {
        EnsureRegistered();
        var instance = GetModuleHandle(null);
        var hwnd = CreateWindowEx(
            WsExNoActivate | WsExToolWindow | (layered ? WsExLayered : 0),
            ClassName,
            "WowWallPaper video surface",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            parent,
            0,
            instance,
            0);

        if (hwnd != 0 && layered)
        {
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        }

        return hwnd;
    }

    public static void Destroy(nint hwnd)
    {
        if (hwnd != 0)
        {
            DestroyWindow(hwnd);
        }
    }

    private static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            var instance = GetModuleHandle(null);
            _blackBrush = CreateSolidBrush(0x00000000); // COLORREF black
            var windowClass = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),
                style = CsHRedraw | CsVRedraw,
                lpfnWndProc = Proc,
                hInstance = instance,
                hbrBackground = _blackBrush,
                lpszClassName = ClassName
            };

            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException("Unable to register native video window class.");
            }

            _registered = true;
        }
    }

    private static nint WindowProcImpl(nint hwnd, uint message, nint wParam, nint lParam) =>
        DefWindowProc(hwnd, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public int style;
        public WindowProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int color);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
