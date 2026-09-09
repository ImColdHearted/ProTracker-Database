using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Foot_Tracker.Services.Simulator;
using Foot_Tracker.ViewModels;
using PokemonSim.Simulation;

namespace Foot_Tracker.Views;

/// <summary>
/// §154/§157/§158/§162. The Simulator shell - constructs its own
/// SimulatorViewModel (the AdminConsoleWindow pattern) and keeps itself to
/// wiring dialogs, auto-scrolling the battle log, and disposing the view
/// model when the window closes so a running battle is cancelled cleanly.
/// §162 replaced the manual team builder's Pokemon picker with the
/// screenshot importer and the Pokemon storage - teams are built from
/// what the player actually owns in game. Opened from MainWindow's
/// Simulator menu item, which keeps one instance and re-activates it
/// instead of stacking duplicates. No Admin Login involved anywhere, and
/// closing this window touches nothing but this window: hunting, capture,
/// the Admin Console and the selected client all live their own lives.
/// </summary>
public partial class SimulatorWindow : Window
{
    // §198: drag-to-reorder state. The card being dragged, where it
    // started (so Escape and a lost capture can put it back), and the
    // press point the movement threshold is measured from.
    readonly ItemsControl teamSlotList;

    SimulatorSlotViewModel? dragSlot;
    int dragFromIndex = -1;
    Point dragOrigin;
    bool dragging;

