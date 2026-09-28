using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PokemonSim.Simulation;
using ProTracker.Companion.ViewModels;

namespace ProTracker.Companion.Views.Pages;

/// <summary>
/// §314. The builder as a PAGE rather than a dialog, which is the whole
/// reason it needed writing: SimulatorViewModel asks for Pokemon through
/// RequestStoragePick, a Func returning a Task, and on the desktop that Task
/// is a modal window's ShowDialog. A phone has no modal window - it has
/// §295's page stack - so the Task is completed by this page being left.
///
/// ONE COMPLETION PATH, DELIBERATELY. The Done button only calls Nav.Back();
/// what actually hands the Pokemon over is OnDetachedFromVisualTree, which
/// fires whichever way the page goes away - Done, the title bar's Back, or
/// the hardware back button. Completing on the button instead would leave the
/// awaiting command hung forever the first time somebody used the phone's own
/// back gesture, and a hung [RelayCommand] never re-enables its button.
/// </summary>
public partial class TeamBuilderPage : UserControl
{
    private readonly TeamBuilderViewModel model = new();

    private readonly TaskCompletionSource<IReadOnlyList<ImportedPokemon>> finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TeamBuilderPage()
    {
        InitializeComponent();

        DataContext = model;
    }

    /// <summary>Push the builder and hand back whatever was built on it.
    /// <paramref name="capacity"/> is what the Simulator says it has room
    /// for: the team's free slots for Build from Pokedex, and 1 for a card's
    /// Replace.</summary>
    public static Task<IReadOnlyList<ImportedPokemon>> AskAsync(int capacity)
    {
        var page = new TeamBuilderPage();

        page.model.Capacity = capacity;

        Nav.Push(page, "Build from Pokedex");

        return page.finished.Task;
    }

    /// <summary>§316. Open on a card that already exists - the team's Update
    /// button. Comes back with the edited Pokemon, or null if nothing was
    /// saved, so Back really does leave the card alone.</summary>
    public static async Task<ImportedPokemon?> EditAsync(ImportedPokemon card)
    {
        var page = new TeamBuilderPage();

        page.model.LoadFrom(card);

        Nav.Push(page, "Update " + card.SpeciesName);

        IReadOnlyList<ImportedPokemon> result = await page.finished.Task;

        return result.Count > 0 ? result[0] : null;
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Nav.Back();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // TrySetResult, not SetResult: a page can be detached more than once
        // in a lifetime and the second one must not throw.
        finished.TrySetResult(model.Basket);
    }
}
