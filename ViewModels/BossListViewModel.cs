using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §260. One boss as the Boss Database grid draws it: the portrait, the name
/// and location the boss file carries, a live cooldown line, and which of the
/// three difficulties that file actually has.
/// </summary>
public sealed partial class BossListItem : ViewModelBase
{
    /// <summary>The FILE NAME without its extension, not the file's own
    /// "bossId" field. BossRepository.Load builds its path as
    /// "{bossId}.json", so the filename is the only id that is guaranteed to
    /// open - and at least one boss file spells the field "BossID" while
    /// another could disagree with its filename outright.</summary>
    public required string BossId { get; init; }

    public required string Name { get; init; }

    public required string Location { get; init; }

    public Bitmap? Portrait { get; init; }

    public bool HasPortrait => Portrait is not null;

    // Two boss files name a portrait the library does not have (Link,
    // Toothless), so the card falls back to the name in the sprite box - the
    // same fallback BossDetailWindow's NPC strip uses. The view negates
    // HasPortrait in the binding rather than reading a second property.

    public required bool HasEasy { get; init; }
    public required bool HasMedium { get; init; }
    public required bool HasHard { get; init; }

    /// <summary>True when the file lists no usable difficulty at all - a stub
    /// or a malformed "difficulties" object. The card says so rather than
    /// showing an empty button row.</summary>
    public bool HasNoDifficulty => !HasEasy && !HasMedium && !HasHard;

    /// <summary>Name and location folded once, at load, so filtering is a
    /// substring test rather than 49 ToLower calls per keystroke.</summary>
    public required string SearchKey { get; init; }

    [ObservableProperty] private string cooldownText = ReadyText;

    private const string ReadyText = "Ready";

    /// <summary>Reads BossCooldownService for this boss, keyed by NAME,
    /// which is what RegisterBossDefeat writes. §274: the Boss Cooldown
    /// window this dd:hh:mm shape was copied from is gone - the card is now
    /// the only place a cooldown is shown OR started, which is where §260 was
    /// heading from the moment it put the line here.</summary>
    public void RefreshCooldown()
    {
        Models.BossCooldownEntry? cooldown = BossCooldownService.GetCooldown(Name);

        if (cooldown is null || cooldown.TimeRemaining <= TimeSpan.Zero)
        {
            CooldownText = ReadyText;
            return;
        }

        TimeSpan left = cooldown.TimeRemaining;

        CooldownText = $"{left.Days:00}:{left.Hours:00}:{left.Minutes:00} left";
    }
}

/// <summary>
/// The Boss Database.
///
/// Ported from ProTrackerandDatabase.cs's boss menu (OpenBoss /
/// BossDifficultyMenuItem_Click), which was a hand-built ToolStripMenuItem
/// tree of ~48 bosses x 3 difficulties. §22 replaced that with one list box, a
/// difficulty combo and an Open button; §260 replaces THAT with a grid of
/// portrait cards, because a bare list of 49 names was the one window in the
/// app that looked like nothing else in it.
///
/// Two things the list could not do and this does:
///
/// The difficulty belongs to the boss. The combo offered Easy, Medium and Hard
/// for every boss in the database, and ten of the 49 do not have all three -
/// BattleBot, the three Guardians and Team Rocket are Hard only, Sage and The
/// Pumpkin King are Easy only, Ash Westbrook has no Medium, Morty no Easy, and
/// Maribela carries an Easy section whose only content is a note saying to
/// look under Medium or Hard. Each card offers exactly the difficulties its
/// own file has, so a difficulty that does not exist cannot be picked.
///
/// And a boss is a picture, a place and a cooldown, not a string. The card
/// carries the portrait, the location, and how long until this boss can be
/// fought again.
///
/// §274 folded the Boss Cooldown window into this one. That window was this
/// list again - smaller, with no search, no way to open the boss, and the
/// same 49 files read a second way. Its one thing this did not have was the
/// manual "start the cooldown", for when automatic detection missed a fight
/// (§91); that is now a click on the card's own cooldown line. One window
/// shows a boss's cooldown and starts it.
///
/// The scan stays non-recursive, so DataFiles/Bosses/CustomBosses is still not
/// listed here. That is not an oversight of this section: BossRepository.Load
/// and BossCooldownService.GetAllBossNames both look only at the top level, so
/// a custom boss shown here could not be opened. Listing them is a change to
/// all three, and a different section.
/// </summary>
public sealed partial class BossListViewModel : ViewModelBase
{
    private readonly List<BossListItem> all = new();

