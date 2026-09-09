using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Collections.Generic;
using System.Linq;
using Foot_Tracker.Services;

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

        // §163: for the enumeration fallback below.
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd,
            out uint processId);

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

            // §163: MainWindowHandle came up empty for every process - see
            // FindAllProWindows for why that happens and what this does.
            return FindAllProWindows()
                .Select(c => c.Handle)
                .FirstOrDefault(IntPtr.Zero);
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

            // §163: Process.MainWindowHandle can be Zero (or point at a
            // hidden helper window) even while the game is plainly on
            // screen - it is a heuristic of the FIRST visible top-level
            // window the process created, and GPU-rendered clients have
            // been seen to confuse it after driver or Windows updates.
            // When the polite route finds nothing but PROClient processes
            // exist, walk every top-level window and take each process's
            // largest visible one instead.
            if (clients.Count == 0)
                clients = FindByWindowEnumeration();

            return clients
                .OrderBy(c => c.ProcessId)
                .ToList();
        }

        private static List<ProClientInfo> FindByWindowEnumeration()
        {
            var found = new List<ProClientInfo>();

            var processIds = new HashSet<uint>();

            foreach (Process process in Process.GetProcessesByName("PROClient"))
            {
                try
                {
                    processIds.Add((uint)process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (processIds.Count == 0)
                return found;

            var bestByProcess = new Dictionary<uint, (IntPtr Handle, long Area)>();

            NativeMethods.EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle) || IsIconic(handle))
                    return true;

                GetWindowThreadProcessId(handle, out uint processId);

                if (!processIds.Contains(processId))
                    return true;

                if (!TryGetWindowBounds(handle, out Rectangle bounds))
                    return true;

                long area = (long)bounds.Width * bounds.Height;

                // Tiny windows are tooltips and splash remnants; the game
                // itself is never that small.
                if (area < 64 * 64)
                    return true;

                if (!bestByProcess.TryGetValue(processId, out (IntPtr Handle, long Area) best) ||
                    area > best.Area)
                {
                    bestByProcess[processId] = (handle, area);
                }

                return true;
            }, IntPtr.Zero);

            foreach (KeyValuePair<uint, (IntPtr Handle, long Area)> entry in bestByProcess)
            {
                found.Add(new ProClientInfo
                {
                    Handle = entry.Value.Handle,
                    ProcessId = (int)entry.Key,
                    DisplayName = $"PRO Client - PID {entry.Key}"
                });
            }

            return found;
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