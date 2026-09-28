using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Foot_Tracker.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §407/§410. The Maps window's pointer work - the same as the Spawn
/// Locations window's (§403): any button drags the picture, the wheel
/// zooms about the pointer, a click on a marker picks it. §410: the
/// pointer moving over the picture names the marker beneath it in a chip
/// and lights it; the pointer over a table row lights that row's markers;
/// a marker clicked on a Pokémon's card selects the row for its map. The
/// picture opens at 50% on the middle of the world; a species handed in
/// at construction (the Search's Locations button) opens on its card.
/// </summary>
public partial class MapsWindow : Window
{
    private readonly MapsViewModel vm;

    private readonly Panel surface;
    private readonly MapBoxLayer layer;
    private readonly ScrollViewer scroller;

    private bool panning;

    private Point panStart;

    private Vector panOrigin;

    private bool panMoved;

    /// <summary>§410. What the pointer is over on the picture, so a move
    /// that changes nothing does not redraw.</summary>
    private int hoveredShape = -1;

    private int hoveredPin = -1;

    public MapsWindow() : this(null)
    {
    }

    public MapsWindow(string? species)
    {
        InitializeComponent();

        surface = this.FindControl<Panel>("MapSurface")!;
        layer = this.FindControl<MapBoxLayer>("BoxLayer")!;
        scroller = this.FindControl<ScrollViewer>("MapScroller")!;

        // §410: markers, not a labelled grid.
        layer.ShowLabels = false;

        vm = new MapsViewModel();
        DataContext = vm;

        vm.Redraw += Redraw;
        vm.BringIntoView += BringIntoView;

        // §409: the boss's own window, on the first difficulty its file
        // has - unowned, as the boss list opens it.
        vm.OpenBoss = boss =>
        {
            var detail = new BossDetailWindow();
            detail.LoadBoss(boss.BossId, FirstDifficulty(boss));
            detail.Show();
        };

        scroller.AddHandler(PointerWheelChangedEvent, Scroller_PointerWheelChanged, RoutingStrategies.Tunnel);

        surface.Cursor = new Cursor(StandardCursorType.Hand);

        Opened += (_, _) =>
        {
            Redraw();

            if (species is not null)
            {
                ShowPokemon(species);
            }
            else
            {
                // The middle of the world: Johto and Kanto.
                scroller.UpdateLayout();
                scroller.Offset = new Vector(
                    vm.CanvasWidth / 2 - scroller.Viewport.Width / 2,
                    vm.CanvasHeight / 2 - scroller.Viewport.Height / 2);
            }
        };

        Closed += (_, _) =>
        {
            vm.Redraw -= Redraw;
            vm.BringIntoView -= BringIntoView;
            vm.Dispose();
        };
    }

    /// <summary>A species' card, from outside - the Search's Locations
    /// button on an already-open window.</summary>
    public void ShowPokemon(string species)
    {
        vm.Query = species;
        vm.ShowPokemon(species);
    }

    private void Redraw()
    {
        layer.Zoom = vm.Zoom;
        layer.Boxes = vm.Shapes.ToList();
        layer.Highlighted = vm.Highlighted;
        layer.Hovered = HoveredBoxes();
        layer.BoxMethods = vm.BoxMethods;
        layer.FlashOn = vm.FlashOn;
        layer.SelectedIndex = -1;
        layer.Draft = null;
        layer.Pins = vm.PinShapes.ToList();
        layer.HighlightedPins = vm.HighlightedPins;
        layer.HoveredPin = hoveredPin;
        layer.SelectedPin = -1;
        layer.Redraw();
    }

    /// <summary>The boxes lit by the pointer: the one under it on the
    /// picture, or every one of the table row it is on.</summary>
    private IReadOnlyList<int> HoveredBoxes()
    {
        if (hoveredShape >= 0)
            return new[] { hoveredShape };

        return vm.Hovered;
    }