    /// <summary>The cards actually on screen - "all", filtered by SearchText.</summary>
    public ObservableCollection<BossListItem> Bosses { get; } = new();

    [ObservableProperty] private string searchText = string.Empty;

    [ObservableProperty] private string countText = string.Empty;

    [ObservableProperty] private bool isEmpty;

    /// <summary>Raised with (bossId, difficulty) when a card's difficulty
    /// button is pressed - the View shows BossDetailWindow.</summary>
    public event Action<string, BossDifficulty>? OpenRequested;

    public BossListViewModel()
    {
        all.AddRange(ReadAll());

        RefreshCooldowns();
        ApplyFilter();
    }

    /// <summary>
    /// §288. Every boss the folder holds, by name. Pulled out of the
    /// constructor so the Search window (§280) can list bosses without
    /// building a Boss Database to do it - the light read below is the whole
    /// point of this class and there is no reason for a second copy of it.
    ///
    /// The scan stays non-recursive, for the reason in the class remarks.
    /// </summary>
    public static List<BossListItem> ReadAll()
    {
        var found = new List<BossListItem>();

        string bossesFolder = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");

        if (Directory.Exists(bossesFolder))
        {
            foreach (string file in Directory.GetFiles(bossesFolder, "*.json"))
            {
                BossListItem? item = ReadBoss(file);

                if (item is not null)
                    found.Add(item);
            }
        }

        // By NAME, not by filename: the list was ordered by file, which put
        // "LtSurge" where "Lt. Surge" reads.
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        return found;
    }

