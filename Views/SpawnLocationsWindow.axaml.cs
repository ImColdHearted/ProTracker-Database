using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §403. The Spawn Locations window's pointer work - the read-only half
/// of the Map Boxes editor's (§399/§400): a drag pans, the wheel zooms
/// about the pointer, a click on a box picks its map in the list. The
/// picture opens at 1:1 (§404 - fitted was too small to read) with the
/// first found box brought to the middle of the pane.
/// </summary>
public partial class SpawnLocationsWindow : Window
{
    private readonly SpawnLocationsViewModel vm;

    private readonly Panel surface;
    private readonly MapBoxLayer layer;
    private readonly ScrollViewer scroller;

    private bool panning;

    private Point panStart;

    private Vector panOrigin;

    private bool panMoved;

    public SpawnLocationsWindow(string species)
    {
        InitializeComponent();

        surface = this.FindControl<Panel>("MapSurface")!;
        layer = this.FindControl<MapBoxLayer>("BoxLayer")!;
        scroller = this.FindControl<ScrollViewer>("MapScroller")!;

        vm = new SpawnLocationsViewModel(species);
        DataContext = vm;

        vm.Redraw += Redraw;
        vm.BringIntoView += BringIntoView;

        scroller.AddHandler(PointerWheelChangedEvent, Scroller_PointerWheelChanged, RoutingStrategies.Tunnel);

        surface.Cursor = new Cursor(StandardCursorType.Hand);

        Opened += (_, _) =>
        {
            // §404: 1:1, with the first found box in the middle of the
            // pane - the pane has its size once laid out.
            Redraw();

            if (vm.Highlighted.Count > 0 && vm.Highlighted[0] < vm.Shapes.Count)
                BringIntoView(vm.Shapes[vm.Highlighted[0]]);
        };

        Closed += (_, _) =>
        {
            vm.Redraw -= Redraw;
            vm.BringIntoView -= BringIntoView;
            vm.Dispose();
        };
    }

    private void Redraw()
    {
        layer.Zoom = vm.Zoom;
        layer.Boxes = vm.Shapes.ToList();
        layer.Highlighted = vm.Highlighted;
        layer.FlashOn = vm.FlashOn;
        layer.SelectedIndex = -1;
        layer.Draft = null;
        layer.Redraw();
    }

    /// <summary>Scrolls so the box sits in the middle of the pane.</summary>
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
            Pan(e);
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

        panning = false;
        e.Pointer.Capture(null);

        // A click picks the map whose box is under it - or, on a box
        // already picked, or on nothing, widens back to every box.
        if (!moved)
        {
            SpawnLocationRow? hit = vm.RowAt(px, py);
            vm.SelectedRow = hit is not null && hit != vm.SelectedRow ? hit : null;
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

    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        vm.FitTo(scroller.Viewport.Width - 2, scroller.Viewport.Height - 2);
        scroller.Offset = new Vector(0, 0);
    }

    /// <summary>The map's spawn page (§397), one window per map.</summary>
    private void PageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SpawnLocationRow row })
            return;

        string map = row.Map.Map;

        WindowRegistry.ShowOrActivate(this, () => new SpawnMapWindow(map), contentKey: SpawnMap.KeyFor(map));
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
