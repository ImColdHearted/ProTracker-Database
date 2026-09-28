using Foot_Tracker.Models;
using Serilog;
using System;
using SkiaSharp;
using Foot_Tracker.Tracking.Capture;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Services;


namespace Foot_Tracker.Tracking
{
    public sealed class EncounterTracker : IDisposable
    {
        private CancellationTokenSource? cancellationTokenSource;
        private Task? trackingTask;

        private bool encounterAlreadyRegistered;
        private bool catchResultAlreadyRegistered;
        private bool rareEncounterAlreadyRegistered;
        private bool waitingForBattleToDisappear;
        private int consecutiveBattleScans;

        // §293. Where the battle window WAS when this encounter was
        // registered. The end-of-battle wait used to end only when the
        // locator found no window at all; a tester whose "Pokemon in <map>"
        // panel was open on the right of the client never got there, because
        // the locator, with the real battle gone, settled on that panel
        // (522x307 at (1409,118) against a battle at (245,116) 894x526) and
        // kept reporting a battle in every frame - "Run away - waiting for
        // battle to close" for the rest of the session, one encounter per
        // Start. A window somewhere else is not this battle lingering; it is
        // this battle gone.
        private SKRectI lockedBattleBounds;

        // §299. While waiting for a battle to close: when the located
        // window first stopped being THIS battle, or MinValue while it still
        // is. §293 ended the wait when the locator settled somewhere else;
        // two testers' logs show the two ways it can settle in the SAME
        // place and mean the same thing.
        //
        // The first: PRO draws every battle in the same spot, so the next
        // encounter opens a window §293's position test cannot tell from the
        // one that just ended. The second: with the real battle gone the
        // locator latches onto another dark panel that happens to sit there
        // - one tester's chat panel, read as a battle window at (1946,996)
        // 614x361 while its "battle message" region OCR'd trade chat. Either
        // way the window on screen is not the battle that ended, and the
        // title says so.
        //
        // His session: one encounter at 13:37:10, the next at 13:39:10, and
        // "waiting for battle to close" for the two minutes between.
        private DateTime battleNotOursSinceUtc = DateTime.MinValue;

        // Read at most this often while waiting. The wait polls five times a
        // second (NormalScanDelayMs) and the title read is an OCR; twice a
        // second is far more often than a battle can change and a fifth of
        // the work.
        private DateTime nextWaitingTitleReadUtc = DateTime.MinValue;
        private const int WaitingTitleReadIntervalMs = 500;

        // How long the window has to go on not being this battle before the
        // wait ends. Long enough that no run of bad OCR reaches it - at one
        // read every 500ms that is six disagreeing reads - and short enough
        // that the next encounter is registered while it is still on screen;
        // the tester's own gap between a run-away and the following battle
        // title was twenty seconds.
        private static readonly TimeSpan BattleNotOursGrace = TimeSpan.FromSeconds(3);

        // §294. When the capture first went blank, or MinValue while it is
        // not. §134's blank-frame alarm fired on the first dark frame it saw,
        // and PRO's map-change screen - "LOADING MAP" on black, a second or
        // two - is a dark frame: every map change told the player the tracker
        // could not see the client, and then tracked the next encounter
        // perfectly well. A capture that is genuinely blank (the fault §134
        // was written for) stays blank; a loading screen does not. So the
        // alarm now waits for the dark to last.
        private DateTime blankSinceUtc = DateTime.MinValue;

        // The recording that reported this holds seven map loads; measured
        // four frames a second, the capture sits at or under the blank
        // threshold for a quarter to three-quarters of a second on each. Six
        // seconds is that with headroom for a slower machine, and still far
        // short of how long anyone would wait before wondering why the
        // tracker was quiet.
        private static readonly TimeSpan BlankFrameGrace = TimeSpan.FromSeconds(6);

        // Guards the "identifying Pokemon" status message from firing on every
        // single scan tick (up to ~50/sec during an active battle, per
        // BattleScanDelayMs) while OCR repeatedly fails to match a Pokemon name -
        // which is EXPECTED and permanent for a boss battle, since a boss's name
        // will never match a wild Pokemon. Without this, the resulting message
        // spam drowns out BossCooldownTracker's own, much less frequent status
        // updates, making boss cooldown detection look broken even when it's
        // working correctly in the background (a separate, independent tracker -
        // see BossCooldownTracker.cs).
        private bool identifyingStatusAlreadyShown;
        private const int BattleConfirmationScans = 3;
        private const int NormalScanDelayMs = 200;
        private const int BattleScanDelayMs = 20;
        private const int RareCheckIntervalMs = 100;
        private const int RareCheckWindowMs = 5000;

        // ---- §259 the form match waits for the popup to go -----------
        //
        // The wild sprite's anchor (CounterpartMatcher.AnchorX/Y, 56% across
        // and 45% down the battle window) sits INSIDE the rare-encounter
        // popup while that popup is up - measured on the report's frame:
        // anchor at (356, 581), popup spanning x 225..540, y 505..685. So
        // the frame the popup is detected on is precisely the frame the
        // sprite cannot be seen in, and matching it scored the popup's own
        // pixels against the library, called the normal sprite the best of a
        // bad lot, and saved a crop of the popup "for the library". The
        // match is queued here instead and run on the first frame after the
        // popup is gone - the player pressing Ok - while the battle is still
        // on. Cleared with the rest of the encounter state; capped so a
        // popup left up for minutes does not hold a copy of the species
        // forever.
        private string? pendingFormMatchSpecies;
        private DateTime pendingFormMatchUntilUtc = DateTime.MinValue;
        private const int FormMatchWaitMs = 60000;

        // ---- §134 idle-status honesty ------------------------------
        //
        // A Linux tester's screenshot showed the status line still reading
        // "Tracking started." with a wild battle open on screen and nothing
        // being counted. Every quiet failure this loop can sit in between
        // battles - frames arriving that never contain a battle window,
        // blank frames, a frame too small to hold one, a battle-window
        // shape whose title never reads, a stale boss/PVP stand-down flag -
        // left that first line exactly as it was, so the screenshot could
        // not say which one it was looking at. Capture failures ("Waiting
        // for PROClient...") and thrown errors already replace it; this
        // closes the gap for the cases that throw nothing.
        //
        // Blank and undersized frames are reported at once. The ordinary
        // "frames are fine, no battle on screen" note waits
        // IdleStatusDelaySeconds, so a normal hunt between two encounters
        // is not narrated, and every text is emitted only when it CHANGES,
        // so the line never flickers. Each emitted text also goes to the
        // log, which is where a report gets read.
        private const int IdleStatusDelaySeconds = 15;

        private const int UnreadableTitleDelaySeconds = 10;

        private DateTime idleSinceUtc = DateTime.MinValue;

        private DateTime unreadableTitleSinceUtc = DateTime.MinValue;

        private string lastIdleStatus = string.Empty;

        private DateTime nextRareCheckUtc =
            DateTime.MinValue;

        private DateTime rareCheckUntilUtc =
            DateTime.MinValue;

        // A user report ("the levels are catching half the time") pointed at
        // a rendering race rather than a calibration error: the same species
        // at the same live moment sometimes logged a real level and
        // sometimes "Lv. ?", which a wrong crop/OCR setup would not produce
        // (that would fail the same way every time for a given resolution/
        // species). LevelDetector.TryDetectLevel and
        // GenderDetector.TryDetectGender are both called exactly once, on
        // the very same tick the battle title first becomes OCR-readable -
        // there is no guarantee PRO has finished drawing the name/level/
        // gender tag by that exact frame. RareEncounterDetector already
        // solves the same class of problem for its own shiny/form indicator
        // (see RareCheckIntervalMs/RareCheckWindowMs above) by re-checking
        // for up to five seconds after an encounter registers; this mirrors
        // that pattern for Level/Gender specifically, just with a much
        // shorter window, since unlike a shiny sparkle's animation, a static
        // name tag either finishes rendering almost immediately or - for
        // Gender specifically, on a genuinely genderless species - never
        // will, and there is no reason to keep the Hunting Log waiting long
        // for the latter case.
        //
        // Unlike the rare-encounter re-check, this one gates whether
        // EncounterLevelDetected/EncounterGenderDetected/EncounterDetected
        // have fired at all yet - see levelGenderEventsPending below and
        // FireEncounterEvents/FlushPendingLevelGenderEvents.
        private const int LevelGenderRetryIntervalMs = 100;
        private const int LevelGenderRetryWindowMs = 1000;

        private DateTime nextLevelGenderRetryUtc =
            DateTime.MinValue;

        private DateTime pendingEventsDeadlineUtc =
            DateTime.MinValue;

        // Multi-frame level consensus - see MIGRATION_GUIDE.md §95-§96. The
        // wild tag stays on screen for the whole battle, so instead of
        // trusting whichever single frame the register/catch tick happened
        // to sample, level reads are collected every LevelSampleIntervalMs
        // while the battle runs, and the catch-time level is the value with
        // the most votes among those seen at least LevelConsensusVotes
        // times - decided at catch time over the whole battle's samples
        // (SelectConsensusLevel), not locked to the first pair of agreeing
        // reads, so two early freak frames that happen to agree can still
        // be outvoted by a cleaner majority later (§96). The Hunting Log's
        // catch-time level prefers that consensus over the single catch-
        // tick read, so one weak frame (a mid-render tag, a glint misread)
        // can never outvote several clean ones. Values are candidates,
        // never averaged, and every piece of this state is cleared per
        // encounter (ResetLevelConsensus), so two encounters can never mix.
        // 500ms x 16 samples spans ~8s of battle - measured against §96's
        // real capture clips, where junk reads cluster in short bursts that
        // a longer, sparser window rides out.
        //
        // §396: quality before count. A level-23 Hariyama registered as 23,
        // then the status line and the history record "corrected" it to 28
        // from later reads of the same tag - three battles in a row. The
        // tag never changes during a battle, so reads of it are not
        // independent samples: whatever made one frame misread (the
        // SingleBlock rescue reading a 3 as an 8, §98; a glint or a badge
        // sharing the crop) makes every later frame like it misread the
        // same way, and a plain count of agreeing reads then measures how
        // long the disturbance lasted, not which value is right. So the
        // tally is kept per LevelReadQuality (levelVotesByQuality, indexed
        // by the enum's trust order), and the BEST quality that produced
        // any read at all decides: within it, LevelConsensusVotes agreeing
        // reads and the most-voted value as before; the tiers below it are
        // never consulted, however many reads they hold. A confirmed 23 from
        // clean reads is therefore never outvoted by any number of rescued
        // 28s, while a battle whose every read was rescued (the large-GUI
        // clips of §96-§97) decides among them exactly as it did.
        private const int LevelSampleIntervalMs = 500;
        private const int MaxLevelSamples = 16;
        private const int LevelConsensusVotes = 2;

