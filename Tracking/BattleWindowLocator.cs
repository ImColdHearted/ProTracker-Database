using System;
using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    public static class BattleWindowLocator
    {
        private const int MinBattleWidth = 450;

        // Allow larger GUI scales / larger displays.
        private const int MaxBattleWidth = 1800;

        private const int MinBattleHeight = 260;
        private const int MaxBattleHeight = 1100;

        // PRO's battle interface stays very close to this
        // width-to-height ratio across the recordings.
        private const double BattleAspectRatio = 1.70;

        /// <summary>
        /// Attempts to locate the PRO battle window inside a full PRO screenshot.
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
            // the single hottest path in the whole detection pipeline.
            SKColor[] pixels = screenshot.Pixels;
            int width = screenshot.Width;

            /*
             * The battle title bar is a long, dark gray horizontal strip.
             *
             * Instead of assuming where it is on the screen,
             * scan for a long horizontal run of pixels matching
             * that general appearance.
             */

            for (int y = 0;
                 y < screenshot.Height - 40;
                 y++)
            {
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
                                battleBounds =
                                    candidate.Value;

                                return true;
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
                        battleBounds =
                            candidate.Value;

                        return true;
                    }
                }
            }

            return false;
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
}