using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Foot_Tracker.Tracking
{
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

        public static Bitmap CropImage(
    Bitmap source,
    Rectangle region)
        {
            // Prevent an invalid rectangle from going outside the image.
            Rectangle imageBounds = new Rectangle(
                0,
                0,
                source.Width,
                source.Height
            );

            region = Rectangle.Intersect(
                region,
                imageBounds
            );

            if (region.Width <= 0 ||
                region.Height <= 0)
            {
                throw new ArgumentException(
                    "The crop region is outside the screenshot."
                );
            }

            return source.Clone(
                region,
                source.PixelFormat
            );
        }

        public static Rectangle GetBattleTitleRegion(
            Rectangle battleBounds)
        {
            // Focus on:
            //
            //      VS. Wild PokemonName
            //
            // rather than OCRing the entire title bar,
            // logo + player name + empty space included.

            int x =
                battleBounds.X +
                (int)(battleBounds.Width * 0.30);

            int y =
                battleBounds.Y;

            int width =
                (int)(battleBounds.Width * 0.52);

            int height =
                (int)Math.Round(
                    battleBounds.Height * 0.09
                );

            height =
                Math.Clamp(
                    height,
                    45,
                    75
                );

            return new Rectangle(
                x,
                y,
                width,
                height
            );
        }

        public static Bitmap DrawDebugRegion(
    Bitmap source,
    Rectangle region)
        {
            Bitmap debugImage =
                new Bitmap(source);

            using Graphics graphics =
                Graphics.FromImage(debugImage);

            using Pen pen =
                new Pen(Color.Red, 3);

            graphics.DrawRectangle(
                pen,
                region
            );

            return debugImage;
        }
    }
}