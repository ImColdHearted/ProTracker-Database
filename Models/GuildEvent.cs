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

        // §103 rewards block (see the Events board reference design) - all
        // optional, all defaulting to "nothing", so every post saved before
        // these fields existed loads unchanged with no migration: an item
        // name typed free-form, a second Pokemon picked as the prize (as
        // opposed to PokemonName above, the post's SUBJECT), and a
        // Pokédollar amount. Zero/empty means "not part of this post".
        public string ItemReward { get; set; } = string.Empty;
        public string PokemonReward { get; set; } = string.Empty;
        public long PokeDollars { get; set; }

        // §107 event-button switches, set by the two circular markers on the
        // Create Event form. Both default to TRUE, and that default is what
        // makes every post saved before §107 existed keep exactly the
        // behaviour it already had: System.Text.Json only assigns properties
        // the JSON actually contains, so a legacy post's missing keys leave
        // these initializers alone and its card still carries both buttons.
        // No migration pass, same as the §103 rewards block above.
        //
        // Off means the button is left OFF that post's card entirely rather
        // than shown greyed out - a permanently dead control on every card
        // reads as a broken button, while an absent one reads as "this event
        // does not do that", which is what the poster actually chose. The two
        // are independent on purpose: submissions with entries hidden is a
        // legitimate blind-entry event, and entries with submissions closed is
        // how a finished event keeps its results readable.
        public bool AllowPokemonSubmissions { get; set; } = true;
        public bool AllowViewEntries { get; set; } = true;

        // §143. How many entries the events server holds for this post,
        // filled in when the board is fetched from it; 0 for a post that
        // only exists on this machine. The card shows the larger of this
        // and the entries cached locally, so a count is never lower than
        // what View Entries would list.
        public int EntryCount { get; set; }
    }
}
