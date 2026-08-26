using Avalonia.Media;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    // Ported from WinForms. System.Drawing.Color -> Avalonia.Media.Color.
    // Argb ints are stored the same way WinForms Color.ToArgb() produced them
    // (0xAARRGGBB), so existing saved appearance-settings.json files stay compatible.
    public class AppearanceSettings
    {
        public string BackgroundId { get; set; } =
            "Midnight";

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

                return BackgroundId
                    .ToLowerInvariant() switch
                {
                    "midnight" =>
                        Color.FromRgb(10, 15, 61),

                    "blood" =>
                        Color.FromRgb(83, 21, 13),

                    "slate" =>
                        Color.FromRgb(89, 87, 87),

                    "pride" =>
                        Color.FromRgb(77, 165, 21),

                    "pink" =>
                        Color.FromRgb(190, 55, 125),

                    "violet" =>
                        Color.FromRgb(144, 26, 190),

                    _ =>
                        Color.FromRgb(15, 24, 90)
                };
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

        // "Default" means "use the app's built-in base size" rather than overriding
        // it - see ThemeManager.BuildFontSize.
        public string FontSizeName { get; set; } =
            "Default";

        // "None" (default) means no shadow. Applies specifically to the hunting
        // sprite border boxes on MainWindow (Currently Hunting/Current Encounter/
        // Previous Encounter) - see ThemeManager.BorderShadowCatalog.
        public string BorderShadowName { get; set; } =
            "None";

        // Fill color for the hunting sprite border boxes on MainWindow - they're
        // fully transparent by default (Colors.Transparent), same as before this
        // setting existed. Lets a sprite stay visible against a busy/light custom
        // background image instead of the background showing straight through
        // the box.
        public int SpriteBoxBackgroundColorArgb { get; set; } =
            ToArgbInt(Colors.Transparent);

        // Tint for Border Shadow above - previously always black at whatever
        // opacity the chosen preset (Light/Medium/Strong) baked in. The preset
        // still controls offset/blur/spread/alpha; this only supplies the RGB,
        // so "Strong" stays more opaque than "Light" no matter what color is
        // picked here. See ThemeManager.BuildBorderShadow.
        public int BoxShadowColorArgb { get; set; } =
            ToArgbInt(Colors.Black);

        // Text Shadow applies globally to every TextBlock in the app (see
        // Style Selector="TextBlock" in App.axaml) - same reach as
        // FontFamilyName/FontSizeName above, unlike BorderShadowName, which only
        // ever affects the hunting-sprite boxes. "None" (default) means no
        // shadow, same shape as BorderShadowName right above - see
        // ThemeManager.TextShadowCatalog for the Small/Medium/Large presets.
        // (A "Text Highlight" toggle briefly existed alongside this - removed
        // after trying it, since a TextBlock's Background fills its whole
        // layout box rather than hugging the glyphs, which didn't look good in
        // practice.)
        public string TextShadowName { get; set; } =
            "None";

        public int TextShadowColorArgb { get; set; } =
            ToArgbInt(Colors.Black);

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
        public Color BoxShadowColor =>
            FromArgbInt(BoxShadowColorArgb);

        [JsonIgnore]
        public Color TextShadowColor =>
            FromArgbInt(TextShadowColorArgb);

        [JsonIgnore]
        public IReadOnlyList<Color> CustomGradientColors =>
            CustomGradientColorArgbs
                .Select(FromArgbInt)
                .ToList();

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