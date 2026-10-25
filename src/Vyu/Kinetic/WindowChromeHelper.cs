using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vyu.Kinetic;

/// <summary>Native polish for frameless windows: dark frame, Windows 11 rounded corners, capture exclusion.</summary>
public static class WindowChromeHelper
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public static void ApplyDarkRounded(Window w, uint borderColorBgr = 0x00201410)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);         // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
        int border = unchecked((int)borderColorBgr);
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);        // DWMWA_BORDER_COLOR
    }

    /// <summary>Keeps a window out of screen captures.</summary>
    public static void ExcludeFromCapture(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowDisplayAffinity(hwnd, 0x11);
    }

    /// <summary>Tool window that never steals focus from the game (toasts).</summary>
    public static void MakeNoActivate(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
