using SkiaSharp;

namespace Foot_Tracker.Tracking;

/// <summary>
/// Small SkiaSharp-based helpers replacing the System.Drawing.Bitmap/Graphics/
/// Rectangle operations the OCR pipeline (BattleWindowLocator, EncounterDetector,
/// CatchDetector, RareEncounterDetector) used to rely on directly.
///
/// This exists because System.Drawing.Common does not work outside Windows in
/// modern .NET (it throws PlatformNotSupportedException for essentially any
/// Bitmap/Graphics operation on Linux/macOS, not just screen capture) - so the
/// whole OCR pipeline needed a cross-platform imaging library, not just the
/// window-capture step. SkiaSharp already ships with Avalonia and works
/// identically on all three platforms.
/// </summary>
internal static class ImageOps
{
    /// <summary>Builds a rect from (x, y, width, height), matching System.Drawing.Rectangle's
    /// constructor semantics (SKRectI's own constructor instead takes left/top/right/bottom).</summary>
    public static SKRectI MakeRect(int x, int y, int width, int height) =>
        SKRectI.Create(x, y, width, height);

    public static SKRectI Intersect(SKRectI a, SKRectI b) =>
        SKRectI.Intersect(a, b);

    public static bool IsEmpty(SKRectI rect) =>
        rect.Width <= 0 || rect.Height <= 0;

    /// <summary>Crops to a region, clamped to the source bitmap's bounds.</summary>
    public static SKBitmap Crop(SKBitmap source, SKRectI region)
    {
        SKRectI bounds = MakeRect(0, 0, source.Width, source.Height);
        SKRectI clamped = Intersect(region, bounds);

        if (IsEmpty(clamped))
            throw new ArgumentException("The crop region is outside the screenshot.");

        // ExtractSubset is a stable, version-independent SkiaSharp API (a direct
        // pixel-buffer subset, no drawing/sampling APIs involved - those have
        // churned between SkiaSharp versions, e.g. SKPaint.FilterQuality, removed
        // between the 2.x and 3.x lines - see FootTracker.Avalonia.csproj for why
        // this project moved to 3.119.0).
        var cropped = new SKBitmap();
        source.ExtractSubset(cropped, clamped);
        return cropped;
    }

    /// <summary>
    /// Nearest-neighbor upscale, matching the original's InterpolationMode.NearestNeighbor -
    /// sharp pixel edges read better by Tesseract for small UI text than smooth interpolation.
    ///
    /// Reads/writes the whole pixel buffer in bulk via SKBitmap.Pixels rather than calling
    /// GetPixel/SetPixel per pixel - each of those crosses into native Skia code individually,
    /// which is the expensive part; indexing a managed array afterward is effectively free.
    /// </summary>
    public static SKBitmap Resize(SKBitmap source, int newWidth, int newHeight)
    {
        SKColor[] sourcePixels = source.Pixels;
        var destPixels = new SKColor[newWidth * newHeight];

        for (int y = 0; y < newHeight; y++)
        {
            int sourceY = Math.Min(source.Height - 1, y * source.Height / newHeight);
            int sourceRowStart = sourceY * source.Width;
            int destRowStart = y * newWidth;

            for (int x = 0; x < newWidth; x++)
            {
                int sourceX = Math.Min(source.Width - 1, x * source.Width / newWidth);
                destPixels[destRowStart + x] = sourcePixels[sourceRowStart + sourceX];
            }
        }

        var resized = new SKBitmap(newWidth, newHeight, source.ColorType, source.AlphaType);
        resized.Pixels = destPixels;
        return resized;
    }

