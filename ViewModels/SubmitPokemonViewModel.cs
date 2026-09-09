using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs SubmitPokemonWindow (MIGRATION_GUIDE.md §106) - one player's entry
/// into an Events-board post: their name, the Pokemon, and a screenshot as
/// proof. Reached from the "Submit Pokemon" button on that post's card.
///
/// Two ways to attach the screenshot today, and a third one coming. "Choose
/// image" picks any saved PNG; "Capture PRO client" grabs the currently bound
/// PRO window through the same capture service the whole OCR pipeline already
/// uses, so a player with their Pokemon summary open can submit without ever
/// leaving the app or cropping anything by hand. The planned main-window
/// button - auto-detect the open Pokemon and cut a clean screenshot with no
/// mouse-drawn region - is the third, and it slots into exactly the same
/// field: it only has to produce PNG bytes, which is why SetCapturedImage
/// takes bytes rather than a path.
/// </summary>
public sealed partial class SubmitPokemonViewModel : ViewModelBase
{
    private readonly string _eventId;
    private readonly IWindowCaptureService _captureService = WindowCaptureServiceFactory.Instance;

    /// <summary>Full path of the image to submit - a file the player picked,
    /// or one this window captured into the entries folder. Null means "no
    /// screenshot", which is a valid entry (see GuildEventEntryService).</summary>
    private string? _screenshotPath;

    public string EventTitle { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string username = string.Empty;

    [ObservableProperty] private string pokemonName = string.Empty;
    [ObservableProperty] private Bitmap? pokemonSprite;
    [ObservableProperty] private Bitmap? screenshotPreview;
    [ObservableProperty] private bool hasScreenshot;

    [ObservableProperty] private string statusMessage =
        EventsSyncService.IsOnline
            ? "Attach a screenshot of your Pokemon, then submit. Your name and Pokemon go to the shared board; the screenshot stays on this machine."
            : "Attach a screenshot of your Pokemon, then submit. Entries stay on this machine for now.";

    /// <summary>Set by the View - opens the same single-Pokemon picker the
    /// compose form uses (EventPokemonPickerWindow). Returns the chosen name,
    /// "" for Clear, or null if the dialog was cancelled.</summary>
    public Func<Task<string?>>? RequestPokemonPick { get; set; }

    /// <summary>Set by the View - the OS image picker. Returns the chosen
    /// file's path, or null if cancelled.</summary>
    public Func<Task<string?>>? RequestImagePath { get; set; }

    /// <summary>Raised once an entry has been recorded - the View closes
    /// itself and the board refreshes its entry counts.</summary>
    public event Action? Submitted;

    public SubmitPokemonViewModel(string eventId, string eventTitle)
    {
        _eventId = eventId;
        EventTitle = eventTitle;

        // Same convenience as the compose form: whoever submitted last is
        // almost certainly whoever is submitting now.
        Username = EventSubmitterNameService.GetLastName();
    }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Username);

    [RelayCommand]
    private async Task ChoosePokemon()
    {
        if (RequestPokemonPick is null)
            return;

        string? chosen = await RequestPokemonPick();

        // null = cancelled outright, leave the current choice alone.
        if (chosen is not null)
            SetPokemon(chosen);
    }

    [RelayCommand]
    private void ClearPokemon() => SetPokemon(string.Empty);

    private void SetPokemon(string name)
    {
        PokemonName = name;
        PokemonSprite = string.IsNullOrWhiteSpace(name)
            ? null
            : PokemonSpriteService.GetEncounterSprite(name);
    }

    [RelayCommand]
    private async Task ChooseImage()
    {
        if (RequestImagePath is null)
            return;

        string? path = await RequestImagePath();

        if (!string.IsNullOrWhiteSpace(path))
            SetScreenshot(path, "Screenshot attached from file.");
    }

    /// <summary>Captures the bound PRO client exactly as the tracker's own
    /// detectors see it - the whole client window for now; the planned
    /// auto-detect button will hand this same field a tightly cropped
    /// Pokemon summary instead.</summary>
    [RelayCommand]
    private void CapturePro()
    {
        byte[]? png = _captureService.CaptureSelectedWindowPng();

        if (png is null || png.Length == 0)
        {
            StatusMessage = "No PRO client is bound to capture - open PRO and press Play (or Assign Client) first, or attach a saved image instead.";
            return;
        }

        string? path = GuildEventEntryService.SaveCapturedScreenshot(png);

        if (path is null)
        {
            StatusMessage = "The capture could not be saved - attach a saved image instead.";
            return;
        }

        SetScreenshot(path, "Captured from the PRO client.");
    }

    [RelayCommand]
    private void ClearScreenshot()
    {
        _screenshotPath = null;
        ScreenshotPreview = null;
        HasScreenshot = false;
        StatusMessage = "Screenshot removed - an entry without one is still allowed.";
    }

    private void SetScreenshot(string path, string message)
    {
        _screenshotPath = path;

        try
        {
            ScreenshotPreview = new Bitmap(path);
            HasScreenshot = true;
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            // A file that isn't a readable image is a failed attachment, not
            // a failed submission - say so and carry on.
            _screenshotPath = null;
            ScreenshotPreview = null;
            HasScreenshot = false;
            StatusMessage = $"That file couldn't be read as an image ({ex.Message}).";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        if (EventsSyncService.IsOnline)
        {
            // §143: the shared board. The server's copy (its id, its clock)
            // is what gets cached here, with this machine's screenshot beside
            // it. A refusal keeps the window open with everything filled in.
            try
            {
                EventEntry accepted = await EventsSyncService.SubmitEntryAsync(_eventId, Username, PokemonName);

                GuildEventEntryService.Record(accepted, _screenshotPath);
            }
            catch (EventsSyncException ex)
            {
                StatusMessage = $"Not submitted - {ex.Message}.";
                return;
            }
        }
        else
        {
            GuildEventEntryService.Submit(_eventId, Username, PokemonName, _screenshotPath);
        }

        EventSubmitterNameService.SetLastName(Username);

        Submitted?.Invoke();
    }
}
