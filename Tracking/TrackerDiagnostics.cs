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
            bool ok, int width = 0, int height = 0, string? error = null, int meanBrightness = -1)
        {
            if (ok)
            {
                Interlocked.Exchange(ref lastCaptureOkTicksUtc, DateTime.UtcNow.Ticks);
                LastFrameWidth = width;
                LastFrameHeight = height;
                LastFrameBrightness = meanBrightness;
            }
            else
            {
                Interlocked.Exchange(ref lastCaptureFailTicksUtc, DateTime.UtcNow.Ticks);
                LastCaptureError = error ?? string.Empty;
            }

            LogCaptureIfWorthIt(ok, width, height, error, meanBrightness);
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

            if (signature == lastCaptureSignature && !heartbeatDue)
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

        public static string RunTrackingCheck()
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
                            windows.Select(w => $"PID {w.ProcessId}, handle 0x{w.Handle:X}, title '{w.DisplayName}'")));

                Stage("Bound window",
                    capture.HasSelectedClient ? "yes" : "NO - tracking has nothing to capture");

                ManualBattleBounds? manual = BattleWindowLocator.ManualBounds;

                Stage("Manual boundaries",
                    manual is null
                        ? "not set - automatic detection only"
                        : $"set: {manual.Width}x{manual.Height} at ({manual.X},{manual.Y}), drawn on a " +
                          $"{manual.FrameWidth}x{manual.FrameHeight} frame; takes over when automatic detection " +
                          "finds nothing, or finds a box away from it while it holds a battle window");

                if (capture.HasSelectedClient)
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

                            Stage("Capture",
                                $"{frame.Width}x{frame.Height}, mean brightness {brightness}/255" +
                                (brightness >= 0 && brightness <= BlankFrameBrightness
                                    ? " - BLANK FRAME"
                                    : string.Empty));

                            bool locatedNow = BattleWindowLocator.TryLocate(frame, out SKRectI bounds);

                            Stage("Battle window",
                                locatedNow
                                    ? $"located at ({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}" +
                                      (BattleWindowLocator.LastLocateUsedManual
                                          ? " (through the saved manual boundaries)"
                                          : string.Empty)
                                    : "not in this frame (expected when no battle is open right now)");

                            if (locatedNow)
                            {
                                bool named = EncounterDetector.TryDetectEncounter(
                                    frame, out string pokemonName, out bool looksLikeBattleTitle);

                                Stage("Title OCR",
                                    named ? "Pokemon read: " + pokemonName
                                    : looksLikeBattleTitle ? "a 'VS' title is readable but no Pokemon name matched it"
                                    : "no 'VS' title could be read from the located window");
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
            Log.Information("Capture environment: {Description}", description);
        }

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
