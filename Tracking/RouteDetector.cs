using System;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
using Serilog;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Foot_Tracker.Tracking;

/// <summary>
/// Reads the game client's own top-right corner HUD - the route/location name
/// banner and the "Poke Time: HH:MM" readout shown next to it - via the same
/// crop -> upscale -> threshold -> Tesseract pipeline every other detector in
/// this folder uses (see BossBattleDetector.cs for the original template).
/// Unlike boss names or Pokemon names, there is currently no catalog anywhere
/// in this app of every PRO location name to validate OCR'd route text
/// against (most of SharedPokemonLibrary/Data/Regions is still empty), so the
/// route name is shown as whatever cleaned text OCR reads, not matched
/// against a known list the way BossBattleDetector matches boss names.
///
/// Two things here are a first-pass estimate rather than a confirmed
/// calibration, both from a single reference screenshot at one window size:
/// - CornerRegionX/Y/Width/Height, the crop rectangle below.
/// - PageSegMode.SparseText, chosen because the corner has UI icons mixed in
///   with 2-3 short stacked lines rather than one clean paragraph; SingleBlock
///   would be the first alternative to try if this reads consistently blank.
/// If the route name or time comes back blank/garbled in practice, a fresh
/// screenshot with the corner clearly visible is what's needed to correct
/// either of these - the same "confirmed via a real tester's screenshot/log"
/// bar every other detector in this folder was tuned against.
///
/// Day/Night/Morning is bucketed from the in-game "Poke Time" readout, not
/// the player's own system clock. The client shows a separate "Local Time"
/// right next to "Poke Time" specifically because PRO's day/night cycle runs
/// on one shared server clock common to every player, not each player's own
/// timezone - "Poke Time" is assumed to be that shared clock. This assumption
/// is new and untested; nothing in this codebase confirmed it previously.
/// </summary>
public static class RouteDetector
{
    // Fractions of the full captured window - see the class doc comment above.
    private const float CornerRegionX = 0.74f;
    private const float CornerRegionY = 0f;
    private const float CornerRegionWidth = 0.26f;
    private const float CornerRegionHeight = 0.18f;

    private static readonly object ocrLock = new();
    private static Engine? engine;

    public static void Initialize()
    {
        if (engine != null)
            return;

        string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

        if (!Directory.Exists(tessDataPath))
        {
            throw new DirectoryNotFoundException(
                $"Tesseract data folder not found:\n{tessDataPath}"
            );
        }

        engine = new Engine(tessDataPath, Language.English, EngineMode.Default);
    }

    /// <summary>
    /// Crops the top-right corner of <paramref name="screenshot"/> and OCRs it
    /// for a route/location name and a "Poke Time" reading. Either output can
    /// come back null independently (e.g. the route banner isn't currently
    /// showing, or only the time readout is legible) - callers should treat
    /// a null as "leave whatever was shown before," not "clear it." Returns
    /// false only when nothing at all could be read from the crop.
    /// </summary>
    public static bool TryDetectCorner(
        SKBitmap screenshot,
        out string? routeName,
        out string? timeOfDay)
    {
        routeName = null;
        timeOfDay = null;

        int x = (int)(screenshot.Width * CornerRegionX);
        int y = (int)(screenshot.Height * CornerRegionY);
        int width = (int)(screenshot.Width * CornerRegionWidth);
        int height = (int)(screenshot.Height * CornerRegionHeight);

        SKRectI cornerRegion = ImageOps.MakeRect(x, y, width, height);

        using SKBitmap cornerCrop = ImageOps.Crop(screenshot, cornerRegion);
        using SKBitmap prepared = PrepareForOcr(cornerCrop);

        string rawText = ReadText(prepared, PageSegMode.SparseText);

        LogOcrAttemptIfChanged(rawText, cornerRegion);

        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        timeOfDay = ExtractTimeOfDay(rawText);
        routeName = ExtractRouteName(rawText);

        return routeName != null || timeOfDay != null;
    }

    /// <summary>
    /// Parses a "Poke Time: HH:MM" style reading (tolerant of OCR mangling the
    /// colon/label spacing) and buckets it using PRO's fixed day/night cycle:
    /// 20:00-04:00 Night, 04:00-10:00 Morning, 10:00-20:00 Day. Deliberately
    /// requires the "Poke" qualifier rather than matching any nearby HH:MM,
    /// so a garbled read returns null (previous value stays on screen)
    /// instead of risking a silent match against "Local Time" instead -
    /// showing the wrong clock's reading confidently would be worse than not
    /// updating at all.
    /// </summary>
    private static string? ExtractTimeOfDay(string rawText)
    {
        Match match = Regex.Match(
            rawText,
            @"Po[ck]e\s*Time[^\d]{0,5}(\d{1,2})[:\.](\d{2})",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups[1].Value, out int hour) || hour is < 0 or >= 24)
            return null;

        if (hour >= 20 || hour < 4)
            return "Night";

        return hour < 10 ? "Morning" : "Day";
    }

    /// <summary>
    /// Picks the longest OCR'd line that looks like a place name rather than
    /// a UI label or a misread icon: mostly letters/spaces, not a "Time"
    /// line. No catalog exists yet to validate against (see the class doc
    /// comment), so this is a best-effort cleanup rather than a confirmed
    /// match against a known route name.
    /// </summary>
    private static string? ExtractRouteName(string rawText)
    {
        string[] lines = rawText.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? best = null;

        foreach (string line in lines)
        {
            if (line.Length < 3)
                continue;

            if (line.Contains("Time", StringComparison.OrdinalIgnoreCase))
                continue;

            int letterCount = line.Count(char.IsLetter);

            if (letterCount < line.Length * 0.7)
                continue;

            if (best == null || line.Length > best.Length)
                best = line;
        }

        return best;
    }

    private static string ReadText(SKBitmap bitmap, PageSegMode pageSegMode)
    {
        Initialize();

        lock (ocrLock)
        {
            byte[] pngBytes = ImageOps.EncodePng(bitmap);

            using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
            using TesseractOCR.Page page = engine!.Process(image, pageSegMode);

            return page.Text ?? string.Empty;
        }
    }

    private static SKBitmap PrepareForOcr(SKBitmap source)
    {
        const int scale = 3;

        SKBitmap resized = ImageOps.Resize(source, source.Width * scale, source.Height * scale);
        ImageOps.ThresholdToBlackAndWhite(resized, 150);

        return resized;
    }

    private static string? lastLoggedOcrText;

    private static void LogOcrAttemptIfChanged(string rawText, SKRectI cornerRegion)
    {
        string normalized = rawText.Replace("\r", " ").Trim();

        if (normalized == lastLoggedOcrText)
            return;

        lastLoggedOcrText = normalized;

        Log.Information(
            "RouteDetector OCR (corner region {Region}): {OcrText}",
            cornerRegion,
            string.IsNullOrWhiteSpace(normalized) ? "(empty)" : normalized);
    }
}
