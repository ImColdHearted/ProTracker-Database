using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs SessionEncounterHistoryWindow - the per-Pokemon encounter history,
/// opened by clicking a row in either Session Encounters table on the main
/// window. NOT the Catch Logs drilldown - that is
/// HuntLogSpeciesDetailViewModel ("Catch History"), which shows only
/// successful catches.
///
/// Two views behind one toggle since §112:
///
/// SESSION (the original, and still the default) is the current hunt. Records
/// IS the live collection SessionEncounterHistoryService maintains for this
/// Pokemon - bound directly, never copied or rebuilt - so a new encounter
/// appends one row, a late level/location refinement updates its row through
/// the record's own property notifications, and Reset's Clear() empties it in
/// place, all without this class doing anything beyond keeping the empty-state
/// flags current.
///
/// ALL TIME is the permanent log in EncounterDatabaseService: every encounter
/// of this species on this profile, ever, with the date each one happened.
/// Deliberately NOT live - it is a query result, loaded on demand when the
/// toggle is turned on and paged from there, because the whole point of §112
/// is that this table can hold millions of rows and must never be loaded
/// whole. Paging is keyset (ask for rows older than the last one shown), so
/// the hundredth page costs what the first one did.
///
/// Subscribes to the session collection's CollectionChanged in the
/// constructor; Dispose (wired to the window's Closed event, same pattern as
/// PreviouslyBattledUsersViewModel) unsubscribes so closing the window cannot
/// leak this instance for the rest of the app's lifetime.
/// </summary>
public sealed partial class SessionEncounterHistoryViewModel : ViewModelBase, IDisposable
{
    public string PokemonName { get; }

    public string Title => $"{PokemonName} - Encounter History";

    /// <summary>The live current-hunt collection, owned by the service.</summary>
    public ObservableCollection<SessionEncounterRecord> Records { get; }

    /// <summary>§128. One page of Records. The window used to bind the
    /// whole collection and scroll, which is fine at fifty rows and not at
    /// the 2,640 a single species reached in a real hunt.</summary>
    public ObservableCollection<SessionEncounterRecord> SessionPage { get; } = new();

    /// <summary>Both lists page at the same size, which is also the maximum
    /// agreed for the front encounter table.</summary>
    public static int PageSize => EncounterDatabaseService.PageSize;

    /// <summary>The loaded pages of the permanent log, newest first. Grows by
    /// a page at a time and is thrown away when the toggle goes back to
    /// Session, so nothing here pins a large result set open.</summary>
    public ObservableCollection<EncounterLogRow> AllTimeRecords { get; } = new();

