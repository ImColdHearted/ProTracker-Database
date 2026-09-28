using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Foot_Tracker.ViewModels;

// The shape RequestItemPick hands back. Named rather than spelt out three
// times: the tuple reads as noise at every use site, and the names Picked and
// ItemName are the whole point of it.
using ItemPick = (bool Picked, string? ItemName);

namespace ProTracker.Companion.Views.Pages;

/// <summary>
/// §316. The held item, on a phone. SimulatorViewModel asks for one through
/// RequestItemPick - a Func returning (bool Picked, string? ItemName) - and on
/// the desktop that is a modal window. Here it is a page, completed the same
/// way TeamBuilderPage is: by being LEFT.
///
/// The distinction the tuple draws matters and is kept. "No Item" is a PICK -
/// it clears the slot deliberately - while going back without touching
/// anything is not a pick at all and must leave the slot as it was. So the
/// flag is set by the view model's Confirmed event, which fires for a card and
/// for No Item and for nothing else, and the detach reports whatever the flag
/// then says.
/// </summary>
public partial class ItemPickerPage : UserControl
{
    private readonly SimulatorItemPickerViewModel model = new();

    private readonly TaskCompletionSource<ItemPick> finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool picked;

    public ItemPickerPage()
    {
        InitializeComponent();

        model.Confirmed += OnConfirmed;

        DataContext = model;
    }

    public static Task<ItemPick> AskAsync()
    {
        var page = new ItemPickerPage();

        Nav.Push(page, "Held item");

        return page.finished.Task;
    }

    private void OnConfirmed()
    {
        picked = true;

        Nav.Back();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        model.Confirmed -= OnConfirmed;

        ItemPick answer = (picked, picked ? model.SelectedName : null);

        finished.TrySetResult(answer);
    }
}
