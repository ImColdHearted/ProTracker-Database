using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Foot_Tracker.Models;
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
//
// §389: an output device of the user's choosing (Sound Settings, "Play
// through"; UiPreferences.SoundOutputDevice), because a Linux hunter's
// alerts kept leaving his headset. PlaySound takes the pin; the alert is
// checked against the devices actually present (SoundOutputDevices) and
// plays on the system default, saying so, when the chosen one is not
// there. On Linux the device travels to the system player as its own
// option (paplay --device, pw-play --target, aplay -D, mpv --audio-device,
// and the PULSE_SINK / AUDIODEV environment the rest read - see
// DeviceOptions), and a player that then fails is retried once on the
// default rather than blacklisted. On Windows PlaySound cannot be told a
// device, so a pinned clip goes through waveOut instead (PlayWithWaveOut):
// the wave's own samples handed to that device number, released once the
// clip has finished. Nothing pinned changes nothing: the paths above are
// exactly what they were.
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

        // §389: the device the player was pointed at, and the same clip
        // again on the system default should the player fail because of it.
        public SoundOutputDevice? Device { get; init; }
        public Action? Retry { get; init; }
    }

    // §389: the devices present the last time anyone looked (WarmUp at
    // every hunt start, Sound Settings when it opens or saves, or the first
    // alert if neither has) - what a pinned device is checked against on
    // Linux, where listing means running a tool or two. Windows re-lists
    // at play time instead; that is a couple of winmm calls. Guarded by
    // playbackLock, with the pins already reported missing since the last
    // listing, so a hunt with the headset off logs it once.
    private static IReadOnlyList<SoundOutputDevice>? presentDevices;
    private static readonly HashSet<string> missingReported = new(StringComparer.Ordinal);

    // §389: the clip waveOut is playing on a pinned Windows device. Guarded
    // by playbackLock; released by StopWaveOut on the playback thread.
    private static WaveOutRun? currentWaveOut;

    private sealed class WaveOutRun
    {
        public required IntPtr Handle { get; init; }
        public required IntPtr Header { get; init; }
        public required IntPtr Data { get; init; }
        public required string SoundName { get; init; }
        public Timer? Cleanup { get; set; }
    }

    // §389: winmm's wave-out interface, the device-choosing route PlaySound
    // does not have. Declared everywhere, called only behind
    // OperatingSystem.IsWindows(), like the imports above.
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr handle);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetErrorTextW(uint error, StringBuilder text, uint size);

    private const uint CALLBACK_NULL = 0;

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
    // (see MaxVolumePercent); anything above 100 plays as 100. §389:
    // outputDevice is the pinned device, or null (and SystemDefault) for
    // whatever the operating system plays through.
    internal static void PlaySound(
        string? soundName, SoundPriority priority = SoundPriority.Form, int volumePercent = MaxVolumePercent,
        SoundOutputDevice? outputDevice = null)
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

        if (outputDevice is { IsSystemDefault: true })
            outputDevice = null;

        Log.Information(
            "Sound alert ({Priority}): {SoundName} requested at {Volume}%{Through}.",
            priority, soundName, volumePercent, outputDevice is null ? string.Empty : " through " + outputDevice.Label);

        // §103 took MCI off the UI thread, where a cold device open could
        // visibly stall the very displays the alert celebrates. §146 moved
        // it from a thread-pool worker to the dedicated thread above.
        Enqueue(() => PlayCore(soundName, soundFilePath, priority, volumePercent, requestedAtUtc, outputDevice));
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

    private static void PlayCore(
        string soundName, string soundFilePath, SoundPriority priority, int volumePercent, DateTime requestedAtUtc,
        SoundOutputDevice? outputDevice)
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
                    PlayWithSystemPlayer(
                        soundName, soundFilePath, wavePath, scaledWavePath, haveWave, volumePercent, priority, requestedAtUtc, timer,
                        ResolveOutput(outputDevice, soundName));

                    return;
                }

                // Ignored return value: fails harmlessly (nothing is open
                // under this alias yet) on every play except one that starts
                // before the previous sound finished - the one case "open"
                // alone would otherwise reject outright. All three players
                // are stopped, whichever one carried the last clip (§389
                // added waveOut).
                mciSendStringW($"close {DeviceAlias}", null, 0, IntPtr.Zero);
                PlaySoundW(null, IntPtr.Zero, 0);
                StopWaveOut();

                // §389: a pinned device that is present takes the clip
                // through waveOut - PlaySound has no way to name a device.
                // A device that cannot be opened, or a wave waveOut will not
                // take, falls through to PlaySound on the default, and the
                // log says so.
                SoundOutputDevice? output = haveWave ? ResolveOutput(outputDevice, soundName) : null;

                if (output is not null)
                {
                    string pinnedWave = scaledWavePath ?? wavePath;
                    double pinnedMs = WaveLengthMs(wavePath);

                    if (PlayWithWaveOut(soundName, pinnedWave, output, pinnedMs, out string failure))
                    {
                        playingPriority = (int)priority;
                        playingEndsAtUtc = DateTime.UtcNow.AddMilliseconds(pinnedMs);

                        Log.Information(
                            "Sound {SoundName}: playing {File} at {Volume}% through {Device} (waveOut device {Index}) - worker picked up after {QueueMs:F0}ms, started at {StartMs}ms, clip length {LengthMs:F0}ms",
                            soundName,
                            Path.GetFileName(pinnedWave),
                            scaledWavePath is null ? MaxVolumePercent : volumePercent,
                            output.Label,
                            output.Index,
                            (requestedAtUtc == default ? 0 : (DateTime.UtcNow - requestedAtUtc).TotalMilliseconds - timer.ElapsedMilliseconds),
                            timer.ElapsedMilliseconds,
                            pinnedMs);

                        return;
                    }

                    Log.Warning(
                        "Sound {SoundName}: {Device} could not take the clip ({Failure}) - playing on the system default instead.",
                        soundName, output.Label, failure);
                }

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
    /// .wav, when no copy could be made) gets its own volume option. §389:
    /// <paramref name="output"/> is the pinned device, already checked to be
    /// present (null for the system default); the players that can be
    /// pointed at it are tried first, and one that fails with it is retried
    /// once on the default - see OnSystemPlayerExited.</summary>
    private static void PlayWithSystemPlayer(
        string soundName, string soundFilePath, string wavePath, string? scaledWavePath, bool haveWave,
        int volumePercent, SoundPriority priority, DateTime requestedAtUtc, Stopwatch timer, SoundOutputDevice? output)
    {
        // Latest wins, as on Windows: whatever is still playing stops first.
        StopSystemPlayer();

        var tried = new List<string>();

        // §389: with a device pinned, the players that can be pointed at it
        // go first (a stable sort - the §148 order holds within each half);
        // the rest are a last resort that plays on the default.
        IEnumerable<(string Exe, string[] Args, bool PlaysWave, bool PlaysMp3)> candidates = output is null
            ? SystemPlayers
            : SystemPlayers.OrderBy(player => DeviceOptions(player.Exe, output) is null ? 1 : 0);

        foreach ((string exe, string[] args, bool playsWave, bool playsMp3) in candidates)
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

            // §389: the device, as this player takes it - an option, an
            // environment variable, or both. A player that takes neither
            // plays on the default, and the log says so.
            (string[] Arguments, (string Name, string Value)[] Environment)? deviceOptions =
                output is null ? null : DeviceOptions(exe, output);

            if (output is not null)
            {
                if (deviceOptions is null)
                {
                    Log.Information("Sound {SoundName}: {Player} cannot be pointed at {Device} - playing on the system default.", soundName, exe, output.Label);
                }
                else
                {
                    foreach (string arg in deviceOptions.Value.Arguments)
                        startInfo.ArgumentList.Add(arg);

                    foreach ((string name, string value) in deviceOptions.Value.Environment)
                        startInfo.Environment[name] = value;
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
                Device = deviceOptions is null ? null : output,
                Retry = deviceOptions is null
                    ? null
                    : () => PlayCore(soundName, soundFilePath, priority, volumePercent, DateTime.UtcNow, null),
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
                "Sound {SoundName}: playing {File} at {Volume}% via {Player} (pid {Pid}){Through} - worker picked up after {QueueMs:F0}ms, started at {StartMs}ms, clip length {LengthMs:F0}ms",
                soundName,
                Path.GetFileName(file),
                playedPercent,
                exe,
                process.Id,
                run.Device is null ? string.Empty : " through " + run.Device.Label,
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
            else if (exitCode != 0 && run.Retry is not null && run.Device is not null)
            {
                // §389: the player was pointed at a device and failed - a
                // headset switched off since the list was made, most likely.
                // The device is dropped from the present list, the same
                // clip goes again on the system default, and the player is
                // NOT blacklisted: it did nothing wrong. The retry carries
                // no device, so it cannot come back here.
                string stderr = run.StandardError.ToString().Trim();

                Log.Warning(
                    "Sound {SoundName}: {Player} could not play through {Device} - exit code {ExitCode} after {Ms:F0}ms{Detail}. Retrying on the system default, which alerts use until the device is listed again.",
                    run.SoundName, run.Player, run.Device.Label, exitCode, ms,
                    stderr.Length == 0 ? string.Empty : " (" + stderr + ")");

                lock (playbackLock)
                {
                    MarkOutputUnavailable(run.Device);
                }

                Enqueue(run.Retry);
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
    /// from a desktop entry with a bare environment. Null when absent.
    /// §389: shared with SoundOutputDevices, which runs the listing tools.</summary>
    internal static string? FindOnPath(string executable)
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
    /// the read-scale-write either. §389: and lists the output devices, so
    /// the first alert finds the pinned device already checked - on Linux
    /// that is a tool or two run, not something an alert should wait for -
    /// and the log says at the start of the hunt whether the device is
    /// there.</summary>
    internal static void WarmUp(SoundOutputDevice? outputDevice, params (string? SoundName, int VolumePercent)[] sounds)
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
            if (outputDevice is { IsSystemDefault: false })
            {
                try
                {
                    IReadOnlyList<SoundOutputDevice> present = RefreshOutputDevices();
                    SoundOutputDevice? match = present.FirstOrDefault(device => device.Matches(outputDevice));

                    if (match is not null)
                        Log.Information("Sound output: {Label} is present ({Id}) - alerts play through it.", match.Label, match.Id);
                    else
                        Log.Warning("Sound output: {Label} is not present now ({Count} devices listed) - alerts play on the system default until it is back.", outputDevice.Label, present.Count);
                }
                catch
                {
                    // The first alert lists again; nothing to surface here.
                }
            }

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

    /// <summary>§389. Lists the output devices now and remembers them as
    /// the ones present - what a pinned device is checked against on Linux
    /// until the next listing. Sound Settings calls this for its picker;
    /// WarmUp calls it at every hunt start. Any thread.</summary>
    internal static IReadOnlyList<SoundOutputDevice> RefreshOutputDevices()
    {
        IReadOnlyList<SoundOutputDevice> found = SoundOutputDevices.List();

        lock (playbackLock)
        {
            presentDevices = found;
            missingReported.Clear();
        }

        return found;
    }

    /// <summary>§389. The pinned device as it is present right now, or null
    /// for the system default - because nothing is pinned, or because the
    /// pinned device is not there, which is logged once per listing.
    /// Windows lists afresh each time (two winmm calls, and device numbers
    /// move when something is plugged in); Linux uses the last listing,
    /// making one only if nobody has yet. Called under playbackLock.</summary>
    private static SoundOutputDevice? ResolveOutput(SoundOutputDevice? wanted, string soundName)
    {
        if (wanted is null || wanted.IsSystemDefault)
            return null;

        IReadOnlyList<SoundOutputDevice> present = OperatingSystem.IsWindows()
            ? SoundOutputDevices.List()
            : presentDevices ??= SoundOutputDevices.List();

        foreach (SoundOutputDevice candidate in present)
        {
            if (candidate.Matches(wanted))
                return candidate;
        }

        if (missingReported.Add(wanted.Pin))
        {
            Log.Warning(
                "Sound {SoundName}: the chosen output device {Label} is not present ({Count} listed) - playing on the system default until it is.",
                soundName, wanted.Label, present.Count);
        }

        return null;
    }

    /// <summary>§389. A device a player just failed with is taken off the
    /// present list, so the alerts after it go to the default without each
    /// paying for the failure; the next listing puts it back if it is there.
    /// Called under playbackLock.</summary>
    private static void MarkOutputUnavailable(SoundOutputDevice device)
    {
        if (presentDevices is null)
            return;

        var remaining = new List<SoundOutputDevice>(presentDevices.Count);

        foreach (SoundOutputDevice candidate in presentDevices)
        {
            if (!candidate.Matches(device))
                remaining.Add(candidate);
        }

        presentDevices = remaining;
    }

    /// <summary>§389. How each system player is pointed at a device: the
    /// arguments to add and the environment to set, or null for a player
    /// that cannot be pointed at this kind of device at all (aplay knows
    /// nothing of a Pulse sink, paplay nothing of an ALSA card).
    ///
    /// A Pulse sink name is what paplay's --device and mpv's pulse/ prefix
    /// take, what pw-play's --target takes under PipeWire (the sink IS the
    /// node), what mpg123's pulse output calls -a, and what every libpulse
    /// client - SoX's pulseaudio driver, ffplay through SDL - reads from
    /// PULSE_SINK; the variable is set for all of them, belt and braces. An
    /// ALSA PCM is aplay's -D, mpv's alsa/ prefix, mpg123's alsa -a, and the
    /// AUDIODEV that SoX and SDL's ALSA driver read, with the driver named
    /// beside it so the variable is read by that driver and not another.</summary>
    internal static (string[] Arguments, (string Name, string Value)[] Environment)? DeviceOptions(string executable, SoundOutputDevice device)
    {
        string id = device.Id;

        if (device.Backend == SoundOutputDevice.PulseBackend)
        {
            (string Name, string Value)[] pulse = { ("PULSE_SINK", id) };

            return executable switch
            {
                "paplay" => (new[] { "--device=" + id }, pulse),
                "pw-play" => (new[] { "--target=" + id }, pulse),
                "mpv" => (new[] { "--audio-device=pulse/" + id }, pulse),
                "mpg123" => (new[] { "-o", "pulse", "-a", id }, pulse),
                "play" => (Array.Empty<string>(), new[] { ("AUDIODRIVER", "pulseaudio"), ("AUDIODEV", id), ("PULSE_SINK", id) }),
                "ffplay" => (Array.Empty<string>(), new[] { ("SDL_AUDIODRIVER", "pulseaudio"), ("PULSE_SINK", id) }),
                _ => null,
            };
        }

        if (device.Backend == SoundOutputDevice.AlsaBackend)
        {
            (string Name, string Value)[] none = Array.Empty<(string, string)>();

            return executable switch
            {
                "aplay" => (new[] { "-D", id }, none),
                "mpv" => (new[] { "--audio-device=alsa/" + id }, none),
                "mpg123" => (new[] { "-o", "alsa", "-a", id }, none),
                "play" => (Array.Empty<string>(), new[] { ("AUDIODRIVER", "alsa"), ("AUDIODEV", id) }),
                "ffplay" => (Array.Empty<string>(), new[] { ("SDL_AUDIODRIVER", "alsa"), ("AUDIODEV", id) }),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>§389. Windows: the wave's samples to the chosen device
    /// through waveOut - open the device number with the clip's own format,
    /// hand it one buffer with the whole clip, and let it play; the buffer
    /// and the device are released by StopWaveOut, from a timer once the
    /// clip has had time to finish or from the next alert, whichever comes
    /// first. False with the reason when the device or the format is
    /// refused, and the caller plays on the default. Called under
    /// playbackLock on the playback thread.</summary>
    private static bool PlayWithWaveOut(string soundName, string wavePlayed, SoundOutputDevice device, double lengthMs, out string failure)
    {
        failure = string.Empty;

        if (device.Index < 0)
        {
            failure = "no device number";
            return false;
        }

        byte[] bytes = File.ReadAllBytes(wavePlayed);

        if (!WaveVolume.TryLocateSamples(bytes, out WaveVolume.WaveFormat format, out int dataStart, out int dataLength, out string reason))
        {
            failure = reason;
            return false;
        }

        // PCM or IEEE float - the two tags a plain WAVEFORMATEX describes.
        if (format.FormatTag is not (1 or 3) || dataLength <= 0)
        {
            failure = format.FormatTag is not (1 or 3) ? $"format tag {format.FormatTag} is not PCM" : "no samples";
            return false;
        }

        var waveFormat = new WaveFormatEx
        {
            FormatTag = format.FormatTag,
            Channels = format.Channels,
            SamplesPerSec = format.SampleRate,
            AvgBytesPerSec = format.ByteRate,
            BlockAlign = format.BlockAlign,
            BitsPerSample = format.BitsPerSample,
            Size = 0,
        };

        uint error = waveOutOpen(out IntPtr handle, (uint)device.Index, ref waveFormat, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);

        if (error != 0)
        {
            failure = DescribeWaveOutError(error);
            return false;
        }

        uint headerSize = (uint)Marshal.SizeOf<WaveHeader>();
        IntPtr data = Marshal.AllocHGlobal(dataLength);
        IntPtr header = Marshal.AllocHGlobal((int)headerSize);

        try
        {
            Marshal.Copy(bytes, dataStart, data, dataLength);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)dataLength }, header, false);

            error = waveOutPrepareHeader(handle, header, headerSize);

            if (error == 0)
                error = waveOutWrite(handle, header, headerSize);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            error = uint.MaxValue;
        }

        if (error != 0)
        {
            if (failure.Length == 0)
                failure = DescribeWaveOutError(error);

            waveOutReset(handle);
            waveOutUnprepareHeader(handle, header, headerSize);
            waveOutClose(handle);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            return false;
        }

        var run = new WaveOutRun
        {
            Handle = handle,
            Header = header,
            Data = data,
            SoundName = soundName,
        };

        currentWaveOut = run;

        // Released on the playback thread once the clip has had time to end,
        // plus a margin - unless a newer alert has released it already.
        run.Cleanup = new Timer(
            _ => Enqueue(() =>
            {
                lock (playbackLock)
                {
                    if (ReferenceEquals(currentWaveOut, run))
                        StopWaveOut();
                }
            }),
            null,
            (int)Math.Max(0, lengthMs) + 500,
            Timeout.Infinite);

        return true;
    }

    /// <summary>§389. Stops and releases whatever waveOut is playing: reset
    /// (which completes the buffer whether or not it finished), unprepare,
    /// close, free. Called under playbackLock.</summary>
    private static void StopWaveOut()
    {
        WaveOutRun? run = currentWaveOut;

        if (run is null)
            return;

        currentWaveOut = null;

        try
        {
            run.Cleanup?.Dispose();
            waveOutReset(run.Handle);
            waveOutUnprepareHeader(run.Handle, run.Header, (uint)Marshal.SizeOf<WaveHeader>());
            waveOutClose(run.Handle);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Sound {SoundName}: the waveOut device could not be released cleanly.", run.SoundName);
        }
        finally
        {
            Marshal.FreeHGlobal(run.Header);
            Marshal.FreeHGlobal(run.Data);
        }
    }

    /// <summary>§389. winmm's own wording for a waveOut return code.</summary>
    private static string DescribeWaveOutError(uint error)
    {
        var buffer = new StringBuilder(256);

        return waveOutGetErrorTextW(error, buffer, (uint)buffer.Capacity) == 0 && buffer.Length > 0
            ? $"{buffer} (waveOut error {error})"
            : $"waveOut error {error}";
    }
}
