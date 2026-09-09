using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs EventEntryDetailWindow (MIGRATION_GUIDE.md §106) - one submission,
/// opened by clicking its row in the entries table. The screenshot is the
/// point of this window; the header fields above it (who, which Pokemon,
/// when) are just enough context to know whose it is.
///
/// Built to grow: the user's stated plan is for more data to sit under the
/// image later. Anything added to EventEntry surfaces here as another
/// property and another line in the window, with no structural change - which
/// is why the image is bottom-anchored in a scroll view rather than
/// stretched to fill whatever space is left.
/// </summary>
public sealed partial class EventEntryDetailViewModel : ViewModelBase
{
    public string Username { get; }
    public string PokemonName { get; }
    public string SubmittedText { get; }
    public Bitmap? Sprite { get; }

    [ObservableProperty] private Bitmap? screenshot;
    [ObservableProperty] private bool hasScreenshot;
    [ObservableProperty] private string screenshotNote = string.Empty;

    public EventEntryDetailViewModel(EventEntry entry)
    {
        bool hasPokemon = !string.IsNullOrWhiteSpace(entry.PokemonName);

        Username = entry.Username;
        PokemonName = hasPokemon ? entry.PokemonName : "(none given)";
        Sprite = hasPokemon ? PokemonSpriteService.GetEncounterSprite(entry.PokemonName) : null;
        SubmittedText = entry.SubmittedAtUtc.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm");

        string? path = GuildEventEntryService.GetScreenshotPath(entry);

        if (path is null)
        {
            ScreenshotNote = "This entry was submitted without a screenshot.";
            return;
        }

        try
        {
            Screenshot = new Bitmap(path);
            HasScreenshot = true;
        }
        catch (Exception ex)
        {
            // The file is there but unreadable - say so plainly rather than
            // showing an empty frame.
            Log.Warning(ex, "Entry screenshot could not be loaded from {Path}", path);
            ScreenshotNote = "This entry's screenshot could not be opened.";
        }
    }
}
