using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed record BossScraperListItem(string BossId, string DisplayName, bool HasExistingData);

/// <summary>
/// Backs BossScraperWindow, the admin-only tool (behind AdminLoginWindow /
/// AdminAuthService) that fetches a boss page from the PRO wiki and turns it
/// into DataFiles/Bosses/{bossId}.json via BossWikiScraperService. See that
/// class for the actual fetching/parsing - this ViewModel is just the boss
/// picker + preview + save workflow wrapped around it.
///
/// One HttpClient is shared for the process lifetime rather than created per
/// fetch, which is the standard .NET guidance for avoiding socket exhaustion
/// from many short-lived clients. This is also the only feature in the whole
/// app that makes an outbound network call at all - see
/// MainWindow.ReportProblemButton_Click's remarks on why everything else here
/// deliberately avoids that. That's fine specifically for this tool because
/// it never runs unless AdminAuthService.Verify already passed, and it only
/// ever talks to the public wiki the user explicitly asked this to read from.
/// </summary>
public sealed partial class BossScraperViewModel : ViewModelBase
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // MediaWiki installs generally expect a descriptive User-Agent on API
        // calls; sending none risks being throttled or blocked outright.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ProTracker-BossWikiScraper", "1.0"));
        return client;
    }

    // Best-effort bossId -> wiki page title guesses for every boss file that
    // exists in DataFiles/Bosses today. Built by directly cross-referencing
    // every filename against https://wiki.pokemonrevolution.net/index.php?title=Bosses's
    // own "Repeatable Bosses" listing (checked 2026-08-24 - see
    // MIGRATION_GUIDE.md) rather than guessed from naming conventions alone.
    // Still just a starting point, not trusted blindly: WikiPageTitle below is
    // a plain editable textbox pre-filled from this table, so a boss added
    // later, a renamed wiki page, or the Lance ambiguity noted below is one
    // edit away from being fixed rather than a hard failure.
    private static readonly Dictionary<string, string> KnownWikiPageTitles = new()
    {
        ["Ashwestbrook"] = "Ash Westbrook (boss)",
        ["BattleBot"] = "BattleBot (boss)",
        ["Brock"] = "Brock (boss)",
        ["Bruno"] = "Bruno (boss)",
        ["Bugsy"] = "Bugsy (boss)",
        ["Chuck"] = "Chuck (boss)",
        ["Cie"] = "Cie (boss)",
        ["Erika"] = "Erika (boss)",
        ["GamersPewdieAndDiepy"] = "Gamers Pewdie and Diepy (boss)",
        ["George"] = "George (boss)",
        ["GingeryJones"] = "Gingery Jones (boss)",
        ["GuardianEntei"] = "Guardian (Entei) (boss)",
        ["GuardianRaikou"] = "Guardian (Raikou) (boss)",
        ["GuardianSuicune"] = "Guardian (Suicune) (boss)",
        ["JessieAndJames"] = "Jessie & James (boss)",
        ["Klohver"] = "Klohver (boss)",
        ["Koichi"] = "Koichi (boss)",
        // The wiki has two different Lance bosses: a one-time "Quest Boss"
        // ("Lance (boss)") and this repeatable one fought at the Dragons
        // Shrine ("Lance (Dragons Shrine) (boss)"). Lance.json already holds
        // a full team/rewards table (10KB, not a stub), which matches a
        // repeatable farm target far better than a one-off story boss, so
        // that's the default here - but it's a guess dressed up as a
        // default, not a confirmed match. Worth checking before fetching.
        ["Lance"] = "Lance (Dragons Shrine) (boss)",
        ["Letrix"] = "Letrix (boss)",
        ["Link"] = "Link (boss)",
        ["Logan"] = "Logan (boss)",
        ["Lorelei"] = "Lorelei (boss)",
        ["LtSurge"] = "Lt. Surge (boss)",
        ["Maribela"] = "Maribela (boss)",
        ["MedusaAndEldir"] = "Medusa & Eldir (boss)",
        ["Misty"] = "Misty (boss)",
        ["Morty"] = "Morty (boss)",
        ["Naero"] = "Naero (boss)",
        ["NarutoFanboy"] = "Naruto Fanboy (boss)",
        ["Neroli"] = "Neroli (boss)",
        ["OfficerJenny"] = "Officer Jenny (boss)",
        ["OfficerShamac"] = "Officer Shamac (boss)",
        ["Prehax"] = "Prehax (boss)",
        ["ProfessorBirch"] = "Professor Birch (boss)",
        ["ProfessorElm"] = "Professor Elm (boss)",
        ["ProfessorOak"] = "Professor Oak (boss)",
        ["ProfessorRowan"] = "Professor Rowan (boss)",
        ["Sage"] = "Sage (boss)",
        ["Saphirr"] = "Saphirr (boss)",
        ["SharyAndShaui"] = "Shary & Shaui (boss)",
        ["Spectify"] = "Spectify (boss)",
        ["Steven"] = "Steven (boss)",
        ["Terminator"] = "Terminator (boss)",
        ["ThePumpkinKing"] = "The Pumpkin King (boss)",
        ["Thor"] = "Thor (boss)",
        ["Tigerous"] = "Tigerous (boss)",
        ["Toothless"] = "Toothless (boss)",
        ["Urahara"] = "Urahara (boss)",
        ["Xylos"] = "Xylos (boss)",
    };

    public ObservableCollection<BossScraperListItem> Bosses { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private BossScraperListItem? selectedBoss;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private string wikiPageTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool hasResult;

    [ObservableProperty] private string statusMessage =
        "Pick a boss, check the wiki page title, then Fetch & Preview.";

    [ObservableProperty] private string? previewJson;
    [ObservableProperty] private bool existingFileHadRealData;

    private BossScrapeResult? _lastResult;

    public BossScraperViewModel()
    {
        string bossesFolder = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");
        if (!Directory.Exists(bossesFolder))
            return;

        foreach (string file in Directory.GetFiles(bossesFolder, "*.json").OrderBy(x => x))
        {
            string bossId = Path.GetFileNameWithoutExtension(file);
            bool hasData = FileHasPopulatedDifficulties(file);
            Bosses.Add(new BossScraperListItem(bossId, hasData ? bossId : $"{bossId} (stub)", hasData));
        }
    }

    // Same "has this actually been filled in" check BossWikiScraperService
    // itself uses (a stub file never has a "difficulties" key at all) - used
    // here purely to annotate the picker, so it deliberately never throws:
    // a boss file this can't even parse is a bigger problem than the picker
    // should try to solve, and Fetch & Preview's own error handling will
    // surface it clearly if the admin selects that boss anyway.
    private static bool FileHasPopulatedDifficulties(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("difficulties", out JsonElement diff) &&
                   diff.ValueKind == JsonValueKind.Object &&
                   diff.EnumerateObject().Any();
        }
        catch
        {
            return false;
        }
    }

    partial void OnSelectedBossChanged(BossScraperListItem? value)
    {
        HasResult = false;
        PreviewJson = null;
        Warnings.Clear();
        StatusMessage = "Pick a boss, check the wiki page title, then Fetch & Preview.";

        WikiPageTitle = value is not null && KnownWikiPageTitles.TryGetValue(value.BossId, out string? known)
            ? known
            : string.Empty;
    }

    private bool CanFetch() => !IsBusy && SelectedBoss is not null && !string.IsNullOrWhiteSpace(WikiPageTitle);

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        if (SelectedBoss is null)
            return;

        BossScraperListItem boss = SelectedBoss;
        string title = WikiPageTitle.Trim();

        IsBusy = true;
        HasResult = false;
        PreviewJson = null;
        Warnings.Clear();
        StatusMessage = $"Fetching \"{title}\"...";

        try
        {
            BossScrapeResult result = await BossWikiScraperService.ScrapeAsync(boss.BossId, title, SharedHttpClient);

            _lastResult = result;
            PreviewJson = result.PreviewJson;
            ExistingFileHadRealData = result.ExistingFileHadRealData;
            HasResult = true;

            foreach (string warning in result.Warnings)
                Warnings.Add(warning);

            StatusMessage = result.Warnings.Count == 0
                ? "Fetched cleanly - review the preview below, then Save if it looks right."
                : $"Fetched with {result.Warnings.Count} warning{(result.Warnings.Count == 1 ? "" : "s")} - review carefully before saving.";
        }
        catch (Exception ex)
        {
            _lastResult = null;
            StatusMessage = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && HasResult && _lastResult is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_lastResult is null)
            return;

        try
        {
            _lastResult.Save();

            // Deliberately does not try to live-refresh this boss's "(stub)"
            // annotation in the picker above - re-running OnSelectedBossChanged's
            // reset logic (via reassigning SelectedBoss to reflect the change)
            // would immediately wipe the success message and preview this line
            // just set. Reopening the window picks up the change instead; the
            // status message below is confirmation enough that the save landed.
            StatusMessage = $"Saved DataFiles/Bosses/{_lastResult.BossId}.json. " +
                             "Reopen this window to see the boss list reflect it.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }
}
