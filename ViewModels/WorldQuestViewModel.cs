using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.ViewModels;

/// <summary>§254. One counted catch as the World Quest Catches window lists
/// it. Top-level rather than nested, for the compiled DataTemplate. Number
/// is the catch's place in the count (the first catch is 1) so a player can
/// say "the third one was the misread"; the list shows newest first. AtUtc
/// and Total together are what WorldQuestService.Remove matches on.</summary>
public sealed record WorldQuestCatchRow(int Number, string When, int Total, string Source, DateTime AtUtc)
{
    public string NumberText => $"#{Number}";
    public string TotalText => $"{Total} IVs";
}

/// <summary>
/// §233, §251. The World Quest half of the main window's stats panel.
///
/// §233 built this as the view model of a World Quest window of its own. §251
/// folded that window into the main window: while World Quest mode (§250) is
/// on, the stats panel shows this view model's figures where the hunt stats
/// were, and its three ways of counting a catch where Report a Problem was.
/// MainWindowViewModel owns one instance for the life of the window, starts
/// it on the way into the mode and stops it on the way out.
///
/// It does two things. It shows the quest the events server read out of PRO's
/// own announcement - species, the tier thresholds, and a countdown off the
/// DERIVED end time rather than the stale date the announcement prints. And
/// it counts each catch's IV total into a running total held only on this
/// machine, by whichever of three routes the player chooses:
///
///   - AUTO DETECT, a toggle. While it is on, the PRO client is grabbed every
///     second and a half and the catch preview panel is read. Off each time
///     the mode is entered; the player turns it on. Opt-in because a capture
///     is not free and a bug in the watcher must only ever cost the player
///     who chose it.
///   - SUBMIT SCREENSHOT. A picked image file is read by the same detector,
///     once. For the player whose client the watcher cannot see - a second
///     monitor's DPI, a capture method the client defeats - and for a catch
///     the watcher missed while it was off.
///   - ADD IVS, the total typed by hand. Always available, and the only route
///     when the detector cannot read a panel at all.
///
/// WHY THE WATCH IS STILL NOT IN THE HUNTING LOOP. The hunting loop is the
/// tracker's most load-bearing code and it runs whenever anyone is hunting;
/// a World Quest runs for a weekend a month. Keeping the watch on its own
/// timer beside the loop, started only by the Auto Detect button, means it
/// costs nothing at all the rest of the time.
///
/// ONE CATCH PER APPEARANCE. The preview panel stays on screen until the
/// player answers it, so a poll every second and a half would otherwise count
/// the same catch a dozen times. A reading is only accepted on the panel's
/// RISING EDGE: the watcher must first see a frame with no panel in it before
/// it will count another. Two catches with identical IVs are still counted
/// twice, because the panel closed in between - which is the point. A
/// screenshot has no edge to rise on; each submitted file counts once.
///
/// §235. THE FIGURES AIM AT ONE TICKET AT A TIME. A player contributing to a
/// quest has exactly one question - how much further to the next reward -
/// and four figures answer it together: how many IVs are still needed, how
/// many catches that is at the rate this player is actually managing, what
/// they have put in, and how far along that is. The moment the 0.5% tier is
/// met all four re-aim at the 3% tier, because the first question has been
/// answered and the only one left is the second ticket. Nothing on the panel
/// is left pointing at a target already reached.
/// </summary>
public sealed partial class WorldQuestViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often the client is grabbed while Auto Detect is on.
    /// The preview panel waits for the player, so it is on screen for
    /// seconds at least - there is nothing to be gained by looking more
    /// often, and a capture is not free.</summary>
    private const int WatchIntervalMs = 1500;

    /// <summary>The countdown only needs to be right to the second.</summary>
    private const int ClockIntervalMs = 1000;

    /// <summary>0.5% of the goal earns a Mysterious Ticket, 3% a second one -
    /// the same thresholds the World Quest Calculator uses.</summary>
    private const double FirstTicketShare = 0.005;
    private const double SecondTicketShare = 0.03;

    private readonly DispatcherTimer clock;
    private readonly DispatcherTimer watch;
    private readonly SemaphoreSlim watchGate = new(1, 1);

    private WorldQuest? quest;
    private WorldQuestProgress progress = new(string.Empty, Array.Empty<WorldQuestSubmission>());

    /// <summary>False until a frame with no preview panel in it is seen -
    /// see the class remark on rising edges. Reset to false each time Auto
    /// Detect is turned on, so a panel already on screen at that moment is
    /// counted once.</summary>
    private bool panelWasVisible;

    private bool disposed;

    public WorldQuestViewModel()
    {
        clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClockIntervalMs) };
        clock.Tick += (_, _) => UpdateCountdown();

        watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WatchIntervalMs) };
        watch.Tick += async (_, _) => await LookAsync();
    }

    /// <summary>§251. Set by the view: opens a file picker for a screenshot
    /// and returns its local path, or null when the player cancelled. Null
    /// when no view wired one, in which case Submit Screenshot does nothing.</summary>
    public Func<Task<string?>>? RequestScreenshotFile { get; set; }

    /// <summary>§264. Returns the clipboard's image as PNG bytes, or null when
    /// there is no image on it. Set by the view, which is the only thing that
    /// can reach Avalonia's clipboard; null when no view wired one, in which
    /// case Ctrl+V does nothing.</summary>
    public Func<Task<byte[]?>>? RequestClipboardImage { get; set; }

    [ObservableProperty] private Bitmap? sprite;

    [ObservableProperty] private string pokemonName = "-";

    // The four figures, each with a small caption under it saying what it is
    // measured against - which matters more than usual here, because all four
    // change target when the first ticket is earned.
    [ObservableProperty] private string ivsNeededText = "-";
    [ObservableProperty] private string ivsNeededCaption = "";
    [ObservableProperty] private string averageSubmissionsText = "-";
    [ObservableProperty] private string averageSubmissionsCaption = "";
    [ObservableProperty] private string obtainedText = "0";
    [ObservableProperty] private string obtainedCaption = "";
    [ObservableProperty] private string currentPercentText = "0%";
    [ObservableProperty] private string currentPercentCaption = "";

    /// <summary>§254. Every catch counted for the quest, newest first - the
    /// World Quest Catches window's list. Rebuilt from the progress after
    /// every change, so a catch Auto Detect counts while the window is open
    /// appears in it, and a removal there changes the figures here.</summary>
    public ObservableCollection<WorldQuestCatchRow> Catches { get; } = new();

    [ObservableProperty] private bool hasCatches;
    [ObservableProperty] private string catchesSummaryText = "";

    [ObservableProperty] private string countdownText = "00:00:00";
    [ObservableProperty] private string endsText = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string manualTotalText = "";
    [ObservableProperty] private bool hasQuest;
    [ObservableProperty] private bool busy;

    /// <summary>§251. Whether the watcher is running. Only ever set through
    /// ToggleAutoDetect, Stop, or the quest ending; the timer follows it in
    /// OnAutoDetectEnabledChanged so the flag and the timer cannot disagree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoDetectText))]
    private bool autoDetectEnabled;

    /// <summary>What the Auto Detect button reads: the state it is in, not
    /// the action it would take, because a toggle that reads "Turn on" while
    /// on is the classic way to make a player click it twice.</summary>
    public string AutoDetectText => AutoDetectEnabled ? "Auto Detect: On" : "Auto Detect: Off";

    partial void OnAutoDetectEnabledChanged(bool value)
    {
        if (value)
        {
            panelWasVisible = false;
            watch.Start();
        }
        else
        {
            watch.Stop();
        }
    }

    /// <summary>
    /// Loads the quest and this machine's count for it, and starts the
    /// countdown. Auto Detect is NOT started - the player turns it on.
    ///
    /// Called with the quest in hand on the way into World Quest mode, so
    /// there is no second fetch of what the toggle just fetched. Called with
    /// nothing at startup when the mode was restored from its marker (§250):
    /// then the quest is fetched BY ID - the marker's id, not whichever quest
    /// happens to be running now - because the mode is on for one quest and
    /// the figures shown must be that quest's. If the server does not answer,
    /// or no longer lists it, the marker's id and species are enough to keep
    /// counting; only the ticket thresholds are missing, and the figures say
    /// so rather than showing a distance to nothing.
    /// </summary>
    public async Task StartAsync(WorldQuest? known = null)
    {
        Stop();

        WorldQuestMode.ActiveQuest? marker = WorldQuestMode.Current;
        WorldQuest? found = known;
        string? failure = null;

        if (found is null)
        {
            if (marker is null)
            {
                StatusMessage = "World Quest hunting is not on.";
                return;
            }

            Busy = true;

            try
            {
                found = await WorldQuestService.FetchAsync(marker.MessageId);
            }
            catch (EventsSyncException ex)
            {
                failure = $"The World Quest could not be fetched - {ex.Message}.";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "World Quest: fetching quest {QuestId} failed.", marker.MessageId);
                failure = "The World Quest could not be fetched - see today's log.";
            }
            finally
            {
                Busy = false;
            }
        }

        bool fromMarker = false;

        if (found is null && marker is not null)
        {
            // §251. Enough to count against: the id keys the progress file
            // and the species gates the detector. TotalIvs and SingleIvs of
            // zero make RefreshProgress show "the quest did not say" for the
            // tickets, which is the truth.
            found = new WorldQuest(
                marker.MessageId, marker.Pokemon,
                TotalIvs: 0, SingleIvs: 0, AverageSubmissions: 0,
                LowestTier: string.Empty, Reward: string.Empty, Duration: string.Empty, EndTimeText: string.Empty,
                // §298: EndedUtc null, for the same reason EndsUtc is - the
                // server did not answer, so nothing is known about the end
                // either way, and counting goes on rather than stopping on a
                // guess.
                StartedUtc: marker.EnteredUtc, EndsUtc: null, EndedUtc: null, Parsed: false);
            fromMarker = true;
        }

        if (found is null)
        {
            StatusMessage = failure ?? "No World Quest is running right now.";
            return;
        }

        try
        {
            Begin(found);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest: the quest panel could not be set up.");
            StatusMessage = "The World Quest figures could not be set up - see today's log.";
            return;
        }

        if (fromMarker)
        {
            StatusMessage = failure is not null
                ? failure + " Counting continues for the quest this session was started for; the ticket figures need the server."
                : "The server no longer lists this World Quest - counting continues for it, without the ticket figures.";
        }
        else
        {
            StatusMessage = "Auto Detect is off. Turn it on to read catches from the preview panel, submit a screenshot, or add IV totals by hand.";
        }
    }

    private void Begin(WorldQuest found)
    {
        quest = found;
        HasQuest = true;
        PokemonName = quest.Pokemon;

        // §235. The community goal, the lowest tier and the reward name are
        // still fetched and are still what the tiers are worked out FROM; they
        // are simply not shown. The announcement's own "average submissions"
        // figure is not shown either - the panel works that number out from
        // what this player is actually managing, which is the one version of
        // it that can tell them how many more catches to make.

        // §234. A quest runs 24 hours from the moment it starts OR ends as
        // soon as the community goal is met, whichever comes first. The
        // tracker only ever knows this player's own contribution, never the
        // community total, so it cannot see the second condition coming - the
        // countdown is a MAXIMUM, and this text (the countdown's tooltip since
        // §251) says so rather than letting a player read six remaining hours
        // as a promise.
        //
        // The announcement's own printed end date is quoted beside it and
        // deliberately not used for anything - see WorldQuestService's remark
        // on why the countdown runs off the derived time instead.
        EndsText = string.IsNullOrWhiteSpace(quest.EndTimeText)
            ? "A quest runs 24 hours, or ends as soon as the community goal is met - so this is the longest it can still be running."
            : $"A quest runs 24 hours, or ends as soon as the community goal is met. The announcement reads \"{quest.EndTimeText}\".";

        Sprite = PokemonSpriteService.GetSprite(quest.Pokemon);

        progress = WorldQuestService.Load(quest.MessageId);
        RefreshProgress();

        clock.Start();
        UpdateCountdown();
    }

    /// <summary>§251. The way out of the mode: the watcher and the countdown
    /// stop, the quest is forgotten, and the instance is ready for the next
    /// StartAsync. Nothing is disposed - the same instance serves every entry
    /// for the life of the main window.</summary>
    public void Stop()
    {
        AutoDetectEnabled = false;
        clock.Stop();
        quest = null;
        progress = new WorldQuestProgress(string.Empty, Array.Empty<WorldQuestSubmission>());
        HasQuest = false;
        ManualTotalText = string.Empty;
        RefreshCatches();
    }

    // ------------------------------------------------------------- commands

    /// <summary>§298. The two ways a quest is over, in one place: its own
    /// time ran out, or somebody who could see both servers said the goal
    /// had been met. The countdown and the watcher both ask this rather than
    /// each testing the half it happens to know about.</summary>
    private bool QuestIsOver =>
        quest is not null
        && (quest.EndedUtc is not null || (quest.EndsUtc is { } ends && ends <= DateTime.UtcNow));

    /// <summary>§298. The sentence for a quest that is over, which differs by
    /// HOW it ended: a quest someone ended early is one whose goal was met,
    /// and saying "the 24 hours are up" about it would be wrong.</summary>
    private string EndedMessage =>
        quest?.EndedUtc is not null
            ? "This World Quest is over - the community goal was met on both servers. Your count stands; nothing more can be added to it."
            : "This World Quest has ended.";

    /// <summary>§251. The opt-in watcher. Refuses without a quest to detect
    /// for, and after the quest has ended, saying why either way.</summary>
    [RelayCommand]
    private void ToggleAutoDetect()
    {
        if (quest is null)
        {
            StatusMessage = "No World Quest is loaded, so there is nothing to detect for.";
            return;
        }

        if (AutoDetectEnabled)
        {
            AutoDetectEnabled = false;
            StatusMessage = "Auto Detect is off. Submit a screenshot or add IV totals by hand.";
            return;
        }

        if (QuestIsOver)
        {
            StatusMessage = EndedMessage;
            return;
        }

        AutoDetectEnabled = true;
        StatusMessage = $"Auto Detect is on - watching the client for {quest.Pokemon} catch previews. A catch it misses can still be added by hand.";
    }

    /// <summary>§251. One image file through the same detector the watcher
    /// uses. The picker is the view's; the read is off the UI thread; the
    /// result is one submission, or a message saying what to do instead.
    ///
    /// §258. A second thing to try when there is no catch preview in the
    /// image: the Pokemon's SUMMARY CARD, read by the simulator importer's
    /// card OCR through <see cref="WorldQuestCardReader"/>. A card that read
    /// all six IVs is one submission, like a preview. A card that read a
    /// majority but not all of them is NOT submitted: its partial total is
    /// put in the manual box and the status names the rows that did not
    /// read, so the player adds those and presses Add IVs - one catch, one
    /// entry, counted by hand. The preview is always tried first; a frame
    /// with both a preview and a card behind it reads as the preview.</summary>
    [RelayCommand]
    private async Task SubmitScreenshot()
    {
        if (quest is null)
        {
            StatusMessage = "No World Quest is loaded.";
            return;
        }

        if (RequestScreenshotFile is null || Busy)
            return;

        string? path;

        try
        {
            path = await RequestScreenshotFile();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest: the screenshot picker failed.");
            StatusMessage = $"A screenshot could not be picked - {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
            return;

        string file = path;

        await ReadAndApply(
            species => ReadScreenshot(file, species),
            Path.GetFileName(file));
    }

    /// <summary>
    /// §264. The clipboard route: Win+Shift+S then Ctrl+V, with no file to
    /// save and no picker to walk through. It is the same read and the same
    /// outcomes as Submit Screenshot - only where the pixels came from
    /// differs, which is why both go through ReadAndApply rather than each
    /// carrying its own copy of the seven answers a read can have.
    /// </summary>
    [RelayCommand]
    private async Task PasteScreenshot()
    {
        if (quest is null)
        {
            StatusMessage = "No World Quest is loaded.";
            return;
        }

        if (RequestClipboardImage is null || Busy)
            return;

        byte[]? image;

        try
        {
            image = await RequestClipboardImage();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest: reading the clipboard failed.");
            StatusMessage = $"The clipboard could not be read - {ex.Message}";
            return;
        }

        if (image is null || image.Length == 0)
        {
            StatusMessage = "There is no image on the clipboard. Take a shot with Win+Shift+S, then press Ctrl+V here.";
            return;
        }

        await ReadAndApply(species => ReadScreenshotBytes(image, species), "the clipboard");
    }

    /// <summary>§264. Read off the UI thread, then answer - the one place that
    /// turns a reading into a submission, a refusal or a half-filled box, so
    /// the file route and the clipboard route cannot drift apart.</summary>
    private async Task ReadAndApply(Func<string, ScreenshotReading> read, string sourceLabel)
    {
        if (quest is null)
            return;

        string species = quest.Pokemon;

        Busy = true;

        try
        {
            ScreenshotReading result = await Task.Run(() => read(species));

            // Stop may have run while the read was off-thread.
            if (quest is null)
                return;

            ApplyScreenshotReading(result, species);
        }
        catch (Exception ex)
        {
            // The file name or "the clipboard" only. A full path names the
            // player's folders, and the name is enough to match the report to
            // what was read.
            Log.Warning(ex, "World Quest: reading a screenshot failed - {Source}", sourceLabel);
            StatusMessage = "That screenshot could not be read - add the IV total by hand.";
        }
        finally
        {
            Busy = false;
        }
    }

    private void ApplyScreenshotReading(ScreenshotReading result, string species)
    {
        if (quest is null)
            return;

        if (result.Preview is { } reading)
        {
            progress = WorldQuestService.Add(quest.MessageId, reading.Total, reading.Species, automatic: true);
            RefreshProgress();

            StatusMessage = $"Screenshot read - added {reading.Total} IVs ({string.Join(", ", reading.Ivs)}).";
            return;
        }

        WorldQuestCardReading? card = result.Card;

        if (card is null)
        {
            StatusMessage = $"No catch preview or summary card for {species} could be read from that screenshot. A PNG (Win+Shift+S) of the client with the preview or the Pokemon's summary open reads best; otherwise add the IV total by hand.";
            return;
        }

        switch (card.Outcome)
        {
            case WorldQuestCardOutcome.WrongSpecies:
                StatusMessage = card.Species.Length == 0
                    ? $"A summary card was found but its name did not read, so it cannot be counted as a {species} - nothing added."
                    : $"That summary card is a {card.Species}, not the quest's {species} - nothing added.";
                return;

            case WorldQuestCardOutcome.TooFewRows:
                StatusMessage = $"The summary card read only {card.ReadCount} of 6 IVs ({string.Join(", ", card.Missing)} did not read) - too few to build on. Add the IV total by hand.";
                return;
        }

        if (card.Complete)
        {
            progress = WorldQuestService.Add(quest.MessageId, card.ReadTotal, card.Species, automatic: true);
            RefreshProgress();

            StatusMessage = $"Summary card read - added {card.ReadTotal} IVs ({string.Join(", ", card.Ivs)}).";
            return;
        }

        // A majority read, the rest did not. Nothing is committed: the
        // partial total goes in the box for the player to complete, so
        // the catch lands as ONE entry rather than a partial plus a
        // top-up, and the number they press Add IVs on is the number
        // they checked against the card.
        ManualTotalText = card.ReadTotal.ToString(CultureInfo.InvariantCulture);

        string missing = card.Missing.Count == 1
            ? $"{card.Missing[0]} did not read"
            : $"{string.Join(", ", card.Missing)} did not read";

        StatusMessage = $"Summary card read {card.ReadCount} of 6 IVs - {card.ReadTotal} so far ({string.Join(", ", card.Ivs.Select(iv => iv?.ToString(CultureInfo.InvariantCulture) ?? "?"))}). {missing}: add {(card.Missing.Count == 1 ? "it" : "them")} to the number in the box and press Add IVs.";
    }

    /// <summary>§258. What one screenshot came to: the preview reading when
    /// there was a preview, else the card reading when there was a card,
    /// else neither.</summary>
    private readonly record struct ScreenshotReading(PreviewIvReading? Preview, WorldQuestCardReading? Card);

    /// <summary>Runs off the UI thread; touches no property. The preview
    /// detector goes first and wins; the card reader is only asked when it
    /// found nothing.</summary>
    private static ScreenshotReading ReadScreenshot(string path, string questSpecies) =>
        ReadFrame(SKBitmap.Decode(path), questSpecies);

    /// <summary>§264. The same read, from bytes off the clipboard.</summary>
    private static ScreenshotReading ReadScreenshotBytes(byte[] image, string questSpecies) =>
        ReadFrame(SKBitmap.Decode(image), questSpecies);

    private static ScreenshotReading ReadFrame(SKBitmap? decoded, string questSpecies)
    {
        using SKBitmap? frame = decoded;

        if (frame is null)
            return default;

        PreviewIvReading? preview = PreviewIvDetector.Read(frame, questSpecies);

        if (preview is not null)
            return new ScreenshotReading(preview, null);

        return new ScreenshotReading(null, WorldQuestCardReader.Read(frame, questSpecies));
    }

    /// <summary>The manual route: the total the player read off the panel
    /// themselves. Always available, and the only path when the detector
    /// cannot read a panel.</summary>
    [RelayCommand]
    private void SubmitPokemon()
    {
        if (quest is null)
            return;

        if (!int.TryParse(ManualTotalText.Trim(), out int total) ||
            total < 0 || total > WorldQuestService.MaxSubmissionTotal)
        {
            StatusMessage = "Enter the catch's IV total as a number from 0 to 186 (six IVs of up to 31).";
            return;
        }

        progress = WorldQuestService.Add(quest.MessageId, total, quest.Pokemon, automatic: false);
        ManualTotalText = string.Empty;
        RefreshProgress();
        StatusMessage = $"Added {total} IVs by hand.";
    }

    /// <summary>§254. Takes one specific catch out of the count - the row the
    /// player pressed Remove on in the World Quest Catches window. Matched by
    /// its time and total, not its position, so a catch Auto Detect counted
    /// after the list was drawn cannot shift the removal onto a neighbour;
    /// if the row is already gone the list is simply refreshed and the
    /// status says so.</summary>
    [RelayCommand]
    private void RemoveCatch(WorldQuestCatchRow? row)
    {
        if (quest is null || row is null)
            return;

        (WorldQuestProgress after, bool removed) = WorldQuestService.Remove(quest.MessageId, row.AtUtc, row.Total);

        progress = after;
        RefreshProgress();

        StatusMessage = removed
            ? $"Removed catch {row.NumberText} ({row.Total} IVs)."
            : "That catch was already removed - the list has been refreshed.";
    }

    // -------------------------------------------------------------- watcher

    /// <summary>One look at the client. Everything expensive happens on a
    /// worker; only the property writes come back here.</summary>
    private async Task LookAsync()
    {
        // A tick that arrives while the last one is still working is dropped,
        // not queued - a slow capture must never build a backlog of frames
        // describing a screen that has already changed.
        if (!watchGate.Wait(0))
            return;

        try
        {
            string species = quest?.Pokemon ?? string.Empty;

            PreviewIvReading? reading = await Task.Run(() => LookOnce(species));

            if (reading is null)
            {
                // No panel this frame - the next one that has a panel is a new
                // catch. This is the whole of the one-catch-per-appearance
                // rule.
                panelWasVisible = false;
                return;
            }

            if (panelWasVisible)
                return;

            panelWasVisible = true;

            if (quest is null)
                return;

            progress = WorldQuestService.Add(quest.MessageId, reading.Total, reading.Species, automatic: true);
            RefreshProgress();

            StatusMessage = $"World Quest Pokémon detected - added {reading.Total} IVs ({string.Join(", ", reading.Ivs)}).";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest: a look at the client failed.");
            StatusMessage = "Pokémon not detected - submit a screenshot or add the IV total by hand.";
        }
        finally
        {
            watchGate.Release();
        }
    }

    /// <summary>Grabs the client and reads it. Runs off the UI thread; touches
    /// no property. Returns null for every failure - a client that is not
    /// running, a capture that failed, a frame with no panel in it - because
    /// they all mean the same thing to the caller: no catch to count.</summary>
    private static PreviewIvReading? LookOnce(string questSpecies)
    {
        byte[]? png = CaptureClientPng();

        if (png is null)
            return null;

        using SKBitmap? frame = SKBitmap.Decode(png);

        return frame is null ? null : PreviewIvDetector.Read(frame, questSpecies);
    }

    /// <summary>The same capture the card importer uses (§165, §225): the
    /// tracker's bound client if there is one, otherwise the first PRO window
    /// found, released again afterwards so this watcher never steals a
    /// binding a hunt is relying on.</summary>
    private static byte[]? CaptureClientPng()
    {
        try
        {
            IWindowCaptureService service = WindowCaptureServiceFactory.Instance;

            if (!service.IsAvailable)
                return null;

            bool temporary = false;

            if (!service.HasSelectedClient)
            {
                IReadOnlyList<ClientWindowInfo> windows = service.FindClientWindows("PROClient");

                if (windows.Count == 0)
                    return null;

                service.SelectWindow(windows[0].Handle);
                temporary = true;
            }

            try
            {
                return service.CaptureSelectedWindowPng();
            }
            finally
            {
                if (temporary)
                    service.ClearSelectedWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest: capturing the PRO client failed.");
            return null;
        }
    }

    // -------------------------------------------------------------- display

    /// <summary>
    /// §235. Recomputes all four figures against whichever ticket is still
    /// ahead. Called after every catch, automatic or manual, and after an
    /// undo - which means an undo that drops the player back under the first
    /// tier re-aims the figures at it again, rather than leaving them pointed
    /// at a tier they no longer qualify for.
    /// </summary>
    private void RefreshProgress()
    {
        int collected = progress.Collected;
        int catches = progress.Count;
        int first = FirstTicketIvs();
        int second = SecondTicketIvs();

        ObtainedText = DisplayNumber.Count(collected);
        ObtainedCaption = catches switch
        {
            0 => "no catches yet",
            1 => "from 1 catch",
            _ => $"from {DisplayNumber.Count(catches)} catches",
        };

        // Which ticket the figures are aiming at. Below the first tier it is
        // the first; at or above it, the second; at or above both, neither -
        // and the figures say so rather than showing a distance of zero to a
        // target that no longer exists.
        bool haveFirst = first > 0 && collected >= first;

        // The second is only "earned" once the first is. They cannot normally
        // cross - 3% is six times 0.5% - but the two figures come from
        // different places (the first is printed in the announcement, the
        // second is worked out from the goal), so an announcement whose two
        // numbers disagree must not be able to report both tickets earned to
        // a player who has not passed the first.
        bool haveSecond = haveFirst && second > 0 && collected >= second;

        int target = haveFirst ? second : first;

        if (haveSecond || target <= 0)
        {
            IvsNeededText = haveSecond ? "0" : "-";
            IvsNeededCaption = haveSecond
                ? "both tickets earned"
                : "the quest did not say";
            AverageSubmissionsText = haveSecond ? "0" : "-";
            AverageSubmissionsCaption = haveSecond ? "nothing left to catch" : "";
        }
        else
        {
            int remaining = Math.Max(0, target - collected);

            IvsNeededText = DisplayNumber.Count(remaining);
            IvsNeededCaption = haveFirst
                ? $"for the 2nd ticket ({DisplayNumber.Count(target)})"
                : $"for the 1st ticket ({DisplayNumber.Count(target)})";

            // How many more catches that is at the rate this player is
            // actually managing - not at the rate the announcement quoted,
            // which describes the server rather than them, and which a test
            // quest does not carry at all. It needs at least one catch to have
            // a rate at all, so before that it is a dash rather than a
            // confident number resting on nothing.
            if (catches > 0 && collected > 0)
            {
                double average = (double)collected / catches;
                int more = (int)Math.Ceiling(remaining / average);

                AverageSubmissionsText = DisplayNumber.Count(more);
                AverageSubmissionsCaption =
                    $"at your {average.ToString("0.#", CultureInfo.CurrentCulture)} IV average";
            }
            else
            {
                AverageSubmissionsText = "-";
                AverageSubmissionsCaption = "after your first catch";
            }
        }

        // Progress toward that same ticket, so the percentage and the two
        // figures beside it are never describing different targets. Guarded,
        // not assumed: a quest whose per-player figure the Worker could not
        // read would divide by zero here, and a panel showing "Infinity%" is
        // worse than one showing a dash.
        if (target <= 0)
            CurrentPercentText = haveSecond ? "100%" : "-";
        else if (collected >= target)
            CurrentPercentText = "100%";
        else
            CurrentPercentText = Truncate(collected, target, 1).ToString("0.#", CultureInfo.CurrentCulture) + "%";

        // The share of the whole community goal, underneath. That is the
        // number the reward tiers are actually quoted in - the first ticket is
        // 0.5% of the goal and the second is 3% - so it lets a player check
        // the panel's arithmetic against the announcement's own wording.
        int goal = quest?.TotalIvs ?? 0;

        CurrentPercentCaption = goal > 0
            ? $"{Truncate(collected, goal, 3).ToString("0.###", CultureInfo.CurrentCulture)}% of the goal"
            : string.Empty;

        RefreshCatches();
    }

    /// <summary>§254. The catches window's list, from the same progress the
    /// four figures were just computed from - so the list and the figures
    /// can never disagree about what is counted. Numbered in the order they
    /// were counted, shown newest first.</summary>
    private void RefreshCatches()
    {
        Catches.Clear();

        IReadOnlyList<WorldQuestSubmission> submissions = progress.Submissions;
        DateTime today = DateTime.Now.Date;

        for (int i = submissions.Count - 1; i >= 0; i--)
        {
            WorldQuestSubmission submission = submissions[i];
            DateTime local = submission.AtUtc.ToLocalTime();

            // The time alone inside today; the weekday as well once a quest
            // has crossed midnight, which a 24-hour quest always does.
            string when = local.Date == today
                ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
                : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);

            Catches.Add(new WorldQuestCatchRow(
                i + 1,
                when,
                submission.Total,
                submission.Automatic ? "Detected" : "By hand",
                submission.AtUtc));
        }

        HasCatches = Catches.Count > 0;

        int collected = progress.Collected;
        string species = quest?.Pokemon ?? "the quest";

        CatchesSummaryText = Catches.Count switch
        {
            0 => $"No catches counted for {species} yet.",
            1 => $"1 catch counted for {species} - {DisplayNumber.Count(collected)} IVs.",
            _ => $"{DisplayNumber.Count(Catches.Count)} catches counted for {species} - {DisplayNumber.Count(collected)} IVs in all.",
        };
    }

    /// <summary>
    /// <paramref name="part"/> as a percentage of <paramref name="whole"/>,
    /// rounded DOWN to <paramref name="places"/> decimals rather than to the
    /// nearest.
    ///
    /// §235. Rounding to nearest overstates. At 5,249 IVs of a 5,250 tier the
    /// nearest tenth is 100.0%, and a figure reading "100%" directly beside
    /// "1 IV needed" has just told the player they are finished when they are
    /// one catch short. Every percentage here is progress toward something, so
    /// none of them may ever round up into looking reached.
    /// </summary>
    private static double Truncate(int part, int whole, int places)
    {
        double scale = Math.Pow(10, places);

        return Math.Floor(100.0 * scale * part / whole) / scale;
    }

    /// <summary>The first Mysterious Ticket: 0.5% of the community goal, which
    /// is the per-player figure the announcement prints directly.</summary>
    private int FirstTicketIvs() => quest?.SingleIvs ?? 0;

    /// <summary>
    /// The second Mysterious Ticket: 3% OF THE COMMUNITY GOAL, not six times
    /// the first tier.
    ///
    /// §234. Those two are not the same sum. Both tiers round up, so six times
    /// an already-rounded-up first tier overshoots: a goal of 500 gives a real
    /// 3% tier of 15 and a six-times answer of 18, and 100 gives 3 against 6.
    /// The real August quest happens to agree (744 and 4464) because its goal
    /// divides evenly, which is exactly why the disagreement went unnoticed
    /// until a test quest could be made with any goal at all. The goal is
    /// right there in the quest, so it is used; six times the first tier is
    /// only the fallback for a quest whose goal did not parse.
    /// </summary>
    private int SecondTicketIvs()
    {
        int goal = quest?.TotalIvs ?? 0;

        if (goal > 0)
            return Math.Max(1, (int)Math.Ceiling(goal * SecondTicketShare));

        int first = FirstTicketIvs();

        return first > 0
            ? Math.Max(1, (int)Math.Ceiling(first * (SecondTicketShare / FirstTicketShare)))
            : 0;
    }

    /// <summary>§298. Told, by the main window's five-minute poll, that the
    /// quest this panel is counting for has been declared over. Everything a
    /// countdown reaching zero does, for the reason a countdown cannot see:
    /// the watcher off, the clock stopped, the panel saying why. Ignored
    /// when there is no quest, when it names a different one, or when this
    /// one was already known to be over - the poll repeats every five
    /// minutes and must not keep re-announcing the same fact over whatever
    /// the player is reading.</summary>
    public void NoteEnded(WorldQuest ended)
    {
        if (quest is null || ended.EndedUtc is null)
            return;

        if (!string.Equals(quest.MessageId, ended.MessageId, StringComparison.Ordinal))
            return;

        if (quest.EndedUtc is not null)
            return;

        quest = quest with { EndedUtc = ended.EndedUtc, EndsUtc = ended.EndsUtc };

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        // §298: a quest somebody ended is over now, whatever its own clock
        // says - the whole point of declaring it is that the clock is wrong.
        if (QuestIsOver)
        {
            CountdownText = "00:00:00";
            clock.Stop();
            // §251: through the property, so the button reads Off and the
            // timer stops in the one place that stops it.
            AutoDetectEnabled = false;
            StatusMessage = EndedMessage;
            return;
        }

        if (quest?.EndsUtc is null)
        {
            CountdownText = "--:--:--";
            return;
        }

        // §298: the "the time ran out" branch that used to live here has gone
        // into QuestIsOver above, which tests exactly the same thing and one
        // more besides. There is no second copy of it to fall out of step.
        TimeSpan left = quest.EndsUtc.Value - DateTime.UtcNow;

        CountdownText = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            (int)left.TotalHours,
            left.Minutes,
            left.Seconds);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        clock.Stop();
        watch.Stop();
        watchGate.Dispose();

        // Sprite is NOT disposed. PokemonSpriteService hands out bitmaps from
        // a shared cache (see its LoadSprite), so the one here is the same
        // object the Boss Database and the encounter table are showing -
        // disposing it here would blank the sprite everywhere else in the app.
    }
}
