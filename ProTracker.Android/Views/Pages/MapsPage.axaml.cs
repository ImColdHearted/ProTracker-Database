using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Threading;
using Foot_Tracker.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

/// <summary>
/// §423. The Map Explorer's touch work, where MapsWindow.axaml.cs does the
/// pointer work. Same view model; what differs is what a finger can do:
///
/// PAN is the ScrollViewer's own touch scrolling, with its inertia. The
/// desktop pans by hand because a mouse drag does not scroll a
/// ScrollViewer; a finger does, so nothing is written for it. The one
/// consequence: the moment a drag passes the scroll threshold the
/// ScrollViewer takes the pointer, and this page's press is cancelled -
/// which is exactly what makes a tap a tap and a drag a drag.
///
/// TAP is a press and a release within a few pixels of each other. A pin
/// tapped opens its boss or its stop; a box tapped picks its map (the row
/// on a Pokémon's card, or the map's card) - as a click does on the desktop.
///
/// PINCH zooms about the fingers through Avalonia's PinchGestureRecognizer,
/// continuously rather than in the desktop's steps, clamped to the same
/// range the steps span. The +/- buttons step as the desktop's do.
///
/// HOVER does not exist. The layer's hover chip is never set, and a row
/// lights its markers when tapped rather than when pointed at.
///
/// One page, kept by MainView across tab switches (a static), so one view
/// model listens to the spawn and pin services rather than one per visit.
/// </summary>
public partial class MapsPage : UserControl
{
    private const double TapSlop = 12;

    private readonly MapsViewModel vm;

    private readonly Panel surface;
    private readonly MapBoxLayer layer;
    private readonly ScrollViewer scroller;

    private bool pressed;
    private Point pressedAt;

    private bool pinching;
    private double pinchStartZoom;

    private bool centred;

    public MapsPage()
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

        // The boss's own page, on the first difficulty its file has - as
        // the boss list opens it.
        vm.OpenBoss = boss => Nav.Push(new BossDetailPage(boss.BossId, FirstDifficulty(boss)), "Boss");

        // Avalonia 12: the gesture events live on InputElement itself
        // (the Gestures class is no longer public).
        surface.GestureRecognizers.Add(new PinchGestureRecognizer());
        surface.Pinch += Surface_Pinch;
        surface.PinchEnded += Surface_PinchEnded;
        surface.AddHandler(PointerCaptureLostEvent, Surface_PointerCaptureLost);

        AttachedToVisualTree += (_, _) =>
        {
            Redraw();

            if (centred)
                return;

            centred = true;

            // The middle of the world, once the scroller has a size - a
            // page attached this instant has none yet.
            Dispatcher.UIThread.Post(() =>
            {
                scroller.UpdateLayout();
                scroller.Offset = new Vector(
                    vm.CanvasWidth / 2 - scroller.Viewport.Width / 2,
                    vm.CanvasHeight / 2 - scroller.Viewport.Height / 2);
            }, DispatcherPriority.Loaded);
        };
    }

    /// <summary>A species' card, from outside - a Search hit.</summary>
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
        layer.Hovered = vm.Hovered;
        layer.BoxMethods = vm.BoxMethods;
        layer.FlashOn = vm.FlashOn;
        layer.SelectedIndex = -1;
        layer.Draft = null;
        layer.Pins = vm.PinShapes.ToList();
        layer.HighlightedPins = vm.HighlightedPins;
        layer.HoveredPin = -1;
        layer.SelectedPin = -1;
        layer.HoverText = null;
        layer.Redraw();
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

    // ---------------------------------------------------------------- tap

    private void Surface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!vm.HasImage || pinching)
            return;

        pressed = true;
        pressedAt = e.GetPosition(surface);
    }

    private void Surface_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // The ScrollViewer took the finger: it is a drag, not a tap.
        pressed = false;
    }

    private void Surface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!pressed || pinching)
        {
            pressed = false;
            return;
        }

        pressed = false;

        Point onSurface = e.GetPosition(surface);

        if (Math.Abs(onSurface.X - pressedAt.X) > TapSlop || Math.Abs(onSurface.Y - pressedAt.Y) > TapSlop)
            return;

        double zoom = vm.Zoom > 0 ? vm.Zoom : 1;
        int px = (int)Math.Floor(onSurface.X / zoom);
        int py = (int)Math.Floor(onSurface.Y / zoom);

        // Pins sit on top, so they win - a stop's opens its card, a boss's
        // its card; a box picks its map.
        int pin = PinIndexAt(onSurface);

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

    // -------------------------------------------------------------- pinch

    private void Surface_Pinch(object? sender, PinchEventArgs e)
    {
        if (!vm.HasImage)
            return;

        if (!pinching)
        {
            pinching = true;
            pressed = false;
            pinchStartZoom = vm.Zoom;
        }

        double min = MapsViewModel.ZoomSteps[0];
        double max = MapsViewModel.ZoomSteps[^1];
        double target = Math.Clamp(pinchStartZoom * e.Scale, min, max);

        ZoomAbout(e.ScaleOrigin, target);

        e.Handled = true;
    }

    private void Surface_PinchEnded(object? sender, PinchEndedEventArgs e)
    {
        pinching = false;
        e.Handled = true;
    }

    /// <summary>Zooms so that the picture pixel under a surface point stays
    /// under the same screen point - the wheel's rule on the desktop.</summary>
    private void ZoomAbout(Point onSurface, double target)
    {
        double before = vm.Zoom;

        if (before <= 0 || Math.Abs(target - before) < 0.0001)
            return;

        double screenX = onSurface.X - scroller.Offset.X;
        double screenY = onSurface.Y - scroller.Offset.Y;
        double pixelX = onSurface.X / before;
        double pixelY = onSurface.Y / before;

        vm.Zoom = target;

        scroller.UpdateLayout();
        scroller.Offset = new Vector(pixelX * target - screenX, pixelY * target - screenY);
    }

    // ------------------------------------------------------------- search

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

    // --------------------------------------------------------------- rows

    /// <summary>A location row tapped: selected, its map's markers lit and
    /// brought into view - and brought into view again when it was the
    /// selected row already, since the selection alone would not fire twice.</summary>
    private void LocationRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: MapsLocationRow row })
            return;

        if (ReferenceEquals(vm.SelectedLocation, row))
            vm.FocusMap(row.Map);
        else
            vm.SelectedLocation = row;
    }

    /// <summary>A species tapped on a map's card: its own card.</summary>
    private void SpeciesRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MapsLocationRow row })
        {
            string species = row.Species;

            Dispatcher.UIThread.Post(() =>
            {
                vm.Query = species;
                vm.ShowPokemon(species);
            });
        }
    }

    /// <summary>A stop on the every-stop card: its own card, its pin lit.</summary>
    private void StopRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MapsStopRow row })
        {
            vm.Query = PokestopCatalogService.LabelFor(row.Pin);
            vm.ShowPokestop(row.Pin);
        }
    }
}
