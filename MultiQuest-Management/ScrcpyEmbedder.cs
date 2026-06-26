using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;

namespace MultiQuest_Management
{
    /// <summary>
    /// Scrcpy embedding stub - all methods are no-ops
    /// Scrcpy functionality has been removed, using RTSP only
    /// </summary>
    public static class ScrcpyEmbedder
    {
        public static void HideAll(IEnumerable<Process> processes) { }

        public static void HideWindow(IntPtr handle) { }

        public static void ShowAll(IEnumerable<Process> processes) { }

        public static void Adjust(Process process, FrameworkElement host, Window parent) { }

        public static void Attach(Process process, FrameworkElement host, Window parent) { }
    }
}
