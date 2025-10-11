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

            // 부모를 owner로 설정
            SetParent(hwnd, new WindowInteropHelper(owner).Handle);

            // 좌표/크기(DPI 반영)
            var pt = host.TransformToAncestor(owner).Transform(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(owner);
            int x = (int)(pt.X * dpi.DpiScaleX);
            int y = (int)(pt.Y * dpi.DpiScaleY);
            int w = (int)(host.ActualWidth * dpi.DpiScaleX);
            int h = (int)(host.ActualHeight * dpi.DpiScaleY);

            // 테두리 제거
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

            var pt = host.TransformToAncestor(owner).Transform(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(owner);
            int x = (int)(pt.X * dpi.DpiScaleX);
            int y = (int)(pt.Y * dpi.DpiScaleY);
            int w = (int)(host.ActualWidth * dpi.DpiScaleX);
            int h = (int)(host.ActualHeight * dpi.DpiScaleY);

            MoveWindow(hwnd, x, y, w, h, true);
        }
    }
}



