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
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        /// <summary>Win32 창을 즐시 숨기다. Kill 전에 호출하면 임베드 창이 화면에 남는 현상을 막는다.</summary>
        public static void HideWindow(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, SW_HIDE);
        }

        /// <summary>살아있는 scrcpy 프로세스 전체를 숨긴다 (다른 패널로 전환 시 호출).</summary>
        public static void HideAll(IEnumerable<Process> procs)
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p == null || p.HasExited) continue;
                    var hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
                }
                catch { }
            }
        }

        /// <summary>살아있는 scrcpy 프로세스 전체를 다시 표시한다 (Meta Device 패널로 복귀 시 호출).</summary>
        public static void ShowAll(IEnumerable<Process> procs)
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p == null || p.HasExited) continue;
                    var hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_SHOW);
                }
                catch { }
            }
        }

        public static void Attach(Process proc, Border host, Window owner)
        {
            if (proc == null || host == null || owner == null) return;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            var ownerHwnd = new WindowInteropHelper(owner).Handle;
            SetParent(hwnd, ownerHwnd);

            // 스타일 제거 (타이틀바·테두리 없애기)
            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            SetWindowLong(hwnd, GWL_STYLE, style);

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

            MoveWindow(hwnd, x, y, w, h, true);
            ShowWindow(hwnd, SW_SHOW);
        }

        public static void Adjust(Process proc, Border host, Window owner)
        {
            if (proc == null || host == null || owner == null || proc.HasExited) return;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            void DoAdjust()
            {
                if (proc.HasExited) return;
                var hwnd2 = proc.MainWindowHandle;
                if (hwnd2 == IntPtr.Zero) return;
                try
                {
                    var pt  = host.TransformToAncestor(owner).Transform(new Point(0, 0));
                    var dpi = VisualTreeHelper.GetDpi(owner);
                    int x = (int)(pt.X * dpi.DpiScaleX);
                    int y = (int)(pt.Y * dpi.DpiScaleY);
                    int w = (int)(host.ActualWidth  * dpi.DpiScaleX);
                    int h = (int)(host.ActualHeight * dpi.DpiScaleY);

                    // bRepaint=false: 위치/크기를 바꿀 때만 OS가 재렌더링 → 깜빡임 방지
                    // 좌표 비교를 생략하고 항상 호출: GetWindowRect는 스크린 좌표라 부모 기준 좌표와 비교 불가
                    MoveWindow(hwnd2, x, y, w, h, false);
                }
                catch { }
            }

            if (owner.Dispatcher.CheckAccess())
                DoAdjust();
            else
                owner.Dispatcher.BeginInvoke(DoAdjust, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
}



