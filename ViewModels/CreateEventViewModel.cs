using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs CreateEventWindow - the Team Magma-only form for posting a new
/// entry to the guild Events board (see EventsViewModel/EventsWindow for
/// the read-only side everyone sees, and RemoveEventViewModel for the
/// delete side). Nothing here checks the login itself: by the time this
/// ViewModel exists, MainWindow.AdminLoginButton_Click has already
/// confirmed it via AdminLoginWindow/AdminAuthService and the admin has
/// picked "Create Event…" on AdminActionsWindow, and this window simply
/// isn't reachable any other way. See GuildEventService for why a post only
/// reaches this one machine's board so far, not other players'.
/// </summary>
public sealed partial class CreateEventViewModel : ViewModelBase
{
    public GuildEventType[] EventTypes { get; } = Enum.GetValues<GuildEventType>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private GuildEventType eventType = GuildEventType.Giveaway;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string title = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string message = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string postedByName = string.Empty;

    // Optional - see EventPokemonPickerWindow. Empty PokemonName means "none
    // chosen"; CreateEventWindow.axaml drives the sprite thumbnail's and the
    // Clear button's visibility straight off this same string (via the
    // existing StringNotEmptyConverter), so there's no separate HasPokemon
    // flag to keep in sync here.
    [ObservableProperty] private string pokemonName = string.Empty;
    [ObservableProperty] private Bitmap? pokemonSprite;

    // §103 rewards (all optional - see GuildEvent's reward fields): the item
    // is typed free-form because no item catalog exists anywhere in this app
    // to pick from (the reference design's Choose... became a text box, the
    // one working equivalent); the reward Pokémon reuses the exact same
    // picker as the subject Pokémon above; Pokédollars is digits-only,
    // parsed defensively at post time.
    [ObservableProperty] private string itemReward = string.Empty;
    [ObservableProperty] private string pokemonRewardName = string.Empty;
    [ObservableProperty] private Bitmap? pokemonRewardSprite;
    [ObservableProperty] private string pokeDollarsText = string.Empty;

    // §107: the two circular markers, one per button an event's card can
    // carry (see EventsWindow's Submit Pokémon / View Entries pair). Both
    // start ON so the form's default post is identical to what §106 shipped -
    // an admin who never touches the markers keeps getting exactly the card
    // they already know. Plain bools rather than an enum or a flags value
    // because the two are genuinely independent: any of the four combinations
    // is a sensible event.
    //
    // Deliberately NOT reset by Post(), unlike title/message/rewards: which
    // buttons an event carries is a property of the KIND of event being
    // posted, so it belongs with the type and the poster's name in the
    // "leave it filled in for the next post" group rather than the
    // per-post group.
    [ObservableProperty] private bool allowPokemonSubmissions = true;
    [ObservableProperty] private bool allowViewEntries = true;

    [ObservableProperty] private string statusMessage =
        EventsSyncService.IsOnline
            ? "Posts go to the shared events server - every tracker sees them on its next refresh."
            : "Posts show up on the Events board right away - local test board, so only on this machine for now.";

    /// <summary>
    /// Set by CreateEventWindow.axaml.cs to show EventPokemonPickerWindow.
    /// Returns the chosen Pokemon name, "" if Clear was picked, or null if the
    /// dialog was cancelled outright (current choice left alone either way) -
    /// same Request*/Func hook pattern as MainWindowViewModel.RequestPokemonSelection.
    /// </summary>
    public Func<Task<string?>>? RequestPokemonPick { get; set; }

    /// <summary>§143/§153. Wired by CreateEventWindow to AdminTokenWindow:
    /// signs this run in (an admin login or the master token) the first time
    /// a post needs it. False means the admin cancelled.</summary>
    public Func<Task<bool>>? RequestAdminSignIn { get; set; }

    private bool CanPost() =>
        !string.IsNullOrWhiteSpace(Title) &&
        !string.IsNullOrWhiteSpace(Message) &&
        !string.IsNullOrWhiteSpace(PostedByName);

