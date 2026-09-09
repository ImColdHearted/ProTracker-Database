using System;
using Foot_Tracker.Models;
using SkiaSharp;
using Serilog;

namespace Foot_Tracker.Tracking
{
    // This file groups the two detectors that work by direct pixel inspection
    // rather than OCR - BattleWindowLocator (finds where the floating battle
    // window is) and GenderDetector (reads the wild Pokemon's gender icon by
    // color) - moved here from two separate files of their own, as part of
    // consolidating nine of the Tracking folder's ten detector-style files
    // down to four (this file, WildEncounterDetectors.cs, BossPvpDetectors.cs,
    // and SharedOcrEngine.cs). See MIGRATION_GUIDE.md for the full writeup.
    //
    // Purely a move: neither class's own detection logic changed at all here,
    // unlike the two OCR files, which also picked up a shared Tesseract
    // engine (see SharedOcrEngine.cs) alongside the move.

    public static class BattleWindowLocator
    {
        // §134: internal, not private - EncounterTracker's frame-health
        // status uses the same number to say "this frame is too small to
        // ever hold a battle window", and one constant cannot drift from
        // itself.
        internal const int MinBattleWidth = 450;

        // Allow larger GUI scales / larger displays.
        private const int MaxBattleWidth = 1800;

        private const int MinBattleHeight = 260;
        private const int MaxBattleHeight = 1100;

        // PRO's battle interface stays very close to this
        // width-to-height ratio across the recordings.
        // §135: internal - the Set Screen Boundaries window derives a
        // drawn box's height from its width with this same number, so a
        // manual box has the proportions every detector region was measured
        // against.
        internal const double BattleAspectRatio = 1.70;

        // See the rejection guard inside BuildCandidate for what this is for.
        private const double EdgeMarginFraction = 0.02;

        // §142: the edge rejection above only applies to candidates in the
        // top rows of the capture, where a bordered window's own OS title bar
        // lives - see BuildCandidate. Five percent of a 768-high capture is
        // 38 rows, comfortably past any title bar; the real battle window's
        // top has never been above 11% of the frame in any measured capture.
        private const double TopChromeFraction = 0.05;

        // §142: how closely the automatic scan's box has to match the saved
        // manual box to be treated as the same window - see TryManualOverride.
        internal const double AgreeLeftTopFraction = 0.03;
        internal const double AgreeWidthFraction = 0.08;

        // How many rows past the first acceptable one to keep probing
        // before settling on the widest.
        //
        // §119 set this to 12 on the theory that the run reaches full
        // width a row or two below the top edge. §120 measured it across
        // seven frames at three resolutions and two GUI scales and that was
        // wrong: the PRO logo sits ON the bar's left end, not beside it, and
        // it is tall, so the upper rows of the bar are cut short by the logo
        // rather than by the bar ending. On one 1366x768 frame the run only
        // reached full width 28 rows below the first acceptable one. Sweeping
        // the depth, the result converged at 40 and did not move again at 60
        // or 80, so 60 sits comfortably past convergence with headroom for a
        // taller bar at a larger GUI scale. The cost is bounded and paid only
        // when a battle is actually on screen; the no-battle case never
        // accepts a candidate and never probes at all.
        internal const int TitleBarProbeRows = 60;

