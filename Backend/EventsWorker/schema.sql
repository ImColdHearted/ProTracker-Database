-- Pro Tracker & Database - Events board schema (D1 / SQLite).
-- Run once in the D1 dashboard: your database -> Console -> paste -> Execute.
-- Safe to run again: every statement is IF NOT EXISTS.

CREATE TABLE IF NOT EXISTS schema_version (
  version INTEGER NOT NULL
);

INSERT INTO schema_version (version)
SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);

CREATE TABLE IF NOT EXISTS events (
  id                         TEXT PRIMARY KEY,           -- 32 hex chars, server-generated
  type                       TEXT NOT NULL,              -- GuildEventType name
  title                      TEXT NOT NULL,
  message                    TEXT NOT NULL,              -- max 75 chars, the card's rule
  posted_by                  TEXT NOT NULL,
  posted_at_utc              TEXT NOT NULL,              -- ISO-8601 UTC, server clock
  pokemon_name               TEXT NOT NULL DEFAULT '',
  item_reward                TEXT NOT NULL DEFAULT '',
  pokemon_reward             TEXT NOT NULL DEFAULT '',
  poke_dollars               INTEGER NOT NULL DEFAULT 0,
  allow_pokemon_submissions  INTEGER NOT NULL DEFAULT 1,
  allow_view_entries         INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_events_posted ON events (posted_at_utc DESC);

CREATE TABLE IF NOT EXISTS entries (
  id                TEXT PRIMARY KEY,                    -- 32 hex chars, server-generated
  event_id          TEXT NOT NULL,
  username          TEXT NOT NULL,
  pokemon_name      TEXT NOT NULL DEFAULT '',
  submitted_at_utc  TEXT NOT NULL,                       -- ISO-8601 UTC, server clock
  submitter_hash    TEXT NOT NULL                        -- SHA-256 of the tracker's install token; never the token
);

CREATE INDEX IF NOT EXISTS idx_entries_event     ON entries (event_id, submitted_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_entries_submitter ON entries (submitter_hash, submitted_at_utc DESC);

-- Section 150: presence. One row per tracker with a hunt running, keyed by a
-- random id the tracker makes at each start (never its install token). Rows
-- older than fifteen minutes are pruned on every write; the admin route only
-- ever returns a count. The Worker also creates this table on first use, so
-- an existing database does not need this statement run by hand.
CREATE TABLE IF NOT EXISTS presence (
  run_id         TEXT PRIMARY KEY,                       -- 32 hex chars, per app start
  last_seen_utc  TEXT NOT NULL,                          -- ISO-8601 UTC, server clock
  version        TEXT                                    -- section 231; null from clients older than it
);

CREATE INDEX IF NOT EXISTS idx_presence_seen ON presence (last_seen_utc);

-- Section 151: one sample per ten-minute bucket - the live count at that
-- moment, nothing else. Eight days are kept; the admin route summarises the
-- last seven. Created by the Worker on first use as well.
-- Section 153: delegated admin logins. Only digests of client-derived
-- verifiers are stored - never a password, never anything reversible. The
-- Worker also creates this table on first use, so running this file again
-- is never required.
CREATE TABLE IF NOT EXISTS admin_logins (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  username         TEXT NOT NULL UNIQUE COLLATE NOCASE,
  verifier_digest  TEXT NOT NULL,
  can_view_status  INTEGER NOT NULL DEFAULT 0,
  created_utc      TEXT NOT NULL,
  last_used_utc    TEXT,
  revoked          INTEGER NOT NULL DEFAULT 0,
  failed_attempts  INTEGER NOT NULL DEFAULT 0,
  locked_until_utc TEXT
);

CREATE TABLE IF NOT EXISTS presence_samples (
  bucket_utc      TEXT PRIMARY KEY,                      -- "2026-09-01T06:10", UTC
  sampled_at_utc  TEXT NOT NULL,
  trackers        INTEGER NOT NULL
);

-- Section 226. Report a Problem deliveries. This table is the rate limiter
-- and nothing else: one row per report that actually reached Discord,
-- holding the SHA-256 of the sending install token and the time. No
-- screenshot, no log, no note and no player is stored here or anywhere else
-- on the server - the files go straight through to the channel. Rows older
-- than two days are deleted on every write. The Worker creates this table
-- on first use, so running this file again is never required.
CREATE TABLE IF NOT EXISTS reports (
  id           TEXT PRIMARY KEY,
  sender_hash  TEXT NOT NULL,
  sent_at_utc  TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_reports_sender ON reports (sender_hash, sent_at_utc);

-- Section 232. World Quests, read out of the Discord channel PRO's
-- announcements are followed into. History is kept on purpose: quests happen
-- about once a month, which makes "what was last month's" a question worth
-- being able to answer. raw holds the message text whether or not it parsed,
-- so a format change can be diagnosed from what actually arrived rather than
-- from memory. parsed is 0 when the message mentioned a World Quest but no
-- Pokemon could be read from it. The Worker creates this on first use.
-- Section 234: a row whose message_id starts "test-" is a quest the admin
-- started from the console for testing, not one PRO announced. A real Discord
-- message id is all digits, so the two can never be confused, and
-- DELETE /v1/admin/world-quests/test removes the test ones and only those.
-- Section 298: ended_utc is set when the admin declares a quest finished
-- because both servers have met the goal. A quest ends 24 hours after it
-- starts OR the moment the community goal is met, and nothing the Worker can
-- read says which happened - so the second condition arrives as a judgement
-- from someone who played it, not as a fact off the wire. NULL is the normal
-- state; see POST /v1/admin/world-quests/{id}/end.
CREATE TABLE IF NOT EXISTS world_quests (
  message_id    TEXT PRIMARY KEY,                  -- the Discord message, and the dedupe key
  pokemon       TEXT,
  total_ivs     INTEGER,
  single_ivs    INTEGER,
  avg_subs      INTEGER,
  lowest_tier   TEXT,
  reward        TEXT,
  duration      TEXT,                              -- as printed
  end_time_text TEXT,                              -- as printed, no timezone, display only
  started_utc   TEXT,                              -- the message's own timestamp
  ends_utc      TEXT,                              -- started_utc plus the duration
  parsed        INTEGER NOT NULL DEFAULT 0,
  raw           TEXT,
  seen_utc      TEXT NOT NULL,
  ended_utc     TEXT                             -- section 298: declared over by the admin, NULL until then
);

CREATE INDEX IF NOT EXISTS idx_quests_started ON world_quests (started_utc DESC);

-- Section 239: what PRO announced, read out of their own #announcements
-- channel followed into the admin's Discord server. source is "discord" for
-- anything the poll found and "manual" for a row added through
-- POST /v1/admin/announcements - the seed, since Discord relays nothing that
-- was posted before the channel was followed. image_url is empty unless the
-- post carried a picture on a host the Worker was willing to pass on; see
-- ANNOUNCE_IMAGE_HOSTS. The Worker creates this on first use.
CREATE TABLE IF NOT EXISTS announcements (
  id         TEXT PRIMARY KEY,                 -- the Discord message id, or "manual-..."
  author     TEXT,                             -- the embed's author, not the relaying webhook
  body       TEXT,                             -- content and embeds flattened together
  image_url  TEXT,                             -- https, allow-listed host, or empty
  link       TEXT,
  posted_utc TEXT NOT NULL,
  source     TEXT NOT NULL,                    -- discord | manual
  seen_utc   TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_announcements_posted ON announcements (posted_utc DESC);

-- Section 397: the Spawns pages under the tracker's Game Data menu. One row
-- per map the admin filed under a region (Kanto, Johto, Hoenn, Sinnoh or
-- Other); pokemon is the page itself - a JSON array of the species the
-- admin's Pokedex scans named the map for, each with land/water,
-- morning/day/night and membersOnly - replaced whole on every publish.
-- map_key is the name folded to [a-z0-9], the key both sides compute, so a
-- map cannot be filed twice under two spellings. Master token only to
-- write; every tracker reads. The Worker creates this on first use.
CREATE TABLE IF NOT EXISTS spawn_maps (
  map_key     TEXT PRIMARY KEY,                 -- "route1", "mtmoon1f"
  region      TEXT NOT NULL,                    -- Kanto | Johto | Hoenn | Sinnoh | Other
  map         TEXT NOT NULL,                    -- the name as the admin typed it
  pokemon     TEXT NOT NULL,                    -- JSON array, sorted by name
  marker      TEXT,                             -- section 399/402: the map's boxes on the region picture, a JSON array of {x,y,width,height,imageWidth,imageHeight} (a 399 row holds one object), or NULL
  linked_to   TEXT,                             -- section 412: the name of the map whose spot this page shares (a safari zone's areas behind one box), or NULL
  updated_utc TEXT NOT NULL,
  updated_by  TEXT NOT NULL                     -- "master"
);

-- section 409: where each boss stands on the world picture - one point per
-- boss, in the picture's pixels with the picture's size, as a map's box.
-- boss_id is the boss file's name folded to [a-z0-9]; boss its display name.
CREATE TABLE IF NOT EXISTS boss_pins (
  boss_id      TEXT PRIMARY KEY,
  boss         TEXT NOT NULL,
  x            INTEGER NOT NULL,
  y            INTEGER NOT NULL,
  image_width  INTEGER NOT NULL,
  image_height INTEGER NOT NULL,
  updated_utc  TEXT NOT NULL,
  updated_by   TEXT NOT NULL
);

-- Section 429: the lowest and highest level met per species per map, from
-- every tracker whose owner opted in. Widened by public posts, never
-- narrowed except by the master token's DELETE.
CREATE TABLE IF NOT EXISTS spawn_levels (
  map_key     TEXT NOT NULL,                    -- "route1", as spawn_maps keys it
  species     TEXT NOT NULL COLLATE NOCASE,     -- the library's name ("Linoone-Galarian"); one row however a client cases it
  min_level   INTEGER NOT NULL,
  max_level   INTEGER NOT NULL,
  samples     INTEGER NOT NULL DEFAULT 0,       -- sightings folded in, for a sense of how sure the range is
  updated_utc TEXT NOT NULL,
  PRIMARY KEY (map_key, species)
);