    public SimulatorWindow()
    {
        InitializeComponent();

        var vm = new SimulatorViewModel();
        DataContext = vm;

        // §162: Import Pokemon opens the screenshot importer - capture the
        // PRO client or browse a screenshot, scan the summary card, add.
        // §198: the dialog stays open across adds now, so what comes back
        // is the whole sitting rather than one Pokemon. Added is read
        // whatever the dialog result, because closing it with the X or
        // Escape must not throw away Pokemon that are already in storage.
        vm.RequestImport = async freeSlots =>
        {
            var dialogVm = new SimulatorImportViewModel { TeamSpaceRemaining = freeSlots };
            dialogVm.Begin();

            var dialog = new SimulatorImportWindow { DataContext = dialogVm };

            await dialog.ShowDialog<bool?>(this);

            IReadOnlyList<ImportedPokemon> added = dialogVm.Added.ToArray();

            dialogVm.Dispose();

            return added;
        };

        // §162: From Storage re-offers everything imported before.
        // §203: the picker stays open across clicks now, so what comes
        // back is everything chosen while it was up. Picked is read
        // whatever the dialog result, because closing it with the X must
        // not throw away picks already made. allowMultiple is false for
        // Replace from Storage, which wants exactly one and gets a window
        // that closes on the first click.
        vm.RequestStoragePick = async allowMultiple =>
        {
            var dialogVm = new SimulatorStorageViewModel { AllowMultiple = allowMultiple };
            var dialog = new SimulatorStorageWindow { DataContext = dialogVm };

            await dialog.ShowDialog<bool?>(this);

            return dialogVm.Picked.Select(p => p.ToImported()).ToArray();
        };

        // §173: New/Edit open the custom opponent editor. A saved file
        // closes the dialog with true, which reloads the list.
        vm.RequestCustomBossEdit = async entry =>
        {
            var dialogVm = new CustomBossViewModel();
            dialogVm.Load(entry);

            var dialog = new CustomBossWindow { DataContext = dialogVm };

            return await dialog.ShowDialog<bool?>(this) == true;
        };

        // §159: the slot's item button opens the held-item picker.
        vm.RequestItemPick = async () =>
        {
            var dialogVm = new SimulatorItemPickerViewModel();
            var dialog = new SimulatorItemPickerWindow { DataContext = dialogVm };

            bool confirmed = await dialog.ShowDialog<bool?>(this) == true;

            return (confirmed, dialogVm.SelectedName);
        };

        // §160: clicking a teammate in the battle row asks first, showing
        // who would come in and what it holds.
        vm.RequestSwitchConfirm = async (slot, sendIn) =>
        {
            var dialog = new SimulatorSwitchConfirmWindow
            {
                DataContext = new SimulatorSwitchConfirmViewModel
                {
                    Slot = slot,
                    Prompt = sendIn ? $"Send {slot.Species} in?" : $"Switch to {slot.Species}?",
                    ActionLabel = sendIn ? "Send In" : "Switch In"
                }
            };

            return await dialog.ShowDialog<bool?>(this) == true;
        };

        // §198: dragging a team card up or down reorders the party, and
        // the top card is the lead. This is a plain pointer drag rather
        // than the platform drag-and-drop stack: it never leaves this
        // list, and the pointer path behaves the same on X11 as it does on
        // Windows, which matters here - the tracker has Linux users.
        teamSlotList = this.FindControl<ItemsControl>("TeamSlotList")!;

        teamSlotList.PointerPressed += TeamList_PointerPressed;
        teamSlotList.PointerMoved += TeamList_PointerMoved;
        teamSlotList.PointerReleased += (_, _) => FinishDrag();

        // A lost capture ends the drag where it stands rather than undoing
        // it: releasing the pointer also drops the capture, so restoring
        // here would be a race with the release that sometimes threw the
        // drop away. Escape is the deliberate undo.
        teamSlotList.PointerCaptureLost += (_, _) => FinishDrag();

        // Escape puts it back. Tunnelled from the window because the list
        // itself never takes focus, and guarded on a drag being in
        // progress so it cannot interfere with anything else.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        var logList = this.FindControl<ListBox>("LogList")!;

        vm.LogLines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && vm.LogLines.Count > 0)
                logList.ScrollIntoView(vm.LogLines.Count - 1);
        };

        Closed += (_, _) => vm.Dispose();
    }

    // ---- §198: drag a team card to reorder the party ----

    void TeamList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SimulatorViewModel vm)
            return;

        if (!e.GetCurrentPoint(teamSlotList).Properties.IsLeftButtonPressed)
            return;

        // A press that landed on one of the card's own buttons belongs to
        // that button. Avalonia's Button marks the event handled, so this
        // is belt and braces - but the item and Remove buttons are close
        // enough together that a stray drag would be expensive.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>() != null)
            return;

        Point at = e.GetPosition(teamSlotList);
        int index = IndexAt(at.Y, nearest: false);

        if (index < 0 || index >= vm.TeamSlots.Count)
            return;

        dragSlot = vm.TeamSlots[index];
        dragFromIndex = index;
        dragOrigin = at;
        dragging = false;
    }

    void TeamList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragSlot == null || DataContext is not SimulatorViewModel vm)
            return;

        // The button can have been let go somewhere this list never heard
        // about - a press that released outside the window, say. Without
        // this the next stroll of the mouse across the list would pick the
        // card up again with no button held.
        if (!e.GetCurrentPoint(teamSlotList).Properties.IsLeftButtonPressed)
        {
            FinishDrag();
            return;
        }

        Point at = e.GetPosition(teamSlotList);

        if (!dragging)
        {
            // A few pixels of slack, so a click on a card stays a click.
            if (Math.Abs(at.Y - dragOrigin.Y) < 6 && Math.Abs(at.X - dragOrigin.X) < 6)
                return;

            dragging = true;
            e.Pointer.Capture(teamSlotList);
            teamSlotList.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }

        int target = IndexAt(at.Y, nearest: true);

        // The list reorders live under the pointer, which is the whole of
        // the feedback - no ghost card, no insertion line, and nothing
        // that has to be unwound if the window closes mid-drag.
        if (target >= 0 && target != vm.TeamSlots.IndexOf(dragSlot))
            vm.MoveSlot(dragSlot, target);
    }

    void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (dragSlot == null || e.Key != Key.Escape)
            return;

        CancelDrag();
        e.Handled = true;
    }

    /// <summary>Leave the card where the drag put it.</summary>
    void FinishDrag() => EndDrag(putBack: false);

    /// <summary>Put the card back where the drag started. Escape only -
    /// see the capture-lost wiring for why that is the one undo.</summary>
    void CancelDrag() => EndDrag(putBack: true);

    void EndDrag(bool putBack)
    {
        SimulatorSlotViewModel? slot = dragSlot;
        int from = dragFromIndex;
        bool moved = dragging;

        dragSlot = null;
        dragFromIndex = -1;
        dragging = false;
        teamSlotList.Cursor = Cursor.Default;

        if (putBack && moved && slot != null && from >= 0 && DataContext is SimulatorViewModel vm)
            vm.MoveSlot(slot, from);
    }

    /// <summary>Which card a Y coordinate falls on, measured from the
    /// containers' own boxes. Hit-testing whatever visual is under the
    /// pointer would land on a sprite or a line of text instead.
    ///
    /// With nearest, a coordinate in the gap between two cards - or past
    /// either end of the list - answers with the closest card, which is
    /// what a drag in flight wants. Without it, only a coordinate actually
    /// inside a card answers, so pressing in the gap between two of them
    /// does not pick either one up.</summary>
    int IndexAt(double y, bool nearest)
    {
        if (DataContext is not SimulatorViewModel vm)
            return -1;

        int nearestIndex = -1;
        double nearestGap = double.MaxValue;

        for (int i = 0; i < vm.TeamSlots.Count; i++)
        {
            Control? container = teamSlotList.ContainerFromIndex(i);

            if (container == null)
                continue;

            Point? corner = container.TranslatePoint(new Point(0, 0), teamSlotList);

            if (corner == null)
                continue;

            double top = corner.Value.Y;
            double bottom = top + container.Bounds.Height;

            if (y >= top && y < bottom)
                return i;

            if (!nearest)
                continue;

            double gap = y < top ? top - y : y - bottom;

            if (gap < nearestGap)
            {
                nearestGap = gap;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }
}
