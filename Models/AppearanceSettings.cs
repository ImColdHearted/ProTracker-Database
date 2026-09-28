using Avalonia.Media;
using System.Text.Json.Serialization;
// §133. For ThemeManager.DefaultFontSizeName below.
//
// This direction of reference - a model naming a service - is safe here for
// one specific reason: DefaultFontSizeName is a const, so the compiler
// inlines the literal at this use site and nothing about ThemeManager is
// touched at runtime. That matters because ThemeManager.Current is itself
// initialised from AppearanceSettingsRepository.Load(), which constructs
// this class. Were the constant a static readonly instead, reading it here
// would run ThemeManager's static initialiser from inside its own, and the
// value that came back would depend on which one the runtime started first.
// If it ever needs to stop being a const, this reference has to go first.
using Foot_Tracker.Services;

namespace Foot_Tracker.Models
{
    // Ported from WinForms. System.Drawing.Color -> Avalonia.Media.Color.
    // Argb ints are stored the same way WinForms Color.ToArgb() produced them
    // (0xAARRGGBB), so existing saved appearance-settings.json files stay compatible.
    public class AppearanceSettings
    {
        // §104: the six preset backgrounds (Midnight/Blood/Slate/Pride/Pink/
        // Violet) were removed from the program at the user's request, leaving
        // Slate as the app's one built-in look. The property itself stays -
        // ThemeManager still resolves it to Slate.png, and
        // AppearanceSettingsRepository.Load coerces any other saved preset
        // name to Slate, so an existing settings file lands on the new default
        // instead of a background that no longer exists. Custom image, custom
        // color and custom gradient backgrounds are untouched by any of that.
        public const string DefaultBackgroundId = "Slate";

        public string BackgroundId { get; set; } =
            DefaultBackgroundId;

        public bool UseCustomBackground { get; set; }

        public string CustomBackgroundPath { get; set; } =
            string.Empty;

        public int CustomBackgroundColorArgb { get; set; } =
            ToArgbInt(Colors.Black);

        // Two-to-four-color linear gradient the user builds themselves in the
        // "Create Gradient" dialog (see CustomGradientViewModel) - a fourth
        // background mode alongside preset/custom image/custom color above,
        // gated by its own flag rather than folded into UseCustomBackground
        // since a gradient needs multiple colors and a direction instead of
        // one color. Stored as a plain list of ARGB ints (2-4 entries)
        // rather than fixed Start/End/Third/Fourth fields, so the dialog's
        // +/- buttons can add or remove a color without a fixed-shape
        // migration - CustomGradientViewModel enforces the 2-4 range before
        // Apply is ever reachable, and BuildGradientBrush below falls back
        // safely if a hand-edited settings file has fewer than 2. No legacy
        // shape to migrate from: this replaced an earlier two-field
        // (Start/End only) version before that version ever shipped in a
        // working build. Defaults reuse the Midnight/Slate preset colors so
        // an unconfigured gradient still looks intentional rather than
        // picking two arbitrary colors. See ThemeManager.BuildGradientBrush/
        // GradientDirectionCatalog for how these combine into the actual
        // LinearGradientBrush.
        public bool UseCustomGradient { get; set; }

        public List<int> CustomGradientColorArgbs { get; set; } =
            new() { ToArgbInt(Color.FromRgb(10, 15, 61)), ToArgbInt(Color.FromRgb(89, 87, 87)) };

        public string CustomGradientDirection { get; set; } =
            "Top to Bottom";

        [JsonIgnore]
        public Color BackgroundColor
        {
            get
            {
                if (UseCustomBackground)
                {
                    return FromArgbInt(
                        CustomBackgroundColorArgb);
                }

                // §104: one built-in background remains, so this is Slate's
                // own flat gray - the fallback used when Slate.png cannot be
                // found on disk, and what the Appearance preview paints
                // behind a transparent panel.
                return Color.FromRgb(89, 87, 87);
            }
        }

