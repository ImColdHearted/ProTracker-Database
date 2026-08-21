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

        [JsonIgnore]
        public Color TextColor =>
            FromArgbInt(TextColorArgb);

        [JsonIgnore]
        public Color BorderColor =>
            FromArgbInt(BorderColorArgb);

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