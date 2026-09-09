using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;
using SkiaSharp;

namespace Foot_Tracker.Services;

/// <summary>
/// The Admin Console's OCR inspector engine (MIGRATION_GUIDE.md §101): loads
/// a screenshot file and runs THE PRODUCTION DETECTORS against it - the same
/// BattleWindowLocator, EncounterDetector, LevelDetector, GenderDetector and
/// RouteDetector the live tracker calls, byte for byte. There is no second
/// OCR implementation here to drift out of sync: this class only sequences
/// the real ones and formats what they said. Raw per-detector OCR text is
/// captured through TrackerDiagnostics' recording ring (the detectors'
/// existing log-on-change taps), which Replay temporarily enables around the
/// run and restores afterward.
///
/// A replay NEVER touches hunting data by construction: it calls the
/// detectors directly, not EncounterTracker, so no encounter events exist to
/// reach MainWindowViewModel - the counters, Catch Logs, histories and
/// lifetime stats have no code path into this class at all. Safe to run in
/// any mode; the console additionally lives behind Admin Login.
/// </summary>
public static class DiagnosticReplayService
{
    public sealed class ReplayResult
    {
        public string FileName { get; init; } = string.Empty;
        public bool BattleWindowFound { get; init; }
        public string BattleBounds { get; init; } = string.Empty;
        public string PokemonName { get; init; } = string.Empty;
        public string LevelText { get; init; } = string.Empty;
        public string GenderText { get; init; } = string.Empty;
        public string LocationText { get; init; } = string.Empty;
        public string RawOcrLines { get; init; } = string.Empty;
        public byte[]? OverlayPng { get; init; }

        public string Summary =>
            $"{FileName}\n" +
            (BattleWindowFound
                ? $"  battle window: {BattleBounds}\n" +
                  $"  name: {PokemonName}\n" +
                  $"  level: {LevelText}\n" +
                  $"  gender: {GenderText}\n"
                : "  battle window: not found (not a battle frame, or locator miss)\n") +
            $"  location: {LocationText}\n" +
            (RawOcrLines.Length > 0 ? $"  raw OCR:\n{RawOcrLines}" : string.Empty);
    }

    /// <summary>Runs every production detector against one PNG screenshot.
    /// <paramref name="includeOverlay"/> renders the DebugRegionOverlay
    /// annotated copy (the exact overlay Report a Problem draws) so the
    /// console can show WHERE each detector looked - disabled by default,
    /// purely a preview image, never involved in the OCR itself.</summary>
    public static ReplayResult Replay(string pngPath, bool includeOverlay)
    {
        using SKBitmap? screenshot = ImageOps.DecodePng(File.ReadAllBytes(pngPath));

        if (screenshot is null)
        {
            return new ReplayResult
            {
                FileName = Path.GetFileName(pngPath),
                LocationText = "(file could not be decoded as a PNG)"
            };
        }

        return ReplayCore(screenshot, Path.GetFileName(pngPath), includeOverlay);
    }

    /// <summary>§103 "Screenshot client": captures the LIVE bound PRO client
    /// through the exact capture service the tracker itself uses and runs the
    /// production detectors on that frame - the inspector's answer to "what
    /// would the tracker see RIGHT NOW". Null-safe: no bound/visible client
    /// simply reports so.</summary>
    public static ReplayResult ReplayLiveClient(bool includeOverlay)
    {
        byte[]? pngBytes = WindowCaptureServiceFactory.Instance.CaptureSelectedWindowPng();

        if (pngBytes is null || pngBytes.Length == 0)
        {
            return new ReplayResult
            {
                FileName = $"Live client {DateTime.Now:HH:mm:ss}",
                LocationText = "(no PRO client window is currently bound/capturable)"
            };
        }

        using SKBitmap? screenshot = ImageOps.DecodePng(pngBytes);

        if (screenshot is null)
        {
            return new ReplayResult
            {
                FileName = $"Live client {DateTime.Now:HH:mm:ss}",
                LocationText = "(live capture could not be decoded)"
            };
        }

        return ReplayCore(screenshot, $"Live client {DateTime.Now:HH:mm:ss}", includeOverlay);
    }

    private static ReplayResult ReplayCore(SKBitmap screenshot, string label, bool includeOverlay)
    {

        // Capture each detector's raw text through the production taps: turn
        // the recording ring on for the duration of this run, then restore
        // whatever state the admin had chosen.
        bool recordingWasEnabled = TrackerDiagnostics.RecordingEnabled;
        int before = TrackerDiagnostics.RecordingCount;

        if (!recordingWasEnabled)
            TrackerDiagnostics.RecordingEnabled = true;

        bool found;
        SKRectI bounds = default;
        string name = "(none)";
        string levelText = "Lv. ?";
        string genderText = "(unknown)";
        string locationText = "(none)";

        try
        {
            found = BattleWindowLocator.TryLocate(screenshot, out bounds);

            if (found)
            {
                if (EncounterDetector.TryDetectEncounter(screenshot, out string detected, out _) &&
                    !string.IsNullOrWhiteSpace(detected))
                {
                    name = PokemonSpriteService.ResolveEncounterName(detected);
                }

                int? level = LevelDetector.TryDetectLevel(screenshot, bounds);

                if (level is int lv)
                    levelText = $"Lv. {lv}";

                string? gender = GenderDetector.TryDetectGender(screenshot, bounds);

                if (!string.IsNullOrWhiteSpace(gender))
                    genderText = gender;
            }

            if (RouteDetector.TryDetectCorner(screenshot, out string? route))
                locationText = route ?? "(no confirmed map name)";
        }
        finally
        {
            if (!recordingWasEnabled)
                TrackerDiagnostics.RecordingEnabled = false;
        }

        // The raw lines this run appended to the ring.
        var raw = new StringBuilder();
        IReadOnlyList<TrackerDiagnostics.DiagnosticEntry> entries = TrackerDiagnostics.GetRecordingSnapshot();

        for (int i = Math.Max(0, before); i < entries.Count; i++)
        {
            raw.AppendLine($"    [{entries[i].Detector}] {entries[i].Text.Replace("\n", " / ")}");
        }

        byte[]? overlay = null;

        if (includeOverlay)
        {
            using SKBitmap annotated = DebugRegionOverlay.DrawDetectionRegions(screenshot);
            overlay = ImageOps.EncodePng(annotated);
        }

        return new ReplayResult
        {
            FileName = label,
            BattleWindowFound = found,
            BattleBounds = found ? $"({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}" : string.Empty,
            PokemonName = name,
            LevelText = levelText,
            GenderText = genderText,
            LocationText = locationText,
            RawOcrLines = raw.ToString().TrimEnd(),
            OverlayPng = overlay
        };
    }
}