        /// <summary>§349. Which shape this file is in.
        ///
        /// 1 is everything written before §349: one Text Color, one Border
        /// Color and one Font for the whole window. 2 is per-section.
        /// Defaults to 1 rather than 2 deliberately - an older file has no
        /// such key, and System.Text.Json leaves the initializer's value, so
        /// defaulting to 2 would mark every old file as already migrated and
        /// silently lose its colours.</summary>
        public int SettingsVersion { get; set; } = 1;

        // §349. The four below are LEGACY. Nothing sets them any more and the
        // Appearance window no longer shows them - each section has its own
        // now. They stay declared because the migration has to READ them:
        // remove the property and System.Text.Json drops the key before
        // anyone can copy it, which is the §104 comment's point running in
        // reverse.
        public int TextColorArgb { get; set; } =
            ToArgbInt(Colors.White);

        public int BorderColorArgb { get; set; } =
            ToArgbInt(Colors.White);

        // "Default" means "use the app's built-in font (Inter)" rather than
        // overriding it - ThemeManager maps that sentinel to an actual FontFamily.
        public string FontFamilyName { get; set; } =
            "Default";

        // §133: a catalog key, not the word "Default". The catalog was
        // renamed to pixel names and this was left holding a name nothing
        // could resolve - which BuildFontSize then turned into a
        // KeyNotFoundException rather than a fallback. 11px is the size
        // "Default" always meant, so a fresh install looks identical.
        public string FontSizeName { get; set; } =
            ThemeManager.DefaultFontSizeName;

        // Fill color for the hunting sprite border boxes on MainWindow - they're
        // fully transparent by default (Colors.Transparent), same as before this
        // setting existed. Lets a sprite stay visible against a busy/light custom
        // background image instead of the background showing straight through
        // the box.
        public int SpriteBoxBackgroundColorArgb { get; set; } =
            ToArgbInt(Colors.Transparent);

        // §104: Border Shadow, Box Shadow Color, Text Shadow, Text Shadow
        // Color, Text Stroke and Text Stroke Color all lived here and were
        // removed from the program at the user's request. Their keys can
        // still sit in an older appearance-client*.json - System.Text.Json
        // ignores properties the model no longer declares, so an existing
        // file loads cleanly and simply drops them on the next save.

        // Fill color for MainWindow's own action buttons (Set Target/Start/
        // Stop/Reset/Always on Top/Move Stats Left-Right/Pause Since Form) -
        // see the Button.themedButton/ToggleButton.themedButton style in
        // App.axaml. Scoped the same way SpriteBoxBackgroundColor is (a style
        // class, not every Button in every window) rather than reaching into
        // dialogs like Appearance/Exclude Stats themselves. Defaults to a
        // plain dark gray approximating FluentTheme's own default button fill
        // rather than something that would prove the setting exists (like
        // hot pink) - unlike SpriteBoxBackgroundColor above,
        // Transparent isn't a safe "matches what was
        // already there" default here, since FluentTheme buttons already
        // have a real, opaque fill out of the box.
        public int ButtonColorArgb { get; set; } =
            ToArgbInt(Color.FromRgb(45, 45, 48));

        // Background for the stats panel Border in MainWindow.axaml - that
        // Border currently sets no Background at all, so it shows the
        // window's own BackgroundColor/image straight through. Defaults to
        // Transparent to match that exact prior look (a true no-op default,
        // the same reasoning SpriteBoxBackgroundColor's own default uses).
        public int StatsBackgroundColorArgb { get; set; } =
            ToArgbInt(Colors.Transparent);

        // Background for the two session-encounter DataGrids in
        // MainWindow.axaml - they already hardcoded Background="Transparent"
        // locally, so Transparent here reproduces that exact prior look
        // rather than guessing at one.
        public int EncountersBackgroundColorArgb { get; set; } =
            ToArgbInt(Colors.Transparent);