        private readonly Dictionary<int, int>[] levelVotesByQuality =
            CreateLevelTallies();

        // §396: what the registration tick read, for the log line that
        // names a refinement disagreeing with it. Null until a registration
        // reads a level; cleared with the votes.
        private int? registrationLevelRead;

        private int levelSamplesTaken;

        // Last value EncounterLevelRefined reported for the current
        // encounter - the "only fire on a change" memory. See
        // RecordLevelVote; cleared by ResetLevelConsensus.
        private int? lastRefinedConsensusLevel;

        private DateTime nextLevelSampleUtc =
            DateTime.MinValue;

        // True from the moment a new encounter is registered until
        // EncounterLevelDetected/EncounterGenderDetected/EncounterDetected
        // actually fire for it - either immediately (Level and Gender both
        // resolved on the first try, the common case and the only case
        // before this fix existed) or after some number of retries within
        // LevelGenderRetryWindowMs. pendingPokemonName/pendingLevel/
        // pendingGender hold whatever is already known while this is true.
        private bool levelGenderEventsPending;
        private string pendingPokemonName = string.Empty;
        private int? pendingLevel;
        private string? pendingGender;

        // Throttle for RouteDetector's corner-of-screen OCR (the map-name
        // banner) - see the CornerInfoDetected event below and its call site
        // in ScanOnce(). Runs far less often than the surrounding battle-
        // detection scan: a map name changes when the player walks somewhere
        // new, not 50 times a second like a mid-battle scan needs to. Kept at
        // 5s in §102 after the evidence-log replay showed transition
        // staleness is closed by the in-battle refinement flag on the event,
        // not by scanning the corner harder.
        private const int CornerCheckIntervalMs = 5000;

        private DateTime nextCornerCheckUtc =
            DateTime.MinValue;

        // Set by MainWindowViewModel from BossCooldownTracker.BossBattleActiveChanged
        // (a separate, independent tracker - see BossCooldownTracker.cs). Boss
        // battles reuse the exact same battle-window UI as wild encounters, so
        // without this flag this tracker would happily OCR the boss's active
        // Pokemon and register it as a "wild encounter" instead of leaving Win/
        // Loss detection to BossCooldownTracker - confirmed via a real tester's
        // log. volatile because BossCooldownTracker's event fires from its own
        // background tracking thread, not this one.
        private volatile bool bossBattleActive;

        public void SetBossBattleActive(bool active) =>
            bossBattleActive = active;

        // Set by MainWindowViewModel from PvpTracker.PvpBattleActiveChanged (a
        // separate, independent tracker - see PvpTracker.cs). PVP battles reuse
        // the exact same battle-window UI as wild encounters/boss battles, so
        // without this flag this tracker would OCR the opponent's active Pokemon
        // and register it as a "wild encounter" - the same interference problem
        // bossBattleActive above exists to prevent, just for PVP instead of boss
        // battles.
        private volatile bool pvpBattleActive;

        public void SetPvpBattleActive(bool active) =>
            pvpBattleActive = active;

        public bool IsRunning =>
            cancellationTokenSource != null &&
            !cancellationTokenSource.IsCancellationRequested;

        public event Action<string>? EncounterDetected;

        public event Action<string>? StatusChanged;

        public event Action<string, RareEncounterType>? RareEncounterDetected;

        /// <summary>§138. Raised a moment after RareEncounterDetected for a
        /// Form, from a background task, with what CounterpartMatcher made
        /// of the wild sprite: which event's counterpart it is, or that none
        /// in the library fits. Not raised at all when the library holds no
        /// counterpart image for the species. Background thread - the
        /// subscriber posts to the UI thread as for every other event here.</summary>
        public event Action<string, CounterpartMatcher.MatchResult>? CounterpartIdentified;

        public event Action<CatchResult>? CatchResultDetected;

        // Fires only with a catalog-CONFIRMED map name (§102 - see
        // RouteDetector.TryDetectCorner). The bool is true when a wild
        // encounter's battle lock was held - and its level/gender events had
        // already fired - when this corner frame was read: the view model
        // uses it to correct the CURRENT history record's location, because
        // the player cannot move mid-battle, so a battle-time confirmation
        // is the encounter's true map even when the record was created a few
        // seconds earlier with the previous map still current. Gating on
        // levelGenderEventsPending mirrors §99's EncounterLevelRefined
        // ordering guard: without it, a corner event posted during the
        // pending window would race ahead of the encounter's own
        // registration and relabel the PREVIOUS record instead.
        public event Action<string?, bool>? CornerInfoDetected;

        // Fired once per confirmed encounter, immediately before EncounterDetected
        // below (same ScanOnce call, same background thread) - MainWindowViewModel
        // relies on that ordering to stash the level into a pending field before
        // its EncounterDetected handler reads it back out (Dispatcher.UIThread.Post
        // preserves the order two posts from the same thread were queued in). Null
        // when LevelDetector couldn't read a level for this encounter - see its
        // calibration caveats.
        public event Action<int?>? EncounterLevelDetected;

        // Fired whenever the §96 level-vote consensus first settles on - or,
        // with more votes, moves to - a value DURING the battle, so the
        // session encounter history (MIGRATION_GUIDE.md §99) can correct an
        // encounter that registered with a null or misread level without
        // waiting for a catch. At most a handful of firings per encounter
        // (only on change, never once per sample); reset per encounter
        // alongside the votes themselves (ResetLevelConsensus). Same
        // threading contract as every other event here: raised on the
        // tracker's own thread, consumers post to the UI thread.
        public event Action<int>? EncounterLevelRefined;

        // Same firing-order guarantee and reasoning as EncounterLevelDetected
        // above (fired before EncounterDetected, same ScanOnce call) - added
        // alongside it for GenderDetector.cs. "Female", "Male", or null (see
        // GenderDetector's own doc comment for what null covers).
        public event Action<string?>? EncounterGenderDetected;

        // Fired once per successful catch, immediately before CatchResultDetected
        // below (same ScanOnce call, same background thread) - see
        // MIGRATION_GUIDE.md §76. Same firing-order guarantee
        // EncounterLevelDetected/EncounterGenderDetected already rely on
        // (Dispatcher.UIThread.Post preserves the order posts from the same
        // thread were queued in), just for a fresh Level/Gender reading taken
        // at catch time instead of encounter time - see ScanOnce's SUCCESSFUL
        // CATCH case for why catch time reads more reliably. Null when
        // LevelDetector couldn't read a level from this frame either.
        public event Action<int?>? CatchLevelDetected;

        // Same firing-order guarantee and reasoning as CatchLevelDetected
        // above. "Female", "Male", or null - see GenderDetector's own doc
        // comment for what null covers.
        public event Action<string?>? CatchGenderDetected;

        private string lastDetectedPokemon =
    string.Empty;

        public void Start()
        {
            if (IsRunning)
                return;

            encounterAlreadyRegistered = false;
            catchResultAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            pendingFormMatchSpecies = null;
            lastDetectedPokemon = string.Empty;
            identifyingStatusAlreadyShown = false;
            consecutiveBattleScans = 0;
            waitingForBattleToDisappear = false;
            battleNotOursSinceUtc = DateTime.MinValue;
            nextWaitingTitleReadUtc = DateTime.MinValue;
            levelGenderEventsPending = false;
            pendingPokemonName = string.Empty;
            pendingLevel = null;
            pendingGender = null;
            ResetLevelConsensus();
            BeginIdlePeriod();

            cancellationTokenSource =
                new CancellationTokenSource();

            trackingTask = Task.Run(
                () => TrackingLoopAsync(
                    cancellationTokenSource.Token
                )
            );
            Log.Information("Encounter tracking started.");
            TrackerDiagnostics.SetState("Tracking");
            StatusChanged?.Invoke("Tracking started.");
        }

        public async Task StopAsync()
        {
            if (cancellationTokenSource == null)
                return;

            cancellationTokenSource.Cancel();

            try
            {
                if (trackingTask != null)
                    await trackingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping.
            }

            cancellationTokenSource.Dispose();
            cancellationTokenSource = null;
            trackingTask = null;
            encounterAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            pendingFormMatchSpecies = null;
            waitingForBattleToDisappear = false;
            battleNotOursSinceUtc = DateTime.MinValue;
            nextWaitingTitleReadUtc = DateTime.MinValue;

            catchResultAlreadyRegistered = false;
            lastDetectedPokemon = string.Empty;
            levelGenderEventsPending = false;
            pendingPokemonName = string.Empty;
            pendingLevel = null;
            pendingGender = null;
            ResetLevelConsensus();

            Log.Information("Encounter tracking stopped.");
            TrackerDiagnostics.SetState("Stopped");
            StatusChanged?.Invoke("Tracking stopped.");
        }

        private int GetCurrentScanDelay()
        {
            // Once we know which Pokemon we're battling,
            // poll much faster so short catch-result messages
            // are not missed at PRO's Max dialogue speed.
            if (encounterAlreadyRegistered &&
                !waitingForBattleToDisappear)
            {
                return BattleScanDelayMs;
            }

            return NormalScanDelayMs;
        }

