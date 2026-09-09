using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Serilog;

namespace Foot_Tracker.Services;

// Plays a short sound when Since Shiny or Since Form resets - on Windows
// through winmm (PlaySound, then the legacy MCI "Media Control Interface"
// API as a fallback), on Linux and macOS through the system's own
// command-line player (§148, see PlayWithSystemPlayer) - originally a
// provisional single-sound/single-switch test (see MIGRATION_GUIDE.md
// #65), now backing an independent sound choice per stat (see
// MIGRATION_GUIDE.md #71): MainWindowViewModel.SinceFormSound/
// SinceShinySound, set from the File menu's Since Form Sound/Since Shiny
// Sound submenus. MainWindowViewModel.OnRareEncounterDetected is the call
// site for real alerts; since §146 the Sound Settings window's Test buttons
// go through the same PlaySound too.
//
// MCI (winmm.dll), not a NuGet audio package: this project deliberately
// targets plain net10.0 rather than net10.0-windows, to stay buildable on
// Linux/macOS (see the .csproj's own TargetFramework comment). NAudio - the
// obvious off-the-shelf choice - only compiles its actual playback classes
// (WaveOutEvent and friends) into its Windows-qualified targets; referencing
// it from a plain net10.0 project would restore fine but leave those types
// missing at compile time, confirmed against NAudio's own source/docs. A
// bare P/Invoke declaration has no such restriction - like
// NativeMethods.cs's user32.dll calls, it compiles on every platform and
// only ever runs on Windows, gated by OperatingSystem.IsWindows() checks
// rather than needing a second TargetFramework just for a couple of sound
// effects. MCI also plays an .mp3 directly - no WAV conversion, no
// separate decoder.
//
// §146: every MCI call now runs on one long-lived thread of its own (not a
// thread-pool worker), every MCI return code is checked and logged with
// MCI's own wording, and every alert leaves an Information line whether it
// played, was skipped, or had no sound selected - so a rare encounter that
// goes by in silence can be explained from the log alone.
//
// §147: the first Test press with §146's logging answered "MCI error 266,
// cannot load the device driver" on every attempt. The MPEGVideo driver
// (mciqtz32, DirectShow underneath) initialises COM as a single-threaded
// apartment on the calling thread; a .NET worker thread starts multi-
// threaded, so that initialisation fails and the driver never loads - which
// is why forms played before §103 moved playback off the (STA) UI thread and
// never after. Two changes: the playback thread is now STA, and each clip
// ships as a .wav beside its .mp3 and is played first through winmm's
// PlaySound - the plain wave path that needs no MCI driver, no DirectShow
// and no COM at all. MCI with the .mp3 is the fallback when no .wav exists.
//
// §148: Linux and macOS. No NuGet audio package here either - the .wav from
// §147 is raw PCM, and every desktop OS ships a command-line tool that
// plays raw PCM: afplay on every Mac; paplay (PulseAudio and PipeWire's
// Pulse layer), pw-play (PipeWire) or aplay (ALSA) on practically every
// Linux desktop, with ffplay, mpv, play (SoX) and mpg123 as further
// fallbacks. The first one found on the PATH is started as a child process
// with the .wav (or the .mp3, for the players that decode it); latest-wins
// kills the previous child; the priority window comes from the RIFF header
// as on Windows; a player that exits non-zero is logged with its stderr and
// skipped for the rest of the run so the next alert tries the next one; no
// player at all is a Warning naming the packages to install. The class is
// no longer Windows-only; the winmm imports are declared everywhere and
// called only behind OperatingSystem.IsWindows(), as before.
//
// §152: a volume per sound (0-100, Sound Settings sliders, UiPreferences.
// SinceFormSoundVolume/SinceShinySoundVolume). Neither PlaySound nor aplay
// has a volume control, and the players that do all count differently, so
// the volume is applied to the clip itself: below 100% the .wav played is a
// copy with its samples scaled (WaveVolume), made on first use and kept
// under %LOCALAPPDATA%\ProTracker\SoundCache - see ScaledWavePath. The
// .mp3 fallbacks get the nearest thing each player offers (MCI's setaudio,
// the command-line players' own options - see VolumeArguments). Every play
// logs the percentage it went out at.
internal static class SoundNotificationService
{
    /// <summary>§103 priority policy, highest wins: a Shiny alert may
    /// interrupt anything; a Form alert may interrupt another Form alert but
    /// never cuts a Shiny that is still audibly playing (measured via MCI's
    /// own reported clip length, not a guess). Before this, the two alerts
    /// shared one MCI alias with unconditional close-then-play, so a Form
    /// confirmed moments after a Shiny silenced the rarer sound - the exact
    /// "never heard the shiny" shape the user reported.</summary>
    internal enum SoundPriority
    {
        Form = 1,
        Shiny = 2,
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(
        string command,
        StringBuilder? returnBuffer,
        int returnLength,
        IntPtr callbackWindow);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool mciGetErrorStringW(
        int errorCode,
        StringBuilder buffer,
        int bufferLength);

