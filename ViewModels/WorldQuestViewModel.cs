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

/// <summary>
/// §233. The World Quest window.
///
/// It does two things at once. It shows the quest the events server read out
/// of PRO's own announcement - species, the goal, the tier thresholds, and a
/// countdown off the DERIVED end time rather than the stale date the
/// announcement prints. And, while it is open, it watches the PRO client for
/// the catch preview panel and adds each catch's IV total to a running count
/// held only on this machine.
///
/// WHY THE WATCH LIVES HERE AND NOT IN THE HUNTING LOOP. The hunting loop is
/// the tracker's most load-bearing code and it runs whenever anyone is
/// hunting; a World Quest runs for a weekend a month. Putting the watch in
/// this window means it costs nothing at all the rest of the time, and a bug
/// in it can only ever affect the window the player deliberately opened. The
/// price is that the window has to be open for catches to be read
/// automatically, which the status line says plainly.
///
/// ONE CATCH PER APPEARANCE. The preview panel stays on screen until the
/// player answers it, so a poll every second and a half would otherwise count
/// the same catch a dozen times. A reading is only accepted on the panel's
/// RISING EDGE: the watcher must first see a frame with no panel in it before
/// it will count another. Two catches with identical IVs are still counted
/// twice, because the panel closed in between - which is the point.
///
/// §235. THE WHOLE WINDOW AIMS AT ONE TICKET AT A TIME. A player contributing
/// to a quest has exactly one question - how much further to the next reward -
/// and four figures answer it together: how many IVs are still needed, how
/// many catches that is at the rate this player is actually managing, what
/// they have put in, and how far along that is. The moment the 0.5% tier is
/// met all four re-aim at the 3% tier, because the first question has been
/// answered and the only one left is the second ticket. Nothing on the window
/// is left pointing at a target already reached.
///
/// What that displaced: the community goal, the lowest tier and the reward
/// name were all read off the announcement and shown, and none of them change
/// or need doing anything about. The list of counted catches went the same
/// way - Remove Last is the only thing anyone did with it, and that button is
/// still here.
/// </summary>
public sealed partial class WorldQuestViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often the client is grabbed while the window is open.
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
    /// see the class remark on rising edges. Starts false so a panel already
    /// on screen when the window opens is counted once.</summary>
    private bool panelWasVisible;

    private bool disposed;

    public WorldQuestViewModel()
    {
        clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClockIntervalMs) };
        clock.Tick += (_, _) => UpdateCountdown();

        watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WatchIntervalMs) };
        watch.Tick += async (_, _) => await LookAsync();
    }

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

    [ObservableProperty] private string countdownText = "00:00:00";
    [ObservableProperty] private string endsText = "";
    [ObservableProperty] private string statusMessage = "Looking for a World Quest...";
    [ObservableProperty] private string manualTotalText = "";
    [ObservableProperty] private bool hasQuest;
    [ObservableProperty] private bool busy;

    /// <summary>Fetches the quest, loads this machine's count for it, and
    /// starts both timers. Called by the window on open.</summary>
    public async Task StartAsync()
    {
        Busy = true;

        try
        {
            quest = await WorldQuestService.FetchActiveAsync();
        }
        catch (EventsSyncException ex)
        {
            StatusMessage = $"The World Quest could not be fetched - {ex.Message}.";
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest: fetching the running quest failed.");
            StatusMessage = "The World Quest could not be fetched - see today's log.";
            return;
        }
        finally
        {
            Busy = false;
        }

        if (quest is null)
        {
            StatusMessage = "No World Quest is running right now. This window will have something to show when the next one starts.";
            return;
        }

        HasQuest = true;
        PokemonName = quest.Pokemon;

        // §235. The community goal, the lowest tier and the reward name are
        // still fetched and are still what the tiers are worked out FROM; they
        // are simply not shown any more. The announcement's own "average
        // submissions" figure is not shown either - the window now works that
        // number out from what this player is actually managing, which is the
        // one version of it that can tell them how many more catches to make.

        // §234. A quest runs 24 hours from the moment it starts OR ends as
        // soon as the community goal is met, whichever comes first. The
        // tracker only ever knows this player's own contribution, never the
        // community total, so it cannot see the second condition coming - the
        // countdown is a MAXIMUM, and the window says so rather than letting a
        // player read six remaining hours as a promise.
        //
        // The announcement's own printed end date is shown as-is beside it and
        // deliberately not used for anything - see WorldQuestService's remark
        // on why the countdown runs off the derived time instead.
        EndsText = string.IsNullOrWhiteSpace(quest.EndTimeText)
            ? "A quest runs 24 hours, or ends as soon as the community goal is met - so this is the longest it can still be running."
            : $"A quest runs 24 hours, or ends as soon as the community goal is met. The announcement reads \"{quest.EndTimeText}\".";

        Sprite = PokemonSpriteService.GetSprite(quest.Pokemon);

        progress = WorldQuestService.Load(quest.MessageId);
        RefreshProgress();
        UpdateCountdown();

        StatusMessage = "Watching for catches. Leave this window open - Pokémon Not Detected means you can still use Submit Pokémon.";

        clock.Start();
        watch.Start();
    }

    // ------------------------------------------------------------- commands

    /// <summary>The manual fallback: the total the player read off the panel
    /// themselves. Always available, and the only path when the automatic
    /// read misses.</summary>
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

    /// <summary>The undo. §235 removed the list of counted catches, so this
    /// names what it took out - without a list there is nothing else to look
    /// at afterwards to check it took the right one.</summary>
    [RelayCommand]
    private void RemoveLast()
    {
        if (quest is null || progress.Count == 0)
        {
            StatusMessage = "There is nothing to remove yet.";
            return;
        }

        int removed = progress.Submissions[^1].Total;

        progress = WorldQuestService.RemoveLast(quest.MessageId);
        RefreshProgress();
        StatusMessage = $"Removed the most recent catch ({removed} IVs).";
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
            StatusMessage = "Pokémon not detected - please use Submit Pokémon.";
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
    /// found, released again afterwards so this window never steals a binding
    /// a hunt is relying on.</summary>
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
    /// tier re-aims the window at it again, rather than leaving it pointed at
    /// a tier they no longer qualify for.
    /// </summary>
    private void RefreshProgress()
    {
        int collected = progress.Collected;
        int catches = progress.Count;
        int first = FirstTicketIvs();
        int second = SecondTicketIvs();

        ObtainedText = collected.ToString("#,0", CultureInfo.CurrentCulture);
        ObtainedCaption = catches switch
        {
            0 => "no catches yet",
            1 => "from 1 catch",
            _ => $"from {catches:#,0} catches",
        };

        // Which ticket the window is aiming at. Below the first tier it is the
        // first; at or above it, the second; at or above both, neither - and
        // the window says so rather than showing a distance of zero to a
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

            IvsNeededText = remaining.ToString("#,0", CultureInfo.CurrentCulture);
            IvsNeededCaption = haveFirst
                ? $"for the 2nd ticket ({target:#,0})"
                : $"for the 1st ticket ({target:#,0})";

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

                AverageSubmissionsText = more.ToString("#,0", CultureInfo.CurrentCulture);
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
        // read would divide by zero here, and a window showing "Infinity%" is
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
        // the window's arithmetic against the announcement's own wording.
        int goal = quest?.TotalIvs ?? 0;

        CurrentPercentCaption = goal > 0
            ? $"{Truncate(collected, goal, 3).ToString("0.###", CultureInfo.CurrentCulture)}% of the goal"
            : string.Empty;
    }

    /// <summary>
    /// <paramref name="part"/> as a percentage of <paramref name="whole"/>,
    /// rounded DOWN to <paramref name="places"/> decimals rather than to the
    /// nearest.
    ///
    /// §235. Rounding to nearest overstates. At 5,249 IVs of a 5,250 tier the
    /// nearest tenth is 100.0%, and a window reading "100%" directly beside
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

    private void UpdateCountdown()
    {
        if (quest?.EndsUtc is null)
        {
            CountdownText = "--:--:--";
            return;
        }

        TimeSpan left = quest.EndsUtc.Value - DateTime.UtcNow;

        if (left <= TimeSpan.Zero)
        {
            CountdownText = "00:00:00";
            clock.Stop();
            watch.Stop();
            StatusMessage = "This World Quest has ended.";
            return;
        }

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
        // a shared cache (see its LoadSprite), so the one in this window is the
        // same object the Boss Database and the encounter table are showing -
        // disposing it here would blank the sprite everywhere else in the app
        // the moment this window closed.
    }
}
