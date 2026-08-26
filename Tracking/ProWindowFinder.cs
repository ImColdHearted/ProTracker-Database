using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Collections.Generic;
using System.Linq;

namespace Foot_Tracker.Tracking
{
    // Windows-only (Win32 user32.dll P/Invoke). Used by ScreenCapture.cs and
    // Tracking/Capture/WindowsWindowCaptureService.cs.
    [SupportedOSPlatform("windows")]
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

        public sealed class ProClientInfo
        {
            public IntPtr Handle { get; init; }

            public int ProcessId { get; init; }

            public string DisplayName { get; init; } =
                string.Empty;

            public override string ToString()
            {
                return DisplayName;
            }
        }

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

        public static List<ProClientInfo> FindAllProWindows()
        {
            List<ProClientInfo> clients =
                new List<ProClientInfo>();

            Process[] processes =
                Process.GetProcessesByName("PROClient");

            foreach (Process process in processes)
            {
                try
                {
                    IntPtr handle =
                        process.MainWindowHandle;

                    if (handle == IntPtr.Zero)
                        continue;

                    if (!IsWindowVisible(handle))
                        continue;

                    if (IsIconic(handle))
                        continue;

                    clients.Add(
                        new ProClientInfo
                        {
                            Handle = handle,
                            ProcessId = process.Id,
                            DisplayName =
                                $"PRO Client - PID {process.Id}"
                        }
                    );
                }
                catch
                {
                    // Process may have closed while scanning.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return clients
                .OrderBy(c => c.ProcessId)
                .ToList();
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