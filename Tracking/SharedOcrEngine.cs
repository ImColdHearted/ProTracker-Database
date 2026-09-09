using TesseractOCR;
using TesseractOCR.Enums;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// One shared Tesseract engine used by every OCR-based detector in this
    /// folder - EncounterDetector, LevelDetector, RouteDetector, CatchDetector,
    /// RareEncounterDetector (WildEncounterDetectors.cs) and BossBattleDetector,
    /// PvpBattleDetector (BossPvpDetectors.cs) - instead of each loading its own
    /// independent copy of the same English-language tessdata. Before this,
    /// seven detectors meant seven separate Engine instances (and seven
    /// separate locks) held in memory at once for what was, in six of the
    /// seven cases, an identical configuration - this holds exactly one.
    ///
    /// GetEngine()/Lock are the only two things a detector needs: call
    /// GetEngine() to get the shared instance (created lazily on first use,
    /// same as every detector's own Initialize() used to do), and hold Lock
    /// for the duration of any Process() call, exactly like every detector's
    /// own former ocrLock did. LevelDetector is the one exception worth
    /// knowing about: it still narrows the engine's character whitelist to
    /// digits-plus-"Lv." for its own reads, but now does so immediately
    /// before its own Process() call and restores it immediately after, both
    /// inside the same Lock - see its own ReadText for why that has to
    /// happen there now instead of once here at startup, the way a private,
    /// single-purpose engine could afford to.
    /// </summary>
    internal static class SharedOcrEngine
    {
        public static readonly object Lock = new();
        private static Engine? engine;

        public static Engine GetEngine()
        {
            if (engine != null)
                return engine;

            string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

            if (!Directory.Exists(tessDataPath))
            {
                throw new DirectoryNotFoundException(
                    $"Tesseract data folder not found:\n{tessDataPath}"
                );
            }

            engine = new Engine(tessDataPath, Language.English, EngineMode.Default);
            return engine;
        }

        /// <summary>Admin Console's "Restart OCR Services" (§101): disposes
        /// the shared engine under the same Lock every Process() call holds,
        /// so the next detector read lazily creates a fresh one. Useful when
        /// Tesseract has wedged internally; touches nothing else - no
        /// detector state, no hunting data.</summary>
        public static void Restart()
        {
            lock (Lock)
            {
                engine?.Dispose();
                engine = null;
            }
        }
    }
}