    [RelayCommand]
    private async Task ChoosePokemon()
    {
        if (RequestPokemonPick is null)
            return;

        string? chosen = await RequestPokemonPick();

        // null = dialog was cancelled outright - leave whatever was already
        // chosen (if anything) untouched. "" = Clear was picked on purpose.
        if (chosen is not null)
            SetPokemon(chosen);
    }

    [RelayCommand]
    private void ClearPokemon() => SetPokemon(string.Empty);

    private void SetPokemon(string name)
    {
        PokemonName = name;
        PokemonSprite = string.IsNullOrWhiteSpace(name) ? null : PokemonSpriteService.GetEncounterSprite(name);
    }

    [RelayCommand]
    private async Task ChoosePokemonReward()
    {
        if (RequestPokemonPick is null)
            return;

        string? chosen = await RequestPokemonPick();

        if (chosen is not null)
            SetPokemonReward(chosen);
    }

    [RelayCommand]
    private void ClearPokemonReward() => SetPokemonReward(string.Empty);

    private void SetPokemonReward(string name)
    {
        PokemonRewardName = name;
        PokemonRewardSprite = string.IsNullOrWhiteSpace(name) ? null : PokemonSpriteService.GetEncounterSprite(name);
    }

    [RelayCommand(CanExecute = nameof(CanPost))]
    private async Task Post()
    {
        // Digits-only, clamped at zero - a stray non-number just means "no
        // Pokédollar reward" rather than a blocked post.
        long pokeDollars = long.TryParse(PokeDollarsText.Trim(), out long parsed) && parsed > 0 ? parsed : 0;

        if (EventsSyncService.IsOnline)
        {
            // §143: the shared board. The server assigns the id and the
            // time; the local copy caches what it returns. A refusal leaves
            // the form filled in so nothing typed is lost.
            if (!await EnsureAdminSignInAsync())
            {
                StatusMessage = "Not posted - changing the shared board needs an admin sign-in.";
                return;
            }

            var draft = new GuildEvent
            {
                Type = EventType,
                Title = Title.Trim(),
                Message = Message.Trim(),
                PostedBy = PostedByName.Trim(),
                PokemonName = PokemonName.Trim(),
                ItemReward = ItemReward.Trim(),
                PokemonReward = PokemonRewardName.Trim(),
                PokeDollars = pokeDollars,
                AllowPokemonSubmissions = AllowPokemonSubmissions,
                AllowViewEntries = AllowViewEntries
            };

            try
            {
                GuildEvent posted = await EventsSyncService.PostEventAsync(draft);

                GuildEventService.Upsert(posted);
                StatusMessage = $"Posted \"{posted.Title}\" to the shared board - every tracker sees it on its next refresh.";
            }
            catch (EventsSyncException ex)
            {
                if (ex.Unauthorized)
                    EventsSyncService.ForgetAdminCredentials();

                StatusMessage = ex.Unauthorized
                    ? "Not posted - the events server rejected the sign-in. Press Post again to sign in."
                    : $"Not posted - {ex.Message}.";
                return;
            }
        }
        else
        {
            GuildEventService.Post(
                EventType, Title, Message, PostedByName, PokemonName,
                ItemReward, PokemonRewardName, pokeDollars,
                AllowPokemonSubmissions, AllowViewEntries);

            StatusMessage = $"Posted \"{Title}\" - open the Events menu to see it on the board.";
        }

        // Keep the name, type and the two button markers (§107) filled in -
        // convenience for posting more than one thing in a row. Clear the
        // per-post fields (title, message, Pokémon, rewards) so the form is
        // ready for the next post.
        Title = string.Empty;
        Message = string.Empty;
        SetPokemon(string.Empty);
        SetPokemonReward(string.Empty);
        ItemReward = string.Empty;
        PokeDollarsText = string.Empty;
    }

    /// <summary>§143/§153. True when EventsSyncService holds an admin
    /// credential - the master token, a signed-in login, or a login
    /// remembered on this machine - asking through RequestAdminSignIn
    /// otherwise.</summary>
    private async Task<bool> EnsureAdminSignInAsync()
    {
        if (EventsSyncService.HasAdminCredentials)
            return true;

        if (RequestAdminSignIn is null)
            return false;

        return await RequestAdminSignIn();
    }
}