        private void RearmForNextEncounter(
    string status)
        {
            // An encounter still waiting on a Level/Gender retry must not be
            // silently dropped just because the battle ended before that
            // retry window elapsed (a very fast catch/run-away right after
            // the name became readable) - report it now with whatever was
            // found, rather than never reporting it at all.
            FlushPendingLevelGenderEvents();

            encounterAlreadyRegistered = false;
            catchResultAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            pendingFormMatchSpecies = null;
            consecutiveBattleScans = 0;
            identifyingStatusAlreadyShown = false;
            rareCheckUntilUtc =
                DateTime.MinValue;

            nextRareCheckUtc =
                DateTime.MinValue;
            waitingForBattleToDisappear = false;

            // §299: the wait is over, however it ended.
            battleNotOursSinceUtc = DateTime.MinValue;
            nextWaitingTitleReadUtc = DateTime.MinValue;

            lastDetectedPokemon =
                string.Empty;

            ResetLevelConsensus();
            BeginIdlePeriod();

            StatusChanged?.Invoke(status);
        }

        // See levelGenderEventsPending's declaration comment. Called from
        // every path that can end an encounter's lifecycle before
        // EncounterLevelDetected/EncounterGenderDetected/EncounterDetected
        // have fired for it - currently just RearmForNextEncounter, the
        // normal end-of-battle path. (ScanOnce's "new Pokemon detected while
        // locked" recovery branch would be the other such path, but as of
        // this writing it can never actually run - see this round's
        // MIGRATION_GUIDE.md entry for the pre-existing, unrelated reason
        // why - so it is not wired up to this method.)
        private void FlushPendingLevelGenderEvents()
        {
            if (!levelGenderEventsPending)
                return;

            levelGenderEventsPending = false;
            FireEncounterEvents(pendingPokemonName, pendingLevel, pendingGender);
        }

        // ---- §115 failure reporting state ----
        //
        // The last root-cause message reported, so a failure that cannot fix
        // itself - missing OCR libraries being the common one - does not
        // write the same stack trace to the log once per scan for as long as
        // the tracker is left running.
        private string lastReportedFailure = string.Empty;
        private DateTime lastReportedFailureUtc = DateTime.MinValue;

        private const int FailureRepeatMinutes = 5;

        /// <summary>
        /// §115. What the user is actually told when a scan throws.
        ///
        /// This used to show ex.Message, and for the most common real failure
        /// that string is "Exception has been thrown by the target of an
        /// invocation." - the message of a TargetInvocationException wrapper
        /// whose actual cause sits in the inner exception and was never shown
        /// to anyone. A tester reading that has been told precisely nothing,
        /// which is why these come back as "it doesn't work" with no detail
        /// and cost a round trip each. Confirmed from a real report bundle:
        /// the log held DllNotFoundException "Failed to find library
        /// 'libleptonica-1.85.0.dll.so'" while the screen said only that
        /// something had been thrown by the target of an invocation.
        ///
        /// So report the ROOT cause, and when that root is a missing OCR
        /// native library - by far the most common way this fails on Linux
        /// and macOS - name the fix for THIS platform.
        ///
        /// The loop deliberately keeps running rather than stopping itself.
        /// Stopping would arguably be better - nothing can be detected
        /// without OCR - but that means reaching into the run-state the
        /// Start/Stop buttons own, and this change was made without a
        /// compiler to check it. The log spam, which was the concrete
        /// problem, is handled here instead.
        /// </summary>
        private void ReportTrackingFailure(Exception ex)
        {
            Exception root = ex.GetBaseException();

            bool ocrLibrariesMissing =
                root is DllNotFoundException &&
                (root.Message.Contains("leptonica", StringComparison.OrdinalIgnoreCase) ||
                 root.Message.Contains("tesseract", StringComparison.OrdinalIgnoreCase));

            // §336: the toolbar gets the fault, the log gets the paragraph.
            // A missing OCR library is the one failure where the fix is a
            // sentence long, and that sentence used to be the toolbar line.
            string message = ocrLibrariesMissing
                ? "Encounter detection cannot start - the Tesseract OCR libraries were " +
                  "not found, so nothing can be read from the game window. " +
                  OcrSetupHint() + " (" + root.Message + ")"
                : $"Tracking error: {root.Message}";

            StatusChanged?.Invoke(
                ocrLibrariesMissing
                    ? "Cannot start - Tesseract OCR libraries not found"
                    : $"Tracking error: {root.Message}");

            Log.Information("Tracking status: {Status}", message);
            TrackerDiagnostics.RecordError(root.Message);

            // The full exception reaches the log the first time it happens,
            // and at most every few minutes after that. A missing library
            // never becomes present mid-run, and without this the log fills
            // with the same twenty-five line stack trace over and over -
            // which is exactly what a real report bundle showed.
            bool sameAsLast = string.Equals(root.Message, lastReportedFailure, StringComparison.Ordinal);
            bool dueAgain = DateTime.UtcNow - lastReportedFailureUtc >
                            TimeSpan.FromMinutes(FailureRepeatMinutes);

            if (sameAsLast && !dueAgain)
                return;

            lastReportedFailure = root.Message;
            lastReportedFailureUtc = DateTime.UtcNow;

            // Log.Error writes the COMPLETE exception - stack trace and inner
            // exception chain - which is what makes a Report a Problem bundle
            // worth reading. That behaviour is unchanged; only how often it
            // repeats, and what the SCREEN says, are different.
            Log.Error(ex, "Encounter tracking loop error");
        }

        /// <summary>§115: how the OCR natives are meant to arrive on
        /// THIS platform, so the instruction is always something the reader
        /// actually has in front of them.
        ///
        /// Linux and macOS each need a setup script, and each is published
        /// beside the executable for its own platform - see the
        /// RID-conditioned Content items in the .csproj. The FOLDER is left
        /// vague on purpose. A zip can end up with those scripts one level
        /// above the app instead of inside it, which is precisely the
        /// packaging accident §116 exists to survive, so naming a folder
        /// here would be wrong about as often as it was right.
        ///
        /// Windows needs no script at all: the TesseractOCR package ships
        /// the Windows natives and a publish drops them in an x64 folder
        /// beside the exe. So a Windows failure here means that folder did
        /// not survive the trip - extracted on its own, or stripped by
        /// antivirus - and the fix is to extract the download again. Do NOT
        /// point Windows at fix-tesseract-native-libs.ps1: that one is a
        /// repo-side tool for preparing a publish and is not a Content item,
        /// so a tester's folder never contains it.</summary>
        /// <summary>§138. Runs CounterpartMatcher on a copy of the frame,
        /// off the tracking thread - the match is a few hundred milliseconds
        /// per candidate image, and the loop must keep watching for the
        /// catch result meanwhile. One call per encounter, from the
        /// rare-check branch that only fires once per encounter.</summary>
        /// <summary>§259. Remember that this encounter's form wants matching,
        /// and do it on the first popup-free frame of the same battle rather
        /// than on the frame the popup was found in - see the field comment.
        /// The species is captured now because lastDetectedPokemon can change
        /// under a forced re-arm before the match runs.</summary>
        private void QueueCounterpartIdentification(string species)
        {
            pendingFormMatchSpecies = species;
            pendingFormMatchUntilUtc = DateTime.UtcNow.AddMilliseconds(FormMatchWaitMs);

            Log.Information(
                "Counterpart match for {Pokemon} queued until the rare-encounter popup closes (the sprite is under it).",
                species);
        }

        /// <summary>§259. The tick-side half: nothing to do unless a match is
        /// queued; then wait for the popup to leave, run once, and forget.
        /// A popup still up after FormMatchWaitMs is reported as not
        /// identified, without a crop - there is nothing under it worth
        /// saving.</summary>
        private void RunPendingCounterpartIdentification(SKBitmap screenshot, SKRectI battleBounds)
        {
            if (pendingFormMatchSpecies is not { } species)
                return;

            if (DateTime.UtcNow > pendingFormMatchUntilUtc)
            {
                pendingFormMatchSpecies = null;

                Log.Information(
                    "Counterpart match for {Pokemon}: the rare-encounter popup was still up after {Seconds}s - not identified.",
                    species, FormMatchWaitMs / 1000);

                CounterpartIdentified?.Invoke(species, new CounterpartMatcher.MatchResult
                {
                    Species = species,
                    Identified = false,
                    Summary = "form not identified - the popup stayed over the sprite"
                });
                return;
            }

            if (RarePopupLocator.TryLocate(screenshot, out _))
                return;

            pendingFormMatchSpecies = null;
            StartCounterpartIdentification(screenshot, battleBounds, species);
        }

        private void StartCounterpartIdentification(SKBitmap screenshot, SKRectI battleBounds, string species)
        {
            SKBitmap copy = screenshot.Copy();

            _ = Task.Run(() =>
            {
                try
                {
                    CounterpartMatcher.MatchResult? result =
                        CounterpartMatcher.Identify(copy, battleBounds, species);

                    if (result is not null)
                        CounterpartIdentified?.Invoke(species, result);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Counterpart identification failed for {Pokemon}", species);
                }
                finally
                {
                    copy.Dispose();
                }
            });
        }

        private static string OcrSetupHint()
        {
            if (OperatingSystem.IsLinux())
                return "Run linux-setup.sh from the download - it sits either beside the " +
                       "app or one folder above it - then start tracking again.";

            if (OperatingSystem.IsMacOS())
                return "Run macos-start.sh from the download - it installs and links the " +
                       "libraries through Homebrew.";

            return "Extract the whole download again, keeping the x64 folder next to " +
                   "ProTrackerDatabase.Avalonia.exe, and check that antivirus has not " +
                   "removed leptonica-1.85.0.dll or tesseract55.dll from inside it.";
        }

        // ---- §134 idle-status helpers ------------------------------
        //
        // See the field comment above for why these exist. The rules, in
        // one place: an idle period begins at Start and again every time a
        // battle is confirmed over; nothing but a blank frame, an
        // undersized frame or an unreadable battle window may speak before
        // IdleStatusDelaySeconds have passed; and a text already on the
        // line is never re-sent, so the line changes only when the facts do.

        private void BeginIdlePeriod()
        {
            idleSinceUtc = DateTime.UtcNow;
            lastIdleStatus = string.Empty;
            unreadableTitleSinceUtc = DateTime.MinValue;
        }

