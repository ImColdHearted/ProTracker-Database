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

        [JsonIgnore]
        public Color TextColor =>
            FromArgbInt(TextColorArgb);

        [JsonIgnore]
        public Color BorderColor =>
            FromArgbInt(BorderColorArgb);

        [JsonIgnore]
        public Color SpriteBoxBackgroundColor =>
            FromArgbInt(SpriteBoxBackgroundColorArgb);

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