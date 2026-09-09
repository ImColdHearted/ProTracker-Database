using System.Globalization;
using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Foot_Tracker.Controls;

/// <summary>
/// Draws text with an outline (Fill on the inside, Stroke around the glyph
/// edges) - Avalonia has no built-in stroke/outline Effect the way
/// DropShadowEffect covers Text Shadow (see ThemeManager.TextShadowCatalog/
/// BuildTextShadowEffect and the Style Selector="TextBlock" in App.axaml),
/// so this exists as a small custom Shape instead. Confirmed against
/// Avalonia's own source before writing this: there's no public IEffect
/// extension point for a custom stroke effect on stable Avalonia, but
/// Shape + FormattedText.BuildGeometry() together give an exact, hand-rolled
/// equivalent - the same approach the Avalonia community settled on for this
/// same request (GitHub Discussion #7726).
///
/// Deriving from Shape (rather than TextBlock) means Fill/Stroke/
/// StrokeThickness/StrokeDashArray/StrokeLineCap/StrokeJoin/etc. all come
/// for free from the base class - only the text-specific pieces (Text/
/// FontFamily/FontSize/FontWeight/FontStyle) are declared here.
/// CreateDefiningGeometry() is the only rendering hook a Shape subclass can
/// override - Shape.Render itself is sealed and always does
/// context.DrawGeometry(Fill, strokePen, DefiningGeometry) under the hood,
/// so there's no way (and no need) to touch drawing directly.
///
/// Scope note (see MIGRATION_GUIDE.md §78): unlike Text Shadow, which
/// reaches every TextBlock in the app through one global
/// Style Selector="TextBlock" in App.axaml, this control only reaches
/// wherever it's used explicitly in markup - today that's just
/// AppearanceWindow's own live preview. Converting an existing TextBlock
/// elsewhere in the app over to OutlinedTextBlock is a separate, manual
/// change per element; there's no global style that can retarget a
/// different control type the way Effect="{DynamicResource ...}" retargets
/// a shared property on every TextBlock at once.
/// </summary>
public class OutlinedTextBlock : Shape
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, FontFamily>(
            nameof(FontFamily), new FontFamily("Inter"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, double>(nameof(FontSize), 12d);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<FontStyle> FontStyleProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, FontStyle>(nameof(FontStyle), FontStyle.Normal);

    static OutlinedTextBlock()
    {
        // Registers which properties should invalidate the cached geometry -
        // same mechanism Avalonia's own Rectangle/Ellipse/Path use for their
        // own geometry-affecting properties. StretchProperty is inherited
        // from Shape itself (not declared above) but still needs to be
        // listed here so changing it also triggers a rebuild.
        AffectsGeometry<OutlinedTextBlock>(
            TextProperty,
            FontFamilyProperty,
            FontSizeProperty,
            FontWeightProperty,
            FontStyleProperty,
            StretchProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    protected override Geometry? CreateDefiningGeometry()
    {
        if (string.IsNullOrEmpty(Text))
            return null;

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch.Normal);

        // Fill is passed through as FormattedText's own required "foreground"
        // argument, but that argument only matters for APIs that ask
        // FormattedText to paint itself directly - BuildGeometry() returns
        // plain outline geometry with no color baked in. This control's own
        // Shape.Render is what actually paints the result, using this same
        // Fill (and Stroke/StrokeThickness) - so whatever is passed here has
        // no visible effect on its own either way.
        var formattedText = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Fill);

        return formattedText.BuildGeometry(new Point(0, 0));
    }
}