        /// <summary>
        /// §336. One status, said twice: a SHORT line for the toolbar and the
        /// whole explanation for the log.
        ///
        /// The toolbar line sits in an auto-height row above the sprites and
        /// the table. A three-line message made that row three lines tall and
        /// pushed the entire window down; the message changing length moved
        /// everything back up again. A tester reported the jumping as making
        /// him uncomfortable, and he is right - nothing he is reading should
        /// move because the tracker changed its mind about how much to say.
        ///
        /// The row is pinned to one line in MainWindow.axaml, so ANY message
        /// now leaves the layout alone. This is the other half: say the short
        /// thing on the line, so the common case is not an ellipsis.
        ///
        /// What must survive the shortening is §134's and §321's lesson - a
        /// status line that cannot say what is wrong costs hours. So the
        /// short line still NAMES the fault, in capitals where there is one,
        /// and only the paragraph of advice moves to the log. "Tracking, but
        /// the PROClient capture is FROZEN" in a screenshot tells the same
        /// story the full sentence did; "Tracking" alone would not.
        ///
        /// Repeats are judged on the DETAIL, not the line. Two different
        /// faults can shorten to similar words, and deduplicating on the
        /// short form would drop the second one out of the log - which is the
        /// copy that is supposed to still have everything.
        /// </summary>
        private void ReportIdleStatus(string line, string detail, bool immediate = false)
        {
            if (!immediate &&
                DateTime.UtcNow - idleSinceUtc < TimeSpan.FromSeconds(IdleStatusDelaySeconds))
            {
                return;
            }

            if (string.Equals(detail, lastIdleStatus, StringComparison.Ordinal))
                return;

            lastIdleStatus = detail;

            Log.Information("Tracking status: {Status}", detail);
            StatusChanged?.Invoke(line);
        }

        /// <summary>Called for a frame in which no battle window was found
        /// while no encounter lock is held. Brightness comes from the
        /// RecordCapture call made for this same frame a few lines earlier
        /// in ScanOnce, so nothing is re-measured here.</summary>
        private void ReportIdleCaptureHealth(int width, int height)
        {
            int brightness = TrackerDiagnostics.LastFrameBrightness;

            bool blank = brightness >= 0 && brightness <= TrackerDiagnostics.BlankFrameBrightness;

            if (!blank)
            {
                blankSinceUtc = DateTime.MinValue;
            }
            else if (blankSinceUtc == DateTime.MinValue)
            {
                blankSinceUtc = DateTime.UtcNow;
            }

            // §294: a dark frame is a loading screen until it has lasted
            // longer than one. Said calmly meanwhile, because the tracker is
            // fine and the next encounter will be caught.
            if (blank && DateTime.UtcNow - blankSinceUtc < BlankFrameGrace)
            {
                ReportIdleStatus(
                    "Tracking - loading screen",
                    $"Tracking - the game is on a loading screen ({width}x{height}).",
                    immediate: false);
                return;
            }

            if (blank)
            {
                ReportIdleStatus(
                    "Tracking, but the PROClient capture is BLANK",
                    $"Tracking, but the PROClient capture is BLANK ({width}x{height}, " +
                    $"brightness {brightness}/255) - the tracker is being handed an empty " +
                    "image of the game window, so nothing can be detected. Click Report a " +
                    "Problem and send the saved files.",
                    immediate: true);
                return;
            }

            // §321: a capture that has been the same picture for a quarter of
            // an hour
            // is not "no battle right now" either. It comes AFTER the blank
            // check on purpose - a capture that was empty from its first
            // frame is better described as blank, and that check fires in
            // seconds where this one takes ten minutes.
            //
            // This is the status-line half of the warning only. A window
            // frozen ON a battle screen never reaches here, because the
            // locator keeps finding that battle in the dead image; the log
            // verdict in TrackerDiagnostics covers it and does not depend on
            // this path.
            if (TrackerDiagnostics.FramesAreFrozen)
            {
                ReportIdleStatus(
                    "Tracking, but the PROClient capture is FROZEN",
                    $"Tracking, but the PROClient capture is FROZEN ({width}x{height}) - " +
                    "every frame for the last several minutes has been the same picture, so " +
                    "nothing can be detected. If you are playing right now, the tracker is " +
                    "bound to a window that is not being redrawn - usually a second PRO " +
                    "client, or one behind another window. Stop and Start tracking, or use " +
                    "Assign Client to pick the client you are playing on.",
                    immediate: true);
                return;
            }

            // Below the locator's own minimum run width, or shorter than the
            // rows it probes, no frame can ever produce a battle window - so
            // this is not "no battle right now", it is "this window cannot
            // be the game".
            if (width < BattleWindowLocator.MinBattleWidth ||
                height < BattleWindowLocator.TitleBarProbeRows)
            {
                ReportIdleStatus(
                    $"Tracking, but the capture is only {width}x{height}",
                    $"Tracking, but the PROClient capture is only {width}x{height} - too " +
                    "small to contain a battle window, so the wrong window may be selected. " +
                    "Click Report a Problem and send the saved files.",
                    immediate: true);
                return;
            }

            // §336: the healthy line is the one on screen for hours at a
            // time, so it is the one that has to be still. No size, no
            // manual-boundary clause, no punctuation that changes with the
            // state - three words that never move. Both of those facts are
            // one click away in Tracking Check, which is where a reader who
            // wants them is already going, and §333 made the manual-boundary
            // one a verdict there rather than a number to compare.
            ReportIdleStatus(
                "Tracking PROClient",
                $"Tracking - watching PROClient ({width}x{height}); no battle window on screen" +
                (BattleWindowLocator.ManualBounds is null
                    ? "."
                    : " (manual boundaries are saved; they take over when automatic detection finds " +
                      "nothing, or finds a box away from them while they hold a battle window)."));
        }

        /// <summary>
        /// §299. While waiting for a battle to close: is the window the
        /// locator just found still the battle that ended? Answers true
        /// while it is - including while the answer is not yet certain -
        /// and, when it has been certain for BattleNotOursGrace, ends the
        /// wait itself and answers false.
        ///
        /// Three readings of the title, and only the third ends anything:
        ///
        ///   - It reads as this battle. The window is the one that ended,
        ///     lingering; the clock is reset and the wait goes on. This is
        ///     the normal case for the second or two between the last
        ///     message and the window closing.
        ///   - It reads as a battle whose species cannot be named. A boss
        ///     title never matches a wild species and a mangled read often
        ///     does not either, so this counts as "still this battle" - the
        ///     conservative answer, because ending the wait wrongly is what
        ///     counts one battle twice.
        ///   - It does not read as a battle at all, or it names a DIFFERENT
        ///     species. The first is the chat panel the locator settled on;
        ///     the second is the next encounter, already on screen. Both
        ///     mean this battle is over.
        ///
        /// Throttled to WaitingTitleReadIntervalMs; a frame that is not read
        /// on keeps the previous answer rather than resetting the clock,
        /// because the clock measures real time and not reads.
        /// </summary>
        private bool StillTheBattleThatEnded(SKBitmap screenshot, SKRectI battleBounds)
        {
            DateTime now = DateTime.UtcNow;

            if (now >= nextWaitingTitleReadUtc)
            {
                nextWaitingTitleReadUtc =
                    now.AddMilliseconds(WaitingTitleReadIntervalMs);

                bool read = EncounterDetector.TryReadBattleTitle(
                    screenshot,
                    battleBounds,
                    out _,
                    out bool looksLikeBattleTitle,
                    out string? matchedPokemon);

                bool ours =
                    read
                    && looksLikeBattleTitle
                    && (string.IsNullOrWhiteSpace(matchedPokemon)
                        || string.IsNullOrWhiteSpace(lastDetectedPokemon)
                        || string.Equals(
                            matchedPokemon,
                            lastDetectedPokemon,
                            StringComparison.OrdinalIgnoreCase));

                if (ours)
                {
                    battleNotOursSinceUtc = DateTime.MinValue;
                    return true;
                }

                if (battleNotOursSinceUtc == DateTime.MinValue)
                {
                    battleNotOursSinceUtc = now;

                    Log.Information(
                        "Waiting for the {Pokemon} battle to close, but the window at {Bounds} " +
                        "no longer reads as that battle ({Reading}) - watching it for {Seconds:0.#}s.",
                        string.IsNullOrWhiteSpace(lastDetectedPokemon) ? "(unnamed)" : lastDetectedPokemon,
                        battleBounds,
                        !read || !looksLikeBattleTitle
                            ? "no battle title"
                            : "a battle with " + matchedPokemon,
                        BattleNotOursGrace.TotalSeconds);
                }
            }

            if (battleNotOursSinceUtc == DateTime.MinValue)
                return true;

            if (now - battleNotOursSinceUtc < BattleNotOursGrace)
                return true;

            Log.Information(
                "The window at {Bounds} has not been the {Pokemon} battle for {Seconds:0.#}s - " +
                "treating that battle as ended.",
                battleBounds,
                string.IsNullOrWhiteSpace(lastDetectedPokemon) ? "(unnamed)" : lastDetectedPokemon,
                (now - battleNotOursSinceUtc).TotalSeconds);

            RearmForNextEncounter(
                "Confirmed battle ended - ready for next encounter.");

            return false;
        }

        /// <summary>A battle window was located but its title produced no
        /// Pokemon name. While a real "VS" title is being read that is the
        /// identifying message's business; only a title that never reads at
        /// all, for UnreadableTitleDelaySeconds without a break, is reported
        /// here.</summary>
        private void ReportUnreadableTitle(SKRectI battleBounds, bool looksLikeBattleTitle)
        {
            if (looksLikeBattleTitle)
            {
                unreadableTitleSinceUtc = DateTime.MinValue;
                return;
            }

            if (unreadableTitleSinceUtc == DateTime.MinValue)
            {
                unreadableTitleSinceUtc = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - unreadableTitleSinceUtc <
                TimeSpan.FromSeconds(UnreadableTitleDelaySeconds))
            {
                return;
            }

            ReportIdleStatus(
                "Tracking, but no 'VS' title can be read",
                $"Tracking - a battle-window shape is on screen at ({battleBounds.Left}," +
                $"{battleBounds.Top}) {battleBounds.Width}x{battleBounds.Height}" +
                (BattleWindowLocator.LastLocateUsedManual
                    ? " (from your saved manual boundaries)"
                    : string.Empty) +
                ", but no 'VS' title can be read from it. If a wild battle really is open, " +
                "click Report a Problem and send the saved files.",
                immediate: true);
        }