        // §209. Fill behind the encounter table's column headings in
        // MainWindow - the band §208 gave its own brush.
        //
        // Transparent does NOT mean "no header" here, it means "decide it for
        // me": ThemeManager falls back to Table Background, and then to a band
        // derived from the window background, exactly as §208 left it. So this
        // is a true no-op until somebody picks a colour, an appearance file
        // written before §209 keeps the look it had, and there is still no way
        // for the header to end up invisible.
        public int HeaderBackgroundColorArgb { get; set; } =
            ToArgbInt(Colors.Transparent);

        // ------------------------------------------------ §349 per section
        //
        // Four sections, the same five controls each: background, border,
        // text, font, size. Encounter Tables carries a header colour on top.
        //
        // Sprites gets a text colour and a font like the rest, which the
        // §349 mockup did not draw. Its labels ("Hunting", "Current",
        // "Previous") are text and had to render in SOMETHING - and with the
        // global gone, that something would have been a fallback chosen in
        // code that nobody could see or change.
        //
        // Every default here matches the global it replaces, so a file that
        // somehow arrives unmigrated still looks like it always did.

        public int SpriteBoxBorderColorArgb { get; set; } = ToArgbInt(Colors.White);
        public int SpriteBoxTextColorArgb { get; set; } = ToArgbInt(Colors.White);
        public string SpriteBoxFontFamilyName { get; set; } = "Default";
        public string SpriteBoxFontSizeName { get; set; } = ThemeManager.DefaultFontSizeName;

        public int EncountersBorderColorArgb { get; set; } = ToArgbInt(Colors.White);
        public int EncountersTextColorArgb { get; set; } = ToArgbInt(Colors.White);
        public string EncountersFontFamilyName { get; set; } = "Default";
        public string EncountersFontSizeName { get; set; } = ThemeManager.DefaultFontSizeName;

        public int StatsBorderColorArgb { get; set; } = ToArgbInt(Colors.White);
        public int StatsTextColorArgb { get; set; } = ToArgbInt(Colors.White);
        public string StatsFontFamilyName { get; set; } = "Default";
        public string StatsFontSizeName { get; set; } = ThemeManager.DefaultFontSizeName;

        public int ButtonBorderColorArgb { get; set; } = ToArgbInt(Colors.White);
        public int ButtonTextColorArgb { get; set; } = ToArgbInt(Colors.White);
        public string ButtonFontFamilyName { get; set; } = "Default";
        public string ButtonFontSizeName { get; set; } = ThemeManager.DefaultFontSizeName;

        // §380. Whether the headings - the statistics labels, the three
        // sprite-box titles, the encounter table's column headers - are drawn
        // bold. Off for a font that ships one weight only (Griffy, say):
        // Bold text in such a font falls back to Inter (see
        // ThemeManager.BuildFontFamily), so the headings would be the one
        // thing on the window not in the chosen font. One flag for the whole
        // window rather than one per section, because a heading is bold or
        // the theme is not that kind of theme.
        public bool BoldHeadings { get; set; } = true;

        // §382. A picture behind the statistics panel and one behind the
        // encounter table, drawn OVER the window background and UNDER the
        // panel's fill colour, so the fill's alpha tints it - see
        // ThemeManager.BuildPanelBrush. Paths on this disk, like
        // CustomBackgroundPath, and like it never carried by a theme file.
        // Empty means no picture, which is what every existing file reads as.
        public string StatsBackgroundImagePath { get; set; } = string.Empty;

        public string EncountersBackgroundImagePath { get; set; } = string.Empty;

        // §383. The same for the three sprite boxes, which share one fill
        // (SpriteBoxBackgroundColorArgb) and so share one picture.
        public string SpriteBoxBackgroundImagePath { get; set; } = string.Empty;