    /// <summary>
    /// Center-aligned bilinear resize, the smooth counterpart to Resize above.
    /// LevelDetector's §97 rework needs it: measured against the app's own
    /// eng.traineddata, Tesseract's LSTM reads the wild tag's antialiased
    /// grayscale strokes correctly but misreads the same glyphs once they are
    /// upscaled as hard-edged pixel blocks (a real "Lv. 39" read "Lv.34" on
    /// every frame - see MIGRATION_GUIDE.md §97), so the level pipeline scales
    /// with interpolation while every other detector keeps nearest-neighbor
    /// Resize, whose sharp edges those longer-tuned pipelines were calibrated
    /// on. Same bulk managed-array pattern as Resize, same reasoning: one
    /// native crossing for the pixel buffer each way, arithmetic stays managed.
    /// </summary>
    public static SKBitmap ResizeBilinear(SKBitmap source, int newWidth, int newHeight)
    {
        SKColor[] sourcePixels = source.Pixels;
        var destPixels = new SKColor[newWidth * newHeight];

        for (int y = 0; y < newHeight; y++)
        {
            float sourceYf = (y + 0.5f) * source.Height / newHeight - 0.5f;
            int y0 = Math.Clamp((int)Math.Floor(sourceYf), 0, source.Height - 1);
            int y1 = Math.Min(y0 + 1, source.Height - 1);
            float wy = Math.Clamp(sourceYf - y0, 0f, 1f);

            int destRowStart = y * newWidth;

            for (int x = 0; x < newWidth; x++)
            {
                float sourceXf = (x + 0.5f) * source.Width / newWidth - 0.5f;
                int x0 = Math.Clamp((int)Math.Floor(sourceXf), 0, source.Width - 1);
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                float wx = Math.Clamp(sourceXf - x0, 0f, 1f);

                SKColor c00 = sourcePixels[y0 * source.Width + x0];
                SKColor c01 = sourcePixels[y0 * source.Width + x1];
                SKColor c10 = sourcePixels[y1 * source.Width + x0];
                SKColor c11 = sourcePixels[y1 * source.Width + x1];

                float top = c00.Red + (c01.Red - c00.Red) * wx;
                float bottom = c10.Red + (c11.Red - c10.Red) * wx;
                byte r = (byte)(top + (bottom - top) * wy + 0.5f);

                top = c00.Green + (c01.Green - c00.Green) * wx;
                bottom = c10.Green + (c11.Green - c10.Green) * wx;
                byte g = (byte)(top + (bottom - top) * wy + 0.5f);

                top = c00.Blue + (c01.Blue - c00.Blue) * wx;
                bottom = c10.Blue + (c11.Blue - c10.Blue) * wx;
                byte b = (byte)(top + (bottom - top) * wy + 0.5f);

                destPixels[destRowStart + x] = new SKColor(r, g, b);
            }
        }

        var resized = new SKBitmap(newWidth, newHeight, source.ColorType, source.AlphaType);
        resized.Pixels = destPixels;
        return resized;
    }

    /// <summary>Converts every pixel to pure black/white based on a brightness threshold, in place.
    /// Uses the brightest single channel (not the average of all three) specifically so
    /// saturated colored text - e.g. a boss's name rendered in red, (255,0,0) - is
    /// correctly classified as "bright text" rather than "dark background". Averaging
    /// all three channels gives red only ~85/255, well under a typical threshold, which
    /// silently erased boss names before OCR ever saw them (white text is unaffected
    /// either way, since average and max are identical when R=G=B).</summary>
    public static void ThresholdToBlackAndWhite(SKBitmap bitmap, int brightnessThreshold)
    {
        SKColor[] pixels = bitmap.Pixels;

        for (int i = 0; i < pixels.Length; i++)
        {
            SKColor color = pixels[i];
            int brightness = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
            pixels[i] = brightness >= brightnessThreshold ? SKColors.Black : SKColors.White;
        }

        bitmap.Pixels = pixels;
    }

    /// <summary>Encodes to PNG bytes - the interchange format fed to Tesseract via Pix.LoadFromMemory,
    /// which works cross-platform (unlike Tesseract.Drawing's PixConverter, which needs System.Drawing.Bitmap).</summary>
    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// §114. Mean brightness of a frame, 0-255, sampled rather than scanned:
    /// this runs on every captured frame at roughly ten frames a second, so a
    /// full pass over two million pixels would be real work for a number
    /// nobody needs to four decimal places. Every Nth pixel on a fixed grid is
    /// enough to tell a black frame from a game screen, which is the only
    /// question being asked.
    ///
    /// It exists because a failed capture does not always LOOK failed. On
    /// Linux under XWayland, and on Windows when PrintWindow cannot reach a
    /// window's content, the capture succeeds and returns a correctly-sized
    /// image full of nothing. Width and height alone cannot tell that apart
    /// from a working frame - brightness can.
    /// </summary>
    public static int MeanBrightness(SKBitmap bitmap)
    {
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            return -1;

        // About 40x40 sample points wherever the frame is big enough, which
        // is 1600 reads regardless of whether the client is 800x600 or 4K.
        int stepX = Math.Max(1, bitmap.Width / 40);
        int stepY = Math.Max(1, bitmap.Height / 40);

        long total = 0;
        int samples = 0;

        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                total += (pixel.Red + pixel.Green + pixel.Blue) / 3;
                samples++;
            }
        }

        return samples == 0 ? -1 : (int)(total / samples);
    }

    public static SKBitmap? DecodePng(byte[] pngBytes) =>
        SKBitmap.Decode(pngBytes);
}