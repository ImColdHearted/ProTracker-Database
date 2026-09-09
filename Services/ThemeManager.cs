using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Foot_Tracker.Models;
using Foot_Tracker.Tracking;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Ported from WinForms. The original walked the Form's control tree and set
    /// ForeColor/BackColor on every control directly. Avalonia is resource/style driven,
    /// so instead we push the current theme's colors into the Application's resource
    /// dictionary as brushes. Every view then binds with:
    ///   Background="{DynamicResource ThemeBackgroundBrush}"
    ///   Foreground="{DynamicResource ThemeTextBrush}"
    ///   BorderBrush="{DynamicResource ThemeBorderBrush}"
    /// and updates automatically whenever Reload() is called - no control-tree walk needed.
    /// </summary>
    public static class ThemeManager
    {
        // ==============================================================
        // CUSTOM FONTS - see Assets/Fonts/README.txt for the full walkthrough.
        //
        // To add a new bundled font:
        //   1. Drop the .ttf/.otf file(s) in Assets/Fonts/.
        //   2. Add one line below: ["Dropdown Display Name"] = "Font's Actual Internal Name".
        //   3. Rebuild - it shows up in Appearance > Font automatically.
        //
        // The dictionary key is what appears in the Appearance window's Font
        // dropdown. The value must match the font file's own internal family name
        // (often different from the filename) - check by double-clicking the
        // .ttf/.otf file to preview it; the preview window's title bar shows the
        // real name.
        //
        // NOTE: if the font is only a single weight (no separate Bold file), that's
        // fine - BuildFontFamily() below automatically adds Inter as a fallback so
        // Bold-styled text (headers/labels throughout the app) renders in Inter
        // instead of crashing. No extra step needed for that here.
        // ==============================================================
        public static readonly Dictionary<string, string> CustomFontCatalog = new()
        {
            ["American Slasher"] = "American Slasher",
            ["Aquire"] = "Aquire",
            ["Champions Stencil"] = "Champions Stencil",
            ["Lemondrop"] = "Lemondrop",
            ["Minecraft"] = "Minecraft",
            ["Nearo"] = "Nearo",
            ["Neura"] = "Neura",
            ["Sanger Memo"] = "Sanger Memo",
            ["Skylane Horizon"] = "Skylane Horizon"

            // ["Montserrat"] = "Montserrat",
            // ["Press Start 2P"] = "Press Start 2P",
        };

        // Must exactly match the compiled assembly name (defaults to the .csproj
        // filename minus extension, unless overridden by an explicit <AssemblyName>
        // property) - if the project gets renamed again later, update this to match,
        // or every avares:// custom-font lookup silently falls back to Inter with
        // no error at all (this is exactly what happened after the Pro_Tracker rename).
        private const string CustomFontAssemblyName = "ProTrackerDatabase.Avalonia";

        public static AppearanceSettings Current { get; private set; } =
            AppearanceSettingsRepository.Load();

        public const string BackgroundBrushKey = "ThemeBackgroundBrush";
        public const string TextBrushKey = "ThemeTextBrush";
        public const string BorderBrushKey = "ThemeBorderBrush";
        public const string FontFamilyKey = "ThemeFontFamily";
        public const string FontSizeKey = "ThemeFontSize";
        public const string SpriteBoxBackgroundBrushKey = "ThemeSpriteBoxBackgroundBrush";
        public const string ButtonBrushKey = "ThemeButtonBrush";
        public const string StatsBackgroundBrushKey = "ThemeStatsBackgroundBrush";
        public const string EncountersBackgroundBrushKey = "ThemeEncountersBackgroundBrush";

        /// <summary>§208. The three states a button has besides its resting
        /// one, derived from the user's Button Color rather than settings of
        /// their own.
        ///
        /// THE BUG THIS EXISTS FOR. App.axaml sets Background on a blanket
        /// Button selector so the Button Color reaches every button in the
        /// app. FluentTheme's own hover, pressed and disabled visuals are
        /// setters on PART_ContentPresenter inside the button's template, and
        /// they set Background, BorderBrush AND Foreground - so on a themed
        /// button the pointer either changed nothing or swapped in Fluent's
        /// stock grey and stock text colour, neither of which has anything to
        /// do with the colour the user picked. §103 hit the same wall with
        /// the Always on Top toggle and worked around it with a doubled
        /// border rather than a fill.
        ///
        /// Deriving them means there is nothing new to set and nothing that
        /// can drift: pick any Button Color and its three states follow.
        /// Hover and pressed move TOWARD the text colour (14% and 30%), which
        /// is direction-agnostic - it lightens a dark button and darkens a
        /// light one, so it works without knowing which the user chose.
        /// Disabled moves most of the way to the window background instead,
        /// which is what "not part of the conversation" looks like.</summary>
        public const string ButtonHoverBrushKey = "ThemeButtonHoverBrush";

        public const string ButtonPressedBrushKey = "ThemeButtonPressedBrush";

        public const string ButtonDisabledBrushKey = "ThemeButtonDisabledBrush";

        /// <summary>§208. One red for every error line in the app and one
        /// amber for every warning.
        ///
        /// Before this, error text was Red in eight windows, IndianRed in
        /// three and OrangeRed in four, and BossScraperWindow and
        /// GuideScraperWindow drew the SAME "you are about to overwrite"
        /// sentence in two different colours. These are deliberately NOT
        /// user-settable: a colour whose whole job is to say "this went
        /// wrong" should not be something you can accidentally set to the
        /// same value as your body text. They are resources rather than
        /// literals so there is one place to change them.
        ///
        /// Both are picked to stay readable on a light or a dark surface,
        /// since the user chooses the background.</summary>
        public const string ErrorBrushKey = "ThemeErrorBrush";

        public const string WarningBrushKey = "ThemeWarningBrush";

        /// <summary>§208. The fill behind the main window's encounter-table
        /// column headings.
        ///
        /// Those six chips were hard-coded Black on Black with White text, so
        /// the app's most-looked-at surface ignored Border Color, Font Color
        /// and Table Background entirely and sat above a list that follows all
        /// three. They cannot simply be given the table's own brush, though:
        /// Stats Background and Table Background both default to Transparent -
        /// a true no-op for the surfaces they were introduced for - and a
        /// transparent header is not what a user who never opened Appearance
        /// had yesterday. It would have been a visible change from a setting
        /// they never chose.
        ///
        /// §209 puts a Header Background swatch in the Appearance window, so
        /// there are now three rungs and the first one that has been set wins:
        ///
        ///   1. Header Background, if the user picked one. Says exactly this.
        ///   2. Table Background, if they picked one. The header then reads as
        ///      part of the table it heads, which is the sensible default for
        ///      somebody who coloured the table and stopped there.
        ///   3. Otherwise a band derived from the window background, 12% of the
        ///      way toward the text colour.
        ///
        /// The last rung is why this never resolves to nothing: it is always a
        /// real fill, always a step away from whatever is behind it, and always
        /// keeps the heading readable, because the text is the colour the fill
        /// is only a tenth of the way toward.</summary>
        public const string TableHeaderBrushKey = "ThemeTableHeaderBrush";

        /// <summary>§208. How far the derived header band travels from the
        /// window background toward the text colour. Small: it is a heading
        /// band, not a panel.</summary>
        private const double TableHeaderMix = 0.12;

        /// <summary>§208. Fixed, not derived from the user's palette - see
        /// ErrorBrushKey.</summary>
        private static readonly Color ErrorColor = Color.FromRgb(0xE0, 0x52, 0x52);

        private static readonly Color WarningColor = Color.FromRgb(0xE0, 0xA0, 0x30);

        /// <summary>§208. How far the hover and pressed fills travel from the
        /// button's own colour toward the text colour.</summary>
        private const double ButtonHoverMix = 0.14;

        private const double ButtonPressedMix = 0.30;

        /// <summary>§208. How far a disabled button's fill travels toward the
        /// window background. Far enough to read as absent, not so far that
        /// the button's outline becomes the only thing left of it.</summary>
        private const double ButtonDisabledMix = 0.55;

        /// <summary>§188. The two measurements of the panel that every window
        /// other than the main one now puts its content on: the width of its
        /// outline, and the gap between it and the window edge. Both are
        /// Thickness resources rather than fixed numbers in the style,
        /// because both have to be able to be nothing.
        ///
        /// The panels take their fill from the Stats Background colour, whose
        /// default is Transparent. A fixed outline and a fixed inset would
        /// therefore draw a box around, and shift inward, the content of
        /// every window in the app for every user who has never opened the
        /// Appearance window - a visible change from a setting they never
        /// chose, which is the one thing every other colour option here is
        /// careful not to be. Deriving both from the colour makes the default
        /// a complete no-op: with no fill the panel has no outline and takes
        /// no space, so it is not merely invisible but absent from the
        /// layout. The first colour picked brings all three at once.</summary>
        public const string PanelBorderThicknessKey = "ThemePanelBorderThickness";

        public const string PanelMarginKey = "ThemePanelMargin";

        /// <summary>§188. The outline width a panel gets once it has a fill,
        /// matching the main window's stats panel, which sets 1 directly.</summary>
        internal const double PanelBorderWidth = 1;

        /// <summary>§188. How much background shows around a panel once there
        /// is one, so it reads as a card on the picture rather than as a
        /// second window filling the first.</summary>
        internal const double PanelInset = 10;

        /// <summary>§188. The one place that decides whether there is a panel
        /// at all. Any alpha counts as a deliberate choice; only the default
        /// fully transparent colour means no panel.</summary>
        internal static bool PanelIsVisible(Color panelColor) =>
            panelColor.A != 0;

        /// <summary>§141. A ThemeVariant (Light or Dark) for the main window's
        /// menu bar, chosen from the background behind it - see
        /// BackgroundIsLightBehindMenu. MainWindow.axaml wraps its Menu in a
        /// ThemeVariantScope bound to this, so the menu text (and its hover
        /// highlight and dropdowns) come from Fluent's light palette over a
        /// light background and from the dark one everywhere else. The menu
        /// never used the Appearance text colour; it used the app's dark
        /// theme default, white, which disappears on a pale background.</summary>
        public const string MenuVariantKey = "ThemeMenuVariant";

        /// <summary>§141. Relative luminance (WCAG, 0 = black, 1 = white)
        /// above which the strip behind the menu counts as light. Black and
        /// white text have equal contrast at 0.18; a little above that, so a
        /// mid-tone keeps the dark menu the rest of the app wears unless dark
        /// text is clearly the more readable of the two. The green in the
        /// report that asked for this measures 0.31 - white on it is 3.0:1,
        /// black 7.1:1 - so it switches. Raise this to keep white text on
        /// more mid-tones; lower it to switch sooner.</summary>
        public const double LightBackgroundLuminance = 0.25;

        /// <summary>§141. How much of the visible background's height the
        /// menu strip covers - the menu is about 30 px of a 600 px window -
        /// and the main window's default shape, which decides which rows of a
        /// UniformToFill image are on screen at all.</summary>
        internal const double MenuStripFraction = 0.08;
        internal const double MainWindowAspect = 875.0 / 600.0;

        /// <summary>§133. The size a name falls back to, as a NAME rather
        /// than a lookup. It has to exist in FontSizeCatalog below, and the
        /// battery checks that it does - which is the whole point of naming
        /// it here instead of writing the string at each use site.</summary>
        public const string DefaultFontSizeName = "11px";

        /// <summary>The same value as a number, for the one place that needs
        /// a size without a catalog present: BuildFontSize's last resort.</summary>
        public const double DefaultFontSize = 11;

        // Display name -> pixel size. Renamed from descriptive tiers (Default,
        // Large, ... Extra Extra Large) to plain pixel names, so the dropdown
        // says what it does. 11px is what "Default" was, which is why it is
        // the fallback - anyone who never touched the setting sees no change.
        public static readonly Dictionary<string, double> FontSizeCatalog = new()
        {
            ["8px"] = 8,
            ["9px"] = 9,
            ["10px"] = 10,
            ["11px"] = 11,
            ["12px"] = 12,
            ["13px"] = 13,
            ["14px"] = 14,
            ["15px"] = 15
        };

        // For the "Create Gradient" dialog's Direction dropdown (see
        // CustomGradientViewModel). Start/End are relative points (0..1 on
        // each axis, matching Avalonia's own LinearGradientBrush.StartPoint/
        // EndPoint shape) so BuildGradientBrush below can hand a chosen
        // preset straight to the brush with no further conversion.
        public readonly record struct GradientDirectionPreset(RelativePoint Start, RelativePoint End);

        public static readonly Dictionary<string, GradientDirectionPreset> GradientDirectionCatalog = new()
        {
            ["Left to Right"] = new GradientDirectionPreset(
                new RelativePoint(0, 0, RelativeUnit.Relative),
                new RelativePoint(1, 0, RelativeUnit.Relative)),
            ["Right to Left"] = new GradientDirectionPreset(
                new RelativePoint(1, 0, RelativeUnit.Relative),
                new RelativePoint(0, 0, RelativeUnit.Relative)),
            ["Top to Bottom"] = new GradientDirectionPreset(
                new RelativePoint(0, 0, RelativeUnit.Relative),
                new RelativePoint(0, 1, RelativeUnit.Relative)),
            ["Bottom to Top"] = new GradientDirectionPreset(
                new RelativePoint(0, 1, RelativeUnit.Relative),
                new RelativePoint(0, 0, RelativeUnit.Relative)),
            ["Diagonal Down"] = new GradientDirectionPreset(
                new RelativePoint(0, 0, RelativeUnit.Relative),
                new RelativePoint(1, 1, RelativeUnit.Relative)),
            ["Diagonal Up"] = new GradientDirectionPreset(
                new RelativePoint(0, 1, RelativeUnit.Relative),
                new RelativePoint(1, 0, RelativeUnit.Relative))
        };

        public static void Reload()
        {
            Current = AppearanceSettingsRepository.Load();
            Apply();
        }

        /// <summary>§208. A straight linear blend of two colours, t of the
        /// way from a to b, per channel. Alpha travels with them, so mixing
        /// toward a transparent colour fades rather than jumping opaque.
        ///
        /// Deliberately not gamma-correct: these are small nudges of 14% to
        /// 55% between two colours the user already chose to sit next to each
        /// other, and a perceptual blend would land somewhere they did not
        /// pick for no visible gain at this distance.
        ///
        /// Per-channel work is the same Lerp SampleGradient already uses, so
        /// there is one definition of "part way between two colours" in this
        /// file rather than two that could drift.</summary>
        private static Color Mix(Color a, Color b, double t)
        {
            double f = Math.Clamp(t, 0.0, 1.0);

            return Color.FromArgb(
                Lerp(a.A, b.A, f),
                Lerp(a.R, b.R, f),
                Lerp(a.G, b.G, f),
                Lerp(a.B, b.B, f));
        }

        /// <summary>Call once at startup (e.g. from App.axaml.cs OnFrameworkInitializationCompleted).</summary>
        public static void Apply()
        {
            var app = Application.Current;
            if (app is null)
                return;

            app.Resources[BackgroundBrushKey] =
                BuildBackgroundBrush();

            // §141: decided from the same settings the brush above was built
            // from, so the two can never disagree about what is behind the menu.
            app.Resources[MenuVariantKey] =
                BackgroundIsLightBehindMenu() ? ThemeVariant.Light : ThemeVariant.Dark;

            app.Resources[TextBrushKey] =
                new SolidColorBrush(Current.TextColor);

            app.Resources[BorderBrushKey] =
                new SolidColorBrush(Current.BorderColor);

            app.Resources[FontFamilyKey] =
                BuildFontFamily(Current.FontFamilyName);

            app.Resources[FontSizeKey] =
                BuildFontSize(Current.FontSizeName);

            app.Resources[SpriteBoxBackgroundBrushKey] =
                new SolidColorBrush(Current.SpriteBoxBackgroundColor);

            app.Resources[ButtonBrushKey] =
                new SolidColorBrush(Current.ButtonColor);

            app.Resources[StatsBackgroundBrushKey] =
                new SolidColorBrush(Current.StatsBackgroundColor);

            app.Resources[EncountersBackgroundBrushKey] =
                new SolidColorBrush(Current.EncountersBackgroundColor);

            // §208: the button's other three states, in the same pass as the
            // colour they come from, so a saved Appearance change can never
            // leave a button resting in one palette and hovering in another.
            app.Resources[ButtonHoverBrushKey] =
                new SolidColorBrush(Mix(Current.ButtonColor, Current.TextColor, ButtonHoverMix));

            app.Resources[ButtonPressedBrushKey] =
                new SolidColorBrush(Mix(Current.ButtonColor, Current.TextColor, ButtonPressedMix));

            app.Resources[ButtonDisabledBrushKey] =
                new SolidColorBrush(Mix(Current.ButtonColor, Current.BackgroundColor, ButtonDisabledMix));

            // §208: fixed, so that "this went wrong" always looks the same
            // and always looks different from ordinary text.
            app.Resources[ErrorBrushKey] = new SolidColorBrush(ErrorColor);

            app.Resources[WarningBrushKey] = new SolidColorBrush(WarningColor);

            // §208/§209: the encounter table's heading band, first rung that
            // has been set. Its own colour if the user picked one; else the
            // table's, so the two read as one surface; else a step away from
            // the window background, so it is never the transparent nothing
            // both of those default to. See TableHeaderBrushKey.
            app.Resources[TableHeaderBrushKey] = new SolidColorBrush(
                PanelIsVisible(Current.HeaderBackgroundColor)
                    ? Current.HeaderBackgroundColor
                    : PanelIsVisible(Current.EncountersBackgroundColor)
                        ? Current.EncountersBackgroundColor
                        : Mix(Current.BackgroundColor, Current.TextColor, TableHeaderMix));

            // §188: both derived from the same colour the panels are filled
            // with, in the same pass, so nothing can disagree about whether
            // there is a panel there at all.
            bool panel = PanelIsVisible(Current.StatsBackgroundColor);

            app.Resources[PanelBorderThicknessKey] =
                new Thickness(panel ? PanelBorderWidth : 0);

            app.Resources[PanelMarginKey] =
                new Thickness(panel ? PanelInset : 0);
        }

        /// <summary>
        /// NOTE: this only affects text that doesn't already set its own explicit
        /// FontSize - a local value on a control always wins over an inherited
        /// Style default, same rule that made the custom-font Bold fallback
        /// necessary. Headers/labels with a hardcoded size elsewhere in the app
        /// won't change; general body text, buttons, and unstyled labels will.
        /// </summary>
        public static double BuildFontSize(string fontSizeName)
        {
            if (FontSizeCatalog.TryGetValue(
                    NormalizeFontSizeName(fontSizeName), out double size))
            {
                return size;
            }

            // §133. Unreachable while DefaultFontSizeName is a catalog key,
            // which the battery checks. It is here anyway because the line it
            // replaces was `return FontSizeCatalog["Default"];` - also
            // believed unreachable, also correct until the catalog was
            // renamed, and then a KeyNotFoundException on startup for every
            // settings file in existence. A constant cannot do that.
            return DefaultFontSize;
        }

        /// <summary>
        /// §133. Turns whatever a settings file happens to hold into a name
        /// FontSizeCatalog actually contains.
        ///
        /// The catalog was renamed from descriptive tiers to pixel names, and
        /// the rename left three lookups behind that the compiler could not
        /// see: BuildFontSize's fallback indexed the dictionary with the
        /// literal "Default", AppearanceViewModel initialised two fields from
        /// the same string, and AppearanceSettings defaulted FontSizeName to
        /// it. Every one of those was correct the day it was written. None of
        /// them is checked by anything, because a dictionary key is a string
        /// and a string always compiles.
        ///
        /// So there is one place that decides, and everything else asks it.
        ///
        /// Three cases, in order:
        ///   - a current name ("12px") is returned unchanged;
        ///   - anything that parses as a pixel count is snapped INTO the
        ///     catalog's range, so a file holding a size that was once
        ///     offered and no longer is lands on the nearest one that exists
        ///     rather than silently reverting;
        ///   - a legacy tier name falls back to the default, except the two
        ///     whose pixel size is actually recorded in MIGRATION_GUIDE.md
        ///     (§53): "Huge" and "Extra Extra Large" were 23px, above
        ///     anything the new range offers, so they take the largest one.
        ///     The other old tiers are deliberately NOT guessed at - their
        ///     sizes are not written down anywhere, and inventing a mapping
        ///     to look thorough is how a wrong number gets recorded as a
        ///     right one.
        /// </summary>
        public static string NormalizeFontSizeName(string? fontSizeName)
        {
            if (string.IsNullOrWhiteSpace(fontSizeName))
                return DefaultFontSizeName;

            string trimmed = fontSizeName.Trim();

            if (FontSizeCatalog.ContainsKey(trimmed))
                return trimmed;

            if (trimmed.Equals("Huge", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Extra Extra Large", StringComparison.OrdinalIgnoreCase))
            {
                return LargestFontSizeName();
            }

            string digits = trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^2].Trim()
                : trimmed;

            if (!double.TryParse(
                    digits,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double requested))
            {
                return DefaultFontSizeName;
            }

            // Nearest offered size, so 6px lands on the smallest and 20px on
            // the largest instead of both reverting to the middle.
            string nearest = DefaultFontSizeName;
            double bestDistance = double.MaxValue;

            foreach (KeyValuePair<string, double> entry in FontSizeCatalog)
            {
                double distance = Math.Abs(entry.Value - requested);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = entry.Key;
                }
            }

            return nearest;
        }

        /// <summary>The biggest size the catalog offers, found rather than
        /// written down, so adding a tier does not leave this behind.</summary>
        private static string LargestFontSizeName()
        {
            string largest = DefaultFontSizeName;
            double best = double.MinValue;

            foreach (KeyValuePair<string, double> entry in FontSizeCatalog)
            {
                if (entry.Value > best)
                {
                    best = entry.Value;
                    largest = entry.Key;
                }
            }

            return largest;
        }

        /// <summary>
        /// "Default" (or blank/whitespace) maps to Inter, the font bundled via the
        /// Avalonia.Fonts.Inter package - guaranteed present on every platform,
        /// unlike a named system font which may or may not exist on a given machine.
        /// A name matching CustomFontCatalog loads from our own bundled Assets/Fonts/
        /// files via the avares:// resource URI. Anything else falls back to a plain
        /// system-font-name lookup - Avalonia falls back gracefully (not an exception)
        /// if that name isn't found on the current machine.
        /// </summary>
        public static FontFamily BuildFontFamily(string fontFamilyName)
        {
            if (string.IsNullOrWhiteSpace(fontFamilyName) ||
                fontFamilyName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return new FontFamily("Inter");
            }

            if (CustomFontCatalog.TryGetValue(fontFamilyName, out string? internalFontName))
            {
                // Fallback chain, not just the bare font name: many custom fonts
                // (like the ones this was hit with) only ship a single weight, with
                // no embedded Bold variant. Avalonia throws InvalidOperationException
                // ("Could not create glyph typeface... Weight: Bold") rather than
                // synthesizing a fake bold or substituting automatically - confirmed
                // via multiple Avalonia GitHub issues covering this exact error.
                // Appending Inter as a fallback lets Bold-styled text (headers/labels
                // throughout the app) render in Inter instead of crashing, while
                // Regular-weight text still uses the custom font as intended.
                return new FontFamily($"avares://{CustomFontAssemblyName}/Assets/Fonts#{internalFontName}, Inter");
            }

            return new FontFamily(fontFamilyName);
        }

        /// <summary>§104: resolves the one built-in background (Slate) to its
        /// image under SharedPokemonLibrary/Assets/Custom. The preset PICKER
        /// is gone and AppearanceSettingsRepository coerces any older saved
        /// preset name to Slate, so in practice this is only ever called with
        /// "Slate" - it stays parameterised rather than hardcoded so a future
        /// built-in look needs no new plumbing, and an unknown id simply
        /// misses on disk and falls back to the flat color.</summary>
        public static string GetBackgroundPath(string backgroundId)
        {
            if (string.IsNullOrWhiteSpace(backgroundId))
                return string.Empty;

            return Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Custom",
                $"{backgroundId}.png");
        }

        /// <summary>
        /// Backs the "Create Gradient" dialog (see CustomGradientViewModel) - a
        /// fourth background mode alongside the preset/custom-image/custom-color
        /// trio in BuildBackgroundBrush below, gated by its own
        /// UseCustomGradient flag since a gradient needs multiple colors and a
        /// direction rather than the single color custom-color mode stores.
        /// Accepts 2-4 colors (the range CustomGradientViewModel's +/- buttons
        /// enforce) and spaces them evenly across the gradient - two colors
        /// sit at offsets 0/1, three at 0/0.5/1, and so on. Looks up the
        /// direction's start/end points from GradientDirectionCatalog;
        /// "None"/an unrecognized direction name falls back to the catalog's
        /// first entry, and fewer than 2 colors (only possible from a
        /// corrupted/hand-edited settings file - nothing upstream of this
        /// ever validates a saved value before startup) falls back to the
        /// model's own default color pair, rather than either case throwing -
        /// the same defensive shape BuildFontSize/BuildFontFamily above use
        /// for their own unrecognized-input case.
        /// </summary>
        public static LinearGradientBrush BuildGradientBrush(IReadOnlyList<Color> colors, string direction)
        {
            if (string.IsNullOrWhiteSpace(direction) ||
                !GradientDirectionCatalog.TryGetValue(direction, out GradientDirectionPreset preset))
            {
                preset = GradientDirectionCatalog.Values.First();
            }

            IReadOnlyList<Color> stopColors = colors is { Count: >= 2 }
                ? colors
                : new[] { Color.FromRgb(10, 15, 61), Color.FromRgb(89, 87, 87) };

            var brush = new LinearGradientBrush
            {
                StartPoint = preset.Start,
                EndPoint = preset.End
            };

            int lastIndex = stopColors.Count - 1;
            for (int i = 0; i < stopColors.Count; i++)
            {
                brush.GradientStops.Add(new GradientStop(stopColors[i], (double)i / lastIndex));
            }

            return brush;
        }

        /// <summary>
        /// Builds the actual background brush: a live two-color gradient when
        /// UseCustomGradient is set (see BuildGradientBrush above), otherwise
        /// the preset/custom gradient image when one exists on disk, otherwise
        /// a flat SolidColorBrush fallback (either the preset's base color or
        /// the user's custom picked color). Previously this always returned a
        /// SolidColorBrush and loaded the image into an unused resource -
        /// presets never actually showed their gradient art.
        /// </summary>
        private static IBrush BuildBackgroundBrush()
        {
            // §141: whatever GIF was playing belonged to the previous
            // appearance; a new brush is about to replace its target.
            AnimatedBackgroundService.Stop();

            if (Current.UseCustomGradient)
            {
                return BuildGradientBrush(Current.CustomGradientColors, Current.CustomGradientDirection);
            }

            string backgroundPath = GetCurrentBackgroundPath();

            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                var brush = new ImageBrush(new Bitmap(backgroundPath))
                {
                    Stretch = Stretch.UniformToFill
                };

                // §141: a GIF shows its first frame at once through the line
                // above (Avalonia decodes a GIF's first frame like any still
                // image); the remaining frames start playing into this same
                // brush once AnimatedBackgroundService has decoded them.
                if (AnimatedBackgroundService.IsGif(backgroundPath))
                    AnimatedBackgroundService.Start(brush, backgroundPath);

                return brush;
            }

            return new SolidColorBrush(Current.BackgroundColor);
        }

        // ==============================================================
        // §141 - IS THE BACKGROUND LIGHT BEHIND THE MENU?
        // ==============================================================

        /// <summary>True when the strip of background behind the main window's
        /// menu bar is light enough that Fluent's dark-theme white menu text
        /// would be hard to read - see LightBackgroundLuminance. Logged each
        /// time, so a support bundle shows the number behind the decision.</summary>
        public static bool BackgroundIsLightBehindMenu()
        {
            double luminance = MenuStripLuminance();
            bool light = luminance > LightBackgroundLuminance;

            Log.Information(
                "Appearance: relative luminance behind the menu bar {Luminance:0.000} - menu uses the {Variant} palette",
                luminance, light ? "light" : "dark");

            return light;
        }

        /// <summary>Mean relative luminance of the menu strip for the current
        /// settings: sampled along the top edge of a gradient (whatever its
        /// direction), over the top band of an image that is actually on
        /// screen after UniformToFill (a GIF by its first frame), or the flat
        /// colour itself.</summary>
        internal static double MenuStripLuminance()
        {
            if (Current.UseCustomGradient)
                return GradientTopEdgeLuminance(Current.CustomGradientColors, Current.CustomGradientDirection);

            string backgroundPath = GetCurrentBackgroundPath();

            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                double? fromImage = ImageTopBandLuminance(backgroundPath);

                if (fromImage is double value)
                    return value;
            }

            return RelativeLuminance(Current.BackgroundColor);
        }

        /// <summary>WCAG relative luminance of a colour, composited over black
        /// (what a translucent window background sits on) so a faint colour
        /// reads as the dark thing it looks like rather than as its hue.</summary>
        internal static double RelativeLuminance(Color color) =>
            RelativeLuminance(color.R, color.G, color.B, color.A);

        internal static double RelativeLuminance(SKColor color) =>
            RelativeLuminance(color.Red, color.Green, color.Blue, color.Alpha);

        internal static double RelativeLuminance(byte r, byte g, byte b, byte a)
        {
            double luminance =
                0.2126 * Linear(r) +
                0.7152 * Linear(g) +
                0.0722 * Linear(b);

            return luminance * (a / 255.0);
        }

        private static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>The gradient BuildGradientBrush would draw, sampled at 32
        /// points across the middle of the menu strip - the same stops, the
        /// same start/end points, the same fallbacks - and averaged. A
        /// top-to-bottom gradient answers with its first colour, a left-to-
        /// right one with the mean of all of them, which is what the eye sees
        /// behind a menu that spans the width.</summary>
        internal static double GradientTopEdgeLuminance(IReadOnlyList<Color> colors, string direction)
        {
            if (string.IsNullOrWhiteSpace(direction) ||
                !GradientDirectionCatalog.TryGetValue(direction, out GradientDirectionPreset preset))
            {
                preset = GradientDirectionCatalog.Values.First();
            }

            IReadOnlyList<Color> stops = colors is { Count: >= 2 }
                ? colors
                : new[] { Color.FromRgb(10, 15, 61), Color.FromRgb(89, 87, 87) };

            double startX = preset.Start.Point.X;
            double startY = preset.Start.Point.Y;
            double deltaX = preset.End.Point.X - startX;
            double deltaY = preset.End.Point.Y - startY;
            double lengthSquared = deltaX * deltaX + deltaY * deltaY;

            const int samples = 32;
            double y = MenuStripFraction / 2;
            double total = 0;

            for (int i = 0; i < samples; i++)
            {
                double x = (i + 0.5) / samples;

                double t = lengthSquared <= 0
                    ? 0
                    : Math.Clamp(((x - startX) * deltaX + (y - startY) * deltaY) / lengthSquared, 0, 1);

                total += RelativeLuminance(SampleGradient(stops, t));
            }

            return total / samples;
        }

        /// <summary>The colour at position <paramref name="t"/> (0..1) of a
        /// gradient whose stops are spaced evenly, as BuildGradientBrush
        /// spaces them.</summary>
        internal static Color SampleGradient(IReadOnlyList<Color> stops, double t)
        {
            int lastIndex = stops.Count - 1;
            double scaled = Math.Clamp(t, 0, 1) * lastIndex;
            int index = (int)Math.Floor(scaled);

            if (index >= lastIndex)
                return stops[lastIndex];

            double fraction = scaled - index;
            Color from = stops[index];
            Color to = stops[index + 1];

            return Color.FromArgb(
                Lerp(from.A, to.A, fraction),
                Lerp(from.R, to.R, fraction),
                Lerp(from.G, to.G, fraction),
                Lerp(from.B, to.B, fraction));
        }

        private static byte Lerp(byte from, byte to, double fraction) =>
            (byte)Math.Clamp(Math.Round(from + (to - from) * fraction), 0, 255);

        /// <summary>Mean relative luminance of the band of an image that sits
        /// behind the menu. UniformToFill scales the image to cover the window
        /// and centres it, so an image taller than the window's shape loses
        /// its top and bottom: the visible rows are worked out for the main
        /// window's default shape first, then the top MenuStripFraction of
        /// those is sampled on a grid (64 columns, up to 16 rows). Null when
        /// the file will not decode, so the caller falls back to the flat
        /// colour rather than guess.</summary>
        internal static double? ImageTopBandLuminance(string path)
        {
            try
            {
                using SKBitmap? image = ImageOps.DecodePng(File.ReadAllBytes(path));

                if (image is null || image.Width <= 0 || image.Height <= 0)
                    return null;

                (int visibleTop, int visibleHeight) = VisibleRows(image.Width, image.Height);
                int bandRows = Math.Max(1, (int)Math.Round(visibleHeight * MenuStripFraction));

                SKColor[] pixels = image.Pixels;
                int rowStep = Math.Max(1, bandRows / 16);
                int columnStep = Math.Max(1, image.Width / 64);
                double total = 0;
                int count = 0;

                for (int y = visibleTop; y < visibleTop + bandRows && y < image.Height; y += rowStep)
                {
                    for (int x = 0; x < image.Width; x += columnStep)
                    {
                        total += RelativeLuminance(pixels[y * image.Width + x]);
                        count++;
                    }
                }

                return count == 0 ? null : total / count;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Appearance: could not read the background image {Path} to judge its lightness", path);
                return null;
            }
        }

        /// <summary>Which rows of a <paramref name="width"/> x
        /// <paramref name="height"/> image are on screen under UniformToFill
        /// in the main window's default shape: all of them for an image at
        /// least as wide as the window is, a centred band for a taller one.</summary>
        internal static (int Top, int Height) VisibleRows(int width, int height)
        {
            double imageAspect = (double)width / height;

            if (imageAspect >= MainWindowAspect)
                return (0, height);

            int visibleHeight = Math.Max(1, (int)Math.Round(width / MainWindowAspect));
            return ((height - visibleHeight) / 2, visibleHeight);
        }

        private static string GetCurrentBackgroundPath()
        {
            if (Current.UseCustomBackground)
            {
                // "Custom" covers both a user-picked image and a plain custom color -
                // only the former has a file on disk to load.
                return !string.IsNullOrWhiteSpace(Current.CustomBackgroundPath) &&
                       File.Exists(Current.CustomBackgroundPath)
                    ? Current.CustomBackgroundPath
                    : string.Empty;
            }

            return GetBackgroundPath(Current.BackgroundId);
        }
    }
}