    private static BossDifficulty FirstDifficulty(BossInfo boss)
    {
        foreach (string difficulty in boss.Difficulties)
        {
            switch (difficulty)
            {
                case "easy": return BossDifficulty.Easy;
                case "medium": return BossDifficulty.Medium;
                case "hard": return BossDifficulty.Hard;
            }
        }

        return BossDifficulty.Easy;
    }

    /// <summary>§409. The index of the boss pin under a surface point, if
    /// any - the last drawn (topmost) wins; -1 for none.</summary>
    private int PinIndexAt(Point onSurface)
    {
        for (int i = vm.PinShapes.Count - 1; i >= 0; i--)
        {
            if (MapBoxLayer.PinBounds(vm.PinShapes[i], vm.Zoom).Contains(onSurface))
                return i;
        }

        return -1;
    }

    private void BringIntoView(MapBoxShape box)
    {
        double zoom = vm.Zoom;
        double centreX = (box.X + box.Width / 2.0) * zoom;
        double centreY = (box.Y + box.Height / 2.0) * zoom;

        scroller.UpdateLayout();
        scroller.Offset = new Vector(
            centreX - scroller.Viewport.Width / 2,
            centreY - scroller.Viewport.Height / 2);
    }

    private (int x, int y) ToPixel(PointerEventArgs e)
    {
        Point point = e.GetPosition(surface);
        double zoom = vm.Zoom > 0 ? vm.Zoom : 1;

        return ((int)Math.Floor(point.X / zoom), (int)Math.Floor(point.Y / zoom));
    }

