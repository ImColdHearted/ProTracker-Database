using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Foot_Tracker.Tracking
{
    public static class ProWindowFinder
    {
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(
            IntPtr hWnd,
            out RECT lpRect
        );

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        public static IntPtr FindProWindow()
        {
            Process[] processes =
                Process.GetProcessesByName("PROClient");

            foreach (Process process in processes)
            {
                try
                {
                    IntPtr handle = process.MainWindowHandle;

                    if (handle == IntPtr.Zero)
                        continue;

                    if (!IsWindowVisible(handle))
                        continue;

                    if (IsIconic(handle))
                        continue;

                    return handle;
                }
                catch
                {
                }
            }

            return IntPtr.Zero;
        }

        public static bool TryGetWindowBounds(
            IntPtr handle,
            out Rectangle bounds)
        {
            bounds = Rectangle.Empty;

            if (handle == IntPtr.Zero)
                return false;

            if (!GetWindowRect(handle, out RECT rect))
                return false;

            bounds = new Rectangle(
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height
            );

            return true;
        }
    }
}