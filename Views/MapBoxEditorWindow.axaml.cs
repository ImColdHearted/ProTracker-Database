using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §399. The Map Boxes editor's pointer work: dragging a box out over the
/// region picture, picking a saved box by clicking it, panning the
/// picture, and zooming with the wheel about the pointer. Everything the
/// pointer reports is turned into picture pixels here (the surface is the
/// picture at the current zoom, so a screen point divided by the zoom is
/// a picture pixel) and handed to the view model, which owns what is
/// shown and what is sent.
///
/// §400. The wheel alone zooms - the app's scrollbars are invisible
/// (§208), so a wheel that scrolled left a zoomed-in picture with no way
/// to move about it - and moving about is a drag: the left button in Pan
/// mode, the middle or right button in either mode. The drag moves the
/// ScrollViewer's offset by the pointer's travel in the viewport, so the
/// picture follows the pointer exactly.
///
/// The layer over the picture is redrawn on the view model's word: one
/// Redraw after each change, with the zoom, every saved box, the draft and
/// the selection copied across - simpler than binding four properties on a
/// control the view model already drives.
/// </summary>
public partial class MapBoxEditorWindow : Window
{
    private readonly MapBoxEditorViewModel vm;

    private readonly Panel surface;
    private readonly MapBoxLayer layer;
    private readonly ScrollViewer scroller;

    /// <summary>Whether the left button is down on the surface, and where
    /// it went down, in picture pixels.</summary>
    private bool pressed;

    private int pressX;

    private int pressY;

    /// <summary>Whether the pointer moved since it went down - a press
    /// that never moved is a click, which selects rather than draws.</summary>
    private bool dragged;

    /// <summary>§400. Whether a button is down to pan, where it went down
    /// in the viewport, the scroll offset then, and whether the picture
    /// has moved since - a pan that never moved is a click, which selects
    /// the box under it just as a click in Draw mode does.</summary>
    private bool panning;

    private Point panStart;

    private Vector panOrigin;

    private bool panMoved;

    private static readonly Cursor DrawCursor = new(StandardCursorType.Cross);

    private static readonly Cursor PanCursor = new(StandardCursorType.Hand);

    public MapBoxEditorWindow()
    {
        InitializeComponent();

        surface = this.FindControl<Panel>("MapSurface")!;
        layer = this.FindControl<MapBoxLayer>("BoxLayer")!;
        scroller = this.FindControl<ScrollViewer>("MapScroller")!;

        vm = new MapBoxEditorViewModel();
        DataContext = vm;

        // The events-server sign-in, the same window the console uses.
        vm.RequestAdminSignIn = () => AdminTokenWindow.ShowAsync(this);

        vm.Redraw += Redraw;

        // Tunnelling, so the wheel reaches this before the ScrollViewer
        // scrolls on it (§400: every wheel turn zooms; nothing scrolls).
        scroller.AddHandler(PointerWheelChangedEvent, Scroller_PointerWheelChanged, RoutingStrategies.Tunnel);

        // §400: the cursor says which mode the left button is in.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MapBoxEditorViewModel.DrawMode))
                ApplyCursor();
        };

        ApplyCursor();

        Opened += (_, _) => Redraw();