    /// <summary>§288. Which difficulties the card offers, as the row reads
    /// them - "Easy, Medium, Hard", or "no difficulty" for a stub.</summary>
    public static string DifficultiesText(BossListItem boss)
    {
        var parts = new List<string>(3);

        if (boss.HasEasy) parts.Add("Easy");
        if (boss.HasMedium) parts.Add("Medium");
        if (boss.HasHard) parts.Add("Hard");

        return parts.Count == 0 ? "no difficulty" : string.Join(", ", parts);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>§274. Set by the View to show a Yes/No confirmation - the same
    /// ConfirmDialogWindow hook the Boss Cooldown window had, and the same one
    /// HuntLogViewModel and PreviouslyBattledUsersViewModel use.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// §274. Starts this boss's cooldown by hand - the one thing the Boss
    /// Cooldown window did that this list could not.
    ///
    /// It exists because the automatic path can miss a fight
    /// (Tracking/BossCooldownTracker, §91), and a cooldown line nobody can
    /// correct is worse than no line at all. Confirmed first, because it is a
    /// click on something that until now only reported.
    ///
    /// Passes the card's BossId, which §260 already made the FILE NAME rather
    /// than the file's own "bossId" field - and that quietly fixes the old
    /// window, which passed the field. RegisterBossDefeat loads
    /// "{bossId}.json", so a boss whose field disagrees with its filename
    /// started no cooldown at all from that window.
    ///
    /// The Lifetime Stats tally moves too, with a null outcome - the fight's
    /// result is unknown here, so only the fights counter moves. No
    /// double-count risk against the automatic path: a manual start is only
    /// ever needed when that path missed the fight.
    /// </summary>
    [RelayCommand]
    private async Task StartCooldown(BossListItem? boss)
    {
        if (boss is null)
            return;

        bool confirmed = ConfirmAsync is null
            || await ConfirmAsync($"Start the cooldown for {boss.Name}?");

        if (!confirmed)
            return;

        BossCooldownService.RegisterBossDefeat(boss.BossId);
        LifetimeStatsService.AddBossBattle(null);

        // §277: and the per-boss record, as an attempt whose result nobody
        // saw - which is exactly what a manual start is. Null rather than a
        // loss, the same null AddBossBattle has always been given here.
        BossRecordService.Record(boss.BossId, null);

        boss.RefreshCooldown();
    }

    /// <summary>Called by the View when the window is activated: a boss
    /// fought while this window sat open should be current when the player
    /// comes back to it. No timer - the shown resolution is a minute, so one
    /// would cost a tick a second to change something once an hour.</summary>
    public void RefreshCooldowns()
    {
        foreach (BossListItem item in all)
            item.RefreshCooldown();
    }

    private void ApplyFilter()
    {
        string needle = SearchText.Trim();

        Bosses.Clear();

        foreach (BossListItem item in all)
        {
            if (needle.Length == 0 ||
                item.SearchKey.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            {
                Bosses.Add(item);
            }
        }

        IsEmpty = Bosses.Count == 0;

        CountText = Bosses.Count == all.Count
            ? $"{DisplayNumber.Count(all.Count)} bosses"
            : $"{DisplayNumber.Count(Bosses.Count)} of {DisplayNumber.Count(all.Count)} bosses";
    }

    [RelayCommand]
    private void OpenEasy(BossListItem? boss) => Open(boss, BossDifficulty.Easy);

    [RelayCommand]
    private void OpenMedium(BossListItem? boss) => Open(boss, BossDifficulty.Medium);

    [RelayCommand]
    private void OpenHard(BossListItem? boss) => Open(boss, BossDifficulty.Hard);

    private void Open(BossListItem? boss, BossDifficulty difficulty)
    {
        if (boss is not null)
            OpenRequested?.Invoke(boss.BossId, difficulty);
    }

    // ---- reading the files ------------------------------------------------

    /// <summary>
    /// A deliberately LIGHT read: the grid needs six fields and three keys,
    /// while BossData carries every difficulty's full team, its rewards and
    /// its tier chances. Deserializing 49 of those to draw 49 cards would
    /// build some nine hundred Pokemon objects and throw all of them away.
    /// BossRepository.Load still does the full read, once, when a boss is
    /// actually opened.
    ///
    /// A malformed file is skipped, as everywhere else this folder is
    /// scanned.
    /// </summary>
    private static BossListItem? ReadBoss(string file)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string bossId = Path.GetFileNameWithoutExtension(file);

            string name = ReadString(root, "name");

            if (name.Length == 0)
                name = EnumFormatHelper.ToDisplayName(bossId);

            string location = ReadString(root, "location");

            bool easy = HasDifficulty(root, "easy");
            bool medium = HasDifficulty(root, "medium");
            bool hard = HasDifficulty(root, "hard");

            return new BossListItem
            {
                BossId = bossId,
                Name = name,
                Location = location,
                Portrait = LoadPortrait(ReadString(root, "NPCPicture")),
                HasEasy = easy,
                HasMedium = medium,
                HasHard = hard,
                SearchKey = $"{name} {location}"
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A difficulty counts as offered when the file has a section for it AND
    /// that section does not carry an "unavailableMessage" - Maribela's Easy
    /// section exists but says only "This difficulty does not exist, please
    /// look under Medium/Hard", which is a section written to explain an
    /// absence, not a fight to open.
    ///
    /// It deliberately does NOT require a "team": the manually-split dual
    /// bosses nest each combatant's team under their own name, so a real
    /// difficulty can have no top-level team array at all (see
    /// BossDifficultyData.GetNpcTeams).
    /// </summary>
    private static bool HasDifficulty(JsonElement root, string difficulty)
    {
        if (!TryGetProperty(root, "difficulties", out JsonElement difficulties) ||
            difficulties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(difficulties, difficulty, out JsonElement section) ||
            section.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return ReadString(section, "unavailableMessage").Length == 0;
    }

    /// <summary>Case-insensitive, because BossRepository reads these files
    /// with PropertyNameCaseInsensitive and the folder is hand-edited - one
    /// file already spells its id "BossID" where every other spells it
    /// "bossId". A reader stricter than the loader would drop fields the app
    /// itself accepts.</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>The same relative-path resolution BossDetailViewModel uses
    /// for these portraits. §274: and the Boss Cooldown view model, until
    /// this section deleted it.</summary>
    private static Bitmap? LoadPortrait(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        string cleanPath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        string fullPath = Path.Combine(AppContext.BaseDirectory, cleanPath);

        return File.Exists(fullPath) ? new Bitmap(fullPath) : null;
    }
}
