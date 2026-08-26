using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Foot_Tracker.Models;

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
        public const string BorderShadowKey = "ThemeBorderShadow";
        public const string SpriteBoxBackgroundBrushKey = "ThemeSpriteBoxBackgroundBrush";
        public const string TextShadowEffectKey = "ThemeTextShadowEffect";

        // Display name -> pixel size. "Default" matches FluentTheme's typical base
        // control text size closely enough that switching to it looks unchanged.
        public static readonly Dictionary<string, double> FontSizeCatalog = new()
        {
            ["Default"] = 11,
            ["Medium"] = 14,
            ["Large"] = 17,
            ["Extra Large"] = 20,
            ["Extra Extra Large"] = 23
        };

        // For the hunting-sprite border boxes in MainWindow.axaml (see the
        // Border.huntingSprite style in App.axaml). OffsetX/OffsetY/Blur/Spread
        // match Avalonia's BoxShadow syntax; Alpha is the preset's own baked-in
        // opacity (0 = no shadow at all, i.e. "None") - the actual RGB now comes
        // from AppearanceSettings.BoxShadowColor instead of being hardcoded to
        // black, see BuildBorderShadow below.
        public readonly record struct BorderShadowPreset(int OffsetX, int OffsetY, int Blur, int Spread, byte Alpha);

        public static readonly Dictionary<string, BorderShadowPreset> BorderShadowCatalog = new()
        {
            ["None"] = new BorderShadowPreset(0, 0, 0, 0, 0x00),
            ["Light"] = new BorderShadowPreset(0, 0, 4, 1, 0x80),
            ["Medium"] = new BorderShadowPreset(0, 0, 8, 2, 0xA0),
            ["Strong"] = new BorderShadowPreset(0, 0, 12, 3, 0xC0)
        };

        // For the "Text Shadow" dropdown in the Appearance window - applied to
        // every TextBlock in the app (see BuildTextShadowEffect below), unlike
        // BorderShadowPreset above which only ever reaches the hunting-sprite
        // boxes. BlurRadius/OffsetX/OffsetY/Opacity map straight onto
        // DropShadowEffect's own properties of the same names; "None"'s
        // Opacity of 0 is what makes it invisible, not a separate on/off
        // branch in BuildTextShadowEffect. Hand-tuned after seeing the
        // original four presets running (Extra Large/Extra Extra Large added
        // to match FontSizeCatalog's naming, opacity pushed to 1 across the
        // board) - "Medium" is no longer pixel-identical to the old on/off
        // toggle's fixed values, but AppearanceRepository's legacy migration
        // still maps old saves into it as the nearest named tier.
        public readonly record struct TextShadowPreset(double BlurRadius, double OffsetX, double OffsetY, double Opacity);

        public static readonly Dictionary<string, TextShadowPreset> TextShadowCatalog = new()
        {
            ["None"] = new TextShadowPreset(0, 0, 0, 0),
            ["Small"] = new TextShadowPreset(2, 1, 1, 1),
            ["Medium"] = new TextShadowPreset(4, 1, 1, 1),
            ["Large"] = new TextShadowPreset(9, 1, 1, 1),
            ["Extra Large"] = new TextShadowPreset(12, 1, 1, 1),
            ["Extra Extra Large"] = new TextShadowPreset(15, 1, 1, 1)
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

        /// <summary>Call once at startup (e.g. from App.axaml.cs OnFrameworkInitializationCompleted).</summary>
        public static void Apply()
        {
            var app = Application.Current;
            if (app is null)
                return;

            app.Resources[BackgroundBrushKey] =
                BuildBackgroundBrush();

            app.Resources[TextBrushKey] =
                new SolidColorBrush(Current.TextColor);

            app.Resources[BorderBrushKey] =
                new SolidColorBrush(Current.BorderColor);

            app.Resources[FontFamilyKey] =
                BuildFontFamily(Current.FontFamilyName);

            app.Resources[FontSizeKey] =
                BuildFontSize(Current.FontSizeName);

            app.Resources[BorderShadowKey] =
                BuildBorderShadow(Current.BorderShadowName, Current.BoxShadowColor);

            app.Resources[SpriteBoxBackgroundBrushKey] =
                new SolidColorBrush(Current.SpriteBoxBackgroundColor);

            app.Resources[TextShadowEffectKey] =
                BuildTextShadowEffect(Current.TextShadowName, Current.TextShadowColor);
        }

        /// <summary>Looks up a preset from BorderShadowCatalog and combines its
        /// offset/blur/spread/alpha with the caller's chosen RGB - "None"/an
        /// unrecognized name/a zero-alpha preset all resolve to no shadow,
        /// matching the pre-shadow look.</summary>
        public static BoxShadows BuildBorderShadow(string borderShadowName, Color color)
        {
            if (string.IsNullOrWhiteSpace(borderShadowName) ||
                !BorderShadowCatalog.TryGetValue(borderShadowName, out BorderShadowPreset preset) ||
                preset.Alpha == 0)
            {
                return default;
            }

            string shadowColorHex = $"#{preset.Alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

            return BoxShadows.Parse(
                $"{preset.OffsetX} {preset.OffsetY} {preset.Blur} {preset.Spread} {shadowColorHex}");
        }

        /// <summary>
        /// Backs the Appearance window's "Text Shadow" dropdown - applied to
        /// every TextBlock in the app via Style Selector="TextBlock" in
        /// App.axaml (see TextShadowEffectKey), same global reach as
        /// FontFamily/FontSize. Looks up blur/offset/opacity from
        /// TextShadowCatalog (only the color is separately configurable);
        /// "None"/an unrecognized name both fall back to the catalog's own
        /// "None" preset, whose Opacity of 0 makes the result fully invisible -
        /// always a real DropShadowEffect rather than null, the same
        /// "always a valid, harmless value instead of null" approach
        /// BuildBorderShadow's default BoxShadows already uses for "no shadow."
        /// </summary>
        public static DropShadowEffect BuildTextShadowEffect(string textShadowName, Color color)
        {
            if (string.IsNullOrWhiteSpace(textShadowName) ||
                !TextShadowCatalog.TryGetValue(textShadowName, out TextShadowPreset preset))
            {
                preset = TextShadowCatalog["None"];
            }

            return new DropShadowEffect
            {
                Color = color,
                BlurRadius = preset.BlurRadius,
                OffsetX = preset.OffsetX,
                OffsetY = preset.OffsetY,
                Opacity = preset.Opacity
            };
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
            // "Huge" was renamed to "Extra Extra Large" (to line up with Text
            // Shadow's own Extra Large/Extra Extra Large tiers) - aliased here
            // so a settings file saved under the old name doesn't silently
            // fall all the way back to Default's 11px instead of staying at 23px.
            if (fontSizeName == "Huge")
                fontSizeName = "Extra Extra Large";

            if (!string.IsNullOrWhiteSpace(fontSizeName) &&
                FontSizeCatalog.TryGetValue(fontSizeName, out double size))
            {
                return size;
            }

            return FontSizeCatalog["Default"];
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
        /// Midnight/Slate default pair, rather than either case throwing -
        /// the same defensive shape BuildTextShadowEffect/BuildBorderShadow
        /// above use for their own unrecognized-input case.
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
            if (Current.UseCustomGradient)
            {
                return BuildGradientBrush(Current.CustomGradientColors, Current.CustomGradientDirection);
            }

            string backgroundPath = GetCurrentBackgroundPath();

            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                return new ImageBrush(new Bitmap(backgroundPath))
                {
                    Stretch = Stretch.UniformToFill
                };
            }

            return new SolidColorBrush(Current.BackgroundColor);
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