        private string StandDownStatus() =>
            "Encounter tracking is standing down - the " +
            (bossBattleActive ? "boss" : "PVP") +
            " tracker reports a battle in progress. If no boss or PVP battle is on " +
            "screen, that signal is stale; it clears by itself a few seconds after " +
            "the battle window is gone.";

        private async Task TrackingLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // §101 heartbeat: one timestamp per loop pass, BEFORE the
                    // scan, so the Admin Console and watchdog can tell "loop
                    // alive but finding nothing" (heartbeat fresh) from "loop
                    // dead or wedged" (heartbeat stale) - a lack of
                    // encounters alone is never treated as a stall.
                    TrackerDiagnostics.RecordScan();

                    ScanOnce();
                }
                catch (Exception ex)
                {
                    // §115 moved the body of this into ReportTrackingFailure -
                    // see there for why ex.Message was the wrong thing to show.
                    ReportTrackingFailure(ex);
                }

                int delay =
                    GetCurrentScanDelay();

                await Task.Delay(
                    delay,
                    cancellationToken
                );
            }
        }

        private static readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

        private static SKBitmap? CaptureScreenshot()
        {
            byte[]? pngBytes = captureService.CaptureSelectedWindowPng();
            return pngBytes is null ? null : ImageOps.DecodePng(pngBytes);
        }

        private void ScanOnce()
        {
            // A boss battle or PVP battle is running - BossCooldownTracker/
            // PvpTracker own this battle window entirely, so this tracker must not
            // touch it at all: not capture, not OCR, not status messages.
            // Otherwise this loop OCRs the opponent's active Pokemon and wrongly
            // registers it as a wild encounter (confirmed via a real tester's log
            // for boss battles - "Erika sends out Ferrothorn!" was picked up here
            // as if Ferrothorn were a wild encounter target - and PVP screens show
            // real Pokemon sprites/names the same way).
            if (bossBattleActive || pvpBattleActive)
            {
                // §134: after the idle delay, say so - a stale flag here
                // is otherwise indistinguishable from a healthy loop.
                ReportIdleStatus(
                    "Standing down - " + (bossBattleActive ? "boss" : "PVP") + " battle in progress",
                    StandDownStatus());
                return;
            }

            using SKBitmap? screenshot =
                CaptureScreenshot();

            if (screenshot == null)
            {
                TrackerDiagnostics.RecordCapture(
                    ok: false, error: "capture service returned no frame");

                StatusChanged?.Invoke(
                    "Waiting for PROClient..."
                );

                return;
            }

            // §114: brightness travels with the frame so a black capture is
            // distinguishable from a working one in the log. Sampled, not
            // scanned - see ImageOps.Fingerprint.
            //
            // §321: the same grid pass also yields a hash of what it sampled,
            // which is how a frozen capture is caught. Brightness alone could
            // not do it - across five recordings of real play the mean held a
            // single value for up to 8.2 seconds while the game was plainly
            // moving, because ~1600 sample points are easy for a walking
            // sprite to miss entirely.
            var fingerprint = ImageOps.Fingerprint(screenshot);

            TrackerDiagnostics.RecordCapture(
                ok: true, screenshot.Width, screenshot.Height,
                meanBrightness: fingerprint.Mean,
                frameHash: fingerprint.Hash);

            // ============================================================
            // ROUTE NAME (throttled, runs in and out of battle)
            // ============================================================
            //
            // Deliberately runs whether or not a battle window is present -
            // the player is on some map regardless of what else is happening,
            // a battle-time confirmation is exactly what repairs a
            // transition-stale record (§102), and this reuses the screenshot
            // already captured above rather than capturing a second time. See
            // RouteDetector.cs for the OCR itself and CornerCheckIntervalMs's
            // declaration comment for why this doesn't run every tick.
            if (DateTime.UtcNow >= nextCornerCheckUtc)
            {
                nextCornerCheckUtc =
                    DateTime.UtcNow.AddMilliseconds(CornerCheckIntervalMs);

                if (RouteDetector.TryDetectCorner(
                        screenshot,
                        out string? cornerRouteName))
                {
                    // True only while the battle lock is held, the battle is
                    // not already known to be over, and the encounter's own
                    // events are no longer pending - see the event's
                    // declaration comment for why each clause exists.
                    bool duringBattle =
                        encounterAlreadyRegistered &&
                        !waitingForBattleToDisappear &&
                        !levelGenderEventsPending;

                    CornerInfoDetected?.Invoke(cornerRouteName, duringBattle);
                }
            }

            bool battleExists =
    BattleWindowLocator.TryLocate(
        screenshot,
        out SKRectI battleBounds
    );

            // §134: the capture heartbeat line reports how many of the
            // frames since the previous line held a battle window.
            TrackerDiagnostics.RecordBattleLocate(battleExists);

            // ============================================================
            // NO BATTLE WINDOW
            // ============================================================

            if (!battleExists)
            {
                consecutiveBattleScans = 0;

                // ========================================================
                // PRIMARY END-OF-BATTLE PATH
                // ========================================================

                if (waitingForBattleToDisappear)
                {
                    RearmForNextEncounter(
                        "Confirmed battle ended - ready for next encounter."
                    );

                    Log.Information(
                        "Confirmed battle ended - ready for next encounter."
                    );

                    return;
                }

                // ========================================================
                // BATTLE TEMPORARILY HIDDEN
                // ========================================================
                //
                // Do NOT re-arm merely because we cannot currently
                // locate the battle window.
                //
                // The player may have opened:
                // - the map
                // - a Pokemon card / summary
                // - another PRO menu
                //
                // If the battle becomes visible again, the existing
                // encounter lock remains intact.

                // §134: with no lock held there is nothing to protect,
                // and nothing else on this path ever touches the status
                // line - so this is where a frame that shows no battle gets
                // described. Undersized frames are said at once; a blank one
                // only once it has outlasted a map load (§294); an ordinary
                // quiet frame waits out the idle delay.
                if (!encounterAlreadyRegistered)
                    ReportIdleCaptureHealth(screenshot.Width, screenshot.Height);

                unreadableTitleSinceUtc = DateTime.MinValue;

                return;
            }

            // Battle is currently visible again,
            // so any partial no-battle state is cleared.

            // ============================================================
            // CONFIRM NEW BATTLE WINDOW
            // ============================================================

            if (!encounterAlreadyRegistered &&
                    !waitingForBattleToDisappear)
            {
                consecutiveBattleScans++;

                if (consecutiveBattleScans <
                    BattleConfirmationScans)
                {
                    return;
                }
            }

            // ============================================================
            // OLD BATTLE IS ENDING
            // ============================================================

            if (waitingForBattleToDisappear)
            {
                // We already saw either:
                //
                // "Success! You caught..."
                //
                // or
                //
                // "You have run away..."
                //
                // The battle window may remain visible briefly afterward.
                //
                // Absolutely NOTHING in this old battle is allowed to
                // register another encounter.

                // §293. Unless what the locator found is not this battle's
                // window at all. The real battle lingering is the same box
                // within a few pixels; a box elsewhere - the map's Pokemon
                // panel, a menu the locator took for a battle - means the
                // battle has closed and something else is on screen. Judged
                // by the same rule §142 uses to decide whether the scan and
                // a manual box are one window.
                if (!BattleWindowLocator.SameWindow(
                        battleBounds,
                        lockedBattleBounds,
                        screenshot.Width,
                        screenshot.Height))
                {
                    Log.Information(
                        "Battle window moved from {Locked} to {Now} while waiting for it to close - " +
                        "treating the battle as ended.",
                        lockedBattleBounds,
                        battleBounds);

                    RearmForNextEncounter(
                        "Confirmed battle ended - ready for next encounter."
                    );

                    return;
                }

                // §299. The window is still where it was - but is it still
                // THIS battle? §293's position test is the cheap half of the
                // question and cannot answer this one, because PRO draws the
                // next battle in the same place, and other dark panels sit in
                // the same place too. So read the title there - the same read
                // that identified the encounter in the first place - and let
                // it answer. StillTheBattleThatEnded ends the wait itself
                // when it is sure.
                StillTheBattleThatEnded(screenshot, battleBounds);

                return;
            }

            // ============================================================
            // CHECK BATTLE MESSAGE
            // ============================================================

            CatchResult catchResult =
                CatchDetector.Detect(
                    screenshot,
                    battleBounds
                );

            if (catchResult == CatchResult.None)
            {
                // Previous catch-attempt message disappeared.
                // This allows another Pokeball attempt to be counted.

                catchResultAlreadyRegistered = false;
            }
            else if (!catchResultAlreadyRegistered)
            {
                // §395. A battle can end before its title was ever read: a
                // hunter who runs from every non-target the instant the
                // battle opens is out again inside the second or two the
                // title takes to become readable, and this message is the
                // first thing the scan understands about the battle. The
                // outcome used to be raised regardless, and the view model
                // books an outcome to the CURRENT encounter - the previous
                // battle's Pokémon, since this one never registered - so a
                // Makuhita ran away 57 times in 56 encounters while the
                // battle that actually ended was counted for nobody.
                //
                // So identify first. The title is on screen under this
                // message (it is the battle's header, not the message's),
                // and with a readable name the encounter registers now, in
                // order, and the outcome that follows lands on it. With no
                // readable name the outcome is not booked to anyone: the
                // message is remembered so it is not re-read every scan,
                // and the title keeps being tried below on later scans -
                // registration clears the memory, so a title that reads
                // while the message is still up counts both, in order.
                if (!encounterAlreadyRegistered)
                {
                    if (EncounterDetector.TryDetectEncounter(
                            screenshot,
                            out string lateName,
                            out _) &&
                        !string.IsNullOrWhiteSpace(lateName))
                    {
                        Log.Information(
                            "{Result} seen before the encounter was registered - the title reads {Pokemon}; registering it first, then the outcome.",
                            catchResult,
                            lateName);

                        unreadableTitleSinceUtc = DateTime.MinValue;
                        RegisterEncounter(screenshot, battleBounds, lateName);
                    }
                    else
                    {
                        Log.Warning(
                            "{Result} seen before the encounter could be identified - the title is unreadable, so the outcome is counted for nobody rather than for the previous Pokémon. It counts if the title reads while the message is still up.",
                            catchResult);

                        catchResultAlreadyRegistered = true;
                        return;
                    }
                }

                catchResultAlreadyRegistered = true;

                switch (catchResult)
                {
                    // ====================================================
                    // SUCCESSFUL CATCH
                    // ====================================================

                    case CatchResult.Success:

                        // Fresh, catch-time-only Level/Gender reading for the
                        // Hunting Log - see MIGRATION_GUIDE.md §76. By now the
                        // whole catch attempt has played out (at minimum the
                        // Pokeball's throw/shake animation, typically a couple
                        // of seconds), so PRO's own name/level/gender tag has
                        // had far more time to finish rendering than the
                        // single OCR attempt taken the instant the encounter
                        // first appeared - the same reasoning CurrentRouteText's
                        // own corner OCR already benefits from by virtue of
                        // being read late rather than at encounter time.
                        // Deliberately independent of the encounterLevel/
                        // encounterGender read in the REGISTER ENCOUNTER
                        // section below - that pair now only drives the brief
                        // on-screen status text for the moment the encounter
                        // first appears, nothing else reads it anymore. This
                        // pair is what the Hunting Log actually saves - see
                        // MainWindowViewModel.OnCatchResultDetected.
                        int? catchTickLevel = TryDetectLevelSafely(
                            screenshot, battleBounds, lastDetectedPokemon, out LevelReadQuality catchTickQuality);
                        RecordLevelVote(catchTickLevel, catchTickQuality);

                        // Consensus first (§95; selection reworked in §96,
                        // ranked by read quality in §396): the best-supported
                        // value with at least two agreeing reads in the best
                        // quality that read anything outranks whatever this
                        // single frame said. Then - §396 - a lone CLEAN read
                        // of the tag when that is the best evidence the whole
                        // battle produced, because the alternative is this
                        // tick's own lone read, and a clean read outranks a
                        // cluttered or rescued one. Then the catch-tick read
                        // itself - and then null, which the Hunting Log
                        // records as unknown rather than a guess. A lone
                        // unrepeated vote of lesser quality is still never
                        // promoted (§96): the junk reads behind the "level 4"
                        // reports were exactly such one-off values.
                        int? consensus = SelectConsensusLevel();
                        int? catchLevel = consensus ?? LoneCleanLevel() ?? catchTickLevel;

                        if (consensus is int agreed &&
                            catchTickLevel is int single &&
                            agreed != single)
                        {
                            Log.Information(
                                "Catch-tick level {SingleRead} overridden by consensus {Consensus} for {Pokemon}",
                                single,
                                agreed,
                                lastDetectedPokemon
                            );
                        }

                        string? catchGender = TryDetectGenderSafely(screenshot, battleBounds, lastDetectedPokemon);

                        CatchLevelDetected?.Invoke(catchLevel);
                        CatchGenderDetected?.Invoke(catchGender);

                        CatchResultDetected?.Invoke(
                            CatchResult.Success
                        );

                        waitingForBattleToDisappear = true;
                        Log.Information(
    "Successful catch detected for {Pokemon}",
    lastDetectedPokemon
);

                        StatusChanged?.Invoke(
                            "Successful catch - waiting for battle to close."
                        );

                        // CRITICAL:
                        // Do not continue into encounter detection.
                        return;


                    // ====================================================
                    // FAILED BALL
                    // ====================================================

                    case CatchResult.Failed:

                        // Failed catch does NOT end the battle.

                        Log.Information(
    "Failed catch detected for {Pokemon}",
    lastDetectedPokemon
);

                        StatusChanged?.Invoke(
                            "Failed catch detected."
                        );

                        CatchResultDetected?.Invoke(
                            CatchResult.Failed
                        );

                        break;


                    // ====================================================
                    // RAN AWAY, OR THE BATTLE ENDED SOME OTHER WAY
                    // ====================================================
                    //
                    // §123 split these two apart in CatchDetector and
                    // they share this arm deliberately: as far as the
                    // encounter lock is concerned they are the same event -
                    // the battle is over and nothing was caught, so stop
                    // scanning and wait for it to close. That behaviour is
                    // exactly what §101's evidence required and it is
                    // unchanged.
                    //
                    // What IS new is the Invoke below. Neither of these ever
                    // reached the view model before: this arm returned
                    // without raising CatchResultDetected at all, so a run
                    // was detected, logged, and then dropped. Nothing could
                    // count runs because nothing was ever told about them.

                    case CatchResult.RunAway:
                    case CatchResult.BattleEnded:
                    {
                        // Braced: C# gives a switch BLOCK one declaration
                        // space shared by every section, so endedReason below
                        // would otherwise be visible to the arms above it.
                        // Its own scope, and no doubt about it.
                        CatchResultDetected?.Invoke(catchResult);

                        waitingForBattleToDisappear = true;

                        string endedReason =
                            catchResult == CatchResult.RunAway
                                ? "Run away"
                                : "Battle ended (fainted/EXP)";

                        Log.Information(
    "{Reason} detected for {Pokemon} - waiting for battle to close.",
    endedReason,
    lastDetectedPokemon
);

                        StatusChanged?.Invoke(
                            endedReason + " - waiting for battle to close."
                        );

                        // CRITICAL:
                        // Do not scan the old Pokémon again.
                        return;
                    }
                }
            }

            // §259. A queued form match runs on the first frame the popup is
            // not in - only while this encounter's battle is still on.
            if (encounterAlreadyRegistered &&
                !catchResultAlreadyRegistered &&
                !waitingForBattleToDisappear)
            {
                RunPendingCounterpartIdentification(screenshot, battleBounds);
            }

            // ============================================================
            // ENCOUNTER ALREADY REGISTERED
            // ============================================================
            if (encounterAlreadyRegistered &&
                !rareEncounterAlreadyRegistered &&
                !string.IsNullOrWhiteSpace(lastDetectedPokemon) &&
                DateTime.UtcNow <= rareCheckUntilUtc &&
                DateTime.UtcNow >= nextRareCheckUtc)
            {
                nextRareCheckUtc =
                    DateTime.UtcNow.AddMilliseconds(
                        RareCheckIntervalMs
                    );

                RareEncounterType existingRareType =
                    RareEncounterDetector.Detect(
                        screenshot,
                        battleBounds
                    );

                if (existingRareType !=
                    RareEncounterType.None)
                {
                    rareEncounterAlreadyRegistered = true;

                    // Only log when a rare type is actually found. This check
                    // re-runs every RareCheckIntervalMs for up to
                    // RareCheckWindowMs after every single encounter (rare or
                    // not), so logging unconditionally here previously printed a
                    // "Rare encounter detected: X (None)" line roughly ten times
                    // a second for every ordinary encounter too - which reads as
                    // constant false detections at a glance, and made a real
                    // report's log much harder to search through. The raw OCR
                    // attempt (garbled text or not) is still always logged inside
                    // RareEncounterDetector.Detect() itself, so no diagnostic
                    // information is lost by only logging real hits here.
                    Log.Information(
                        "Rare encounter confirmed: {Pokemon} ({RareType})",
                        lastDetectedPokemon,
                        existingRareType
                    );

                    // §138: the status line for a shiny or a form is composed
                    // by MainWindowViewModel now, from the encounter's own
                    // level and map ("Special form detected Lv. 30 Wingull
                    // (Vulcan Cove)"), so the two plain lines that used to be
                    // sent from here are gone - they only ever got overwritten.
                    RareEncounterDetected?.Invoke(
                        lastDetectedPokemon,
                        existingRareType
                    );

                    // §138: which form? The frame is still in hand, so hand a
                    // copy to the matcher off this thread; the loop's own frame
                    // is disposed at the end of the tick.
                    if (existingRareType == RareEncounterType.Form)
                        QueueCounterpartIdentification(lastDetectedPokemon);
                }
            }

            // ============================================================
            // LEVEL CONSENSUS SAMPLING (§95, widened in §96)
            // ============================================================
            //
            // Cheap and self-limiting: runs until MaxLevelSamples attempts
            // are spent - deliberately NOT stopping at the first pair of
            // agreeing reads anymore, so a clean majority can keep piling
            // up past an early junk pair (§96) - and never once the battle
            // is ending.
            if (encounterAlreadyRegistered &&
                !waitingForBattleToDisappear &&
                levelSamplesTaken < MaxLevelSamples &&
                DateTime.UtcNow >= nextLevelSampleUtc)
            {
                nextLevelSampleUtc =
                    DateTime.UtcNow.AddMilliseconds(LevelSampleIntervalMs);

                levelSamplesTaken++;

                int? sampledLevel = TryDetectLevelSafely(
                    screenshot, battleBounds, lastDetectedPokemon, out LevelReadQuality sampledQuality);

                RecordLevelVote(sampledLevel, sampledQuality);
            }

            // ============================================================
            // RETRY LEVEL/GENDER IF NOT YET RESOLVED
            // ============================================================
            //
            // See LevelGenderRetryWindowMs's declaration comment - the name/
            // level/gender tag does not always finish rendering on the exact
            // tick the battle title itself becomes readable, so an encounter
            // that registered with a null Level and/or Gender gets a short
            // second chance here, on later ticks, before it's finally
            // reported with whatever it has.
            if (levelGenderEventsPending &&
                DateTime.UtcNow >= nextLevelGenderRetryUtc)
            {
                nextLevelGenderRetryUtc =
                    DateTime.UtcNow.AddMilliseconds(
                        LevelGenderRetryIntervalMs
                    );

                if (pendingLevel is null)
                {
                    pendingLevel =
                        TryDetectLevelSafely(screenshot, battleBounds, pendingPokemonName, out _);
                }

                if (pendingGender is null)
                {
                    pendingGender =
                        TryDetectGenderSafely(screenshot, battleBounds, pendingPokemonName);
                }

                bool bothResolved =
                    pendingLevel is not null && pendingGender is not null;

                bool retryDeadlinePassed =
                    DateTime.UtcNow >= pendingEventsDeadlineUtc;

                if (bothResolved || retryDeadlinePassed)
                {
                    levelGenderEventsPending = false;
                    FireEncounterEvents(pendingPokemonName, pendingLevel, pendingGender);
                }
            }

            // ============================================================
            // ENCOUNTER IS ALREADY LOCKED
            // ============================================================
            //
            // The current battle has already been counted.
            // Rare detection may continue during its five-second window,
            // and a pending Level/Gender retry may continue during its own
            // much shorter one, but normal encounter OCR must not run again.
            //
            if (encounterAlreadyRegistered)
            {
                return;
            }

            // ============================================================
            // SECONDARY NEW-BATTLE RECOVERY
            // ============================================================

            string? recoveredPokemonName = null;

            if (encounterAlreadyRegistered)
            {
                // Normally the explicit Run Away / Catch detector
                // unlocks us between battles.
                //
                // However, some clients may fail to OCR that ending text.
                // If we can clearly identify a DIFFERENT wild Pokémon,
                // the old battle cannot still be active.

                if (EncounterDetector.TryDetectEncounter(
                        screenshot,
                        out string possiblePokemon,
                        out _) &&
                    !string.IsNullOrWhiteSpace(possiblePokemon) &&
                    !possiblePokemon.Equals(
                        lastDetectedPokemon,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // §336: short on the bar, whole on the log line. The
                    // two names are what a reader needs when this goes
                    // wrong, and the log is where they look for them.
                    StatusChanged?.Invoke($"New Pokémon on screen - {possiblePokemon}");

                    Log.Information(
                        "New Pokemon detected while locked: {Was} -> {Now}. Forcing battle re-arm.",
                        lastDetectedPokemon,
                        possiblePokemon);

                    // Clear the stale battle lock.
                    encounterAlreadyRegistered = false;
                    rareEncounterAlreadyRegistered = false;
                    pendingFormMatchSpecies = null;
                    catchResultAlreadyRegistered = false;
                    identifyingStatusAlreadyShown = false;

                    waitingForBattleToDisappear = false;

                    lastDetectedPokemon = string.Empty;

                    // We already successfully OCR'd the new Pokémon,
                    // so don't make Tesseract identify it again below.
                    recoveredPokemonName =
                        possiblePokemon;
                }
                else
                {
                    // Same Pokémon, no Pokémon, or unreadable title.
                    //
                    // Stay locked. This preserves the duplicate protection
                    // when a menu/window covers and uncovers the battle.
                    return;
                }
            }



            if (encounterAlreadyRegistered)
            {
                // We are still inside the same battle.
                //
                // It does NOT matter if:
                //
                // - the title gets covered
                // - a Pokemon summary window covers it
                // - another player walks over it
                // - OCR temporarily fails
                // - the name disappears and comes back
                //
                // This battle has already been counted.
                //
                // Only an explicit battle-ending message can eventually
                // unlock us.

                return;
            }


            // ============================================================
            // DETECT NEW ENCOUNTER
            // ============================================================

            string pokemonName;

            if (!string.IsNullOrWhiteSpace(
                    recoveredPokemonName))
            {
                // The secondary recovery already identified it.
                pokemonName =
                    recoveredPokemonName;
            }
            else
            {
                if (!EncounterDetector.TryDetectEncounter(
                        screenshot,
                        out pokemonName,
                        out bool looksLikeBattleTitle))
                {
                    // Only show "identifying" for something that actually looks
                    // like a real battle title (contains "VS") - without this,
                    // BattleWindowLocator's generic dark-bar heuristic
                    // false-positiving on other dark UI panels (e.g. NPC
                    // dialogue boxes - confirmed via a real tester's screenshot)
                    // left this message stuck on screen indefinitely for
                    // something that was never a battle at all.
                    if (looksLikeBattleTitle && !identifyingStatusAlreadyShown)
                    {
                        identifyingStatusAlreadyShown = true;

                        StatusChanged?.Invoke(
                            "Battle detected - identifying Pokémon..."
                        );
                    }

                    // §134: a battle-window shape that stays on screen
                    // with no readable title is the one quiet case the
                    // frame-health line cannot see, because a window WAS
                    // located.
                    ReportUnreadableTitle(battleBounds, looksLikeBattleTitle);

                    return;
                }

                if (string.IsNullOrWhiteSpace(
                        pokemonName))
                {
                    return;
                }
            }

            unreadableTitleSinceUtc = DateTime.MinValue;

            // §395: the registration is its own method now, so the battle
            // message check above can register a late-read title too.
            RegisterEncounter(screenshot, battleBounds, pokemonName);
        }

        /// <summary>§395. Counts the encounter whose title just read as
        /// <paramref name="pokemonName"/>: the lock, the level and gender
        /// reads, EncounterDetected, and the registration-tick rare check.
        /// Verbatim the REGISTER ENCOUNTER section of ScanOnce, moved so
        /// that a battle-ending message seen before the title was read can
        /// register the encounter first and be counted for the right
        /// Pokémon - see the CHECK BATTLE MESSAGE section.</summary>
        private void RegisterEncounter(SKBitmap screenshot, SKRectI battleBounds, string pokemonName)
        {
            // ============================================================
            // REGISTER ENCOUNTER
            // ============================================================

            encounterAlreadyRegistered = true;
            lockedBattleBounds = battleBounds;
            consecutiveBattleScans = 0;
            rareCheckUntilUtc =
                DateTime.UtcNow.AddMilliseconds(
                    RareCheckWindowMs
                );

            nextRareCheckUtc =
                DateTime.MinValue;

            lastDetectedPokemon = pokemonName;

            // A brand-new encounter means any catch result from
            // the previous battle is no longer relevant.
            catchResultAlreadyRegistered = false;

            // ...and neither are its level votes (§95).
            ResetLevelConsensus();

            // Wrapped defensively, same as GenderDetector.TryDetectGender
            // immediately below - a bug/edge case in either detector must
            // not be able to swallow an already-confirmed encounter
            // (encounterAlreadyRegistered is already true by this point, and
            // none of the three per-encounter events have fired yet).
            int? encounterLevel = TryDetectLevelSafely(
                screenshot, battleBounds, pokemonName, out LevelReadQuality encounterLevelQuality);
            registrationLevelRead = encounterLevel;
            RecordLevelVote(encounterLevel, encounterLevelQuality);
            string? encounterGender = TryDetectGenderSafely(screenshot, battleBounds, pokemonName);

            // §103: the old generic "Encounter detected..." status line fired
            // from here - MainWindowViewModel.RegisterEncounter now composes
            // the richer "Encountered Lv. X Name (Location)" message instead
            // (it owns the location and the mid-battle refinements this line
            // could never see), so firing a second, poorer status from this
            // thread would only race it.

            // Fire immediately, either way - see MIGRATION_GUIDE.md §76. The
            // Hunting Log no longer reads encounterLevel/encounterGender at
            // all (it now takes a completely fresh, independent reading much
            // later, at catch-success time instead - see ScanOnce's
            // SUCCESSFUL CATCH case), so nothing downstream benefits anymore
            // from delaying this for a same-encounter retry - doing so only
            // ever held up CurrentEncounterSprite/PreviousEncounterSprite
            // from updating, by up to LevelGenderRetryWindowMs (a full
            // second), for a reason that no longer applies. Whatever came
            // back from the single OCR attempt above is shown in the status
            // line above and reported to MainWindowViewModel right now.
            //
            // This line is also the only place levelGenderEventsPending was
            // ever set to true. Removing that assignment - rather than
            // deleting the retry machinery it used to drive - leaves the
            // "RETRY LEVEL/GENDER" block above, FlushPendingLevelGenderEvents,
            // and the pending* fields permanently unreachable but harmless:
            // levelGenderEventsPending can now never become true again, so
            // that block's outer condition never passes and
            // FlushPendingLevelGenderEvents's own early-out always takes it.
            // Left in place rather than removed, since there is no compiler
            // available in the environment this was written in to verify a
            // larger excision would not miss some other reference - see
            // MIGRATION_GUIDE.md §76 for the full reasoning.
            FireEncounterEvents(pokemonName, encounterLevel, encounterGender);

            // Rare (shiny/form) detection runs AFTER FireEncounterEvents on
            // purpose - see MIGRATION_GUIDE.md §94. These invokes become
            // Dispatcher posts handled on the UI thread in the order they
            // fire here, and MainWindowViewModel.RegisterEncounter (the
            // EncounterDetected handler) resets currentEncounterRareType for
            // every new encounter. When this first Detect attempt hit on the
            // very same tick that registered the encounter - the common case
            // for the form popup, which is already fully visible by then -
            // firing the rare event FIRST meant that reset ran one post
            // later and silently wiped the just-stored Shiny/Form flag, so a
            // catch minutes later logged as an ordinary Pokemon. Fired after
            // EncounterDetected instead, the reset lands first and the flag
            // survives until the catch resolves. The re-check window path
            // above was never affected (its posts always trail
            // EncounterDetected by whole ticks) and is unchanged.
            //
            // rareEncounterAlreadyRegistered is also set here now - the old
            // register-tick path never set it, so a hit here left the
            // re-check window armed and the SAME popup fired
            // RareEncounterDetected a second time ~100ms later, double-
            // counting the form/shiny stats and playing the alert sound
            // twice.
            RareEncounterType rareType =
                RareEncounterDetector.Detect(
                    screenshot,
                    battleBounds
                );

            if (rareType != RareEncounterType.None)
            {
                rareEncounterAlreadyRegistered = true;

                Log.Information(
                    "Rare encounter confirmed on registration tick: {Pokemon} ({RareType})",
                    pokemonName,
                    rareType
                );

                // §138: status composed by the view model (see the
                // re-check window path above); the matcher gets a copy of
                // this frame the same way.
                RareEncounterDetected?.Invoke(
                    pokemonName,
                    rareType
                );

                if (rareType == RareEncounterType.Form)
                    QueueCounterpartIdentification(pokemonName);
            }
        }

        // ---- §95/§96 level-consensus helpers (ranked by quality in §396) ----

        /// <summary>§396. One empty tally per LevelReadQuality value, in the
        /// enum's order, so a quality indexes its own tally directly.</summary>
        private static Dictionary<int, int>[] CreateLevelTallies()
        {
            var tallies = new Dictionary<int, int>[Enum.GetValues<LevelReadQuality>().Length];

            for (int i = 0; i < tallies.Length; i++)
                tallies[i] = new Dictionary<int, int>();

            return tallies;
        }

        private void ResetLevelConsensus()
        {
            foreach (Dictionary<int, int> tally in levelVotesByQuality)
                tally.Clear();

            registrationLevelRead = null;
            levelSamplesTaken = 0;
            nextLevelSampleUtc = DateTime.MinValue;
            lastRefinedConsensusLevel = null;
        }

        private void RecordLevelVote(int? level, LevelReadQuality quality)
        {
            if (level is not int value)
                return;

            Dictionary<int, int> tally = levelVotesByQuality[(int)quality];

            tally[value] =
                tally.TryGetValue(value, out int votes) ? votes + 1 : 1;

            // Live refinement for the session encounter history (§99): the
            // moment the tally's best two-plus-vote value appears or changes,
            // report it - the register-time level may have been null, and the
            // history record for this encounter should correct itself without
            // waiting for a catch. Same selection rule as catch time
            // (SelectConsensusLevelCore), fired only on a CHANGE, so a stable
            // consensus costs one small dictionary scan per vote and no
            // events at all.
            //
            // NOT fired while levelGenderEventsPending: until
            // FireEncounterEvents has run, EncounterDetected hasn't been
            // posted yet, so a refinement posted now would reach
            // MainWindowViewModel BEFORE the encounter it belongs to exists
            // there and land on the PREVIOUS encounter's history record (two
            // agreeing sampler votes can beat the 1s retry deadline whenever
            // gender stays unreadable). Suppressed firings are not lost -
            // lastRefinedConsensusLevel deliberately doesn't advance, so the
            // first vote after the events go out re-detects the change and
            // fires then, one sample interval later at most.
            if (!levelGenderEventsPending)
            {
                int? refined = SelectConsensusLevelCore(out int refinedVotes, out LevelReadQuality refinedQuality);

                if (refined is int agreed && agreed != lastRefinedConsensusLevel)
                {
                    // §396: a refinement that CHANGES the level on show - away
                    // from what registration read, or from an earlier
                    // refinement - is the event behind the Hariyama report,
                    // so it is logged with every read of the battle so far.
                    // A first refinement that merely confirms the
                    // registration read is the common case and stays quiet.
                    int? shown = lastRefinedConsensusLevel ?? registrationLevelRead;

                    if (shown is int previous && previous != agreed)
                    {
                        Log.Information(
                            "Level refined from {Previous} to {Level} ({Votes} agreeing {Quality} reads) for {Pokemon} - every read this battle: {Reads}",
                            previous,
                            agreed,
                            refinedVotes,
                            refinedQuality,
                            lastDetectedPokemon,
                            DescribeLevelVotes()
                        );
                    }

                    lastRefinedConsensusLevel = agreed;
                    EncounterLevelRefined?.Invoke(agreed);
                }
            }
        }

        /// <summary>The level with the most votes among those that reached
        /// LevelConsensusVotes in the best quality that read anything (§396),
        /// or null when none did. Called once at catch time so the WHOLE
        /// battle's samples get a say - §95's first-to-two lock let two early
        /// agreeing misreads shut out a cleaner later majority, which §96's
        /// large-GUI capture actually produced. A tie keeps the earliest-seen
        /// candidate; strictly more votes are needed to displace it. When the
        /// battle produced more than one value, every read is logged beside
        /// the choice, so a wrong choice can be traced to its reads.</summary>
        private int? SelectConsensusLevel()
        {
            int? best = SelectConsensusLevelCore(out int bestVotes, out LevelReadQuality bestQuality);

            if (best is int chosen)
            {
                if (DistinctLevelCount() > 1)
                {
                    Log.Information(
                        "Level consensus selected: {Level} ({Votes} agreeing {Quality} reads) for {Pokemon} - every read this battle: {Reads}",
                        chosen,
                        bestVotes,
                        bestQuality,
                        lastDetectedPokemon,
                        DescribeLevelVotes()
                    );
                }
                else
                {
                    Log.Information(
                        "Level consensus selected: {Level} ({Votes} agreeing {Quality} reads) for {Pokemon}",
                        chosen,
                        bestVotes,
                        bestQuality,
                        lastDetectedPokemon
                    );
                }
            }

            return best;
        }

        /// <summary>The selection rule itself, shared by catch time
        /// (SelectConsensusLevel above, which also logs the choice) and
        /// RecordLevelVote's §99 change detection (which runs once per vote
        /// and must not log every time). §396: the first quality tier, in
        /// trust order, that holds any read at all is the only one consulted
        /// - <paramref name="quality"/> names it - and within it the value
        /// with the most votes among those that reached LevelConsensusVotes
        /// wins, ties to the earliest seen. A tier whose reads never agreed
        /// twice yields null even when a lesser tier is unanimous: a
        /// disturbance that keeps the sparse read from succeeding also
        /// makes the rescued reads suspect, and the tiers were ranked for
        /// exactly that.</summary>
        private int? SelectConsensusLevelCore(out int bestVotes, out LevelReadQuality quality)
        {
            bestVotes = 0;
            quality = LevelReadQuality.Clean;

            for (int tier = 0; tier < levelVotesByQuality.Length; tier++)
            {
                Dictionary<int, int> tally = levelVotesByQuality[tier];

                if (tally.Count == 0)
                    continue;

                quality = (LevelReadQuality)tier;
                return BestConfirmedLevel(tally, out bestVotes);
            }

            return null;
        }

        /// <summary>The §96 rule over one tally: most votes among the values
        /// seen at least LevelConsensusVotes times, ties to the earliest.</summary>
        private static int? BestConfirmedLevel(Dictionary<int, int> tally, out int bestVotes)
        {
            int? best = null;
            bestVotes = 0;

            foreach ((int level, int votes) in tally)
            {
                if (votes >= LevelConsensusVotes && votes > bestVotes)
                {
                    best = level;
                    bestVotes = votes;
                }
            }

            return best;
        }

        /// <summary>§396. The one value the Clean tier holds when it holds
        /// exactly one and nothing confirmed it - the tag read once, exactly
        /// as drawn, with every other read of the battle of lesser quality.
        /// Null when the Clean tier is empty, holds rival values, or holds
        /// a confirmed value (which SelectConsensusLevel returns instead).
        /// Catch time's fallback ahead of the catch tick's own lone read.</summary>
        private int? LoneCleanLevel()
        {
            Dictionary<int, int> clean = levelVotesByQuality[(int)LevelReadQuality.Clean];

            if (clean.Count != 1)
                return null;

            foreach ((int level, int votes) in clean)
                return votes < LevelConsensusVotes ? level : null;

            return null;
        }

        /// <summary>§396. How many different levels the battle's reads
        /// produced, over every quality - one means every read agreed.</summary>
        private int DistinctLevelCount()
        {
            var seen = new HashSet<int>();

            foreach (Dictionary<int, int> tally in levelVotesByQuality)
                seen.UnionWith(tally.Keys);

            return seen.Count;
        }

        /// <summary>§396. Every read of the battle so far, for the log:
        /// "23 x3 Clean, 28 x4 Rescued" - each tier in trust order, each
        /// value in the order it was first seen.</summary>
        private string DescribeLevelVotes()
        {
            var parts = new List<string>();

            for (int tier = 0; tier < levelVotesByQuality.Length; tier++)
            {
                foreach ((int level, int votes) in levelVotesByQuality[tier])
                    parts.Add($"{level} x{votes} {(LevelReadQuality)tier}");
            }

            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }

        private static int? TryDetectLevelSafely(
            SKBitmap screenshot, SKRectI battleBounds, string pokemonName, out LevelReadQuality quality)
        {
            try
            {
                return LevelDetector.TryDetectLevel(screenshot, battleBounds, out quality);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "LevelDetector failed for {Pokemon} - logging this encounter with an unknown level",
                    pokemonName
                );

                quality = LevelReadQuality.Clean;
                return null;
            }
        }

        private static string? TryDetectGenderSafely(
            SKBitmap screenshot, SKRectI battleBounds, string pokemonName)
        {
            try
            {
                return GenderDetector.TryDetectGender(screenshot, battleBounds);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "GenderDetector failed for {Pokemon} - logging this encounter with an unknown gender",
                    pokemonName
                );

                return null;
            }
        }

        // Fires the three per-encounter events together, in the same order,
        // from whichever of the two call sites gets there first - ScanOnce's
        // REGISTER ENCOUNTER section (immediately, when Level and Gender
        // both resolved on the first try) or the RETRY LEVEL/GENDER block/
        // FlushPendingLevelGenderEvents (once they resolve on a later tick,
        // or the retry deadline passes first). MainWindowViewModel's
        // firing-order guarantee - see EncounterLevelDetected's declaration
        // comment - holds no matter which path got here, since both funnel
        // through this one method.
        private void FireEncounterEvents(string pokemonName, int? level, string? gender)
        {
            Log.Information(
                "Encounter detected: {Pokemon} (Level {Level}, {Gender})",
                pokemonName,
                level,
                gender ?? "unknown gender"
            );

            // Fired before EncounterDetected below - see EncounterLevelDetected's
            // declaration comment for why the order matters.
            EncounterLevelDetected?.Invoke(level);
            EncounterGenderDetected?.Invoke(gender);
            EncounterDetected?.Invoke(pokemonName);
        }

        public void Dispose()
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();

            cancellationTokenSource = null;
            trackingTask = null;
        }
    }
}