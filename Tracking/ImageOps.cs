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

    public static SKBitmap? DecodePng(byte[] pngBytes) =>
        SKBitmap.Decode(pngBytes);
}