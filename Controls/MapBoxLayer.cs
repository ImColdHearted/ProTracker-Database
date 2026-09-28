using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Foot_Tracker.Models;

namespace Foot_Tracker.Controls;

/// <summary>§399. One box the layer draws: where it is in the picture's
/// own pixels, and what to call it. §412: and which spot it belongs to,
/// when that is not simply its label - the Maps window names a shared spot
/// by the words its maps have in common, which two spots could share; the
/// group keeps them apart. Null groups by the label.</summary>
public sealed record MapBoxShape(string Label, int X, int Y, int Width, int Height, string? Group = null)
{
    /// <summary>What the layer lights together.</summary>
    public string GroupKey => Group ?? Label;
}

/// <summary>§409. One boss pin the layer draws: where the boss stands, in
/// the picture's own pixels, its picture (null draws a disc with the
/// name's first letter) and its name. §419: and whether it is a Pokéstop,
/// which wears a blue ring rather than a boss's gold.</summary>
public sealed record MapPinShape(string Label, int X, int Y, Bitmap? Image, bool Pokestop = false);

/// <summary>
/// §399. The boxes over a region picture in the Map Boxes editor: every
/// saved box, the one being drawn, and which one is selected - drawn with
/// Avalonia's own DrawingContext over an Image that shows the picture at
/// the same zoom, so a box is always exactly where it is in the picture.
///
/// Every box is one screen pixel thick whatever the zoom, on the pixel
/// grid (hence the half-pixel nudge - a one-pixel pen centred on an
/// integer coordinate straddles two pixels and blurs), and outlines the
/// picture pixels it covers: at 4x a box 22 pixels wide is drawn 88 wide.
/// Labels sit just above their box, in the box's colour, so a map can be
/// told from its neighbours at a glance.
///
/// Plain properties and an explicit Redraw rather than styled ones: the
/// window that owns this control also owns every change to what it shows,
/// and one call after each change is simpler than four property
/// registrations. IsHitTestVisible is false; the surface beneath takes the
/// pointer.
///
/// §410/§411. The Maps window draws the same boxes as markers rather than
/// as a labelled grid: with <see cref="ShowLabels"/> off, a box is not
/// drawn at all until it is found or hovered, and a map drawn in several
/// boxes is then shown as one shape; a found map carries the method glyphs the window gives it
/// (<see cref="BoxMethods"/>); a hovered box or pin gets a lighter fill
/// and a chip by the pointer (<see cref="HoverText"/>). Boss pins wear a
/// gold ring at all times so they read as bosses among the boxes. The
/// editor leaves every one of these at its default and looks as before.
/// </summary>
public sealed class MapBoxLayer : Control
{
    private static readonly IBrush SavedBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xE0, 0x50));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x30));
    private static readonly IBrush DraftBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xC8, 0xFF));
    private static readonly IBrush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0xFF));

    // §404: a found box is filled, not just outlined - a one-pixel border
    // on a busy picture is easy to miss; a tinted patch is not. The flash
    // colour is the stronger of the two.
    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xE0, 0x30));
    private static readonly IBrush FlashFill = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x40, 0xFF));
    private static readonly IBrush LabelShadow = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));

    private static readonly Typeface LabelTypeface = Typeface.Default;
    private const double LabelSize = 11;

    public MapBoxLayer()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Screen pixels per picture pixel.</summary>
    public double Zoom { get; set; } = 1;

    public IReadOnlyList<MapBoxShape> Boxes { get; set; } = Array.Empty<MapBoxShape>();

    /// <summary>The box being dragged out, in picture pixels; null for none.</summary>
    public MapBoxShape? Draft { get; set; }

    /// <summary>The index in <see cref="Boxes"/> of the box to draw
    /// highlighted; -1 for none. §402: an index, not a label, since a map
    /// may have several boxes with the same label.</summary>
    public int SelectedIndex { get; set; } = -1;

    /// <summary>§403. The boxes to draw as found - the Spawn Locations
    /// window's answer to "where does it spawn": filled (§404) in the
    /// selected colour, and while <see cref="FlashOn"/> is set, in a
    /// flash colour instead, which the window toggles on a timer so the
    /// eye is led to them.</summary>
    public IReadOnlyList<int> Highlighted { get; set; } = Array.Empty<int>();

    public bool FlashOn { get; set; }

    /// <summary>§410. Whether every box carries its name (the editor) or
    /// only a found, hovered or selected one (the Maps window).</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>§410. The boxes under the pointer, or under the table row
    /// the pointer is on: a lighter fill and the name.</summary>
    public IReadOnlyList<int> Hovered { get; set; } = Array.Empty<int>();

    /// <summary>§410. The pin under the pointer; -1 for none.</summary>
    public int HoveredPin { get; set; } = -1;

    /// <summary>§410. The method glyphs to draw on a found box, by box
    /// index - the ways the shown species is met on that map. A box with
    /// no entry draws none.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<EncounterMethod>> BoxMethods { get; set; } =
        new Dictionary<int, IReadOnlyList<EncounterMethod>>();

    /// <summary>§410. A chip drawn by the pointer - the hovered map's name
    /// and how the species is met there, or the hovered boss - and where
    /// the pointer is, in this control's coordinates. Null draws none.</summary>
    public string? HoverText { get; set; }

    public Point HoverAt { get; set; }

    /// <summary>§409. The boss pins, drawn after the boxes so they sit on
    /// top: each a picture of a fixed screen size (the same at every
    /// zoom, as a map pin is) standing on its point, its name beneath.</summary>
    public IReadOnlyList<MapPinShape> Pins { get; set; } = Array.Empty<MapPinShape>();

    /// <summary>The indices in <see cref="Pins"/> drawn as found - a halo
    /// in the selected colour, the flash colour while the flag is on.</summary>
    public IReadOnlyList<int> HighlightedPins { get; set; } = Array.Empty<int>();

    /// <summary>The index in <see cref="Pins"/> of the pin being dragged
    /// out or picked in the editor; -1 for none.</summary>
    public int SelectedPin { get; set; } = -1;

    /// <summary>Screen pixels a pin's picture is fitted into, and the
    /// disc drawn for a boss with no picture.</summary>
    public const double PinSize = 40;

    private const double PinDiscRadius = 11;

    private static readonly IBrush PinDisc = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30));

    // §410/§411: the marker look of the Maps window - nothing for a box
    // that is neither found nor hovered, a soft fill for a hovered one, a
    // gold ring on every boss pin, and the chip by the pointer.
    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly IBrush HoverFill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush PinRing = new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x40));

    // §419: a Pokéstop's ring is blue, so a stop never reads as a boss.
    private static readonly IBrush StopRing = new SolidColorBrush(Color.FromRgb(0x40, 0xB8, 0xFF));
    private static readonly IBrush ChipFill = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x1A, 0x2E));
    private static readonly IBrush ChipInk = new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xF5));

    /// <summary>Screen pixels across a method glyph on a found box.</summary>
    private const double GlyphSize = 18;
    private static readonly IBrush PinHalo = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xE0, 0x30));
    private static readonly IBrush PinFlashHalo = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x40, 0xFF));

    /// <summary>The screen rectangle a pin's picture covers at a zoom:
    /// fitted into <see cref="PinSize"/> square, standing on its point.
    /// The window uses the same rectangle to hit-test a click.</summary>
    public static Rect PinBounds(MapPinShape pin, double zoom)
    {
        double cx = pin.X * zoom;
        double baseline = pin.Y * zoom;

        double width = PinSize;
        double height = PinSize;

        if (pin.Image is Bitmap image && image.PixelSize.Width > 0 && image.PixelSize.Height > 0)
        {
            double scale = Math.Min(PinSize / image.PixelSize.Width, PinSize / image.PixelSize.Height);
            width = image.PixelSize.Width * scale;
            height = image.PixelSize.Height * scale;
        }
        else
        {
            width = height = PinDiscRadius * 2;
        }

        return new Rect(cx - width / 2, baseline - height, width, height);
    }

    public void Redraw() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double zoom = Zoom;

        if (zoom <= 0)
            return;

        if (ShowLabels)
        {
            // The editor: every box outlined and named, one by one.
            for (int i = 0; i < Boxes.Count; i++)
            {
                bool found = Highlighted.Contains(i);
                bool selected = i == SelectedIndex;

                IBrush brush = found
                    ? FlashOn ? FlashBrush : SelectedBrush
                    : selected ? SelectedBrush : SavedBrush;

                IBrush? fill = found ? FlashOn ? FlashFill : HighlightFill : null;

                DrawBox(context, Boxes[i], brush, zoom, withLabel: true, fill);
            }
        }
        else
        {
            // §411: the Maps window. A box that is neither found nor hovered
            // is not drawn at all - the picture is the game's own - and a
            // map drawn in several pieces is shown as one shape: the pieces
            // united, one outline round the outside, one name, one row of
            // glyphs. Hovering any piece hovers the map.
            DrawMaps(context, zoom);
        }

        if (Draft is MapBoxShape draft)
            DrawBox(context, draft, DraftBrush, zoom, withLabel: false);

        for (int i = 0; i < Pins.Count; i++)
        {
            bool found = HighlightedPins.Contains(i);
            bool hovered = i == HoveredPin;
            IBrush? halo = found ? FlashOn ? PinFlashHalo : PinHalo : i == SelectedPin ? PinHalo : hovered ? HoverFill : null;

            DrawPin(context, Pins[i], zoom, halo, withLabel: ShowLabels || found || hovered || i == SelectedPin);
        }

        if (!string.IsNullOrWhiteSpace(HoverText))
            DrawChip(context, HoverText, HoverAt);
    }

    /// <summary>§411. The found and hovered maps, each drawn as one shape
    /// whatever the number of boxes it was drawn in. Boxes belong to a
    /// map by their label - the Maps window labels every box with its
    /// map's name.</summary>
    private void DrawMaps(DrawingContext context, double zoom)
    {
        var foundLabels = new HashSet<string>(StringComparer.Ordinal);
        var hoveredLabels = new HashSet<string>(StringComparer.Ordinal);

        foreach (int i in Highlighted)
        {
            if (i >= 0 && i < Boxes.Count)
                foundLabels.Add(Boxes[i].GroupKey);
        }

        foreach (int i in Hovered)
        {
            if (i >= 0 && i < Boxes.Count && !foundLabels.Contains(Boxes[i].GroupKey))
                hoveredLabels.Add(Boxes[i].GroupKey);
        }

        if (foundLabels.Count == 0 && hoveredLabels.Count == 0)
            return;

        // The pieces of each map, in box order.
        var pieces = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < Boxes.Count; i++)
        {
            string label = Boxes[i].GroupKey;

            if (!foundLabels.Contains(label) && !hoveredLabels.Contains(label))
                continue;

            if (!pieces.TryGetValue(label, out List<int>? list))
            {
                list = new List<int>();
                pieces[label] = list;
            }

            list.Add(i);
        }

        // Hovered maps under found ones, so a found map stays on top.
        foreach (string label in hoveredLabels)
        {
            if (pieces.TryGetValue(label, out List<int>? list))
                DrawMap(context, label, list, HoverBrush, HoverFill, zoom, null);
        }

        foreach (string label in foundLabels)
        {
            if (!pieces.TryGetValue(label, out List<int>? list))
                continue;

            IBrush brush = FlashOn ? FlashBrush : SelectedBrush;
            IBrush fill = FlashOn ? FlashFill : HighlightFill;

            IReadOnlyList<EncounterMethod>? methods = null;

            foreach (int i in list)
            {
                if (BoxMethods.TryGetValue(i, out IReadOnlyList<EncounterMethod>? found) && found.Count > 0)
                {
                    methods = found;
                    break;
                }
            }

            DrawMap(context, label, list, brush, fill, zoom, methods);
        }
    }

    /// <summary>One map as one shape: its boxes united into a single
    /// geometry (so the outline runs round the outside only, and the
    /// fill has no seams), the name above the top-left of the whole, the
    /// glyphs on the middle of its largest piece.</summary>
    private void DrawMap(DrawingContext context, string group, List<int> indices, IBrush brush, IBrush fill, double zoom, IReadOnlyList<EncounterMethod>? methods)
    {
        // §412: the group is the key; the name drawn is the pieces' label.
        string label = indices.Count > 0 ? Boxes[indices[0]].Label : group;

        Geometry? shape = null;
        Rect whole = default;
        MapBoxShape? largest = null;
        long largestArea = -1;

        foreach (int i in indices)
        {
            MapBoxShape box = Boxes[i];

            if (PixelRect(box, zoom) is not Rect rect)
                continue;

            var piece = new RectangleGeometry(rect);
            shape = shape is null ? piece : new CombinedGeometry { GeometryCombineMode = GeometryCombineMode.Union, Geometry1 = shape, Geometry2 = piece };
            whole = shape == piece ? rect : whole.Union(rect);

            long area = (long)box.Width * box.Height;

            if (area > largestArea)
            {
                largestArea = area;
                largest = box;
            }
        }

        if (shape is null || largest is null)
            return;

        context.DrawGeometry(fill, new Pen(brush, 1), shape);

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new FormattedText(
                label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                LabelSize,
                brush);

            double textTop = whole.Top - text.Height - 1;

            if (textTop < 0)
                textTop = whole.Top + 1;

            var origin = new Point(whole.Left, textTop);
            context.FillRectangle(LabelShadow, new Rect(origin.X - 1, origin.Y, text.Width + 3, text.Height));
            context.DrawText(text, origin);
        }

        if (methods is not null && methods.Count > 0)
            DrawMethods(context, largest, methods, zoom);
    }

    /// <summary>The screen rectangle a box's outline sits on - one pixel
    /// thick on the half-pixel, round the picture pixels it covers - or
    /// null for a box too small to draw at this zoom.</summary>
    private static Rect? PixelRect(MapBoxShape box, double zoom)
    {
        if (box.Width <= 0 || box.Height <= 0)
            return null;

        double left = Math.Floor(box.X * zoom) + 0.5;
        double top = Math.Floor(box.Y * zoom) + 0.5;
        double right = Math.Floor((box.X + box.Width) * zoom) - 0.5;
        double bottom = Math.Floor((box.Y + box.Height) * zoom) - 0.5;

        if (right <= left || bottom <= top)
            return null;

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>§410. The method glyphs on a found box, in a row on its
    /// middle - a box smaller than the row still gets them; they are the
    /// marker, the box is where it is.</summary>
    private static void DrawMethods(DrawingContext context, MapBoxShape box, IReadOnlyList<EncounterMethod> methods, double zoom)
    {
        double centreX = (box.X + box.Width / 2.0) * zoom;
        double centreY = (box.Y + box.Height / 2.0) * zoom;
        double step = GlyphSize + 2;
        double startX = centreX - step * (methods.Count - 1) / 2.0;

        for (int i = 0; i < methods.Count; i++)
            MethodIcon.Draw(context, methods[i], new Point(startX + i * step, centreY), GlyphSize);
    }

    /// <summary>§410. The chip by the pointer: dark backing, light text,
    /// a little to the lower right of the point and kept inside the
    /// control so it never runs off the edge of the picture.</summary>
    private void DrawChip(DrawingContext context, string chipText, Point at)
    {
        var text = new FormattedText(
            chipText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            LabelSize + 1,
            ChipInk);

        double width = text.Width + 12;
        double height = text.Height + 8;
        double x = at.X + 14;
        double y = at.Y + 14;

        if (x + width > Bounds.Width)
            x = Math.Max(0, at.X - width - 6);

        if (y + height > Bounds.Height)
            y = Math.Max(0, at.Y - height - 6);

        var rect = new Rect(x, y, width, height);
        context.DrawRectangle(ChipFill, new Pen(PinRing, 1), rect, 4, 4);
        context.DrawText(text, new Point(x + 6, y + 4));
    }

    private static void DrawPin(DrawingContext context, MapPinShape pin, double zoom, IBrush? halo, bool withLabel = true)
    {
        Rect bounds = PinBounds(pin, zoom);

        if (halo is not null)
        {
            double r = Math.Max(bounds.Width, bounds.Height) / 2 + 6;
            context.DrawEllipse(halo, null, bounds.Center, r, r);
        }

        // §410: the ring that says "boss" whatever the pin shows.
        double ring = Math.Max(bounds.Width, bounds.Height) / 2 + 3;
        context.DrawEllipse(null, new Pen(pin.Pokestop ? StopRing : PinRing, 2), bounds.Center, ring, ring);

        if (pin.Image is Bitmap image)
        {
            context.DrawImage(image, bounds);
        }
        else
        {
            context.DrawEllipse(PinDisc, new Pen(LabelShadow, 1), bounds.Center, PinDiscRadius, PinDiscRadius);
        }

        if (!withLabel || string.IsNullOrWhiteSpace(pin.Label))
            return;

        var text = new FormattedText(
            pin.Label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            LabelSize,
            SelectedBrush);

        var origin = new Point(bounds.Center.X - text.Width / 2, bounds.Bottom + 1);

        context.FillRectangle(LabelShadow, new Rect(origin.X - 1, origin.Y, text.Width + 3, text.Height));
        context.DrawText(text, origin);
    }

    private static void DrawBox(DrawingContext context, MapBoxShape box, IBrush brush, double zoom, bool withLabel, IBrush? fill = null)
    {
        if (box.Width <= 0 || box.Height <= 0)
            return;

        // Outline the covered picture pixels: from the top-left corner of
        // the first to the bottom-right corner of the last, one screen
        // pixel thick and sitting on the pixel grid.
        double left = Math.Floor(box.X * zoom) + 0.5;
        double top = Math.Floor(box.Y * zoom) + 0.5;
        double right = Math.Floor((box.X + box.Width) * zoom) - 0.5;
        double bottom = Math.Floor((box.Y + box.Height) * zoom) - 0.5;

        if (right <= left || bottom <= top)
            return;

        // §404: the fill covers the box's picture pixels exactly; the
        // outline sits on top, on the half-pixel as ever.
        if (fill is not null)
            context.FillRectangle(fill, new Rect(left - 0.5, top - 0.5, right - left + 1, bottom - top + 1));

        var pen = new Pen(brush, 1);
        context.DrawRectangle(null, pen, new Rect(left, top, right - left, bottom - top));

        if (!withLabel || string.IsNullOrWhiteSpace(box.Label))
            return;

        var text = new FormattedText(
            box.Label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            LabelSize,
            brush);

        // Above the box when there is room, inside its top-left corner when
        // the box sits at the top of the picture.
        double textTop = top - text.Height - 1;

        if (textTop < 0)
            textTop = top + 1;

        var origin = new Point(left, textTop);

        // A dark backing so the label reads on light land and sea alike.
        context.FillRectangle(LabelShadow, new Rect(origin.X - 1, origin.Y, text.Width + 3, text.Height));
        context.DrawText(text, origin);
    }
}
