using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using Point = System.Windows.Point;

namespace MultiQuest_Management
{
    public static class ScrcpyEmbedder
    {
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;
        const int SW_SHOW = 5;

        public static void Attach(Process proc, Border host, Window owner)
        {
            if (proc == null || host == null || owner == null) return;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            SetParent(hwnd, new WindowInteropHelper(owner).Handle);

            // 반드시 UI 스레드에서 좌표 계산
            Point pt = default;
            double dpiX = 1, dpiY = 1;
            owner.Dispatcher.Invoke(() =>
            {
                pt = host.TransformToAncestor(owner).Transform(new Point(0, 0));
                var dpi = VisualTreeHelper.GetDpi(owner);
                dpiX = dpi.DpiScaleX;
                dpiY = dpi.DpiScaleY;
            });

            int x = (int)(pt.X * dpiX);
            int y = (int)(pt.Y * dpiY);
            int w = (int)(host.ActualWidth * dpiX);
            int h = (int)(host.ActualHeight * dpiY);

            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            SetWindowLong(hwnd, GWL_STYLE, style);

            MoveWindow(hwnd, x, y, w, h, true);
            ShowWindow(hwnd, SW_SHOW);
        }

        public static void Adjust(Process proc, Border host, Window owner)
        {
            if (proc == null || host == null || owner == null || proc.HasExited) return;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            Point pt = default;
            double dpiX = 1, dpiY = 1;
            owner.Dispatcher.Invoke(() =>
            {
                pt = host.TransformToAncestor(owner).Transform(new Point(0, 0));
                var dpi = VisualTreeHelper.GetDpi(owner);
                dpiX = dpi.DpiScaleX;
                dpiY = dpi.DpiScaleY;
            });

            int x = (int)(pt.X * dpiX);
            int y = (int)(pt.Y * dpiY);
            int w = (int)(host.ActualWidth * dpiX);
            int h = (int)(host.ActualHeight * dpiY);

            MoveWindow(hwnd, x, y, w, h, true);
        }
    }
}