    [ObservableProperty] private bool hasEntries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSessionEmptyState))]
    private bool hasNoEntries = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    [NotifyPropertyChangedFor(nameof(ShowSessionEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowAllTimeEmptyState))]
    private bool showAllTime;

    [ObservableProperty] private bool hasAllTimeEntries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllTimeEmptyState))]
    private bool hasNoAllTimeEntries = true;

    [ObservableProperty] private string allTimeSummary = string.Empty;

    /// <summary>§273. Whether the Shiny/Form column is sorting rather than
    /// just reporting. On, every form comes first, then every shiny, then the
    /// rest - each group still newest first, so the only thing that changed
    /// is which rows you have to page to. Off is the chronological order this
    /// window has always opened in.
    ///
    /// One flag for both views. They reach the order by completely different
    /// means - a slice of an in-memory list against three keyset walks over
    /// the log - but it is one question the reader asked, and two toggles
    /// that could disagree would be two things to explain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RareHeaderText))]
    [NotifyPropertyChangedFor(nameof(RareHeaderTip))]
    private bool rareFirst;

    /// <summary>The header carries the arrow, so the column that is doing the
    /// sorting says so where the sorting was asked for.</summary>
    public string RareHeaderText => RareFirst ? "Shiny/Form \u25B2" : "Shiny/Form";

    public string RareHeaderTip => RareFirst
        ? "Sorted forms first, then shinies. Click to go back to newest first."
        : "Click to bring every form to the top, then every shiny.";



    // ---- §128 paging state -------------------------------------------
    //
    // The two modes page by different mechanisms and cannot share one, which
    // is why there are two sets of these rather than one.
    //
    // Session is an in-memory list: a page is a slice, and any page is
    // reachable directly.
    //
    // All time is a database of up to millions of rows, paged by keyset -
    // "everything older than the last row I showed". That is what keeps page
    // five hundred as cheap as page one (§112 measured OFFSET degrading to
    // 8.6 ms by then, against 0.13 ms flat for keyset). Keyset has no notion
    // of jumping to an arbitrary page, but Previous does not need one: the
    // cursor that opened each page is pushed on a stack, so going back is a
    // pop rather than a re-count. Forward pushes, back pops, and the depth of
    // the stack IS the page number.

    [ObservableProperty] private int sessionPageIndex;
    [ObservableProperty] private int sessionPageCount = 1;

    [ObservableProperty] private int allTimePageIndex;
    [ObservableProperty] private int allTimePageCount = 1;

    /// <summary>§273. Where each already-visited page starts. Chronologically
    /// that is only an id; rare-first it is also WHICH RANK that id sits in,
    /// because the three ranks are walked one after another (see
    /// EncounterDatabaseService.QueryRareFirst). One pair serves both - the
    /// chronological order is the single rank <see cref="AllRanks"/>, so
    /// nothing here has to ask which mode it is in. Index 0 is page one, which
    /// starts at the top of the first rank and so has no id.</summary>
    private readonly List<(int Rank, long? BeforeId)> allTimePageCursors = new() { (0, null) };

    /// <summary>The rank a chronological page starts in: there is only one
    /// list, so it is rank 0 and never advances.</summary>
    private const int AllRanks = 0;

    /// <summary>§273. Back to page one, cursor stack emptied. Called by the
    /// All time toggle and by the Shiny/Form header, which both invalidate
    /// every recorded cursor - the second because the rows a page contains
    /// are about to change.</summary>
    private void ResetAllTimePaging()
    {
        allTimePageCursors.Clear();
        allTimePageCursors.Add((0, null));
        AllTimePageIndex = 0;
    }

    partial void OnSessionPageIndexChanged(int value) => RaisePagerChanged();
    partial void OnSessionPageCountChanged(int value) => RaisePagerChanged();
    partial void OnAllTimePageIndexChanged(int value) => RaisePagerChanged();
    partial void OnAllTimePageCountChanged(int value) => RaisePagerChanged();

    private void RaisePagerChanged()
    {
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(CanPageBack));
        OnPropertyChanged(nameof(CanPageForward));
        OnPropertyChanged(nameof(ShowPager));
    }

    /// <summary>One pager serves both lists - only one is ever on screen, so
    /// a second set of controls would be two things to keep in step for no
    /// gain. Which numbers it shows follows the toggle.</summary>
    public string PageLabel
    {
        get
        {
            int index = ShowAllTime ? AllTimePageIndex : SessionPageIndex;
            int count = ShowAllTime ? AllTimePageCount : SessionPageCount;

            return count <= 1 ? string.Empty : $"Page {index + 1} of {count}";
        }
    }

    public bool CanPageBack =>
        ShowAllTime ? AllTimePageIndex > 0 : SessionPageIndex > 0;

    public bool CanPageForward =>
        ShowAllTime
            ? AllTimePageIndex + 1 < AllTimePageCount
            : SessionPageIndex + 1 < SessionPageCount;

    public bool ShowPager =>
        (ShowAllTime ? AllTimePageCount : SessionPageCount) > 1;

    [RelayCommand]
    private void PageBack()
    {
        if (!CanPageBack)
            return;

        if (ShowAllTime)
        {
            AllTimePageIndex--;
            LoadAllTimePage();
        }
        else
        {
            SessionPageIndex--;
            FillSessionPage();
        }
    }

    [RelayCommand]
    private void PageForward()
    {
        if (!CanPageForward)
            return;

        if (ShowAllTime)
        {
            AllTimePageIndex++;
            LoadAllTimePage();
        }
        else
        {
            SessionPageIndex++;
            FillSessionPage();
        }
    }

    /// <summary>Recomputes the session page count and copies the current page
    /// out. Called whenever Records changes, which during a hunt is every
    /// encounter of this species - so it keeps the reader on the page they
    /// were on unless it stopped existing.</summary>
    private void FillSessionPage()
    {
        SessionPageCount = Math.Max(1, (Records.Count + PageSize - 1) / PageSize);

        if (SessionPageIndex >= SessionPageCount)
            SessionPageIndex = SessionPageCount - 1;

        SessionPage.Clear();

        // §273: the same rows, in whichever order the Shiny/Form header last
        // asked for. OrderBy is a STABLE sort, so within a rank the rows keep
        // the newest-first order Records is already kept in - the rare-first
        // view is the chronological one regrouped, not re-sorted.
        IReadOnlyList<SessionEncounterRecord> ordered = RareFirst
            ? (IReadOnlyList<SessionEncounterRecord>)Records.OrderBy(SessionRankOf).ToList()
            : Records;

        int start = SessionPageIndex * PageSize;

        for (int i = start; i < ordered.Count && i < start + PageSize; i++)
            SessionPage.Add(ordered[i]);
    }

    /// <summary>§273. The session record's rank, by the same rule
    /// EncounterDatabaseService applies to a logged row - forms 0, shinies 1,
    /// everything else 2. The two live apart because the types do, and they
    /// have to keep saying the same thing.</summary>
    internal static int SessionRankOf(SessionEncounterRecord record) =>
        string.IsNullOrWhiteSpace(record.RareType) || record.RareType == "None"
            ? EncounterDatabaseService.RankOrdinary
            : record.RareType == "Shiny"
                ? EncounterDatabaseService.RankShiny
                : EncounterDatabaseService.RankForm;

    /// <summary>§273. The Shiny/Form header. Both views go back to page one:
    /// a page number means nothing across a reordering, and the rows the
    /// reader just asked to see are at the top.</summary>
    [RelayCommand]
    private void ToggleRareFirst()
    {
        RareFirst = !RareFirst;

        if (ShowAllTime)
        {
            ResetAllTimePaging();
            LoadAllTimePage();
        }
        else
        {
            SessionPageIndex = 0;
            FillSessionPage();
        }
    }

    // Each list's empty state depends on BOTH which view is showing and
    // whether that view has rows, and IsVisible takes one binding - so the
    // two conditions are combined here rather than in the XAML.
    public bool ShowSessionEmptyState => !ShowAllTime && HasNoEntries;

    public bool ShowAllTimeEmptyState => ShowAllTime && HasNoAllTimeEntries;

    public string ModeDescription => ShowAllTime
        ? "Every encounter of this Pokémon ever recorded on this client profile, most recent first. This list is not affected by resetting the hunt."
        : "Every encounter of this Pokémon during the current hunt, most recent first. This history clears when the hunt is reset.";

    public SessionEncounterHistoryViewModel(string pokemonName)
    {
        PokemonName = pokemonName;

        Records = SessionEncounterHistoryService.GetHistoryFor(pokemonName);
        Records.CollectionChanged += OnRecordsChanged;

        UpdateEmptyStateFlags();
    }

    // Always raised on the UI thread - the service's contract is that every
    // mutation happens there - so the flags can be set directly, no
    // re-dispatch needed.
    private void OnRecordsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyStateFlags();
    }

    private void UpdateEmptyStateFlags()
    {
        HasEntries = Records.Count > 0;
        HasNoEntries = Records.Count == 0;

        // §128: the page has to follow the collection. Records grows by one
        // every time this species is encountered again while the window is
        // open, so this runs often - which is exactly why FillSessionPage
        // holds the reader's page rather than resetting to the first.
        FillSessionPage();
    }

    /// <summary>
    /// §112. Turning All time ON loads the first page; turning it OFF drops
    /// every loaded row, so a window left open on a heavily-hunted species is
    /// not holding thousands of rows for the rest of the session.
    /// </summary>
    partial void OnShowAllTimeChanged(bool value)
    {
        AllTimeRecords.Clear();

        // §128: the cursor stack belongs to one visit. Coming back to All
        // time after switching away must not reuse ids from a list that has
        // since grown - every new encounter shifts what page two contains.
        ResetAllTimePaging();

        // The pager reads its numbers from whichever mode is showing, so it
        // has to be told the mode changed even when neither count did.
        RaisePagerChanged();

        if (!value)
        {
            AllTimeSummary = string.Empty;
            UpdateAllTimeFlags();
            return;
        }

        // The permanent log is the one part of this window that can be
        // genuinely unavailable (see EncounterDatabaseService's fail-safe
        // contract). Say so, rather than showing an empty list that reads as
        // "you have never seen this Pokemon".
        if (!EncounterDatabaseService.IsAvailable)
        {
            AllTimePageCount = 1;
            AllTimePageIndex = 0;
            AllTimeSummary =
                "The permanent encounter log could not be opened, so there is nothing to show here. " +
                "The current hunt is unaffected - switch back to Session.";
            UpdateAllTimeFlags();
            return;
        }

        LoadAllTimePage();
    }

    // §128 removed LoadMore/LoadNextPage from here. They appended another
    // chunk to a growing list and could only ever report "showing N of M",
    // never which page of how many - the "Page 1 of ??" in the window was
    // that limitation showing through. LoadAllTimePage below replaces the
    // visible rows instead, and the count query it already makes is what
    // turns "??" into a real number. Left as a note rather than silently
    // deleted because CanLoadMore went with them, and its absence is the
    // kind of thing that otherwise reads as an oversight.

    /// <summary>
    /// §128. Replaces the whole visible list with ONE page of the permanent
    /// log, rather than appending another chunk to a growing one.
    ///
    /// The cursor for the page being opened is whatever was recorded when it
    /// was first reached, so Previous is a pop rather than a re-query from
    /// the top - and forward pages keep the keyset property that makes page
    /// five hundred cost what page one costs. The count query is what gives
    /// the pager a real "of N" instead of the "of ??" the Load More button
    /// could never provide.
    /// </summary>
    private void LoadAllTimePage()
    {
        int client = SessionPersistenceService.ActiveClientNumber;

        AllTimeRecords.Clear();

        if (client < 1)
        {
            AllTimePageCount = 1;
            AllTimePageIndex = 0;
            AllTimeSummary =
                "No client profile is assigned, so there is no profile to show a history for.";
            UpdateAllTimeFlags();
            return;
        }

        long total = EncounterDatabaseService.CountFor(PokemonName, client);

        AllTimePageCount =
            (int)Math.Max(1, (total + EncounterDatabaseService.PageSize - 1)
                             / EncounterDatabaseService.PageSize);

        // The log can shrink between visits - a profile switch, or rows aged
        // out - so a remembered page may no longer exist.
        if (AllTimePageIndex >= AllTimePageCount)
            AllTimePageIndex = AllTimePageCount - 1;

        // A page reached for the first time has no cursor recorded yet; it
        // continues from where the previous page stopped.
        while (allTimePageCursors.Count <= AllTimePageIndex)
            allTimePageCursors.Add((0, null));

        (int rank, long? beforeId) = allTimePageCursors[AllTimePageIndex];

        // §273: rare-first walks the three ranks in turn and so needs to be
        // told which one this page starts in; chronological is one list and
        // starts where it left off. Both are keyset, and both cost the same
        // at page five hundred as at page one.
        System.Collections.Generic.IReadOnlyList<EncounterLogRow> page;
        (int Rank, long? BeforeId) nextCursor;

        if (RareFirst)
        {
            EncounterDatabaseService.RankedPage ranked =
                EncounterDatabaseService.QueryRareFirst(
                    PokemonName, client, rank, beforeId, EncounterDatabaseService.PageSize);

            page = ranked.Rows;
            nextCursor = (ranked.NextRank, ranked.NextBeforeId);
        }
        else
        {
            page = EncounterDatabaseService.Query(
                PokemonName, client, beforeId, EncounterDatabaseService.PageSize);

            nextCursor = (AllRanks, page.Count > 0 ? page[page.Count - 1].Id : null);
        }

        foreach (EncounterLogRow row in page)
            AllTimeRecords.Add(row);

        // Record where the NEXT page should start, so moving forward and back
        // again lands on exactly the same rows.
        if (page.Count > 0 && allTimePageCursors.Count == AllTimePageIndex + 1)
            allTimePageCursors.Add(nextCursor);

        AllTimeSummary = total == 0
            ? "No encounters of this Pokémon have been recorded on this profile yet."
            : $"{DisplayNumber.Count(total)} recorded on this profile.";

        UpdateAllTimeFlags();
    }

    private void UpdateAllTimeFlags()
    {
        HasAllTimeEntries = AllTimeRecords.Count > 0;
        HasNoAllTimeEntries = AllTimeRecords.Count == 0;
    }

    public void Dispose()
    {
        Records.CollectionChanged -= OnRecordsChanged;
    }
}
