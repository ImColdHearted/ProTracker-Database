using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Foot_Tracker.Models;
using Foot_Tracker.Tracking.Capture;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// The Admin Console's read side (MIGRATION_GUIDE.md §101): a static
    /// blackboard the tracking pipeline writes cheap facts onto as it works -
    /// a heartbeat per scan, capture success/failure, the last OCR text per
    /// detector, the last accepted encounter - and the opt-in diagnostic
    /// recorder behind the console's Diagnostics tab.
    ///
    /// PERFORMANCE CONTRACT: every Record* call on the hot path is a couple
    /// of field writes (no locks on the scan path except the tiny error/
    /// recording rings, which lock only when something is actually recorded),
    /// and the recorder's per-OCR capture is gated behind one volatile bool
    /// that is false in normal use - the §75/§99/§101 sprite-latency budget
    /// gains no new I/O, no allocation bursts, and no synchronization. The
    /// console reads these fields on a 1-second UI timer only while its
    /// window is open; nothing subscribes to production events.
    ///
    /// NOTHING sensitive belongs here: no credentials ever reach this class
    /// (the login flow hands AdminModeService only a bool), and RecordOcr
    /// stores detector text - game-screen words - never settings or auth
    /// data.
    /// </summary>
    public static class TrackerDiagnostics
    {
        // ---- live status fields (written by the pipeline, read by the console) ----

        public static volatile string StateText = "Stopped";

        private static long lastScanTicksUtc;
        private static long scanCount;
        private static long lastCaptureOkTicksUtc;
        private static long lastCaptureFailTicksUtc;
        public static volatile string LastCaptureError = string.Empty;
        public static volatile int LastFrameWidth;
        public static volatile int LastFrameHeight;

        /// <summary>§114: mean brightness of the last captured frame, 0-255,
        /// or -1 when it was not measured. A correctly-sized frame that is
        /// entirely black is what a failed capture looks like on Linux under
        /// XWayland and on Windows when PrintWindow cannot reach the window's
        /// content - width and height cannot tell those apart, this can.</summary>
        public static volatile int LastFrameBrightness = -1;
        private static long lastOcrTicksUtc;
        public static volatile string LastAcceptedPokemon = string.Empty;
        public static volatile string LastAcceptedLevel = string.Empty;
        public static volatile string LastAcceptedLocation = string.Empty;
        private static long lastEncounterTicksUtc;

        /// <summary>§187: what the last Export/Import attempt actually did,
        /// as one already-formatted sentence, or empty if none was attempted
        /// this session. Written by MainWindowViewModel's export and import
        /// paths; read by RunTrackingCheck so a Report a Problem bundle can
        /// answer "Export did nothing" without a second round trip to the
        /// person who hit it. Never holds the chosen path - only the
        /// outcome, its file extension, and any error text.</summary>
        public static volatile string LastFileDialog = string.Empty;

        private static long lastFileDialogTicksUtc;

        public static DateTime LastFileDialogUtc => TicksToUtc(Interlocked.Read(ref lastFileDialogTicksUtc));

        /// <summary>§187: records one export/import outcome. Deliberately
        /// takes a finished sentence rather than a path or an exception, so
        /// no caller can put a user's folder layout on the blackboard by
        /// accident.</summary>
        public static void RecordFileDialog(string? outcome)
        {
            LastFileDialog = outcome ?? string.Empty;
            Interlocked.Exchange(ref lastFileDialogTicksUtc, DateTime.UtcNow.Ticks);
        }

        public static DateTime LastScanUtc => TicksToUtc(Interlocked.Read(ref lastScanTicksUtc));
        public static DateTime LastCaptureOkUtc => TicksToUtc(Interlocked.Read(ref lastCaptureOkTicksUtc));
        public static DateTime LastCaptureFailUtc => TicksToUtc(Interlocked.Read(ref lastCaptureFailTicksUtc));
        public static DateTime LastOcrUtc => TicksToUtc(Interlocked.Read(ref lastOcrTicksUtc));
        public static DateTime LastEncounterUtc => TicksToUtc(Interlocked.Read(ref lastEncounterTicksUtc));
        public static long ScanCount => Interlocked.Read(ref scanCount);

        private static DateTime TicksToUtc(long ticks) =>
            ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);

        // ---- recent, non-sensitive error summaries (bounded ring) ----

        private static readonly object errorLock = new();
        private static readonly Queue<string> recentErrors = new();
        private const int MaxRecentErrors = 10;

        public static void SetState(string state) => StateText = state;

        public static void RecordScan()
        {
            Interlocked.Exchange(ref lastScanTicksUtc, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref scanCount);
        }

        // ---- §114 capture logging state ----
        //
        // Captures happen about ten times a second, so this cannot log every
        // one. It logs when the SHAPE of the result changes - success/failure,
        // frame size, or brightness band - plus a heartbeat, so a long healthy
        // run still leaves evidence and a long broken one leaves it too.
        private static string lastCaptureSignature = string.Empty;
        private static long lastCaptureLogTicksUtc;

        private const int CaptureLogHeartbeatSeconds = 60;

        /// <summary>Below this mean brightness a frame is treated as blank.
        /// A real PRO screen - even a night-time cave - sits far above it;
        /// the frames a broken capture returns sit at or very near zero.</summary>
        internal const int BlankFrameBrightness = 6;

        private const int NearlyBlankFrameBrightness = 16;

        // §134: how many frames the encounter loop has run the battle
        // locator on since the previous capture line, and how many of those
        // held a battle window. Written by the encounter loop only; read
        // and reset by the line that prints them.
        private static int framesSinceCaptureLog;
        private static int battlesLocatedSinceCaptureLog;

        // ---- §321 frozen-frame detection ----
        //
        // THE FAILURE THIS EXISTS FOR. A Linux tester ran for three hours and
        // counted nothing. Every capture line in his log said "- ok". The
        // capture never failed once - "last failed capture never" - and there
        // were no errors. What his log did say, 177 times, was mean brightness
        // of EXACTLY 170/255. Never 169, never 171. A mean over a whole frame
        // cannot repeat to the byte across eleven thousand live frames; it was
        // one dead image being re-read.
        //
        // He had two PRO clients open. The tracker had bound the one he was
        // not playing on, and on Linux a capture is `import -window <id>`,
        // which reads that window's backing pixmap. Under XWayland an occluded
        // or unfocused X11 window is not repainted, so import kept handing
        // back the last thing drawn into it. The capture SUCCEEDED every time.
        // He fixed it by pressing Stop and Start, which re-bound the window -
        // and nothing anywhere had told him that was the problem.
        //
        // So: a run of captures that are the same picture is not a healthy
        // capture, whatever its return value says.
        //
        // WHAT "THE SAME PICTURE" MEANS, AND WHY IT IS NOT BRIGHTNESS. The
        // first draft of this compared width, height and mean brightness,
        // because those were already measured. Five real PROClient recordings
        // say that is not good enough: during live play the sampled mean
        // repeated unchanged for as long as 8.2 seconds, since ImageOps reads
        // about 1600 grid points and a moving sprite regularly misses all of
        // them. A hash of those same points never repeated past 0.4 seconds in
        // the same footage. The hash is therefore what a freeze is judged on -
        // it costs nothing extra, coming out of the same grid pass.
        //
        // WHY THE THRESHOLD IS A DURATION AND NOT ONLY A FRAME COUNT. The loop
        // asks for a 200 ms delay, so five frames a second - but the tester's
        // own log reports about 61 frames a minute, because on Linux every
        // capture forks `import` and that dominates. One frame count therefore
        // means five times as long on one machine as on another, which is no
        // basis for a verdict. A freeze is called when the picture has been
        // identical for FrozenFrameSeconds AND for at least FrozenFrameRun
        // frames - the duration carries the meaning, the count stops a stalled
        // or barely-running loop from being described as a frozen one.
        private static int identicalFrameRun;
        private static long identicalRunStartTicksUtc;
        private static int lastFrameSignature = int.MinValue;
        private static bool frozenAnnounced;

        /// <summary>§321. How many captures in a row have been the same
        /// picture (same size, same sampled-grid hash).</summary>
        public static int IdenticalFrameRun => Volatile.Read(ref identicalFrameRun);

        /// <summary>§321. How long that run has lasted. Zero when there is no
        /// run.</summary>
        public static TimeSpan IdenticalFrameRunLength
        {
            get
            {
                long started = Interlocked.Read(ref identicalRunStartTicksUtc);

                if (started == 0 || IdenticalFrameRun < 1)
                    return TimeSpan.Zero;

                return TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - started));
            }
        }

        /// <summary>
        /// §321. Fifteen minutes of one unchanging picture.
        ///
        /// The number was argued up twice, both times by measurement.
        ///
        /// It began as a frame count of 60 with no clock at all. Real footage
        /// killed that: see the hash discussion above, and the rate discussion
        /// below.
        ///
        /// It was then five minutes, and the tester's own log killed that. His
        /// log records only brightness, and an identical run necessarily has
        /// identical brightness, so the longest stretch of his log showing no
        /// brightness change is a hard upper bound on any run a
        /// brightness-keyed detector could have seen. In the BROKEN half that
        /// bound is 2h57m - one value, 170, start to finish. In the HEALTHY
        /// half, after he restarted tracking and it worked, there is a 12m 05s
        /// stretch and a 5m 01s one. Five minutes would have called his
        /// working session frozen twice; ten minutes would still have reached
        /// into the longer one. Fifteen clears every stretch in all 1356 of
        /// them.
        ///
        /// That 12-minute stretch is also the sharpest evidence for the hash:
        /// a battle window was located in all 928 of its frames, so the screen
        /// was demonstrably not still while its brightness sat unchanged.
        ///
        /// So the threshold has two independent margins. Against the signature
        /// actually used, the worst honest run measured on live footage is 0.4
        /// seconds, and this is over two thousand times that. Against the much
        /// weaker brightness signal, no stretch of the one real healthy
        /// session on record comes near it either. What it costs is that the
        /// tester's three-hour silence becomes a fifteen-minute one; what it
        /// buys is that nobody who walked away from a paused game is told
        /// their capture is broken.
        ///
        /// REJECTED, and worth recording so it is not re-proposed: also
        /// requiring that no battle window was located during the run. It
        /// would have suppressed both healthy stretches, and it is wrong -
        /// a window frozen ON a battle screen locates a battle in every one
        /// of its dead frames, so the guard would hide exactly the failure
        /// this exists to catch. It only looked attractive because this one
        /// tester's frozen frame happened to have no battle in it.
        /// </summary>
        public const int FrozenFrameSeconds = 900;

        /// <summary>§321. The frame count that must ALSO be reached, so that a
        /// loop which has stopped scanning cannot be reported as a frozen
        /// capture. At the slowest rate seen in the wild - about one frame a
        /// second on the tester's Linux box - fifteen minutes is roughly 900
        /// frames, so this is never the binding constraint on a loop that is
        /// actually running.</summary>
        public const int FrozenFrameRun = 60;

        /// <summary>§321. The capture is returning the same image over and
        /// over - the bound window is not being redrawn.</summary>
        public static bool FramesAreFrozen =>
            IdenticalFrameRun >= FrozenFrameRun &&
            IdenticalFrameRunLength >= TimeSpan.FromSeconds(FrozenFrameSeconds);

        /// <summary>§321. "6m 12s (371 frames)" - the measurement both the log
        /// verdict and TrackingCheck print, so the two cannot drift apart. The
        /// wording around it belongs to each caller.</summary>
        private static string FrozenRunDescription()
        {
            TimeSpan length = IdenticalFrameRunLength;

            string span = length.TotalMinutes >= 1
                ? $"{(int)length.TotalMinutes}m {length.Seconds}s"
                : $"{(int)length.TotalSeconds}s";

            return $"{span} ({IdenticalFrameRun} frames)";
        }

        /// <summary>
        /// §333. Whether the saved boundaries still apply to what is being
        /// captured, said as a verdict rather than as two numbers to compare.
        ///
        /// A box is only a measurement on the frame it was drawn on. On any
        /// other it is the §135 centring rule's guess, and §333 stopped that
        /// guess outranking a scan that succeeded - so what the box will
        /// actually DO changes with this answer, and the reader has to be
        /// told which case they are in.
        /// </summary>
        private static string ManualFrameVerdict(ManualBattleBounds manual)
        {
            int width = LastFrameWidth;
            int height = LastFrameHeight;

            if (width <= 0 || height <= 0)
                return "No frame has been captured yet, so it cannot be checked against one.";

            if (manual.FrameWidth == width && manual.FrameHeight == height)
            {
                return "That is this client's size, so the box is used as drawn: it takes over " +
                       "when automatic detection finds nothing, and also when detection finds a " +
                       "box away from it while this box holds a battle window.";
            }

            return $"THIS CLIENT IS {width}x{height}, WHICH IS NOT THE SIZE IT WAS DRAWN ON. The box " +
                   "is re-centred onto the frame, which assumes the battle window stays centred - " +
                   "true when the game window is resized, not true when it is a different shape or " +
                   "on another monitor. It is therefore only used when automatic detection finds " +
                   "NOTHING, and can no longer overrule detection that worked. If encounters are " +
                   "being missed, redo Set Screen Boundaries on this client, or clear it.";
        }

        /// <summary>
        /// §321. Appended to the frozen verdict: which PRO clients are open
        /// right now.
        ///
        /// The tester had two. The log named neither, so the line that finally
        /// explained his three hours could not point at the thing to click.
        /// This deliberately does NOT claim which one is bound - the capture
        /// interface exposes only whether something is selected, not what -
        /// and it deliberately says nothing at all when there is only one
        /// client, because then the wrong-client story is not the explanation
        /// and suggesting it would send the reader down the wrong path.
        ///
        /// It runs at most once a heartbeat (a minute) and only while frozen,
        /// so the enumeration it costs - a wmctrl fork on Linux - is paid
        /// roughly never. Any failure is swallowed: a hint that cannot be
        /// produced must not take the verdict down with it.
        /// </summary>
        private static string OtherClientsHint()
        {
            try
            {
                IReadOnlyList<ClientWindowInfo> windows =
                    WindowCaptureServiceFactory.Instance.FindClientWindows("PROClient");

                if (windows.Count < 2)
                    return string.Empty;

                return " There are " + windows.Count + " PRO clients open right now (" +
                       string.Join(", ", windows.Select(w => "PID " + w.ProcessId)) +
                       "), so picking the right one is very likely the fix.";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>§134. One call per encounter-loop frame, right after
        /// BattleWindowLocator.TryLocate. Two interlocked increments; no
        /// logging of its own.</summary>
        public static void RecordBattleLocate(bool located)
        {
            Interlocked.Increment(ref framesSinceCaptureLog);

            if (located)
                Interlocked.Increment(ref battlesLocatedSinceCaptureLog);
        }

        public static void RecordCapture(
            bool ok, int width = 0, int height = 0, string? error = null,
            int meanBrightness = -1, int frameHash = 0)
        {
            if (ok)
            {
                Interlocked.Exchange(ref lastCaptureOkTicksUtc, DateTime.UtcNow.Ticks);

                NoteFrameSignature(width, height, meanBrightness, frameHash);

                LastFrameWidth = width;
                LastFrameHeight = height;
                LastFrameBrightness = meanBrightness;
            }
            else
            {
                Interlocked.Exchange(ref lastCaptureFailTicksUtc, DateTime.UtcNow.Ticks);
                LastCaptureError = error ?? string.Empty;

                // A capture that failed is not a frozen one, and must not be
                // allowed to keep a run alive across the gap either.
                ResetIdenticalRun();
            }

            LogCaptureIfWorthIt(ok, width, height, error, meanBrightness);
        }

        /// <summary>§321. One comparison per frame. Callers that cannot supply
        /// a hash (frameHash 0, or no brightness measured) end the run rather
        /// than extend it - an unmeasured frame is not evidence of
        /// anything.</summary>
        private static void NoteFrameSignature(
            int width, int height, int meanBrightness, int frameHash)
        {
            if (frameHash == 0 || meanBrightness < 0 || width <= 0 || height <= 0)
            {
                ResetIdenticalRun();
                return;
            }

            int signature = unchecked((width * 397 ^ height) * 397 ^ frameHash);

            if (signature == int.MinValue)
                signature = int.MinValue + 1;

            if (signature == lastFrameSignature)
            {
                Interlocked.Increment(ref identicalFrameRun);

                // The run started with the frame BEFORE the first repeat, so
                // the clock is only set the first time a repeat is seen.
                Interlocked.CompareExchange(
                    ref identicalRunStartTicksUtc, DateTime.UtcNow.Ticks, 0);
            }
            else
            {
                ResetIdenticalRun();
            }

            lastFrameSignature = signature;
        }

        private static void ResetIdenticalRun()
        {
            Interlocked.Exchange(ref identicalFrameRun, 0);
            Interlocked.Exchange(ref identicalRunStartTicksUtc, 0);
            frozenAnnounced = false;
        }

        /// <summary>
        /// §114. Puts the capture result in the LOG FILE, not just on the
        /// Admin Console's live blackboard. Before this, a tester whose
        /// captures were failing produced a log that was simply empty of
        /// detector activity - and an empty log looks identical whether the
        /// frames were black, the client was never found, or OCR never
        /// started. Three separate testers on three operating systems all
        /// reported "it runs but nothing counts" and none of their logs could
        /// tell those apart.
        ///
        /// The line says what is wrong in words rather than only in numbers,
        /// because the person reading it first is usually not the developer.
        /// </summary>
        private static void LogCaptureIfWorthIt(
            bool ok, int width, int height, string? error, int meanBrightness)
        {
            // Band the brightness so ordinary frame-to-frame variation does
            // not count as a change worth logging.
            int band = meanBrightness < 0 ? -1 : meanBrightness / 16;
            string signature = $"{ok}|{width}x{height}|{band}|{error}";

            long nowTicks = DateTime.UtcNow.Ticks;
            long sinceLog = nowTicks - Interlocked.Read(ref lastCaptureLogTicksUtc);
            bool heartbeatDue = sinceLog > TimeSpan.FromSeconds(CaptureLogHeartbeatSeconds).Ticks;

            // §321: a capture that has just gone frozen is worth a line
            // immediately. Its brightness has by definition not changed, so
            // the de-duplication above would otherwise hold it back for a
            // whole heartbeat - which is exactly the silence being fixed.
            bool frozenNow = FramesAreFrozen && !frozenAnnounced;

            if (frozenNow)
                frozenAnnounced = true;

            if (signature == lastCaptureSignature && !heartbeatDue && !frozenNow)
                return;

            lastCaptureSignature = signature;
            Interlocked.Exchange(ref lastCaptureLogTicksUtc, nowTicks);

            if (!ok)
            {
                Log.Warning(
                    "Capture FAILED - no frame returned. {Error}",
                    string.IsNullOrWhiteSpace(error) ? "(no reason given)" : error);
                return;
            }

            string verdict;

            if (meanBrightness < 0)
                verdict = "ok (brightness not measured)";
            else if (meanBrightness <= BlankFrameBrightness)
                // §134: the §114 text blamed Wayland here, which §117
                // disproved with this very line (161/255 through XWayland).
                // A verdict names what was seen, not a culprit it cannot know.
                verdict = "ok BUT THE FRAME IS BLANK - the capture is returning an " +
                          "empty image, so nothing can ever be detected. On Windows " +
                          "that is PrintWindow being unable to reach the window's " +
                          "content; on Linux, import/maim handed back an empty image " +
                          "for this window (not a Wayland-versus-Xorg question - " +
                          "XWayland captures are proven to work, MIGRATION_GUIDE.md " +
                          "section 117).";
            else if (meanBrightness <= NearlyBlankFrameBrightness)
                verdict = "ok but the frame is almost entirely dark - if nothing is " +
                          "being detected, suspect the capture rather than the OCR.";
            else if (FramesAreFrozen)
                // §321: the loudest verdict there is, because this is the one
                // that looked like success for three hours. It names what was
                // measured and then what usually causes it, in that order -
                // the §134 rule that a verdict states what was seen rather
                // than a culprit it cannot prove applies here too. The capture
                // genuinely cannot tell a dead window from a game nobody is
                // touching; five minutes of it is worth saying either way.
                verdict = "THE CAPTURE IS FROZEN - every frame for the last " +
                          FrozenRunDescription() +
                          " has been the same picture, so nothing can be detected from it. " +
                          "Either nothing at all is happening on the screen we are capturing, " +
                          "or - far more likely if you are playing - we are bound to a window " +
                          "that is not being redrawn: a second PRO client, one behind another " +
                          "window or on another workspace, or a minimised one. Stop and Start " +
                          "tracking to re-bind it, or use Assign Client to pick the client you " +
                          "are actually playing on." + OtherClientsHint();
            else
                verdict = "ok";

            // §134: the locate counts ride on the line that already
            // exists, so a quiet log still says whether the frames ever
            // held a battle window - the one fact the 8/30 Linux report
            // could not answer.
            int frames = Interlocked.Exchange(ref framesSinceCaptureLog, 0);
            int located = Interlocked.Exchange(ref battlesLocatedSinceCaptureLog, 0);

            Log.Information(
                "Capture {Width}x{Height}, mean brightness {Brightness}/255 - {Verdict}; " +
                "battle window located in {Located} of the {Frames} frames since the previous capture line",
                width, height, meanBrightness, verdict, located, frames);
        }

        // ---- §134 one-shot tracking check ---------------------------
        //
        // Runs the same pipeline the tracking loop runs, ONCE, on demand,
        // and writes every stage's outcome to the log as one block - so a
        // single Report a Problem click answers, on the tester's own machine
        // and in pipeline order: can the capture tools be found, is a
        // PROClient window listed, is one bound, does a frame come back,
        // how big and how bright is it, is a battle window in it, can its
        // title be read, and does the OCR engine load at all. The 8/30
        // Linux report needed exactly that list and had a screenshot of a
        // status line instead.
        //
        // Read-only with respect to hunting data: it touches no session, no
        // counter, no catch log and no file but the log. It shares the OCR
        // lock with the live loop, so it is safe while tracking runs, and
        // the one extra capture it takes is the same call the Report a
        // Problem screenshot already makes.

        public static string RunTrackingCheck(string? trackerWindow = null)
        {
            var lines = new List<string>();

            void Stage(string name, string result) => lines.Add(name + ": " + result);

            try
            {
                IWindowCaptureService capture = WindowCaptureServiceFactory.Instance;

                Stage("Platform",
                    $"{capture.PlatformName}; {Environment.OSVersion}; " +
                    $"XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)"}; " +
                    $"WAYLAND_DISPLAY set={!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))}; " +
                    $"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "(unset)"}");

                // §187: the data context comes before the capture stages
                // because it changes what every later line MEANS - in Admin
                // Client mode encounters are being counted into a throwaway
                // session and import is refused outright, so a report that
                // says "nothing saved" is describing the mode working, not a
                // fault. It was previously absent from this list entirely.
                Stage("Admin Client mode",
                    Services.AdminModeService.IsActive
                        ? "ACTIVE" +
                          (Services.AdminModeService.RestoredFromMarker
                              ? " (restored from its marker file at startup, not entered this session)"
                              : " (entered this session)") +
                          " - hunting data goes to the isolated diagnostic session, hunt data import is " +
                          "refused, and nothing reaches a normal client's files. Assign Client leaves it. " +
                          "Admin login this process: " +
                          (Services.AdminModeService.IsAuthenticated ? "yes" : "no")
                        : "not active - the normal client data context is in use");

                Stage("Last export/import",
                    string.IsNullOrEmpty(LastFileDialog)
                        ? "none attempted this session"
                        : LastFileDialog + ", " + Age(LastFileDialogUtc));

                Stage("Capture tools",
                    capture.IsAvailable
                        ? "available"
                        : "NOT AVAILABLE - " + (capture.LastError ?? "(no reason given)"));

                IReadOnlyList<ClientWindowInfo> windows = capture.FindClientWindows("PROClient");

                Stage("PROClient windows",
                    windows.Count == 0
                        ? "none found" + (string.IsNullOrWhiteSpace(capture.LastError)
                            ? string.Empty
                            : " - " + capture.LastError)
                        : windows.Count + " found: " + string.Join("; ",
                            windows.Select(w => $"PID {w.ProcessId}, handle 0x{w.Handle:X}, title '{w.DisplayName}'{WindowBounds(w)}")));

                // §391: the tracker's own window beside the game's, because
                // on the screen-copy route (see "Capture environment") a
                // tracker sitting over the game is captured in its place.
                Stage("Tracker window", trackerWindow ?? "(not reported)");

                // §391: "NO - tracking has nothing to capture" was wrong on
                // both counts. A window is bound only when the player picks
                // one; everyone else captures the first PRO client found,
                // and always has (ScreenCapture.CaptureProWindow). A report
                // said it while the loop was capturing every frame.
                Stage("Bound window",
                    capture.HasSelectedClient
                        ? "yes - chosen by the player"
                        : windows.Count > 0
                            ? "not chosen - the first PRO client window found is captured, as it always has been"
                            : "NO - none chosen and none found to fall back to");

                Stage("Capture environment",
                    LastCaptureEnvironment.Length == 0 ? "(nothing logged yet this session)" : LastCaptureEnvironment);

                ManualBattleBounds? manual = BattleWindowLocator.ManualBounds;

                // §333: this line used to print the saved frame size beside
                // the current one and leave the reader to compare them. A
                // tester spent a day on a box drawn at 1440x1252 being
                // re-centred onto a 1259x1366 client, landing 82 pixels out
                // of place, and overruling a scan that had the window right.
                // Every number needed was on this line. None of them said so.
                Stage("Manual boundaries",
                    manual is null
                        ? "not set - automatic detection only"
                        : $"set: {manual.Width}x{manual.Height} at ({manual.X},{manual.Y}), drawn on a " +
                          $"{manual.FrameWidth}x{manual.FrameHeight} frame. " +
                          ManualFrameVerdict(manual));

                // §391: whenever anything can be captured, not only when a
                // window was chosen - the loop captures on the fallback, so
                // the check must test the same thing. A player who never
                // picked a window used to get a report with no Capture,
                // Battle window or Title OCR line, which are the three that
                // say why an encounter was not counted.
                if (capture.HasSelectedClient || windows.Count > 0)
                {
                    byte[]? png = capture.CaptureSelectedWindowPng();

                    if (png is null || png.Length == 0)
                    {
                        Stage("Capture", "FAILED - " + (capture.LastError ?? "(no reason given)"));
                    }
                    else
                    {
                        using SKBitmap? frame = ImageOps.DecodePng(png);

                        if (frame is null)
                        {
                            Stage("Capture", png.Length + " bytes came back but did not decode as a PNG image");
                        }
                        else
                        {
                            int brightness = ImageOps.MeanBrightness(frame);

                            // §321: the run of identical frames goes on the
                            // line a tester actually sends back. His report
                            // said "Capture: ok" while the capture had been
                            // dead for three hours.
                            string frozen = FramesAreFrozen
                                ? " - FROZEN: the tracking loop's frames have been identical for "
                                  + FrozenRunDescription()
                                  + ", so the bound window is not being redrawn"
                                : IdenticalFrameRun > 1
                                    ? " (the loop's last frames were identical for "
                                      + FrozenRunDescription() + ")"
                                    : string.Empty;

                            Stage("Capture",
                                $"{frame.Width}x{frame.Height}, mean brightness {brightness}/255" +
                                (brightness >= 0 && brightness <= BlankFrameBrightness
                                    ? " - BLANK FRAME"
                                    : string.Empty)
                                + frozen);

                            bool locatedNow = BattleWindowLocator.TryLocate(frame, out SKRectI bounds);

                            // §342: and, when the scan has never found one,
                            // whether it has been refusing runs for being
                            // too large. Empty in every other case.
                            string oversize = BattleWindowLocator.OversizeRefusal;

                            Stage("Battle window",
                                locatedNow
                                    ? $"located at ({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}" +
                                      (BattleWindowLocator.LastLocateUsedManual
                                          ? " (through the saved manual boundaries)"
                                          : string.Empty)
                                    : "not in this frame (expected when no battle is open right now)");

                            if (oversize.Length > 0)
                                Stage("Battle window size", "REFUSED - " + oversize);

                            if (locatedNow)
                            {
                                // §363: this line used to say "a 'VS' title is
                                // readable but no Pokemon name matched it" and
                                // stop, which names the failure and withholds
                                // the one fact that explains it. The map branch
                                // below has printed what it read since it was
                                // written; this branch never did. Read the title
                                // through TryReadBattleTitle rather than
                                // TryDetectEncounter so the text is in hand -
                                // and so it is read from the same bounds the
                                // "Battle window" line above just reported,
                                // instead of locating the window a second time.
                                bool readAny = EncounterDetector.TryReadBattleTitle(
                                    frame, bounds, out string titleText,
                                    out bool looksLikeBattleTitle, out string? matchedPokemon);

                                string readAs =
                                    " - OCR read: '" +
                                    titleText.Replace("\r", " ").Replace("\n", " ").Trim() +
                                    "'";

                                Stage("Title OCR",
                                    !readAny
                                        ? "nothing readable in the title strip - it is too dark, or Tesseract returned no text"
                                    : matchedPokemon != null
                                        ? "Pokemon read: " + matchedPokemon + readAs
                                    : looksLikeBattleTitle
                                        ? "a 'VS' title is readable but no Pokemon name matched it" + readAs
                                        : "no 'VS' title could be read from the located window" + readAs);
                            }
                            else
                            {
                                bool mapRead = RouteDetector.TryDetectCorner(frame, out string? routeName);

                                Stage("OCR engine",
                                    "loaded and ran on the map-name corner" +
                                    (mapRead
                                        ? $" (read '{routeName}')"
                                        : " (no map name matched - normal on many screens)"));
                            }
                        }
                    }
                }

                Stage("Tracking loop",
                    $"state={StateText}; scans={ScanCount}; last scan {Age(LastScanUtc)}; " +
                    $"last good capture {Age(LastCaptureOkUtc)}; last failed capture {Age(LastCaptureFailUtc)}" +
                    (string.IsNullOrEmpty(LastCaptureError) ? string.Empty : " (" + LastCaptureError + ")"));

                Stage("Last frame the loop saw",
                    LastFrameWidth > 0
                        ? $"{LastFrameWidth}x{LastFrameHeight}, mean brightness {LastFrameBrightness}/255"
                        : "none yet");

                Stage("Last accepted encounter",
                    string.IsNullOrEmpty(LastAcceptedPokemon)
                        ? "none this session"
                        : $"{LastAcceptedPokemon} {LastAcceptedLevel} at {LastAcceptedLocation}, {Age(LastEncounterUtc)}");

                IReadOnlyList<string> errors = GetRecentErrors();

                Stage("Recent errors", errors.Count == 0 ? "none" : string.Join(" | ", errors));
            }
            catch (Exception ex)
            {
                lines.Add("Check stopped early: " + ex.GetBaseException().Message);
            }

            string report = string.Join(Environment.NewLine, lines);

            Log.Information("Tracking check:{NewLine}{Report}", Environment.NewLine, report);

            return report;
        }

        /// <summary>§391. " at (x,y) WxH" for a window on Windows, where the
        /// finder can ask; empty elsewhere, or when the window is gone.</summary>
        private static string WindowBounds(ClientWindowInfo window)
        {
            try
            {
                if (OperatingSystem.IsWindows()
                    && ProWindowFinder.TryGetWindowBounds(new IntPtr(window.Handle), out System.Drawing.Rectangle bounds))
                {
                    return $" at ({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height}";
                }
            }
            catch
            {
                // A line of context, not worth failing the check over.
            }

            return string.Empty;
        }

        private static string Age(DateTime utc)
        {
            if (utc == DateTime.MinValue)
                return "never";

            double seconds = (DateTime.UtcNow - utc).TotalSeconds;

            return seconds < 0 ? "just now" : $"{seconds:F0}s ago";
        }

        /// <summary>§114: one line, written when a client window is bound,
        /// describing the things about the player's setup that decide whether
        /// capture can work at all. Backend-specific - see the Windows and
        /// Linux capture services for what each one puts in it.</summary>
        public static void LogCaptureEnvironment(string description)
        {
            LastCaptureEnvironment = description;
            Log.Information("Capture environment: {Description}", description);
        }

        /// <summary>§391. The last environment line, so the tracking check
        /// can print which route the frames are coming through (PrintWindow,
        /// the compositor, or a copy of the screen rectangle - the one route
        /// on which the tracker's own window can cover the game) without a
        /// reader having to find it in a log that may not have arrived.</summary>
        public static volatile string LastCaptureEnvironment = string.Empty;

        public static void RecordEncounter(string pokemonName, string levelText, string location)
        {
            LastAcceptedPokemon = pokemonName;
            LastAcceptedLevel = levelText;
            LastAcceptedLocation = location;
            Interlocked.Exchange(ref lastEncounterTicksUtc, DateTime.UtcNow.Ticks);
        }

        public static void RecordError(string summary)
        {
            lock (errorLock)
            {
                recentErrors.Enqueue($"{DateTime.Now:HH:mm:ss} {summary}");

                while (recentErrors.Count > MaxRecentErrors)
                    recentErrors.Dequeue();
            }
        }

        public static IReadOnlyList<string> GetRecentErrors()
        {
            lock (errorLock)
            {
                return recentErrors.ToArray();
            }
        }

        // ---- opt-in diagnostic recording (Admin Console Diagnostics tab) ----

        /// <summary>One retained diagnostic sample: which detector, the raw
        /// text it produced, when - plus, for the level detector only, the
        /// small prepared crop as PNG bytes (a few KB; never a full frame).</summary>
        public sealed class DiagnosticEntry
        {
            public DateTime TimeUtc { get; init; }
            public string Detector { get; init; } = string.Empty;
            public string Text { get; init; } = string.Empty;
            public byte[]? CropPng { get; init; }
        }

        // Volatile so the disabled-path cost in RecordOcr is one read + one
        // predictable branch. Enabled only from the Admin Console.
        private static volatile bool recordingEnabled;

        public static bool RecordingEnabled
        {
            get => recordingEnabled;
            set
            {
                recordingEnabled = value;
                Log.Information("Diagnostic recording {State}", value ? "enabled" : "disabled");
            }
        }

        private static readonly object recordingLock = new();
        private static readonly Queue<DiagnosticEntry> recording = new();

        // Bounded hard: 120 entries, and crops only ever come from the level
        // detector's already-tiny prepared images. At worst a few hundred KB
        // in memory, nothing on disk until SaveRecordingAsync is asked for.
        private const int MaxRecordingEntries = 120;

        /// <summary>Hot-path tap - called by the detectors' existing
        /// log-on-change points. Free (one volatile read) unless recording
        /// was explicitly enabled in the Admin Console.</summary>
        public static void RecordOcr(string detector, string text, byte[]? cropPng = null)
        {
            Interlocked.Exchange(ref lastOcrTicksUtc, DateTime.UtcNow.Ticks);

            if (!recordingEnabled)
                return;

            lock (recordingLock)
            {
                recording.Enqueue(new DiagnosticEntry
                {
                    TimeUtc = DateTime.UtcNow,
                    Detector = detector,
                    Text = text,
                    CropPng = cropPng
                });

                while (recording.Count > MaxRecordingEntries)
                    recording.Dequeue();
            }
        }

        public static int RecordingCount
        {
            get { lock (recordingLock) return recording.Count; }
        }

        public static IReadOnlyList<DiagnosticEntry> GetRecordingSnapshot()
        {
            lock (recordingLock) return recording.ToArray();
        }

        public static void ClearRecording()
        {
            lock (recordingLock) recording.Clear();
        }

        /// <summary>Writes the current recording to a timestamped folder under
        /// %LocalAppData%\ProTracker\Diagnostics - one text index plus the
        /// retained crops - entirely on a background thread. Returns the
        /// folder path.</summary>
        public static Task<string> SaveRecordingAsync()
        {
            DiagnosticEntry[] snapshot;
            lock (recordingLock) snapshot = recording.ToArray();

            return Task.Run(() =>
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProTracker",
                    "Diagnostics",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss"));

                Directory.CreateDirectory(folder);

                var index = new StringBuilder();
                int cropIndex = 0;

                foreach (DiagnosticEntry entry in snapshot)
                {
                    string cropNote = string.Empty;

                    if (entry.CropPng is { Length: > 0 })
                    {
                        string cropName = $"crop-{++cropIndex:D3}.png";
                        File.WriteAllBytes(Path.Combine(folder, cropName), entry.CropPng);
                        cropNote = " [" + cropName + "]";
                    }

                    index.AppendLine(
                        $"{entry.TimeUtc:HH:mm:ss.fff} {entry.Detector}: {entry.Text.Replace("\n", " / ")}{cropNote}");
                }

                File.WriteAllText(Path.Combine(folder, "ocr-diagnostics.txt"), index.ToString());
                return folder;
            });
        }
    }
}
