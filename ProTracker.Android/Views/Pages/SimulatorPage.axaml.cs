using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;
using PokemonSim.Simulation;

namespace ProTracker.Companion.Views.Pages;

public partial class SimulatorPage : UserControl
{
    private readonly SimulatorViewModel model = new();

    public SimulatorPage()
    {
        InitializeComponent();

        // §313. No battlefield. §201's stadium is a 937x755 asset with both
        // Pokemon on painted pads, and the view model fills a DIFFERENT set
        // of sprite properties for it (ScenePlayerSprite and its size and
        // margin) than the panels use. Switching the scene off here is what
        // makes PlayerSprite and OpponentSprite the ones that get filled, so
        // this is not a cosmetic default - the page would draw two empty
        // boxes without it.
        //
        // §313: four of the five RequestXxx hooks a desktop window supplies
        // are deliberately left null - import, the custom boss editor, the
        // item picker and the switch confirmation are all dialogs. Every
        // command that needs one returns early when its hook is null, so
        // nothing here can reach a NullReferenceException; it simply does
        // nothing, and the page offers no button for it.
        model.BattleSceneEnabled = false;

        // §314: the fifth one is supplied now. On the desktop this opens
        // storage - the Pokemon the player's own cards were read into - and a
        // phone has neither the camera onto the game nor that storage, so on
        // a phone the same question is answered out of the Pokedex.
        //
        // The capacity is §275's rule and it is decided HERE rather than in
        // the builder: PickFromStorage asks for many and passes the team's
        // free slots, ReplaceFromStorage asks for one and passes the same
        // number - which is zero when the team is full, and a full team is
        // exactly when a Replace is wanted most.
        model.RequestStoragePick = (allowMultiple, freeSlots) =>
            TeamBuilderPage.AskAsync(allowMultiple ? freeSlots : 1);

        // §316: and the item picker, which is §159's own view model on a page
        // instead of in a dialog. Until now a phone-built Pokemon battled
        // bare-handed with no way to change that.
        // RequestItemPick is told nothing about WHICH slot asked - the hook
        // the desktop declares takes no argument - so the page is titled for
        // the job rather than for the holder. Inventing a holder here would
        // mean guessing one.
        model.RequestItemPick = ItemPickerPage.AskAsync;

        DataContext = model;
    }

    /// <summary>
    /// §316. Update: open the builder on the Pokemon this card already holds,
    /// and write the answer back into THE SAME slot, keeping its place in the
    /// party order and the item it was holding.
    ///
    /// This is a Click and not a [RelayCommand] on purpose. Everything it does
    /// is navigation - push a page, await it, apply the result - and the
    /// shared view model has no page stack to push onto. What it does use is
    /// the shared model's own SaveTeam, so an edit persists exactly the way a
    /// Replace does rather than through a second path that could disagree.
    /// </summary>
    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        var slot = (sender as Control)?.DataContext as SimulatorSlotViewModel;

        if (slot is null)
            return;

        ImportedPokemon? current = slot.Imported;

        if (current is null)
            return;

        // The item belongs to the card, not to the Pokemon: ImportedPokemon
        // carries no item at all, so it would be lost across the edit unless
        // it is held here and put back.
        string? heldItem = slot.SelectedItemName;

        ImportedPokemon? edited = await TeamBuilderPage.EditAsync(current);

        if (edited is null)
            return;

        slot.Apply(edited);
        slot.SelectedItemName = heldItem;

        model.SetupStatus = $"{edited.SpeciesName} updated.";

        model.SaveTeam();
    }
}