        Closed += (_, _) =>
        {
            vm.Redraw -= Redraw;
            vm.Dispose();
        };
    }

    private void ApplyCursor() => surface.Cursor = vm.DrawMode ? DrawCursor : PanCursor;

    /// <summary>Copies what the view model shows onto the layer and has it
    /// paint.</summary>
    private void Redraw()
    {
        layer.Zoom = vm.Zoom;
        layer.Boxes = vm.Shapes.ToList();

        layer.Draft = vm.Draft is { IsValid: true } draft
            ? new MapBoxShape(string.Empty, draft.X, draft.Y, draft.Width, draft.Height)
            : null;

        layer.SelectedIndex = vm.SelectedBox is null ? -1 : vm.Boxes.IndexOf(vm.SelectedBox);

        // §409: the published pins, and the one being placed after them,
        // drawn as selected so it stands out.
        var pins = vm.PinShapes.ToList();
        int selectedPin = -1;

        if (vm.DraftPin is { IsValid: true } draftPin)
        {
            pins.Add(vm.PlacePin(draftPin));
            selectedPin = pins.Count - 1;
        }
        else if (vm.SelectedPin is BossPin picked)
        {
            for (int i = 0; i < vm.PinShapes.Count; i++)
            {
                if (ReferenceEquals(vm.PinOwner(i), picked))
                    selectedPin = i;
            }
        }

        layer.Pins = pins;
        layer.SelectedPin = selectedPin;

        layer.Redraw();
    }

    /// <summary>§409. The published pin under a screen point, if any - the
    /// last drawn (topmost) wins.</summary>
    private BossPin? PinAt(Point onSurface)
    {
        for (int i = vm.PinShapes.Count - 1; i >= 0; i--)
        {
            if (MapBoxLayer.PinBounds(vm.PinShapes[i], vm.Zoom).Contains(onSurface))
                return vm.PinOwner(i);
        }

        return null;
    }

    /// <summary>The picture pixel under the pointer. The surface is the
    /// picture at the current zoom, so it is the point divided by the zoom;
    /// a point past the picture's edge is clamped by the view model.</summary>
    private (int x, int y) ToPixel(PointerEventArgs e)
    {
        Point point = e.GetPosition(surface);
        double zoom = vm.Zoom > 0 ? vm.Zoom : 1;

        return ((int)Math.Floor(point.X / zoom), (int)Math.Floor(point.Y / zoom));
    }

    private void Surface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A second button while one is down changes nothing.
        if (!vm.HasImage || pressed || panning)
            return;

        PointerPointProperties buttons = e.GetCurrentPoint(surface).Properties;

        // §400: the left button draws in Draw mode and pans in Pan mode
        // (§409: and in Boss mode, where a click that never moved places
        // or picks a pin); the middle and right buttons pan in any mode.
        bool pan = buttons.IsLeftButtonPressed ? vm.PanMode || vm.BossMode || vm.PokestopMode : buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed;

        if (!pan && !buttons.IsLeftButtonPressed)
            return;

        if (pan)
        {
            panning = true;
            panMoved = false;
            panStart = e.GetPosition(scroller);
            panOrigin = scroller.Offset;
        }
        else
        {
            (pressX, pressY) = ToPixel(e);
            pressed = true;
            dragged = false;
        }

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

        if (!pressed)
            return;

        (int x, int y) = ToPixel(e);

        if (!dragged && x == pressX && y == pressY)
            return;

        dragged = true;
        vm.SetDraftFromCorners(pressX, pressY, x, y);
    }

    /// <summary>§400. Moves the picture by the pointer's travel since the
    /// pan began - measured in the viewport, which does not move, rather
    /// than on the surface, which does. The ScrollViewer clamps the
    /// offset to the picture's edges.</summary>
    private void Pan(PointerEventArgs e)
    {
        Point now = e.GetPosition(scroller);
        double dx = now.X - panStart.X;
        double dy = now.Y - panStart.Y;

        // §420: a few pixels of hand-shake on a click is not a pan.
        if (!panMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
            return;

        panMoved = true;
        scroller.Offset = new Vector(panOrigin.X - dx, panOrigin.Y - dy);
    }

    private void Surface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (panning)
        {
            Pan(e);

            bool moved = panMoved;
            (int px, int py) = ToPixel(e);
            Point onSurface = e.GetPosition(surface);

            panning = false;
            e.Pointer.Capture(null);

            if (moved)
                return;

            // A pan that never moved is a click. In Boss mode it picks the
            // pin under it, or places the chosen boss there; otherwise it
            // selects the box under it, as in Draw mode (§417: naming it the
            // link row's main map only while that is empty).
            // §419: Pokéstop mode places and picks pins as Boss mode does.
            if (vm.BossMode || vm.PokestopMode)
            {
                if (PinAt(onSurface) is BossPin pin)
                {
                    vm.DraftPin = null;
                    vm.SelectedPin = pin;
                }
                else
                {
                    vm.SelectedPin = null;
                    vm.SetDraftPin(px, py);
                }
            }
            else
            {
                vm.PickBox(vm.BoxAt(px, py));
            }

            return;
        }

        if (!pressed)
            return;

        (int x, int y) = ToPixel(e);
        bool wasDrag = dragged;

        pressed = false;
        dragged = false;
        e.Pointer.Capture(null);

        if (wasDrag)
        {
            vm.SetDraftFromCorners(pressX, pressY, x, y);
            return;
        }

        // A click: pick the saved box under it (the smallest, when they
        // overlap), or nothing, which clears the selection.
        vm.PickBox(vm.BoxAt(x, y));
    }

    private void Surface_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        pressed = false;
        dragged = false;
        panning = false;
    }

    /// <summary>The wheel steps the zoom (§400: on its own - no modifier),
    /// keeping the picture pixel under the pointer where it is on screen.
    /// A sideways turn is left to the ScrollViewer.</summary>
    private void Scroller_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
            return;

        e.Handled = true;

        if (!vm.HasImage)
            return;

        Point at = e.GetPosition(scroller);
        double before = vm.Zoom;

        // The picture pixel under the pointer, from the scroll offset and
        // the pointer's place in the viewport.
        double pixelX = (scroller.Offset.X + at.X) / before;
        double pixelY = (scroller.Offset.Y + at.Y) / before;

        vm.StepZoom(e.Delta.Y > 0 ? +1 : -1);

        double after = vm.Zoom;

        if (after == before)
            return;

        // The surface has its new size once laid out; the offset is clamped
        // to the extent, so lay out first.
        scroller.UpdateLayout();
        scroller.Offset = new Vector(pixelX * after - at.X, pixelY * after - at.Y);
    }

    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        vm.FitTo(scroller.Viewport.Width - 2, scroller.Viewport.Height - 2);
        scroller.Offset = new Vector(0, 0);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