    private void Surface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!vm.HasImage || panning)
            return;

        PointerPointProperties buttons = e.GetCurrentPoint(surface).Properties;

        if (!buttons.IsLeftButtonPressed && !buttons.IsMiddleButtonPressed && !buttons.IsRightButtonPressed)
            return;

        ClearHover();

        panning = true;
        panMoved = false;
        panStart = e.GetPosition(scroller);
        panOrigin = scroller.Offset;

        e.Pointer.Capture(surface);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (panning)
        {
            Pan(e);
            return;
        }

        Hover(e);
    }

    /// <summary>§410. The marker under the pointer, lit and named by a
    /// chip; pins sit on top, so they win. Nothing under it clears both.</summary>
    private void Hover(PointerEventArgs e)
    {
        if (!vm.HasImage)
            return;

        Point onSurface = e.GetPosition(surface);
        (int px, int py) = ToPixel(e);

        int pin = PinIndexAt(onSurface);
        int shape = pin >= 0 ? -1 : vm.ShapeAt(px, py);

        string? text = null;

        if (pin >= 0 && vm.PinOwner(pin) is BossPin owner)
            text = vm.HoverTextFor(owner);
        else if (shape >= 0 && vm.ShapeOwner(shape) is SpawnMap map)
            text = vm.HoverTextFor(map);

        bool same = pin == hoveredPin && shape == hoveredShape && text == layer.HoverText;

        hoveredPin = pin;
        hoveredShape = shape;
        layer.HoverText = text;
        layer.HoverAt = onSurface;

        // The chip follows the pointer; a change of marker redraws it all.
        if (!same)
            Redraw();
        else if (text is not null)
            layer.Redraw();
    }

    private void ClearHover()
    {
        bool had = hoveredPin >= 0 || hoveredShape >= 0 || layer.HoverText is not null;

        hoveredPin = -1;
        hoveredShape = -1;
        layer.HoverText = null;

        if (had)
            Redraw();
    }

    private void Surface_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!panning)
            ClearHover();
    }

    private void Pan(PointerEventArgs e)
    {
        Point now = e.GetPosition(scroller);
        double dx = now.X - panStart.X;
        double dy = now.Y - panStart.Y;

        if (!panMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2)
            return;

        panMoved = true;
        scroller.Offset = new Vector(panOrigin.X - dx, panOrigin.Y - dy);
    }

    private void Surface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!panning)
            return;

        Pan(e);

        bool moved = panMoved;
        (int px, int py) = ToPixel(e);
        Point onSurface = e.GetPosition(surface);

        panning = false;
        e.Pointer.Capture(null);

        if (moved)
            return;

        // A click on a pin is a search for its boss (§409); on a box, a
        // pick of its map (§410: the row on a Pokémon's card when it has
        // one, the map's card otherwise). Pins sit on top, so they win.
        int pin = PinIndexAt(onSurface);

        // §419: a Pokéstop's pin opens the stop's card.
        if (pin >= 0 && vm.PinOwner(pin) is BossPin stop && PokestopCatalogService.IsPokestop(stop))
        {
            vm.Query = PokestopCatalogService.LabelFor(stop);
            vm.ShowPokestop(stop);
        }
        else if (pin >= 0 && vm.PinOwner(pin) is BossPin owner && BossCatalogService.Find(owner.BossId) is BossInfo boss)
        {
            vm.Query = boss.Name;
            vm.ShowBoss(boss);
        }
        else if (vm.MapAt(px, py) is SpawnMap map)
        {
            vm.PickMap(map);
        }
    }

    private void Surface_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        panning = false;
    }

    private void Scroller_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
            return;

        e.Handled = true;

        if (!vm.HasImage)
            return;

        Point at = e.GetPosition(scroller);
        double before = vm.Zoom;
        double pixelX = (scroller.Offset.X + at.X) / before;
        double pixelY = (scroller.Offset.Y + at.Y) / before;

        vm.StepZoom(e.Delta.Y > 0 ? +1 : -1);

        double after = vm.Zoom;

        if (after == before)
            return;

        scroller.UpdateLayout();
        scroller.Offset = new Vector(pixelX * after - at.X, pixelY * after - at.Y);
    }

    // ------------------------------------------------------------- search

    /// <summary>A pick from the completion list searches at once.</summary>
    private void QueryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox { SelectedItem: string picked } && picked.Length > 0)
        {
            vm.Query = picked;
            vm.SearchCommand.Execute(null);
        }
    }

    private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    // -------------------------------------------------------------- tables

    /// <summary>§410. Each row of the Encounter Locations table lights its
    /// map's markers while the pointer is on it. The grid makes and
    /// recycles rows as it scrolls, so the handlers go on at LoadingRow
    /// and come off at UnloadingRow.</summary>
    private void Grid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.PointerEntered += Row_PointerEntered;
        e.Row.PointerExited += Row_PointerExited;
        e.Row.PointerPressed += Row_PointerPressed;
    }

    private void Grid_UnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.PointerEntered -= Row_PointerEntered;
        e.Row.PointerExited -= Row_PointerExited;
        e.Row.PointerPressed -= Row_PointerPressed;
    }

    /// <summary>A row clicked brings its map into view even when it was
    /// the selected row already - the selection alone would not fire twice.</summary>
    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is DataGridRow { DataContext: MapsLocationRow row } && ReferenceEquals(vm.SelectedLocation, row))
            vm.FocusMap(row.Map);
    }

    private void Row_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is DataGridRow { DataContext: MapsLocationRow row })
            vm.HoverRow(row);
    }

    private void Row_PointerExited(object? sender, PointerEventArgs e)
    {
        vm.HoverRow(null);
    }

    /// <summary>§419. A stop on the every-stop card: its own card, its pin
    /// lit and brought into view.</summary>
    private void StopRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MapsStopRow row })
        {
            vm.Query = PokestopCatalogService.LabelFor(row.Pin);
            vm.ShowPokestop(row.Pin);
        }
    }

    /// <summary>A species picked on a map's card: its own card. Posted,
    /// so the grid finishes its selection before its rows are replaced.</summary>
    private void SpeciesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: MapsLocationRow row })
        {
            string species = row.Species;
            Dispatcher.UIThread.Post(() =>
            {
                vm.Query = species;
                vm.ShowPokemon(species);
            });
        }
    }
}
