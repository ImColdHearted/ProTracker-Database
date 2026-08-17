using System.Drawing;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    public class AppearanceSettings
    {
        public string BackgroundId { get; set; } =
            "Midnight";

        public bool UseCustomBackground { get; set; }

        public string CustomBackgroundPath { get; set; } =
            string.Empty;

        public int CustomBackgroundColorArgb { get; set; } =
            Color.Black.ToArgb();

        [JsonIgnore]
        public Color BackgroundColor
        {
            get
            {
                if (UseCustomBackground)
                {
                    return Color.FromArgb(
                        CustomBackgroundColorArgb);
                }

                return BackgroundId
                    .ToLowerInvariant() switch
                {
                    "midnight" =>
                        Color.FromArgb(10, 15, 61),

                    "blood" =>
                        Color.FromArgb(83, 21, 13),

                    "slate" =>
                        Color.FromArgb(89, 87, 87),

                    "pride" =>
                        Color.FromArgb(77, 165, 21),

                    "pink" =>
                        Color.FromArgb(190, 55, 125),

                    "violet" =>
                        Color.FromArgb(144, 26, 190),

                    _ =>
                        Color.FromArgb(15, 24, 90)
                };
            }
        }

        public int TextColorArgb { get; set; } =
            Color.White.ToArgb();

        public int BorderColorArgb { get; set; } =
            Color.White.ToArgb();

        [JsonIgnore]
        public Color TextColor =>
            Color.FromArgb(TextColorArgb);

        [JsonIgnore]
        public Color BorderColor =>
            Color.FromArgb(BorderColorArgb);
    }
}