        /// <summary>
        /// Attempts to locate the PRO battle window inside a full PRO screenshot:
        /// the automatic scan first and, only when that finds nothing, the box
        /// the player saved by hand in Set Screen Boundaries (§135), if one
        /// is saved and it currently holds something that looks like a battle
        /// window. Every tracker goes through here, so a saved box helps the
        /// boss and PVP trackers exactly as much as the encounter tracker.
        /// </summary>
        public static bool TryLocate(
            SKBitmap screenshot,
            out SKRectI battleBounds)
        {
            battleBounds = SKRectI.Empty;

            if (screenshot == null ||
                screenshot.Width <= 0 ||
                screenshot.Height <= 0)
            {
                return false;
            }

            // Read the whole screenshot's pixels once via the bulk Pixels array
            // rather than calling GetPixel() per pixel in the loops below - each
            // GetPixel() call crosses into native Skia code individually, which
            // is the expensive part; indexing this managed array is effectively
            // free. This full-screenshot scan runs on every polling tick, so it's
            // the single hottest path in the whole detection pipeline. §135:
            // the manual fallback reuses this same array rather than copying
            // the frame a second time.
            SKColor[] pixels = screenshot.Pixels;

            if (LocateByScan(screenshot, pixels, out battleBounds))
            {
                // §142: the scan can also succeed with the WRONG box. On
                // Lugario's 1432x1222 frames the winning run started past the
                // title bar's icon and the 9.5% left expansion could not make
                // up the difference, so the box came out at (189,396) 1053x619
                // against a real window at (0,396) 1243 wide - and every OCR
                // region derived from it read the wrong pixels ('ld Nidoran M'
                // with no 'VS', so no encounter ever registered). The box the
                // player drew in Set Screen Boundaries was right the whole
                // time and was never consulted, because the fallback below
                // only ran when the scan found NOTHING. So: when a saved box
                // disagrees with the scan's box AND itself holds a battle
                // window on this frame, the player's box wins. When the two
                // agree the scan's box is kept, so a setup where automatic
                // detection works is not changed by also having drawn a box.
                if (TryManualOverride(screenshot, pixels, battleBounds, out SKRectI overridden))
                {
                    battleBounds = overridden;
                    LastLocateUsedManual = true;
                    return true;
                }

                LastLocateUsedManual = false;
                return true;
            }

            if (TryManualFallback(screenshot, pixels, out battleBounds))
            {
                LastLocateUsedManual = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// §135. The automatic scan on its own, never the saved box. The
        /// Set Screen Boundaries window shows what THIS finds on the frame it
        /// captured - through TryLocate, a saved box would come back dressed
        /// as an automatic result, and the one question that window exists
        /// to answer ("does automatic detection work here?") would be lost.
        /// </summary>
        public static bool TryLocateAutomatically(
            SKBitmap screenshot,
            out SKRectI battleBounds)
        {
            battleBounds = SKRectI.Empty;

            if (screenshot == null ||
                screenshot.Width <= 0 ||
                screenshot.Height <= 0)
            {
                return false;
            }

            return LocateByScan(screenshot, screenshot.Pixels, out battleBounds);
        }

        private static bool LocateByScan(
            SKBitmap screenshot,
            SKColor[] pixels,
            out SKRectI battleBounds)
        {
            battleBounds = SKRectI.Empty;

            int width = screenshot.Width;

            /*
             * The battle title bar is a long, dark gray horizontal strip.
             *
             * Instead of assuming where it is on the screen,
             * scan for a long horizontal run of pixels matching
             * that general appearance.
             */

            // ================================================================
            // §119: take the WIDEST run in the title bar, not the first one
            // ================================================================
            //
            // This used to return the moment a row produced an acceptable
            // candidate. That makes the whole result depend on a single row -
            // and the first row that qualifies is, by definition, the row
            // where the title bar has only just begun, which is exactly where
            // its top edge is still blending into whatever is behind it.
            //
            // Measured from a real Linux capture (1024x640, report
            // 20260830-154324). The title bar's dark-gray run per row:
            //
            //     y=102   231..689   len 459   <- first row to qualify
            //     y=103   231..940   len 710
            //     y=104   231..940   len 710
            //
            // Row 102 is the boundary row. Past x=689 its pixels sit just
            // outside LooksLikeBattleTitleBar's range, so the run is cut
            // short there and nowhere else. Being first, it won - and every
            // region in the app is a fraction of what it produced:
            //
            //     from y=102:  battleBounds = (187,92,503x296)
            //     from y=103:  battleBounds = (164,93,777x457)
            //     actual window measured off the frame: (164,88,777x454)
            //
            // The knock-on effect is not subtle. The level and gender regions
            // derived from the short bounds do not merely clip their targets,
            // they miss them completely - the level crop lands on the title
            // bar above the name tag, which is why level read as null on
            // every encounter while the species, whose region is wide enough
            // to survive the error, kept working. That combination - species
            // fine, level always null - is what the report showed.
            //
            // So probe a band of rows rather than trusting one. A boundary
            // row can only ever UNDERSTATE the run - a partial view of the
            // bar cannot be wider than a full one - so the widest run in the
            // band is the one to trust for the WIDTH.
            //
            // §120: but only for the width. §119 took the whole
            // candidate from the widest row, and a candidate carries its own
            // Top, derived from the row it was found on. The widest row sits
            // partway DOWN the title bar, so that Top lands some thirty
            // pixels below the real top edge, and every region moves down
            // with it. Measured over seven frames: taking the whole widest
            // candidate put the level crop into the grass on all seven,
            // including the three where the old code had been reading levels
            // correctly. It fixed the width and broke the origin.
            //
            // The two numbers come from different rows and that is not a
            // compromise, it is the geometry. The top edge is by definition
            // the FIRST row of the bar, so the first accepted candidate has
            // it right and every later row has it wrong. The width is only
            // fully visible once past the logo, so a later row has it right
            // and the first row has it short. Take each from the row that
            // knows it.
            //
            // The no-battle case, the one that actually runs constantly, is
            // untouched - it never accepts a candidate and never probes.
            SKRectI widest = SKRectI.Empty;
            int firstAcceptedRow = -1;
            int firstAcceptedTop = 0;

            for (int y = 0;
                 y < screenshot.Height - 40;
                 y++)
            {
                // Far enough past the first hit to have seen the whole bar.
                if (firstAcceptedRow >= 0 &&
                    y > firstAcceptedRow + TitleBarProbeRows)
                {
                    break;
                }

                int rowStart = y * width;
                int runStart = -1;
                int runLength = 0;

                for (int x = 0;
                     x < width;
                     x++)
                {
                    SKColor pixel =
                        pixels[rowStart + x];

                    if (LooksLikeBattleTitleBar(pixel))
                    {
                        if (runStart == -1)
                            runStart = x;

                        runLength++;
                    }
                    else
                    {
                        if (runLength >= MinBattleWidth)
                        {
                            SKRectI? candidate =
                                BuildCandidate(
                                    screenshot,
                                    runStart,
                                    y,
                                    runLength
                                );

                            if (candidate.HasValue &&
                                HasBrightBattleTitle(
                                    pixels,
                                    width,
                                    screenshot.Height,
                                    candidate.Value))
                            {
                                if (candidate.Value.Width > widest.Width)
                                    widest = candidate.Value;

                                if (firstAcceptedRow < 0)
                                {
                                    firstAcceptedRow = y;
                                    firstAcceptedTop = candidate.Value.Top;
                                }
                            }
                        }

                        runStart = -1;
                        runLength = 0;
                    }
                }

                // Handle a run reaching the end of the scan.
                if (runLength >= MinBattleWidth)
                {
                    SKRectI? candidate =
                        BuildCandidate(
                            screenshot,
                            runStart,
                            y,
                            runLength
                        );

                    if (candidate.HasValue &&
                        HasBrightBattleTitle(
                            pixels,
                            width,
                            screenshot.Height,
                            candidate.Value))
                    {
                        if (candidate.Value.Width > widest.Width)
                            widest = candidate.Value;

                        if (firstAcceptedRow < 0)
                        {
                            firstAcceptedRow = y;
                            firstAcceptedTop = candidate.Value.Top;
                        }
                    }
                }
            }

            if (widest.Width > 0)
            {
                // Left/width/height from the widest row, top from the first.
                // The height needs no re-clamp: firstAcceptedTop is at or
                // above widest.Top, and widest was already clamped to fit the
                // frame, so this can only move the box UP - never off the
                // bottom edge.
                battleBounds =
                    ImageOps.MakeRect(
                        widest.Left,
                        firstAcceptedTop,
                        widest.Width,
                        widest.Height
                    );

                return true;
            }

            return false;
        }

        // ---- §135 manual boundaries ---------------------------------
        //
        // A box the player drew in Set Screen Boundaries, saved per client
        // in UiPreferences and pushed here by MainWindowViewModel whenever
        // preferences load. It is a FALLBACK: TryLocate consults it only
        // after the scan above has found nothing, and even then only accepts
        // it when the box currently holds two things a battle window always
        // has - bright title text where the title sits (the same
        // HasBrightBattleTitle test the scan applies) and a long dark run
        // across the top of the box (a relaxed cousin of the scan's own
        // colour test, relative to the box's width rather than the fixed
        // MinBattleWidth, because a game at a small GUI scale is one of the
        // things a manual box exists to rescue). Without that test the box
        // would report a battle on every frame of the overworld and every
        // tracker would OCR empty grass five times a second.
        //
        // The box is in frame pixels and is RE-PLACED, not re-scaled, on a
        // frame of a different size: ManualBattleBounds.LeftIn/TopIn shift
        // it by half the size difference, because PRO centres the battle
        // window (measured across seventeen frames - see that class). If it
        // still does not fit, it is simply not used, and the log says so
        // once - the player redraws it.

        private static volatile ManualBattleBounds? manualBounds;

        private static string lastManualState = string.Empty;

        /// <summary>The box currently saved for the active client, or null.
        /// Read by the status line, the tracking check and the picker.</summary>
        public static ManualBattleBounds? ManualBounds => manualBounds;

        /// <summary>True when the most recent successful TryLocate came from
        /// the saved box rather than the scan. A hint for messages, written
        /// by whichever tracker located last.</summary>
        public static volatile bool LastLocateUsedManual;

        public static void SetManualBounds(ManualBattleBounds? bounds)
        {
            manualBounds = bounds;
            lastManualState = string.Empty;
            LastLocateUsedManual = false;

            if (bounds is null)
            {
                Log.Information("Manual screen boundaries: none - automatic detection only.");
                return;
            }

            Log.Information(
                "Manual screen boundaries: {Width}x{Height} at ({X},{Y}), drawn on a {FrameWidth}x{FrameHeight} frame - " +
                "used when automatic detection finds no battle window, or finds one away from this box while " +
                "this box holds a battle window itself.",
                bounds.Width, bounds.Height, bounds.X, bounds.Y, bounds.FrameWidth, bounds.FrameHeight);
        }

        private static bool TryManualFallback(
            SKBitmap screenshot,
            SKColor[] pixels,
            out SKRectI battleBounds)
        {
            battleBounds = SKRectI.Empty;

            ManualBattleBounds? manual = manualBounds;

            if (manual is null)
                return false;

            if (!TryPlaceManualBox(manual, screenshot, out SKRectI clamped))
                return false;

            if (!BoxHoldsBattleWindow(pixels, screenshot.Width, screenshot.Height, clamped))
            {
                LogManualIfChanged("the saved box does not currently hold a battle window");
                return false;
            }

            battleBounds = clamped;

            LogManualIfChanged(
                $"battle window accepted from the saved box at ({clamped.Left},{clamped.Top}) " +
                $"{clamped.Width}x{clamped.Height}");

            return true;
        }

        /// <summary>§142. The scan found a box, but the box the player drew
        /// disagrees with it. If the player's box currently holds a battle
        /// window itself - the same presence test the fallback applies - it
        /// wins: it was drawn against this very game window in Set Screen
        /// Boundaries, and on the frames that prompted this section it was
        /// right on every frame the scan was wrong on. Boxes that agree keep
        /// the scan's result, so setups where the scan works are untouched;
        /// no box, no change at all.</summary>
        private static bool TryManualOverride(
            SKBitmap screenshot,
            SKColor[] pixels,
            SKRectI scanBounds,
            out SKRectI battleBounds)
        {
            battleBounds = SKRectI.Empty;

            ManualBattleBounds? manual = manualBounds;

            if (manual is null)
                return false;

            if (!TryPlaceManualBox(manual, screenshot, out SKRectI placed))
                return false;

            if (AgreesWithManual(scanBounds, placed, screenshot.Width, screenshot.Height))
                return false;

            if (!BoxHoldsBattleWindow(pixels, screenshot.Width, screenshot.Height, placed))
                return false;

            battleBounds = placed;

            LogManualIfChanged(
                $"the automatic box at ({scanBounds.Left},{scanBounds.Top}) {scanBounds.Width}x{scanBounds.Height} " +
                "disagrees with the saved box, which holds a battle window itself - using the saved box at " +
                $"({placed.Left},{placed.Top}) {placed.Width}x{placed.Height}");

            return true;
        }

        /// <summary>§142. Whether the scan's box and the placed manual box
        /// describe the same window: left and top within 3% of the frame,
        /// width within 8% of the manual box's own. Loose enough that the
        /// scan's usual few-pixel jitter never counts as a disagreement,
        /// tight enough that a box starting 189 pixels into the title bar
        /// (13% of the frame) does.</summary>
        private static bool AgreesWithManual(
            SKRectI scan,
            SKRectI manual,
            int frameWidth,
            int frameHeight)
        {
            return Math.Abs(scan.Left - manual.Left) <= frameWidth * AgreeLeftTopFraction &&
                   Math.Abs(scan.Top - manual.Top) <= frameHeight * AgreeLeftTopFraction &&
                   Math.Abs(scan.Width - manual.Width) <= manual.Width * AgreeWidthFraction;
        }

        /// <summary>§135's placement and fit check, shared by the fallback
        /// and the §142 override: re-centre the saved box into this frame
        /// (ManualBattleBounds.LeftIn/TopIn), clamp it, and refuse it when
        /// less than 90% of it fits - logging that once, since the player has
        /// to redraw it after a window-size or GUI-scale change.</summary>
        private static bool TryPlaceManualBox(
            ManualBattleBounds manual,
            SKBitmap screenshot,
            out SKRectI clamped)
        {
            SKRectI requested = ImageOps.MakeRect(
                manual.LeftIn(screenshot.Width),
                manual.TopIn(screenshot.Height),
                manual.Width,
                manual.Height);
            SKRectI frame = ImageOps.MakeRect(0, 0, screenshot.Width, screenshot.Height);
            clamped = ImageOps.Intersect(requested, frame);

            if (ImageOps.IsEmpty(clamped) ||
                clamped.Width < manual.Width * 0.9 ||
                clamped.Height < manual.Height * 0.9)
            {
                LogManualIfChanged(
                    $"the saved box ({manual.Width}x{manual.Height} at {manual.X},{manual.Y}, drawn on a " +
                    $"{manual.FrameWidth}x{manual.FrameHeight} frame) does not fit the current " +
                    $"{screenshot.Width}x{screenshot.Height} frame, so it is not used - redo Set Screen " +
                    "Boundaries after changing the game's window size or GUI scale");
                return false;
            }

            return true;
        }

        /// <summary>The presence test a manual box has to pass before it is
        /// used, on one frame. Public so the Set Screen Boundaries window can
        /// tell the player, on their own frame, whether the box they are
        /// about to save would engage - the same code, not a description of
        /// it.</summary>
        public static bool BoxHoldsBattleWindow(SKBitmap screenshot, SKRectI box)
        {
            if (screenshot is null || screenshot.Width <= 0 || screenshot.Height <= 0)
                return false;

            return BoxHoldsBattleWindow(screenshot.Pixels, screenshot.Width, screenshot.Height, box);
        }

        private static bool BoxHoldsBattleWindow(
            SKColor[] pixels,
            int screenshotWidth,
            int screenshotHeight,
            SKRectI box)
        {
            if (box.Width < 40 || box.Height < 20)
                return false;

            return HasBrightBattleTitle(pixels, screenshotWidth, screenshotHeight, box) &&
                   HasDarkTitleBand(pixels, screenshotWidth, screenshotHeight, box);
        }

        /// <summary>At least one row in the top band of the box carries a
        /// darkish, greyish run at least half the box wide. The title text
        /// sits mid-bar, so the rows above and below it are clean; a band of
        /// twelve percent of the box's height covers the whole title bar
        /// with room for an imprecise top edge.</summary>
        private static bool HasDarkTitleBand(
            SKColor[] pixels,
            int screenshotWidth,
            int screenshotHeight,
            SKRectI box)
        {
            int bandRows = Math.Max(8, (int)Math.Round(box.Height * 0.12));
            int bottom = Math.Min(Math.Min(box.Bottom, box.Top + bandRows), screenshotHeight);
            int left = Math.Max(0, box.Left);
            int right = Math.Min(box.Right, screenshotWidth);
            int needed = (int)Math.Round(box.Width * 0.5);

            if (needed <= 0)
                return false;

            for (int y = Math.Max(0, box.Top); y < bottom; y++)
            {
                int rowStart = y * screenshotWidth;
                int run = 0;

                for (int x = left; x < right; x++)
                {
                    if (LooksLikeBattleTitleBarRelaxed(pixels[rowStart + x]))
                    {
                        run++;

                        if (run >= needed)
                            return true;
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }

            return false;
        }

        /// <summary>LooksLikeBattleTitleBar with the channel window opened
        /// from 45-100 to 30-120 and the grey tolerance from 15 to 24. Wide
        /// enough to survive a driver's gamma or a slightly different
        /// capture path, still far from grass, water and night tint.</summary>
        private static bool LooksLikeBattleTitleBarRelaxed(SKColor color)
        {
            int max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
            int min = Math.Min(color.Red, Math.Min(color.Green, color.Blue));

            return min >= 30 && max <= 120 && max - min <= 24;
        }

        private static void LogManualIfChanged(string state)
        {
            if (string.Equals(state, lastManualState, StringComparison.Ordinal))
                return;

            lastManualState = state;

            Log.Information("Manual screen boundaries: {State}", state);
        }

        private static bool HasBrightBattleTitle(
            SKColor[] pixels,
            int screenshotWidth,
            int screenshotHeight,
            SKRectI battleBounds)
        {
            SKRectI titleRegion =
                ImageOps.Intersect(
                    GetBattleTitleRegion(battleBounds),
                    ImageOps.MakeRect(0, 0, screenshotWidth, screenshotHeight)
                );

            if (titleRegion.Width <= 0 ||
                titleRegion.Height <= 0)
            {
                return false;
            }

            int brightPixels = 0;

            for (int y = titleRegion.Top;
                 y < titleRegion.Bottom;
                 y++)
            {
                int rowStart = y * screenshotWidth;

                for (int x = titleRegion.Left;
                     x < titleRegion.Right;
                     x++)
                {
                    SKColor color =
                        pixels[rowStart + x];

                    // Require near-white pixels.
                    if (color.Red >= 210 &&
                        color.Green >= 210 &&
                        color.Blue >= 210)
                    {
                        brightPixels++;
                    }
                }
            }

            return brightPixels >= 20;
        }

        private static bool LooksLikeBattleTitleBar(
            SKColor color)
        {
            /*
             * In your captures, the title bar is a neutral
             * dark gray rather than pure black.
             *
             * Require RGB channels to be fairly similar so
             * we don't mistake dark blue water/terrain for it.
             */

            int max =
                Math.Max(
                    color.Red,
                    Math.Max(color.Green, color.Blue)
                );

            int min =
                Math.Min(
                    color.Red,
                    Math.Min(color.Green, color.Blue)
                );

            int difference =
                max - min;

            return
                color.Red >= 45 &&
                color.Red <= 100 &&

                color.Green >= 45 &&
                color.Green <= 100 &&

                color.Blue >= 45 &&
                color.Blue <= 100 &&

                difference <= 15;
        }

        private static SKRectI? BuildCandidate(
            SKBitmap screenshot,
            int runStart,
            int y,
            int runLength)
        {
            if (runLength < MinBattleWidth)
                return null;

            // ============================================================
            // TITLE BAR ADJUSTMENT
            // ============================================================
            //
            // The detected dark-gray run begins AFTER the PRO battle logo.
            // The actual battle window extends farther left.
            //
            // Based on the battle captures, the missing logo/header portion
            // is approximately 9.5% of the detected gray title width.
            //

            int leftExpansion =
                (int)Math.Round(
                    runLength * 0.095
                );

            int battleX =
                Math.Max(
                    0,
                    runStart - leftExpansion
                );

            // Add the missing left-side section back into
            // the true battle-window width.
            int battleWidth =
                runLength + leftExpansion;

            if (battleWidth < MinBattleWidth ||
                battleWidth > MaxBattleWidth)
            {
                return null;
            }


            // The scanned row is slightly inside the title bar.
            int battleY =
                Math.Max(
                    0,
                    y - 10
                );


            // ============================================================
            // PROPORTIONAL HEIGHT
            // ============================================================

            int battleHeight =
                (int)Math.Round(
                    battleWidth / BattleAspectRatio
                );

            if (battleHeight < MinBattleHeight ||
                battleHeight > MaxBattleHeight)
            {
                return null;
            }


            // Do not run outside the captured PRO window.
            if (battleY + battleHeight >
                screenshot.Height)
            {
                battleHeight =
                    screenshot.Height - battleY;
            }

            if (battleHeight < MinBattleHeight)
                return null;

            // ============================================================
            // REJECT CANDIDATES TOUCHING EITHER SCREEN EDGE
            // ============================================================
            //
            // Confirmed via a real tester's screenshots + a 30-second recording
            // sent for a separate (level-tracking) investigation: when the PRO
            // client runs in a bordered window rather than borderless/maximized,
            // its own OS title bar - dark gray, similar RGB range to the real
            // battle title bar this method is looking for - can satisfy
            // LooksLikeBattleTitleBar itself, and if the title bar's light-
            // colored window-control icons/app name happen to land inside the
            // derived title region, HasBrightBattleTitle passes too. Because
            // that false run starts at the captured image's own left edge (x=0)
            // and extends across nearly its full width, it was scanned and
            // accepted before the scan ever reached the real, further-down
            // battle window - three separate reference screenshots (two
            // different resolutions) all produced a "battleBounds" spanning
            // almost the entire captured window instead of the actual floating
            // battle box.
            //
            // The real battle box, being a centered floating overlay, never
            // came remotely this close to either edge in any of six reference
            // screenshots checked (12%-26% clearance on both sides, every
            // time) - so a candidate touching (or, per BuildCandidate's own
            // width-can-overshoot-by-~9.5% note above, exceeding) either edge
            // is rejected outright rather than trusted. EdgeMarginFraction
            // leaves a small buffer beyond an exact 0% touch, in case some
            // other window manager/theme renders its chrome with a few pixels
            // of true margin instead of none at all.
            double edgeMargin = screenshot.Width * EdgeMarginFraction;

            bool touchesEdge =
                battleX < edgeMargin ||
                battleX + battleWidth > screenshot.Width - edgeMargin;

            // §142: only in the top rows. The false positive this rule was
            // built against is a bordered window's own OS title bar, and that
            // bar is always at the very top of the capture. As a blanket rule
            // it also threw away every CORRECT candidate on Lugario's
            // 1432x1222 frames, where the game at a large GUI scale fills the
            // window and the real battle box starts 6 pixels from the left
            // edge - the correct runs were found, built into candidates, and
            // discarded here, leaving only the too-narrow candidates whose
            // runs started deeper inside the title bar. Measured on his
            // 18:13:01 report frame: with this scope the same scan lands on
            // (0,391) 1243 wide, whose title crop reads the full
            // 'VS. Wild Nidoran M'.
            if (touchesEdge &&
                battleY < screenshot.Height * TopChromeFraction)
            {
                return null;
            }

            return ImageOps.MakeRect(
                battleX,
                battleY,
                battleWidth,
                battleHeight
            );
        }

        public static SKRectI GetBattleTitleRegion(
            SKRectI battleBounds)
        {
            // Focus on:
            //
            //      VS. Wild PokemonName
            //
            // rather than OCRing the entire title bar,
            // logo + player name + empty space included.
            //
            // Title bar height scales with PRO's GUI - ~9% of total battle
            // height gives us enough room at both small and large GUI scales.

            int x =
                battleBounds.Left +
                (int)(battleBounds.Width * 0.30);

            int y =
                battleBounds.Top;

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

            return ImageOps.MakeRect(
                x,
                y,
                width,
                height
            );
        }
    }

    /// <summary>
    /// Reads the wild Pokemon's male/female gender symbol - the small icon PRO
    /// draws immediately before "Lv." in the same name/level tag LevelDetector.cs
    /// reads (see that class's doc comment for the full tag layout and how both
    /// were calibrated together from the same six reference screenshots). Unlike
    /// every OCR-based detector in this folder, this is a pure pixel-color check -
    /// the same technique PvpBattleDetector.HasPvpIndicatorBar uses for its own
    /// small colored-icon check, since Tesseract has no particular reason to
    /// reliably recognize a tiny non-text glyph the way a direct color match can.
    ///
    /// Confidence is asymmetric between the two genders, and that's spelled out
    /// below rather than glossed over: FEMALE's color was measured directly from
    /// all six original reference screenshots (every single one happened to show
    /// a female Pokemon) and is trusted accordingly. MALE's color is now also
    /// CONFIRMED - sampled from a seventh screenshot (the first real male one
    /// seen) sent alongside a user report that gender detection was not
    /// registering at all, for either gender. A genderless species (e.g.
    /// Magnemite) and an unrecognized color both fall through to null - "unknown,"
    /// not a guess.
    ///
    /// That report also caught the real bug: MinMatchFraction (below) was set to
    /// a threshold no real screenshot - female or male, any of the seven now
    /// checked - ever actually reached, so this had been silently reporting
    /// "unknown" for every single encounter regardless of region or color
    /// accuracy. See MinMatchFraction's own comment for the measurement that
    /// found this and how the new value was chosen. The region below was
    /// re-checked at the same time on the suspicion it was sized wrong for
    /// female specifically (a plausible read of the same symptom) - that turned
    /// out not to be the actual bug (the old region already contained the icon
    /// fine in all seven screenshots), but it was measurably wider than the icon
    /// needed, so it was tightened anyway on the way past. See
    /// EncounterTracking.cs's LevelGenderRetryWindowMs for a second, independent
    /// fix from the same report - a one-shot rendering-timing race that can
    /// affect this detector and LevelDetector.cs alike, separate from the
    /// MinMatchFraction bug here.
    /// </summary>
    public static class GenderDetector
    {
        // Fractions of battleBounds - same crop origin as LevelDetector.cs/
        // BattleWindowLocator.GetBattleTitleRegion. Measured directly from the
        // icon's own pixel bounding box across all seven reference screenshots
        // (six female, one male) - x: 0.1585-0.1710, y: 0.1348-0.1645 of
        // battleBounds, a strikingly tight range despite differing resolutions/
        // GUI scales/Pokemon names/genders. Width was tightened from an earlier
        // pass that left roughly 2.8x more horizontal margin than the icon
        // actually needed (0.035 wide for an icon only ~0.0125 wide) - a smaller
        // region raises MinMatchFraction's numerator-to-denominator ratio for
        // free, with no accuracy cost, since all seven screenshots' icons still
        // land comfortably inside it. Height was left alone - already a much
        // closer fit (1.5x margin) with no evidence it needed shrinking too.
        private const float GenderRegionX = 0.152f;
        private const float GenderRegionY = 0.125f;
        private const float GenderRegionWidth = 0.025f;
        private const float GenderRegionHeight = 0.045f;

        // CONFIRMED - sampled directly from the female symbol in all six original
        // reference screenshots (a tight cluster: R 191-244, G 8-32, B 90-97, a
        // saturated magenta/pink - notably more red-shifted than mainline Pokemon
        // games' lighter pink, so this is specific to PRO's own art rather than
        // assumed from franchise convention). Re-verified unchanged against the
        // tightened region above - same 22-35 matching pixels either way, since
        // the tightened width only cut empty margin, not any icon pixel.
        private const int FemaleTargetR = 217;
        private const int FemaleTargetG = 20;
        private const int FemaleTargetB = 94;
        private const int FemaleColorTolerance = 55;

        // CONFIRMED - sampled directly from the male symbol's flat-fill pixels in
        // the one real male screenshot checked against this so far (a saturated
        // sky blue, RGB exactly (8,163,244), no variation at all across its 23
        // matching pixels - a flat sprite fill with the anti-aliased edge pixels
        // falling outside FemaleColorTolerance's net rather than being sampled
        // here). Replaces an earlier guess based on the wider Pokemon-franchise
        // convention (blue for male) - the guess turned out close enough that it
        // is unlikely to have caused a real misread on its own (MinMatchFraction
        // below is the actual bug), but this is measured now rather than
        // assumed. Only one screenshot deep, the same "more still welcome"
        // caveat LevelDetector's own doc comment uses for its 3-digit-level case
        // - if a male Pokemon ever logs with the wrong gender, a screenshot of it
        // is what would sharpen this further.
        private const int MaleTargetR = 8;
        private const int MaleTargetG = 163;
        private const int MaleTargetB = 244;
        private const int MaleColorTolerance = 60;

        // Out of the small sampled region's pixels, how many need to match one
        // gender's color before this reports that gender rather than "unknown."
        //
        // Lowered from an earlier 0.05 (5%) after a user report ("not catching
        // that even for female") led to actually checking this against real
        // match counts for the first time - it turned out NO real screenshot
        // checked, female or male, across seven of them, ever reached 5%: the
        // true range measured was 2.68%-6.05% with the tightened region above
        // (1.93%-4.26% with the older, wider one). The 5% figure had been carried
        // over from PvpBattleDetector.HasPvpIndicatorBar's own 2%-region
        // threshold without ever measuring it against this detector's own,
        // much smaller and more tightly-cropped region - exactly the kind of
        // assumption this class's other constants were already trying to avoid.
        // 1.2% leaves roughly 2x margin below the lowest real match fraction
        // seen (079a04c4's 2.68%, the highest-resolution screenshot available),
        // and zero cross-gender false-positive pixels turned up in any of the
        // seven screenshots at this region size. One honest gap: a much larger
        // battle window than any seen so far (BattleWindowLocator allows up to
        // 1800px wide, past the widest - 1325px - actually checked here) could
        // plausibly push the true fraction lower still, since larger screenshots
        // trended toward a lower fraction rather than a proportionally steady
        // one. Not yet observed, so not yet corrected for - a real screenshot
        // from a very large/high-DPI setup would help confirm this still holds.
        private const double MinMatchFraction = 0.012;

        /// <summary>
        /// Returns "Female", "Male", or null (unknown/genderless/unrecognized) for
        /// the wild Pokemon in <paramref name="battleBounds"/>. Whichever color has
        /// more matching pixels wins, provided it clears MinMatchFraction; null
        /// covers both "neither color present" (a genderless species) and "the
        /// crop didn't land on anything readable" the same way - there's no
        /// reliable way to tell those two apart from pixel color alone.
        /// </summary>
        public static string? TryDetectGender(SKBitmap screenshot, SKRectI battleBounds)
        {
            SKRectI region = GetGenderRegion(battleBounds);
            SKRectI screenshotBounds = ImageOps.MakeRect(0, 0, screenshot.Width, screenshot.Height);
            SKRectI clamped = ImageOps.Intersect(region, screenshotBounds);

            if (ImageOps.IsEmpty(clamped))
                return null;

            SKColor[] pixels = screenshot.Pixels;
            int screenshotWidth = screenshot.Width;
            int totalPixels = clamped.Width * clamped.Height;
            int femaleMatches = 0;
            int maleMatches = 0;

            for (int py = clamped.Top; py < clamped.Bottom; py++)
            {
                int rowStart = py * screenshotWidth;

                for (int px = clamped.Left; px < clamped.Right; px++)
                {
                    SKColor color = pixels[rowStart + px];

                    if (Math.Abs(color.Red - FemaleTargetR) <= FemaleColorTolerance &&
                        Math.Abs(color.Green - FemaleTargetG) <= FemaleColorTolerance &&
                        Math.Abs(color.Blue - FemaleTargetB) <= FemaleColorTolerance)
                    {
                        femaleMatches++;
                    }
                    else if (Math.Abs(color.Red - MaleTargetR) <= MaleColorTolerance &&
                             Math.Abs(color.Green - MaleTargetG) <= MaleColorTolerance &&
                             Math.Abs(color.Blue - MaleTargetB) <= MaleColorTolerance)
                    {
                        maleMatches++;
                    }
                }
            }

            string? result =
                femaleMatches > maleMatches && femaleMatches / (double)totalPixels >= MinMatchFraction ? "Female" :
                maleMatches > femaleMatches && maleMatches / (double)totalPixels >= MinMatchFraction ? "Male" :
                null;

            LogIfChanged(result, femaleMatches, maleMatches, totalPixels, clamped);

            return result;
        }

        // Public (not just used internally by TryDetectGender above) so
        // DebugRegionOverlay.cs can draw a box for this exact region in a
        // "Report a Problem" screenshot, the same reasoning LevelDetector.cs's
        // own GetLevelRegion was pulled out for.
        public static SKRectI GetGenderRegion(SKRectI battleBounds)
        {
            int x = battleBounds.Left + (int)(battleBounds.Width * GenderRegionX);
            int y = battleBounds.Top + (int)(battleBounds.Height * GenderRegionY);
            int width = (int)(battleBounds.Width * GenderRegionWidth);
            int height = (int)(battleBounds.Height * GenderRegionHeight);

            return ImageOps.MakeRect(x, y, width, height);
        }

        // Diagnostic logging - only logs when the result actually changes, same
        // dedup-on-change pattern every other detector in this folder uses.
        private static string? lastLoggedResult;

        private static void LogIfChanged(
            string? result, int femaleMatches, int maleMatches, int totalPixels, SKRectI region)
        {
            if (result == lastLoggedResult)
                return;

            lastLoggedResult = result;

            Log.Information(
                "GenderDetector: result={Result}, femaleMatches={FemaleMatches}, maleMatches={MaleMatches}, " +
                "total={Total}, region=({X},{Y},{W}x{H})",
                result ?? "(unknown)", femaleMatches, maleMatches, totalPixels,
                region.Left, region.Top, region.Width, region.Height);
        }
    }
}
