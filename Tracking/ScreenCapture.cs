using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.Tracking
{
    // Windows-only PRO client window capture via PrintWindow (works even when the
    // window is occluded, unlike CopyFromScreen). Used internally by
    // Tracking/Capture/WindowsWindowCaptureService.cs.
    //
    // §257: three routes, tried in order. PrintWindow first, as always. If
    // Windows refuses it or it paints black - which is what happens when the
    // PRO client runs as administrator and this tracker does not - Windows
    // Graphics Capture (Tracking/Capture/GraphicsCapture.cs) reads the window
    // through the compositor, elevated or not, occluded or not, and keeps
    // that window from then on. Only if that too is unavailable does the
    // window's screen rectangle get copied, which needs it visible.
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

        // §114 diagnostics only - never used to decide anything, only to
        // describe the setup in the log. GetDpiForWindow needs Windows 10
        // 1607+, which .NET 10 requires anyway.
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        // §163: a bound handle can die (the client was closed or restarted
        // since it was picked) - captures must notice instead of silently
        // returning null forever.
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        /// <summary>§163. Why the LAST CaptureProWindow call returned null,
        /// in words fit for a status line - or null after a successful
        /// capture. WindowsWindowCaptureService turns this into LastError,
        /// so the import window and Set Screen Boundaries can say what is
        /// actually wrong instead of guessing "minimized or closed".</summary>
        public static string? LastFailureReason { get; private set; }

        // §163: recovery events are worth one log line each time the
        // situation CHANGES, never five lines a second while a hunt polls.
        private static string lastRecoverySignature = string.Empty;

        // §257: the window Windows Graphics Capture has taken over, or Zero.
        // Set the first time PrintWindow fails or paints black for a window
        // and the compositor route succeeds; from then on that window goes
        // to the compositor FIRST, and PrintWindow is tried again for it only
        // if the compositor route fails.
        private static IntPtr graphicsCaptureHandle = IntPtr.Zero;

        // §257: the elevation sentence is two token queries; once per window
        // is plenty for a log line that is deduplicated anyway.
        private static IntPtr elevationNoteHandle = IntPtr.Zero;
        private static string elevationNote = string.Empty;

        private static void LogRecoveryIfChanged(string signature)
        {
            if (string.Equals(signature, lastRecoverySignature, StringComparison.Ordinal))
                return;

            lastRecoverySignature = signature;

            try
            {
                TrackerDiagnostics.LogCaptureEnvironment(signature);
            }
            catch
            {
                // Diagnostics never break capture.
            }
        }

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
            LogEnvironmentFor(handle);
        }

        /// <summary>
        /// §114. Records the three things about a Windows setup that decide
        /// whether capture can work, once, when a client is bound.
        ///
        /// The DPI is the one that matters and the one nobody thinks to
        /// report. This bitmap is sized from GetWindowRect, but GetWindowRect
        /// on ANOTHER process's window returns coordinates in the CALLER's DPI
        /// context, while PrintWindow always renders at the window's real
        /// physical size. If this process is not per-monitor DPI aware and the
        /// PRO client sits on a monitor scaled to anything other than 100%,
        /// those two disagree - the bitmap is allocated at the wrong size and
        /// the capture is silently wrong or fails outright. It works perfectly
        /// for everyone at 100% and mysteriously not at all for anyone with a
        /// scaled display, which is exactly the shape of the reports.
        ///
        /// 96 DPI is 100%, 120 is 125%, 144 is 150%. Anything other than 96
        /// in this line is the first thing to suspect.
        /// </summary>
        // ---- §130 environment re-checking ----------------------------
        //
        // §114 added the line below and called it from SelectProWindow only.
        // A report bundle showed what that costs: the line fired twice at
        // 18:37 and never again while the monitor scaling was changed four
        // times over the next twenty minutes. The one question it exists to
        // answer - what was the DPI when this went wrong - and it could not
        // answer it, because nothing re-selects the window when a display
        // setting changes.
        //
        // So the capture path re-reads it too. Two things keep that from
        // becoming noise: it is checked at most every few seconds rather
        // than on all five scans a second, and it is only written when the
        // answer actually DIFFERS from the last one written. A session that
        // never changes anything gets exactly the one line it gets today.
        private static string lastEnvironmentSignature = string.Empty;

        private static DateTime nextEnvironmentCheckUtc = DateTime.MinValue;

        private const int EnvironmentCheckSeconds = 5;

        /// <summary>Everything worth knowing about how this window will
        /// capture, as one string. Doubles as the change signature - if the
        /// text is identical there is nothing new to say.</summary>
        private static string DescribeEnvironment(IntPtr handle)
        {
            string bounds = TryGetWindowBoundsText(handle);
            uint dpi = GetDpiForWindow(handle);
            int scalePercent = dpi > 0 ? (int)Math.Round(dpi * 100.0 / 96.0) : 0;
            bool minimized = IsIconic(handle);

            return
                $"Windows/PrintWindow; client window {bounds}; " +
                $"monitor DPI {dpi} ({scalePercent}% scaling); " +
                $"minimized={minimized}" +
                (dpi != 96 && dpi != 0
                    ? " - NOTE: display scaling is not 100%, which is the most " +
                      "likely cause if capture produces nothing"
                    : string.Empty);
        }

        private static void LogEnvironmentFor(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;

            try
            {
                string description = DescribeEnvironment(handle);

                lastEnvironmentSignature = description;
                nextEnvironmentCheckUtc =
                    DateTime.UtcNow.AddSeconds(EnvironmentCheckSeconds);

                TrackerDiagnostics.LogCaptureEnvironment(description);
            }
            catch (Exception ex)
            {
                // Diagnostics must never be able to break binding a client.
                TrackerDiagnostics.LogCaptureEnvironment(
                    $"Windows/PrintWindow; environment could not be read: {ex.Message}");
            }
        }

        /// <summary>§130. Called from the capture path. Says nothing unless
        /// something changed, so a log stays readable while still carrying
        /// the moment a display setting moved.</summary>
        private static void LogEnvironmentIfChanged(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;

            if (DateTime.UtcNow < nextEnvironmentCheckUtc)
                return;

            nextEnvironmentCheckUtc =
                DateTime.UtcNow.AddSeconds(EnvironmentCheckSeconds);

            try
            {
                string description = DescribeEnvironment(handle);

                if (string.Equals(description, lastEnvironmentSignature, StringComparison.Ordinal))
                    return;

                // The PREVIOUS value is half the story: "it is 144 DPI now"
                // is worth much less than "it was 96 and became 144", which
                // is the line that dates a change against everything else in
                // the log.
                string previous = lastEnvironmentSignature;

                lastEnvironmentSignature = description;

                TrackerDiagnostics.LogCaptureEnvironment(
                    string.IsNullOrEmpty(previous)
                        ? description
                        : description + " - CHANGED from: " + previous);
            }
            catch
            {
                // Same rule as above: diagnostics never break capture. No
                // message here on purpose - LogEnvironmentFor already
                // reported the failure once when the client was bound, and
                // repeating it every few seconds would bury the log.
            }
        }

        private static string TryGetWindowBoundsText(IntPtr handle) =>
            ProWindowFinder.TryGetWindowBounds(handle, out Rectangle bounds)
                ? $"{bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y})"
                : "(bounds unavailable)";

        public static void ClearSelectedProWindow()
        {
            selectedProWindow = IntPtr.Zero;
        }

        public static Bitmap? CaptureProWindow()
        {
            IntPtr handle =
                selectedProWindow;

            // §163: a bound window that no longer exists must not dead-end
            // every capture - the client was closed or restarted since it
            // was picked, so fall through to finding a live one, and move
            // the binding onto it (the old one can never come back; a
            // relaunched client is a new window). This is what broke both
            // the §162 importer and Set Screen Boundaries with "could not
            // be captured" while the client list plainly saw the game.
            if (handle != IntPtr.Zero && !IsWindow(handle))
            {
                IntPtr live = ProWindowFinder.FindProWindow();

                LogRecoveryIfChanged(live != IntPtr.Zero
                    ? "Windows/PrintWindow; the bound client window no longer exists - rebound to the first live PRO client window."
                    : "Windows/PrintWindow; the bound client window no longer exists and no live PRO client window was found.");

                selectedProWindow = live;
                handle = live;

                if (handle == IntPtr.Zero)
                {
                    LastFailureReason =
                        "The client window this tracker was bound to is gone, and no live PRO client window was found - is the game running and not minimized?";
                    return null;
                }

                LogEnvironmentFor(handle);
            }

            // If the user has not chosen a client,
            // preserve the current behavior and use
            // the first PROClient found.
            if (handle == IntPtr.Zero)
            {
                handle =
                    ProWindowFinder.FindProWindow();
            }

            if (handle == IntPtr.Zero)
            {
                LastFailureReason =
                    "No PRO client window was found - is the game running and not minimized?";
                return null;
            }

            // §130: cheap, throttled, and silent unless something moved.
            LogEnvironmentIfChanged(handle);

            if (!ProWindowFinder.TryGetWindowBounds(
                    handle,
                    out Rectangle bounds))
            {
                LastFailureReason =
                    "The client window vanished while its size was being read - it may be closing.";
                return null;
            }

            if (bounds.Width <= 0 ||
                bounds.Height <= 0)
            {
                LastFailureReason =
                    "The client window reported an empty size - it may be minimized.";
                return null;
            }

            // §257: a window the compositor route has taken over skips
            // PrintWindow, which was refused for it. Minimized is checked
            // first here because the compositor delivers no frames for a
            // minimized window and the last one would be handed back stale.
            if (handle == graphicsCaptureHandle)
            {
                if (IsIconic(handle))
                {
                    LastFailureReason =
                        "The client window is minimized - restore it on screen and try again.";
                    return null;
                }

                Bitmap? composited = TryGraphicsCapture(handle, "was refused when last tried", out _);

                if (composited is not null)
                {
                    LastFailureReason = null;
                    return composited;
                }

                // The compositor route failed for this window after having
                // worked: forget the take-over so PrintWindow gets another
                // chance below, and the fallback chain runs in full.
                graphicsCaptureHandle = IntPtr.Zero;
            }

            Bitmap bitmap = new Bitmap(
                bounds.Width,
                bounds.Height
            );

            bool printed;

            using (Graphics graphics =
                   Graphics.FromImage(bitmap))
            {
                IntPtr hdc = graphics.GetHdc();

                try
                {
                    printed = PrintWindow(
                        handle,
                        hdc,
                        PW_RENDERFULLCONTENT
                    );
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            // §163: PrintWindow can fail outright OR "succeed" into an
            // all-black frame (GPU-rendered clients on some drivers and
            // Windows builds). Either way, when the window is actually on
            // screen, copying its screen rectangle still gets the real
            // picture - occluded parts capture whatever covers them, which
            // is still infinitely better than a dead feature.
            if (!printed || LooksBlank(bitmap))
            {
                string why = printed ? "produced a blank frame" : "failed";

                if (IsIconic(handle))
                {
                    bitmap.Dispose();
                    LastFailureReason =
                        $"PrintWindow {why} and the client window is minimized - restore it on screen and try again.";
                    LogRecoveryIfChanged($"Windows/PrintWindow; PrintWindow {why}; window minimized - no fallback possible.");
                    return null;
                }

                // §257: before copying the screen, ask the compositor. It
                // reads an elevated window, which is the usual reason
                // PrintWindow was refused, and an occluded one, which the
                // screen copy cannot. Once it works for a window it keeps it.
                Bitmap? composited = TryGraphicsCapture(handle, why, out string? compositorFailure);

                if (composited is not null)
                {
                    bitmap.Dispose();
                    graphicsCaptureHandle = handle;
                    LastFailureReason = null;
                    return composited;
                }

                try
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(
                            bounds.X,
                            bounds.Y,
                            0,
                            0,
                            bitmap.Size,
                            CopyPixelOperation.SourceCopy);
                    }

                    // §257: one signature for the whole outcome. A separate
                    // line for the compositor's refusal would alternate with
                    // this one and defeat the deduplication at capture rate.
                    LogRecoveryIfChanged(
                        $"Windows/PrintWindow; PrintWindow {why}; Windows Graphics Capture unavailable ({compositorFailure}) - " +
                        "capturing the window's screen rectangle instead (the window must stay visible on screen). " +
                        ElevationNote(handle));
                }
                catch (Exception ex)
                {
                    bitmap.Dispose();
                    LastFailureReason =
                        $"PrintWindow {why} and the screen-copy fallback also failed ({ex.Message}).";
                    LogRecoveryIfChanged(
                        $"Windows/PrintWindow; PrintWindow {why} and CopyFromScreen threw: {ex.Message}");
                    return null;
                }
            }
            else
            {
                LogRecoveryIfChanged("Windows/PrintWindow; capturing normally.");
            }

            LastFailureReason = null;
            return bitmap;
        }

        /// <summary>§257. One frame through Windows Graphics Capture, as the
        /// Bitmap the rest of this class deals in, or null with
        /// <paramref name="failure"/> saying why, for the caller to fold into
        /// its own log line. Success is logged here, once, with which of the
        /// two processes is elevated - the fact a report bundle needs and
        /// cannot otherwise show.</summary>
        private static Bitmap? TryGraphicsCapture(IntPtr handle, string why, out string? failure)
        {
            byte[]? png = GraphicsCapture.CapturePng(handle, out failure);

            if (png is null)
                return null;

            LogRecoveryIfChanged(
                $"Windows/GraphicsCapture; PrintWindow {why} - Windows Graphics Capture is in use for this window. " +
                ElevationNote(handle) + GraphicsCapture.BorderNote);

            try
            {
                using var stream = new MemoryStream(png);
                using var decoded = new Bitmap(stream);

                // GDI+ keeps a decoded image tied to the stream it came from;
                // the copy stands on its own after the stream is disposed.
                return new Bitmap(decoded);
            }
            catch (Exception ex)
            {
                failure = "the compositor's frame could not be decoded: " + ex.Message;
                return null;
            }
        }

        private static string ElevationNote(IntPtr handle)
        {
            if (handle != elevationNoteHandle)
            {
                elevationNoteHandle = handle;
                elevationNote = ProcessElevation.Describe(handle);
            }

            return elevationNote;
        }

        /// <summary>§163. A sparse sample says whether a frame is pure
        /// black - a real game frame always has lit pixels somewhere
        /// (bars, chat, UI chrome), so all-zero samples mean PrintWindow
        /// painted nothing. ~200 pixels, so a 5-a-second hunt never
        /// notices the cost.</summary>
        private static bool LooksBlank(Bitmap bitmap)
        {
            try
            {
                int stepX = Math.Max(1, bitmap.Width / 16);
                int stepY = Math.Max(1, bitmap.Height / 12);

                for (int y = stepY / 2; y < bitmap.Height; y += stepY)
                {
                    for (int x = stepX / 2; x < bitmap.Width; x += stepX)
                    {
                        Color pixel = bitmap.GetPixel(x, y);

                        if (pixel.R != 0 || pixel.G != 0 || pixel.B != 0)
                            return false;
                    }
                }

                return true;
            }
            catch
            {
                // If sampling itself fails, assume the frame is fine - the
                // fallback is for confirmed blanks only.
                return false;
            }
        }
    }
}