        // §384. Border width and corner radius per section, in pixels. The
        // defaults are what every window drew before there was a setting:
        // a 1px line, square corners - and 3 on buttons, which is Fluent's
        // own ControlCornerRadius. Clamped on the way in by ThemeManager
        // (ClampBorderWidth/ClampCornerRadius), so a hand-edited file cannot
        // draw a 900px border.
        public double SpriteBoxBorderWidth { get; set; } = 1;
        public double SpriteBoxCornerRadius { get; set; } = 0;
        public double EncountersBorderWidth { get; set; } = 1;
        public double EncountersCornerRadius { get; set; } = 0;
        public double StatsBorderWidth { get; set; } = 1;
        public double StatsCornerRadius { get; set; } = 0;
        public double ButtonBorderWidth { get; set; } = 1;
        public double ButtonCornerRadius { get; set; } = 3;

        // §384. A frame picture around the statistics panel, the encounter
        // table and each sprite box: a PNG with a transparent middle, drawn
        // in nine slices (Controls/NineSliceFrame) with the corner size in
        // the picture's pixels. Files on this disk, like the pictures above;
        // not carried by a theme file. Empty means no frame.
        public string StatsFrameImagePath { get; set; } = string.Empty;
        public double StatsFrameInset { get; set; } = 24;
        public string EncountersFrameImagePath { get; set; } = string.Empty;
        public double EncountersFrameInset { get; set; } = 24;
        public string SpriteBoxFrameImagePath { get; set; } = string.Empty;
        public double SpriteBoxFrameInset { get; set; } = 24;

        // §387. A panel behind the whole sprite row - the three groups with
        // their captions - the way the Halloween mock-up drew a box round
        // them. Its own fill, border, corners, picture and frame; the
        // captions keep the Pokémon Sprites text and font. Everything
        // defaults to nothing (transparent fill and border), so a window
        // that never set it looks exactly as it did: ThemeManager derives
        // the border thickness and padding from whether anything shows.
        public int SpriteRowBackgroundColorArgb { get; set; } = ToArgbInt(Colors.Transparent);
        public int SpriteRowBorderColorArgb { get; set; } = ToArgbInt(Colors.Transparent);
        public double SpriteRowBorderWidth { get; set; } = 1;
        public double SpriteRowCornerRadius { get; set; } = 0;
        public string SpriteRowBackgroundImagePath { get; set; } = string.Empty;
        public string SpriteRowFrameImagePath { get; set; } = string.Empty;
        public double SpriteRowFrameInset { get; set; } = 24;

        // §393. The menu bar's own colours - the text of File, Game Info and
        // the rest, and the highlight behind the one the pointer is on or
        // that is open. Transparent (the default, and what every file from
        // before this section reads as) means automatic: black or white by
        // the background behind the strip, as §141 has always chosen, and
        // Fluent's own faint highlight. The menu keeps the window font.
        public int MenuTextColorArgb { get; set; } = ToArgbInt(Colors.Transparent);
        public int MenuHighlightColorArgb { get; set; } = ToArgbInt(Colors.Transparent);

        // §394. The menu bar's font. Empty - the default, and what every
        // file from before this section reads as - follows Statistics, the
        // window font the menu has always worn; a name is its own. See
        // ThemeManager.EffectiveMenuFontFamilyName.
        public string MenuFontFamilyName { get; set; } = string.Empty;
        public string MenuFontSizeName { get; set; } = string.Empty;

        // §393. How far each frame picture reaches past its panel's edge,
        // over whatever is around it - the way an ornate frame sits round a
        // box rather than inside it. Zero, the default, draws the frame
        // within the panel as §384 did. Files like the frames themselves,
        // not part of a shared theme.
        public double StatsFrameOverhang { get; set; }
        public double EncountersFrameOverhang { get; set; }
        public double SpriteBoxFrameOverhang { get; set; }
        public double SpriteRowFrameOverhang { get; set; }

