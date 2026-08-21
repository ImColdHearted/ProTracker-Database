using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Foot_Tracker.Tracking
{
    // Windows-only PRO client window capture via PrintWindow (works even when the
    // window is occluded, unlike CopyFromScreen). Used internally by
    // Tracking/Capture/WindowsWindowCaptureService.cs.
    //
    // The pure image-math helpers that used to live here (CropImage,
    // GetBattleTitleRegion, DrawDebugRegion) moved to ImageOps.cs /
    // BattleWindowLocator.cs, rewritten with SkiaSharp so they work on every OS -
    // System.Drawing.Common (used below) does not work outside Windows in modern
    // .NET. See MIGRATION_GUIDE.md.
    [SupportedOSPlatform("windows")]
    public static class ScreenCapture
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(
            IntPtr hwnd,
            IntPtr hdcBlt,
            uint nFlags
        );

        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        private static IntPtr selectedProWindow =
    IntPtr.Zero;

        public static IntPtr SelectedProWindow =>
            selectedProWindow;

        public static bool HasSelectedClient =>
            selectedProWindow != IntPtr.Zero;

        public static void SelectProWindow(
            IntPtr handle)
        {
            selectedProWindow = handle;
        }

        public static void ClearSelectedProWindow()
        {
            selectedProWindow = IntPtr.Zero;
        }

        public static Bitmap? CaptureProWindow()
        {
            IntPtr handle =
                selectedProWindow;

            // If the user has not chosen a client,
            // preserve the current behavior and use
            // the first PROClient found.
            if (handle == IntPtr.Zero)
            {
                handle =
                    ProWindowFinder.FindProWindow();
            }

            if (handle == IntPtr.Zero)
                return null;

            if (!ProWindowFinder.TryGetWindowBounds(
                    handle,
                    out Rectangle bounds))
            {
                return null;
            }

            if (bounds.Width <= 0 ||
                bounds.Height <= 0)
            {
                return null;
            }

            Bitmap bitmap = new Bitmap(
                bounds.Width,
                bounds.Height
            );

            using (Graphics graphics =
                   Graphics.FromImage(bitmap))
            {
                IntPtr hdc = graphics.GetHdc();

                try
                {
                    bool success = PrintWindow(
                        handle,
                        hdc,
                        PW_RENDERFULLCONTENT
                    );

                    if (!success)
                    {
                        bitmap.Dispose();
                        return null;
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            return bitmap;
        }
    }
}