    // §147: the plain wave player. Null for the sound stops whatever this
    // process last started through it.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string? soundFile, IntPtr module, uint flags);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    // §152: the volume slider's range. 100 plays the clip as shipped; 0 is
    // silence and is not played at all (PlaySound says so, like None).
    internal const int MaxVolumePercent = 100;

    /// <summary>§152. The slider's curve: (percent/100)^2 on the samples,
    /// so halfway down is about half as loud to the ear (-12 dB) rather
    /// than the barely-quieter -6 dB a straight line would give, and 10%
    /// is a whisper (-40 dB) instead of still loud.</summary>
    internal static double GainFor(int volumePercent)
    {
        double fraction = Math.Clamp(volumePercent, 0, MaxVolumePercent) / (double)MaxVolumePercent;
        return fraction * fraction;
    }

    // §152: where the quieter copies of the clips live - one .wav per clip
    // and volume, named after the source's size and write time so a changed
    // clip is never played from a stale copy. Beside the Logs and Database
    // folders; small (a copy is the size of its source), and copies not
    // rewritten for a month are cleared on the next write.
    private static readonly string ScaledSoundFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProTracker", "SoundCache");

    // §152: a clip that could not be scaled is reported once per run, not
    // on every alert. Touched from the playback thread and WarmUp's worker.
    private static readonly ConcurrentDictionary<string, byte> unscalableReported = new(StringComparer.Ordinal);
    private static bool staleCopiesPruned;

    // §148: the non-Windows players in order of preference - executable,
    // the arguments that go before the file, and which of the two shipped
    // formats each one decodes. afplay is built into macOS; paplay/pw-play/
    // aplay cover the Linux desktops; the rest are common extras.
    private static readonly (string Exe, string[] Args, bool PlaysWave, bool PlaysMp3)[] SystemPlayers =
    {
        ("afplay", Array.Empty<string>(), true, true),
        ("paplay", Array.Empty<string>(), true, false),
        ("pw-play", Array.Empty<string>(), true, false),
        ("aplay", new[] { "-q" }, true, false),
        ("ffplay", new[] { "-nodisp", "-autoexit", "-loglevel", "quiet" }, true, true),
        ("mpv", new[] { "--no-video", "--really-quiet" }, true, true),
        ("play", new[] { "-q" }, true, true),
        ("mpg123", new[] { "-q" }, false, true),
    };

    // §148: a player that failed once this run is not tried again - the
    // next alert moves on to the next candidate instead of failing the same
    // way every time. Guarded by playbackLock.
    private static readonly HashSet<string> failedPlayers = new(StringComparer.Ordinal);

    // §148: the child process carrying the current (non-Windows) clip, so
    // latest-wins can stop it. Guarded by playbackLock.
    private static SystemPlayerRun? currentRun;

    private sealed class SystemPlayerRun
    {
        public required Process Process { get; init; }
        public required string Player { get; init; }
        public required string SoundName { get; init; }
        public required DateTime StartedUtc { get; init; }
        public StringBuilder StandardError { get; } = new();
        public bool StoppedByUs { get; set; }
    }

    // Guards the whole open/play sequence (§103) - kept even though §146's
    // single playback thread already serialises the calls, so a second
    // caller added later cannot interleave commands against the shared alias.
    private static readonly object playbackLock = new();

    // §146: one long-lived background thread owns every MCI call. MCI ties
    // an open device to the thread that opened it, and the §103 Task.Run
    // worker was a thread-pool thread the pool is free to retire at any
    // moment - a clip could be cut short, or never start, with nothing
    // logged. This thread is created on the first alert, never exits while
    // the app runs, and is a background thread so it never delays shutdown.
    private static readonly BlockingCollection<Action> playbackQueue = new();
    private static readonly Lazy<Thread> playbackThread = new(StartPlaybackThread);

    // What is (still) playing, for the priority rule above. EndsAtUtc comes
    // from MCI's "status ... length" answer at play time; a clip whose end
    // time has passed no longer blocks anything.
    private static int playingPriority;
    private static DateTime playingEndsAtUtc = DateTime.MinValue;

