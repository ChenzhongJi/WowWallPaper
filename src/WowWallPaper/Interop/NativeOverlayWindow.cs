using System.Runtime.InteropServices;
using System.Text;

namespace WowWallPaper.Interop;

/// <summary>
/// A minimal native GDI child window used to verify whether plain Win32 child
/// windows are composited by the current desktop shell. Unlike WPF HwndSource,
/// this uses the classic window manager painting path.
/// </summary>
internal static class NativeOverlayWindow
{
    private const string ClassName = "WowWallPaperNativeLayerTest";
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int CsHRedraw = 0x0002;
    private const int CsVRedraw = 0x0001;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint DtCenter = 0x00000001;
    private const uint DtVCenter = 0x00000004;
    private const uint DtSingleLine = 0x00000020;
    private const int IdcArrow = 32512;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTop = 0;
    private static readonly nint HwndBottom = 1;

    private static readonly WindowProc Proc = WindowProcImpl;
    private static readonly object Gate = new();
    private static bool _registered;
    private static nint _backgroundBrush;

    public static nint Create(nint parent, int x, int y, int width, int height, bool topMost)
    {
        try
        {
            EnsureRegistered();
            var instance = GetModuleHandle(null);
            var hwnd = CreateWindowEx(
                WsExNoActivate | WsExToolWindow,
                ClassName,
                "WowWallPaper native layer test",
                WsChild | WsVisible | WsClipSiblings,
                x,
                y,
                Math.Max(1, width),
                Math.Max(1, height),
                parent,
                0,
                instance,
                0);
            if (hwnd == 0)
            {
                return 0;
            }

            SetWindowPos(
                hwnd,
                topMost ? HwndTop : HwndBottom,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
            InvalidateRect(hwnd, 0, true);
            UpdateWindow(hwnd);
            return hwnd;
        }
        catch
        {
            return 0;
        }
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
            _backgroundBrush = CreateSolidBrush(0x0000FF00); // COLORREF green
            var wndClass = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),
                style = CsHRedraw | CsVRedraw,
                lpfnWndProc = Proc,
                hInstance = instance,
                hCursor = LoadCursor(0, IdcArrow),
                hbrBackground = _backgroundBrush,
                lpszClassName = ClassName
            };

            if (RegisterClassEx(ref wndClass) == 0)
            {
                throw new InvalidOperationException("Unable to register native overlay window class.");
            }

            _registered = true;
        }
    }

    private static nint WindowProcImpl(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmEraseBkgnd)
        {
            var hdc = wParam;
            var rect = GetClientRectStruct(hwnd);
            FillRect(hdc, ref rect, _backgroundBrush);
            return 1;
        }

        if (message == WmPaint)
        {
            var paint = new PaintStruct();
            var hdc = BeginPaint(hwnd, ref paint);
            try
            {
                var rect = GetClientRectStruct(hwnd);
                SetBkMode(hdc, 1); // TRANSPARENT
                SetTextColor(hdc, 0x00000000); // black text
                var text = "NATIVE TOP LAYER TEST - 原生子窗口测试";
                DrawText(hdc, text, -1, ref rect, DtCenter | DtVCenter | DtSingleLine);
            }
            finally
            {
                EndPaint(hwnd, ref paint);
            }

            return 0;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static RECT GetClientRectStruct(nint hwnd)
    {
        GetClientRect(hwnd, out var rect);
        return rect;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int color);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillRect(nint hdc, ref RECT rect, nint brush);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(nint hdc, int color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(nint hdc, string text, int length, ref RECT rect, uint format);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
