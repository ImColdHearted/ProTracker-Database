using System;
using System.Drawing;

namespace Foot_Tracker.Tracking
{
    public static class BattleWindowLocator
    {
        // PRO's battle window is roughly this size.
        // Give ourselves some tolerance for borders / scaling.
        private const int MinBattleWidth = 450;
        private const int MaxBattleWidth = 1000;

        private const int MinBattleHeight = 300;
        private const int MaxBattleHeight = 650;

        /// <summary>
        /// Attempts to locate the PRO battle window inside a full PRO screenshot.
        /// </summary>
        public static bool TryLocate(
            Bitmap screenshot,
            out Rectangle battleBounds)
        {
            battleBounds = Rectangle.Empty;

            if (screenshot == null ||
                screenshot.Width <= 0 ||
                screenshot.Height <= 0)
            {
                return false;
            }

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
                int runStart = -1;
                int runLength = 0;

                for (int x = 0;
                     x < screenshot.Width;
                     x++)
                {
                    Color pixel =
                        screenshot.GetPixel(x, y);

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
                            Rectangle? candidate =
                                BuildCandidate(
                                    screenshot,
                                    runStart,
                                    y,
                                    runLength
                                );

                            if (candidate.HasValue &&
                                HasBrightBattleTitle(
                                    screenshot,
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
                    Rectangle? candidate =
                        BuildCandidate(
                            screenshot,
                            runStart,
                            y,
                            runLength
                        );

                    if (candidate.HasValue &&
                        HasBrightBattleTitle(
                            screenshot,
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
    Bitmap screenshot,
    Rectangle battleBounds)
        {
            Rectangle titleRegion =
                ScreenCapture.GetBattleTitleRegion(
                    battleBounds
                );

            titleRegion.Intersect(
                new Rectangle(
                    0,
                    0,
                    screenshot.Width,
                    screenshot.Height
                )
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
                for (int x = titleRegion.Left;
                     x < titleRegion.Right;
                     x++)
                {
                    Color color =
                        screenshot.GetPixel(x, y);

                    // Require near-white pixels.
                    if (color.R >= 210 &&
                        color.G >= 210 &&
                        color.B >= 210)
                    {
                        brightPixels++;
                    }
                }
            }

            return brightPixels >= 20;
        }

        private static bool LooksLikeBattleTitleBar(
            Color color)
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
                    color.R,
                    Math.Max(color.G, color.B)
                );

            int min =
                Math.Min(
                    color.R,
                    Math.Min(color.G, color.B)
                );

            int difference =
                max - min;

            return
                color.R >= 45 &&
                color.R <= 100 &&

                color.G >= 45 &&
                color.G <= 100 &&

                color.B >= 45 &&
                color.B <= 100 &&

                difference <= 15;
        }

        private static Rectangle? BuildCandidate(
            Bitmap screenshot,
            int runStart,
            int y,
            int runLength)
        {
            if (runLength < MinBattleWidth)
                return null;

            int width =
                Math.Min(runLength, MaxBattleWidth);

            if (width < MinBattleWidth ||
                width > MaxBattleWidth)
            {
                return null;
            }

            /*
             * The detected row should be somewhere inside the
             * title bar. Move upward slightly to approximate
             * the outer battle-window border.
             */
            int battleX = runStart;

            int battleY =
                Math.Max(0, y - 10);

            // Your PRO battle window is approximately 445px tall.
            int battleHeight =
                Math.Min(
                    500,
                    screenshot.Height - battleY
                );
            if (battleHeight < MinBattleHeight)
                return null;

            if (battleY + battleHeight >
                screenshot.Height)
            {
                battleHeight =
                    screenshot.Height - battleY;
            }

            if (battleHeight < MinBattleHeight ||
                battleHeight > MaxBattleHeight)
            {
                return null;
            }

            return new Rectangle(
                battleX,
                battleY,
                width,
                battleHeight
            );
        }
    }
}