        /// <summary>§349. Copies the four globals into the sixteen
        /// per-section fields, once.
        ///
        /// Called from Load. Guarded by SettingsVersion so it runs exactly
        /// once per file: running it twice would be harmless today, but only
        /// because nothing has edited a section yet - after that it would
        /// overwrite real choices with a stale global.</summary>
        public void MigrateGlobalsToSections()
        {
            if (SettingsVersion >= 2)
                return;

            SpriteBoxBorderColorArgb = BorderColorArgb;
            EncountersBorderColorArgb = BorderColorArgb;
            StatsBorderColorArgb = BorderColorArgb;
            ButtonBorderColorArgb = BorderColorArgb;

            SpriteBoxTextColorArgb = TextColorArgb;
            EncountersTextColorArgb = TextColorArgb;
            StatsTextColorArgb = TextColorArgb;
            ButtonTextColorArgb = TextColorArgb;

            SpriteBoxFontFamilyName = FontFamilyName;
            EncountersFontFamilyName = FontFamilyName;
            StatsFontFamilyName = FontFamilyName;
            ButtonFontFamilyName = FontFamilyName;

            SpriteBoxFontSizeName = FontSizeName;
            EncountersFontSizeName = FontSizeName;
            StatsFontSizeName = FontSizeName;
            ButtonFontSizeName = FontSizeName;

            SettingsVersion = 2;
        }

        [JsonIgnore] public Color SpriteBoxBorderColor => FromArgbInt(SpriteBoxBorderColorArgb);
        [JsonIgnore] public Color SpriteBoxTextColor => FromArgbInt(SpriteBoxTextColorArgb);
        [JsonIgnore] public Color EncountersBorderColor => FromArgbInt(EncountersBorderColorArgb);
        [JsonIgnore] public Color EncountersTextColor => FromArgbInt(EncountersTextColorArgb);
        [JsonIgnore] public Color StatsBorderColor => FromArgbInt(StatsBorderColorArgb);
        [JsonIgnore] public Color StatsTextColor => FromArgbInt(StatsTextColorArgb);
        [JsonIgnore] public Color ButtonBorderColor => FromArgbInt(ButtonBorderColorArgb);
        [JsonIgnore] public Color ButtonTextColor => FromArgbInt(ButtonTextColorArgb);

        [JsonIgnore]
        public Color TextColor =>
            FromArgbInt(TextColorArgb);

        [JsonIgnore]
        public Color BorderColor =>
            FromArgbInt(BorderColorArgb);

        [JsonIgnore]
        public Color SpriteBoxBackgroundColor =>
            FromArgbInt(SpriteBoxBackgroundColorArgb);

        [JsonIgnore] public Color SpriteRowBackgroundColor => FromArgbInt(SpriteRowBackgroundColorArgb);
        [JsonIgnore] public Color SpriteRowBorderColor => FromArgbInt(SpriteRowBorderColorArgb);
        [JsonIgnore] public Color MenuTextColor => FromArgbInt(MenuTextColorArgb);
        [JsonIgnore] public Color MenuHighlightColor => FromArgbInt(MenuHighlightColorArgb);

        [JsonIgnore]
        public IReadOnlyList<Color> CustomGradientColors =>
            CustomGradientColorArgbs
                .Select(FromArgbInt)
                .ToList();

        [JsonIgnore]
        public Color ButtonColor =>
            FromArgbInt(ButtonColorArgb);

        [JsonIgnore]
        public Color StatsBackgroundColor =>
            FromArgbInt(StatsBackgroundColorArgb);

        [JsonIgnore]
        public Color EncountersBackgroundColor =>
            FromArgbInt(EncountersBackgroundColorArgb);

        [JsonIgnore]
        public Color HeaderBackgroundColor =>
            FromArgbInt(HeaderBackgroundColorArgb);

        internal static int ToArgbInt(Color color) =>
            (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;

        internal static Color FromArgbInt(int argb) =>
            Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
    }
}