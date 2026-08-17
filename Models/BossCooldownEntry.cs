using System;

namespace Foot_Tracker.Models
{
    public class BossCooldownEntry
    {
        public string BossName { get; set; } =
            string.Empty;

        public DateTime LastDefeated { get; set; }

        public DateTime ReadyAt { get; set; }

        public TimeSpan TimeRemaining =>
            ReadyAt > DateTime.Now
                ? ReadyAt - DateTime.Now
                : TimeSpan.Zero;


    }
    public class BossCooldownDefinition
    {
        public string BossId { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;

        public string NPCPicture { get; set; } =
            string.Empty;

        public int BossCooldown { get; set; }
    }
}