    // Fixed alias so a repeat play always closes out whatever the previous
    // one opened under the same name first - see PlaySound.
    private const string DeviceAlias = "ProTrackerRareEncounterSound";

    // Display name (shown in the File menu and stored in UiPreferences) ->
    // file name under SharedPokemonLibrary/Sounds. "None" is deliberately
    // NOT an entry here - it has no file to look up, so it's handled as an
    // early return in PlaySound instead. To offer another sound: drop the
    // .mp3 in SharedPokemonLibrary/Sounds (already covered by the
    // .csproj's Content glob for that folder), add one line here, and add
    // one new MenuItem to each of MainWindow.axaml's Since Form Sound/
    // Since Shiny Sound submenus - nothing else needs to change.
    //
    // §147: a .wav with the same name beside the .mp3 (Yay.wav, Shiny.wav -
    // 44.1 kHz 16-bit stereo, converted from the same clips) is what
    // actually plays on Windows now; the .mp3 is the fallback when the .wav
    // is missing. A new sound should ship both.
    internal static readonly Dictionary<string, string> SoundCatalog = new()
    {
        ["Yay"] = "Yay.mp3",
        ["Shiny"] = "Shiny.mp3",
    };

    // Deliberately swallows every failure - a missing sound file, a bad MCI
    // command, anything - rather than letting a sound effect interrupt the
    // hunt it's supposed to be celebrating. Same "must never take down
    // tracking" reasoning already applied to TryDetectLevelSafely/
    // TryDetectGenderSafely in EncounterTracking.cs. Safe to call
    // unconditionally, with any string (null, empty, "None", or an
    // unrecognized name included) - the checks below make all of those a
    // harmless no-op, so callers never need their own "is this actually a
    // sound" check first, on any platform. §152: volumePercent is 0-100
    // (see MaxVolumePercent); anything above 100 plays as 100.
    internal static void PlaySound(string? soundName, SoundPriority priority = SoundPriority.Form, int volumePercent = MaxVolumePercent)
    {
        // §146: each early return says why, at Information - an alert is a
        // rare event, and a silent one used to be indistinguishable from a
        // failed one in the log.
        if (string.IsNullOrWhiteSpace(soundName) || soundName == "None")
        {
            Log.Information("Sound alert ({Priority}): no sound selected - nothing to play.", priority);
            return;
        }

        // §152: selected but turned all the way down.
        if (volumePercent <= 0)
        {
            Log.Information("Sound alert ({Priority}): {SoundName} selected at 0% volume - nothing to play.", priority, soundName);
            return;
        }

        volumePercent = Math.Min(volumePercent, MaxVolumePercent);

        // §148: Windows, Linux and macOS all play; anything else says so.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Log.Information("Sound alert ({Priority}): {SoundName} selected, but sounds are not supported on this platform.", priority, soundName);
            return;
        }

        if (!SoundCatalog.TryGetValue(soundName, out string? fileName))
        {
            Log.Warning("No sound catalog entry for {SoundName} - skipping playback.", soundName);
            return;
        }

        string soundFilePath = Path.Combine(
            AppContext.BaseDirectory,
            "SharedPokemonLibrary",
            "Sounds",
            fileName);

        // §103: requested-at timestamp taken on the caller's thread, so the
        // Debug timeline can separate "how long until the worker ran" from
        // "how long MCI itself took" when measuring a reported delay.
        DateTime requestedAtUtc = DateTime.UtcNow;
        Log.Information("Sound alert ({Priority}): {SoundName} requested at {Volume}%.", priority, soundName, volumePercent);

        // §103 took MCI off the UI thread, where a cold device open could
        // visibly stall the very displays the alert celebrates. §146 moved
        // it from a thread-pool worker to the dedicated thread above.
        Enqueue(() => PlayCore(soundName, soundFilePath, priority, volumePercent, requestedAtUtc));
    }

    private static Thread StartPlaybackThread()
    {
        var thread = new Thread(() =>
        {
            foreach (Action work in playbackQueue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Sound playback work failed.");
                }
            }
        })
        {
            IsBackground = true,
            Name = "ProTracker sound alerts",
        };

