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
                        Color.FromArgb(15, 24, 90),

                    "blood" =>
                        Color.FromArgb(70, 10, 10),

                    "slate" =>
                        Color.FromArgb(45, 45, 50),

                    "pride" =>
                        Color.FromArgb(40, 90, 120),

                    "pink" =>
                        Color.FromArgb(190, 55, 125),

                    "violet" =>
                        Color.FromArgb(10, 85, 65),

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