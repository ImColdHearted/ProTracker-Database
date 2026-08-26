using System;

namespace Foot_Tracker.Models
{
    public enum GuildEventType
    {
        Announcement,
        Giveaway,
        Meetup,
        CommunityHunting,
        CommunityExtermination,
        PvpTournament,
        DungeonNight,
    }

    /// <summary>
    /// One post on the Events board (see EventsWindow/EventsViewModel and
    /// GuildEventService) - a giveaway, a "let's meet up and hunt together"
    /// invite, or a plain announcement.
    ///
    /// Deliberately just a message someone chose to write and post, nothing
    /// inferred or collected automatically about what a player is doing.
    /// That distinction is the whole point of this feature: a live "here's
    /// what everyone's up to" feed would expose people's activity whether
    /// they meant to share it or not, which is the shape of tool that feels
    /// like surveillance and is also the shape of tool most likely to run
    /// into PRO's "external software that gives an advantage" rule. A post
    /// someone deliberately wrote and sent doesn't have either problem - see
    /// MIGRATION_GUIDE.md for the fuller reasoning.
    /// </summary>
    public class GuildEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public GuildEventType Type { get; set; } = GuildEventType.Announcement;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string PostedBy { get; set; } = string.Empty;
        public DateTime PostedAtUtc { get; set; }

        // Optional - most posts aren't about a specific Pokemon (a meetup time,
        // a plain announcement). Empty string means "none chosen", not missing
        // data, so older saved posts from before this field existed just come
        // back as "" on load with no migration needed.
        public string PokemonName { get; set; } = string.Empty;
    }
}