        // §147: single-threaded apartment, the state the UI thread has and
        // the one MCI's MPEGVideo driver needs to load (see the class remarks).
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        Log.Information("Sound playback thread started (apartment {Apartment}).", thread.GetApartmentState());
        return thread;
    }

    private static void Enqueue(Action work)
    {
        _ = playbackThread.Value;
        playbackQueue.Add(work);
    }

    /// <summary>§146. MCI's own wording for a return code, with the number.</summary>
    private static string DescribeMciError(int errorCode)
    {
        var buffer = new StringBuilder(256);

        return mciGetErrorStringW(errorCode, buffer, buffer.Capacity) && buffer.Length > 0
            ? $"{buffer} (MCI error {errorCode})"
            : $"MCI error {errorCode}";
    }

    private static void PlayCore(string soundName, string soundFilePath, SoundPriority priority, int volumePercent, DateTime requestedAtUtc)
    {
        try
        {
            // §147: the .wav beside the catalog's .mp3 is the first choice.
            string wavePath = Path.ChangeExtension(soundFilePath, ".wav");
            bool haveWave = File.Exists(wavePath);

            if (!haveWave && !File.Exists(soundFilePath))
            {
                Log.Warning(
                    "{SoundName} is selected but neither {WavePath} nor {Path} exists - skipping playback.",
                    soundName, wavePath, soundFilePath);

                return;
            }

            // §152: below 100% the wave that plays is the scaled copy; null
            // when none could be made, and the original plays at full volume
            // (ScaledWavePath has logged why). Usually already on disk from
            // WarmUp, so this is one directory look-up. The priority window
            // still comes from the original - a copy is the same length.
            string? scaledWavePath = haveWave && volumePercent < MaxVolumePercent
                ? ScaledWavePath(soundName, wavePath, volumePercent)
                : null;

            lock (playbackLock)
            {
                // Priority rule (§103): never cut a HIGHER-priority clip that
                // is still inside its own measured play window. Equal or
                // higher requests behave exactly as before - latest wins.
                if ((int)priority < playingPriority && DateTime.UtcNow < playingEndsAtUtc)
                {
                    Log.Information(
                        "Sound {SoundName} (priority {Priority}) skipped - a higher-priority sound is still playing for another {RemainingMs:F0}ms",
                        soundName, priority, (playingEndsAtUtc - DateTime.UtcNow).TotalMilliseconds);

                    return;
                }

                var timer = Stopwatch.StartNew();

                // §148: everything from here down is the Windows path; Linux
                // and macOS hand the clip to the system's own player.
                if (!OperatingSystem.IsWindows())
                {
                    PlayWithSystemPlayer(soundName, soundFilePath, wavePath, scaledWavePath, haveWave, volumePercent, priority, requestedAtUtc, timer);
                    return;
                }

                // Ignored return value: fails harmlessly (nothing is open
                // under this alias yet) on every play except one that starts
                // before the previous sound finished - the one case "open"
                // alone would otherwise reject outright. Both players are
                // stopped, whichever one carried the last clip.
                mciSendStringW($"close {DeviceAlias}", null, 0, IntPtr.Zero);
                PlaySoundW(null, IntPtr.Zero, 0);

                // §147: the wave path first. PlaySound reads the file and
                // hands it to the wave device on a thread of winmm's own; no
                // MCI driver, no DirectShow, no COM - the one path that
                // cannot answer "cannot load the device driver".
                if (haveWave)
                {
                    string wavePlayed = scaledWavePath ?? wavePath;

                    if (PlaySoundW(wavePlayed, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT))
                    {
                        double waveMs = WaveLengthMs(wavePath);
                        playingPriority = (int)priority;
                        playingEndsAtUtc = DateTime.UtcNow.AddMilliseconds(waveMs);

                        Log.Information(
                            "Sound {SoundName}: playing {File} at {Volume}% via PlaySound - worker picked up after {QueueMs:F0}ms, started at {StartMs}ms, clip length {LengthMs:F0}ms",
                            soundName,
                            Path.GetFileName(wavePlayed),
                            scaledWavePath is null ? MaxVolumePercent : volumePercent,
                            (requestedAtUtc == default ? 0 : (DateTime.UtcNow - requestedAtUtc).TotalMilliseconds - timer.ElapsedMilliseconds),
                            timer.ElapsedMilliseconds,
                            waveMs);

                        return;
                    }

                    Log.Warning(
                        "Sound {SoundName}: PlaySound refused {WavePath} - falling back to MCI with the .mp3.",
                        soundName, wavePlayed);

                    if (!File.Exists(soundFilePath))
                    {
                        Log.Warning("Sound {SoundName}: no .mp3 beside it either ({Path}). Nothing played.", soundName, soundFilePath);
                        return;
                    }
                }

                // §146: the return codes were ignored before, so a failed open
                // or play left no trace at all. An open that fails with the
                // explicit device type is retried once letting MCI choose the
                // device from the file's extension.
                int openError = mciSendStringW(
                    $"open \"{soundFilePath}\" type MPEGVideo alias {DeviceAlias}",
                    null,
                    0,
                    IntPtr.Zero);

                if (openError != 0)
                {
                    Log.Warning(
                        "Sound {SoundName}: MCI open (MPEGVideo) failed - {Error}; retrying without a device type.",
                        soundName, DescribeMciError(openError));

                    openError = mciSendStringW(
                        $"open \"{soundFilePath}\" alias {DeviceAlias}",
                        null,
                        0,
                        IntPtr.Zero);
                }

                if (openError != 0)
                {
                    Log.Warning(
                        "Sound {SoundName}: MCI could not open {Path} - {Error}. Nothing played.",
                        soundName, soundFilePath, DescribeMciError(openError));

                    return;
                }

                long openMs = timer.ElapsedMilliseconds;

                // §152: MCI's own volume for the .mp3 path, 0-1000 on the
                // same curve as the wave copies. A driver that refuses it
                // plays at full volume, and the log says so.
                int mciVolumePercent = MaxVolumePercent;

                if (volumePercent < MaxVolumePercent)
                {
                    int volumeError = mciSendStringW(
                        $"setaudio {DeviceAlias} volume to {(int)Math.Round(GainFor(volumePercent) * 1000)}",
                        null,
                        0,
                        IntPtr.Zero);

                    if (volumeError == 0)
                        mciVolumePercent = volumePercent;
                    else
                        Log.Information("Sound {SoundName}: MCI would not set the volume ({Error}) - playing at full volume.", soundName, DescribeMciError(volumeError));
                }

                // The clip's own duration, from MCI itself - feeds the
                // priority window above instead of a hard-coded guess.
                var lengthBuffer = new StringBuilder(32);
                mciSendStringW($"status {DeviceAlias} length", lengthBuffer, lengthBuffer.Capacity, IntPtr.Zero);

                int playError = mciSendStringW($"play {DeviceAlias}", null, 0, IntPtr.Zero);

                if (playError != 0)
                {
                    Log.Warning(
                        "Sound {SoundName}: MCI play failed - {Error}. Nothing played.",
                        soundName, DescribeMciError(playError));

                    mciSendStringW($"close {DeviceAlias}", null, 0, IntPtr.Zero);
                    return;
                }

                long playReturnedMs = timer.ElapsedMilliseconds;

                double lengthMs = double.TryParse(lengthBuffer.ToString(), out double parsed) ? parsed : 3000;
                playingPriority = (int)priority;
                playingEndsAtUtc = DateTime.UtcNow.AddMilliseconds(lengthMs);

                // §146: MCI's own word for what the device is doing right
                // after "play" returned - "playing" is the proof of life; a
                // device that says "stopped" here accepted the command and
                // then rendered nothing, which is a different failure from a
                // refused open and is worth its own warning.
                var modeBuffer = new StringBuilder(32);
                mciSendStringW($"status {DeviceAlias} mode", modeBuffer, modeBuffer.Capacity, IntPtr.Zero);
                string mode = modeBuffer.ToString();

                // The §103 measurement line: queue delay (request to worker),
                // device open cost, and when play was actually issued -
                // "playback started" as observable as MCI allows. Information
                // since §146: an alert is rare, and this is its proof of life.
                Log.Information(
                    "Sound {SoundName}: play issued at {Volume}% - device mode \"{Mode}\", worker picked up after {QueueMs:F0}ms, open took {OpenMs}ms, play issued at {PlayMs}ms, clip length {LengthMs:F0}ms",
                    soundName,
                    mciVolumePercent,
                    mode,
                    (requestedAtUtc == default ? 0 : (DateTime.UtcNow - requestedAtUtc).TotalMilliseconds - playReturnedMs),
                    openMs,
                    playReturnedMs,
                    lengthMs);

                if (mode.Length > 0 && mode != "playing")
                {
                    Log.Warning(
                        "Sound {SoundName}: MCI accepted play but reports mode \"{Mode}\" - the clip is probably not audible.",
                        soundName, mode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to play {SoundName} ({Path}).", soundName, soundFilePath);
        }
    }

    /// <summary>§148. Linux and macOS: the first system player on the PATH
    /// that decodes a file we have, started as a child process. Called under
    /// playbackLock from the playback thread. §152: a player given the scaled
    /// .wav copy needs nothing more; one given the .mp3 (or the original
    /// .wav, when no copy could be made) gets its own volume option.</summary>
    private static void PlayWithSystemPlayer(
        string soundName, string soundFilePath, string wavePath, string? scaledWavePath, bool haveWave,
        int volumePercent, SoundPriority priority, DateTime requestedAtUtc, Stopwatch timer)
    {
        // Latest wins, as on Windows: whatever is still playing stops first.
        StopSystemPlayer();

        var tried = new List<string>();

        foreach ((string exe, string[] args, bool playsWave, bool playsMp3) in SystemPlayers)
        {
            if (failedPlayers.Contains(exe))
                continue;

            bool useWave = haveWave && playsWave;

            string? file = useWave ? scaledWavePath ?? wavePath
                : playsMp3 && File.Exists(soundFilePath) ? soundFilePath
                : null;

            if (file is null)
                continue;

            bool preScaled = useWave && scaledWavePath is not null;
            int playedPercent = preScaled ? volumePercent : MaxVolumePercent;

            string? exePath = FindOnPath(exe);

            if (exePath is null)
            {
                tried.Add(exe);
                continue;
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            if (volumePercent < MaxVolumePercent && !preScaled)
            {
                string[]? volumeArguments = VolumeArguments(exe, GainFor(volumePercent));

                if (volumeArguments is null)
                {
                    Log.Information("Sound {SoundName}: {Player} has no volume option - playing at full volume.", soundName, exe);
                }
                else
                {
                    foreach (string arg in volumeArguments)
                        startInfo.ArgumentList.Add(arg);

                    playedPercent = volumePercent;
                }
            }

            startInfo.ArgumentList.Add(file);

            Process? process;

            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Sound {SoundName}: {Player} could not be started - trying the next player.", soundName, exe);
                failedPlayers.Add(exe);
                continue;
            }

            if (process is null)
            {
                Log.Warning("Sound {SoundName}: {Player} did not start - trying the next player.", soundName, exe);
                failedPlayers.Add(exe);
                continue;
            }

            var run = new SystemPlayerRun
            {
                Process = process,
                Player = exe,
                SoundName = soundName,
                StartedUtc = DateTime.UtcNow,
            };

            currentRun = run;

            // stdout is drained and dropped; stderr is kept (bounded) for the
            // log line a non-zero exit produces. Exited is wired before
            // EnableRaisingEvents so a clip that ends instantly is not missed.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null && run.StandardError.Length < 400)
                    run.StandardError.AppendLine(e.Data);
            };
            process.Exited += (_, _) => OnSystemPlayerExited(run);
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            double lengthMs = useWave ? WaveLengthMs(wavePath) : 3000;
            playingPriority = (int)priority;
            playingEndsAtUtc = DateTime.UtcNow.AddMilliseconds(lengthMs);

            Log.Information(
                "Sound {SoundName}: playing {File} at {Volume}% via {Player} (pid {Pid}) - worker picked up after {QueueMs:F0}ms, started at {StartMs}ms, clip length {LengthMs:F0}ms",
                soundName,
                Path.GetFileName(file),
                playedPercent,
                exe,
                process.Id,
                (requestedAtUtc == default ? 0 : (DateTime.UtcNow - requestedAtUtc).TotalMilliseconds - timer.ElapsedMilliseconds),
                timer.ElapsedMilliseconds,
                lengthMs);

            return;
        }

        Log.Warning(
            "Sound {SoundName}: no working sound player on this system (looked for {Players} on the PATH{Failed}). On Linux install pulseaudio-utils (paplay), pipewire (pw-play) or alsa-utils (aplay); afplay is built into macOS. Nothing played.",
            soundName,
            string.Join(", ", tried),
            failedPlayers.Count == 0 ? string.Empty : "; already failed this run: " + string.Join(", ", failedPlayers));
    }

    /// <summary>§152. The player's own volume option, for a clip that is not
    /// a pre-scaled copy. Each one counts differently - afplay, pw-play and
    /// play take the plain factor, ffplay the same as 0-100, mpg123 a factor
    /// of 32768, while paplay (PulseAudio's cubic scale, 0-65536) and mpv
    /// (cubic, 0-100) want its cube root - all worked back from the same
    /// gain so every player lands near the same loudness. Null for aplay,
    /// which has none. Invariant culture: "0,25" is not a number to any of
    /// them.</summary>
    internal static string[]? VolumeArguments(string executable, double gain)
    {
        string factor = gain.ToString("0.####", CultureInfo.InvariantCulture);

        string Whole(double value) => ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);

        return executable switch
        {
            "afplay" => new[] { "-v", factor },
            "paplay" => new[] { "--volume=" + Whole(Math.Cbrt(gain) * 65536) },
            "pw-play" => new[] { "--volume=" + factor },
            "ffplay" => new[] { "-volume", Whole(gain * 100) },
            "mpv" => new[] { "--volume=" + Whole(Math.Cbrt(gain) * 100) },
            "play" => new[] { "-v", factor },
            "mpg123" => new[] { "-f", Whole(gain * 32768) },
            _ => null,
        };
    }

    /// <summary>§152. The path of a copy of the clip scaled to the volume,
    /// made on first use and kept under ScaledSoundFolder, so the players
    /// with no volume control of their own (PlaySound, aplay) still play it
    /// quieter. Null when no copy could be made - the caller plays the
    /// original at full volume, and the reason is logged once per clip.
    /// Safe from any thread: a copy is written under a temporary name and
    /// moved into place, so a second tracker instance (or WarmUp racing an
    /// alert) never reads a half-written file.</summary>
    internal static string? ScaledWavePath(string soundName, string wavePath, int volumePercent)
    {
        try
        {
            var source = new FileInfo(wavePath);

            if (!source.Exists)
                return null;

            string prefix = $"{Path.GetFileNameWithoutExtension(wavePath)}-{source.Length}-{source.LastWriteTimeUtc.Ticks:x}";
            string target = Path.Combine(ScaledSoundFolder, $"{prefix}-v{volumePercent}.wav");

            if (File.Exists(target) && new FileInfo(target).Length == source.Length)
                return target;

            byte[]? scaled = WaveVolume.Scale(File.ReadAllBytes(wavePath), GainFor(volumePercent), out string reason);

            if (scaled is null)
            {
                if (unscalableReported.TryAdd(wavePath, 0))
                    Log.Warning("Sound {SoundName}: {File} cannot be played quieter ({Reason}) - it plays at full volume.", soundName, Path.GetFileName(wavePath), reason);

                return null;
            }

            Directory.CreateDirectory(ScaledSoundFolder);
            PruneStaleCopies();

            string temporary = Path.Combine(ScaledSoundFolder, Path.GetRandomFileName());
            File.WriteAllBytes(temporary, scaled);

            try
            {
                File.Move(temporary, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another instance got there first, and its copy is identical.
                try { File.Delete(temporary); } catch { /* left for the next prune */ }
            }

            Log.Information(
                "Sound {SoundName}: {Volume}% copy of {File} ({Bytes} bytes) ready under {Folder}.",
                soundName, volumePercent, Path.GetFileName(wavePath), scaled.Length, ScaledSoundFolder);

            return target;
        }
        catch (Exception ex)
        {
            if (unscalableReported.TryAdd(wavePath, 0))
                Log.Warning(ex, "Sound {SoundName}: a {Volume}% copy of {File} could not be made - it plays at full volume.", soundName, volumePercent, Path.GetFileName(wavePath));

            return null;
        }
    }

    /// <summary>§152. Once per run, before a copy is written: anything in
    /// the cache folder not rewritten for a month goes - copies at volumes
    /// the slider has left behind, copies of clips that changed. A file
    /// another instance is playing stays; it is small and goes next time.</summary>
    private static void PruneStaleCopies()
    {
        if (staleCopiesPruned)
            return;

        staleCopiesPruned = true;
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-30);

        foreach (string file in Directory.EnumerateFiles(ScaledSoundFolder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    File.Delete(file);
            }
            catch
            {
                // In use, or not ours - either way not worth a line.
            }
        }
    }

    private static void StopSystemPlayer()
    {
        SystemPlayerRun? run = currentRun;

        if (run is null)
            return;

        try
        {
            if (!run.Process.HasExited)
            {
                run.StoppedByUs = true;
                run.Process.Kill();
            }
        }
        catch (Exception ex)
        {
            // Already gone, or not ours to signal - either way the next
            // clip starts regardless.
            Log.Information(ex, "Sound {SoundName}: the previous {Player} could not be stopped.", run.SoundName, run.Player);
        }
    }

    private static void OnSystemPlayerExited(SystemPlayerRun run)
    {
        try
        {
            // Flushes the asynchronous stdout/stderr reads before the buffer
            // is looked at.
            run.Process.WaitForExit();

            int exitCode = run.Process.ExitCode;
            double ms = (DateTime.UtcNow - run.StartedUtc).TotalMilliseconds;

            if (run.StoppedByUs)
            {
                Log.Information("Sound {SoundName}: {Player} stopped after {Ms:F0}ms for a newer alert.", run.SoundName, run.Player, ms);
            }
            else if (exitCode != 0)
            {
                string stderr = run.StandardError.ToString().Trim();

                Log.Warning(
                    "Sound {SoundName}: {Player} exited with code {ExitCode} after {Ms:F0}ms{Detail} - the next alert will try the next player.",
                    run.SoundName, run.Player, exitCode, ms,
                    stderr.Length == 0 ? string.Empty : " (" + stderr + ")");

                lock (playbackLock)
                {
                    failedPlayers.Add(run.Player);
                }
            }
            else
            {
                // A clean exit with complaints on stderr is still worth
                // seeing: ffplay, for one, exits 0 on a machine with no audio
                // device and only says so on stderr.
                string stderr = run.StandardError.ToString().Trim();

                Log.Information(
                    "Sound {SoundName}: {Player} finished after {Ms:F0}ms{Detail}.",
                    run.SoundName, run.Player, ms,
                    stderr.Length == 0 ? string.Empty : " (it also said: " + stderr + ")");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sound {SoundName}: {Player} exit could not be read.", run.SoundName, run.Player);
        }
        finally
        {
            lock (playbackLock)
            {
                if (ReferenceEquals(currentRun, run))
                    currentRun = null;
            }

            run.Process.Dispose();
        }
    }

    /// <summary>§148. The executable's full path from the PATH, with the
    /// usual system directories appended in case the tracker was launched
    /// from a desktop entry with a bare environment. Null when absent.</summary>
    private static string? FindOnPath(string executable)
    {
        IEnumerable<string> directories =
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Concat(new[] { "/usr/bin", "/usr/local/bin", "/bin", "/opt/homebrew/bin" });

        foreach (string directory in directories)
        {
            try
            {
                string candidate = Path.Combine(directory, executable);

                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // An unreadable PATH entry is skipped, not fatal.
            }
        }

        return null;
    }

    /// <summary>§147. The clip length from the RIFF header (data bytes over
    /// the byte rate) - what the priority window needs, since PlaySound
    /// cannot be asked. 3 s when the header cannot be read.</summary>
    internal static double WaveLengthMs(string wavePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(wavePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 12)
                return 3000;

            reader.ReadBytes(12);
            int byteRate = 0;

            while (stream.Position + 8 <= stream.Length)
            {
                string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                uint chunkSize = reader.ReadUInt32();

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    byteRate = (int)reader.ReadUInt32();
                    stream.Position += chunkSize - 12;
                }
                else if (chunkId == "data")
                {
                    return byteRate > 0 ? chunkSize * 1000.0 / byteRate : 3000;
                }
                else
                {
                    stream.Position += chunkSize + (chunkSize & 1);
                }
            }
        }
        catch
        {
            // A clip that will not parse still plays; only the window is guessed.
        }

        return 3000;
    }

    /// <summary>§103: warms the OS file cache for the selected alert clips
    /// when tracking starts, so the first real alert of a session never pays
    /// first-touch disk latency at the moment it matters. Fire-and-forget,
    /// worker thread, every failure swallowed - a warm-up must never matter
    /// functionally. §152: takes each sound with its volume, and makes the
    /// quieter copy now (ScaledWavePath) so the first alert never waits for
    /// the read-scale-write either.</summary>
    internal static void WarmUp(params (string? SoundName, int VolumePercent)[] sounds)
    {
        // §148: no platform gate - warming the file cache is harmless anywhere.
        var clips = new List<(string SoundName, string Path, int VolumePercent)>();

        foreach ((string? soundName, int volumePercent) in sounds)
        {
            if (string.IsNullOrWhiteSpace(soundName) || soundName == "None")
                continue;

            if (!SoundCatalog.TryGetValue(soundName, out string? fileName))
                continue;

            clips.Add((soundName, Path.Combine(AppContext.BaseDirectory, "SharedPokemonLibrary", "Sounds", fileName), volumePercent));
        }

        if (clips.Count == 0)
            return;

        Task.Run(() =>
        {
            foreach ((string soundName, string path, int volumePercent) in clips)
            {
                try
                {
                    // §147: the .wav sibling is what plays; warm it too.
                    string wavePath = Path.ChangeExtension(path, ".wav");

                    foreach (string file in new[] { path, wavePath })
                    {
                        if (File.Exists(file))
                            _ = File.ReadAllBytes(file);
                    }

                    if (volumePercent > 0 && volumePercent < MaxVolumePercent && File.Exists(wavePath))
                        _ = ScaledWavePath(soundName, wavePath, volumePercent);
                }
                catch
                {
                    // Cache warming only - never worth surfacing.
                }
            }
        });
    }
}
