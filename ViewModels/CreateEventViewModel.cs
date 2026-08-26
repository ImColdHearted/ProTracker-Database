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

    [ObservableProperty] private string statusMessage =
        "Posts show up on the Events board right away - local test board, so only on this machine for now.";

    /// <summary>
    /// Set by CreateEventWindow.axaml.cs to show EventPokemonPickerWindow.
    /// Returns the chosen Pokemon name, "" if Clear was picked, or null if the
    /// dialog was cancelled outright (current choice left alone either way) -
    /// same Request*/Func hook pattern as MainWindowViewModel.RequestPokemonSelection.
    /// </summary>
    public Func<Task<string?>>? RequestPokemonPick { get; set; }

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

    [RelayCommand(CanExecute = nameof(CanPost))]
    private void Post()
    {
        GuildEventService.Post(EventType, Title, Message, PostedByName, PokemonName);

        StatusMessage = $"Posted \"{Title}\" - open the Events menu to see it on the board.";

        // Keep the name and type filled in - convenience for posting more
        // than one thing in a row. Clear title/message/Pokemon so the form is
        // ready for the next post - the Pokemon choice is specific to the post
        // just made, not a sticky default the way name/type are.
        Title = string.Empty;
        Message = string.Empty;
        SetPokemon(string.Empty);
    }
}
