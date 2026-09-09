// Pro Tracker & Database - Events board backend.
// A single-file Cloudflare Worker over a D1 (SQLite) database. No dependencies:
// paste it into the dashboard's Worker editor as-is. See README.md beside it
// for the four setup steps, and MIGRATION_GUIDE.md section 143 for the design.
//
// Bindings this Worker expects (Settings -> Bindings / Variables and Secrets):
//   DB           D1 database binding (the "protracker" database)
//   ADMIN_TOKEN  secret - the MASTER admin credential. It lives here and
//                nowhere else; the tracker asks the admin to type it and
//                keeps it in memory for the session. A plain Worker secret is
//                the expected shape; a Secrets Store binding under the same
//                name works too (see adminSecret). Since section 153 it can
//                delegate: it is the only credential that can manage the
//                admin logins below, which in turn can post and remove
//                events without it.
//   QUEST_WEBHOOK  secret, optional - section 232. Where World Quest notices
//                go. Falls back to PRESENCE_WEBHOOK when unset.
//   DISCORD_BOT_TOKEN  secret, optional - section 232. A Discord BOT token,
//                which is not a webhook: webhooks only write, and reading a
//                channel needs a bot. Only ever used to read the one channel
//                below, and the bot needs nothing beyond View Channel and
//                Read Message History on it.
//   WORLD_QUEST_CHANNEL_ID  plain variable - section 232. The channel PRO's
//                announcements are followed into. Not a secret; a channel id
//                is useless without the token.
//   PRESENCE_WEBHOOK  secret, optional - section 230. A Discord webhook URL
//                for the channel the hourly "how many are hunting" note
//                goes to. A different channel from the reports one, and so
//                a different secret. Unset and nothing is ever posted.
//                Needs a Cron Trigger to fire it; see scheduled below.
//   REPORT_WEBHOOK  secret, optional - section 226. A Discord webhook URL.
//                Set it and the tracker's "Report a Problem" button can
//                deliver its bundle straight to a private channel; leave it
//                unset and /v1/report answers 503 and the tracker falls
//                back to the message it has always shown, which tells the
//                player the files are in Downloads. The URL lives here and
//                nowhere else - a webhook URL is a write credential, and a
//                decompilable client is exactly where it must not be.
//
// Routes (all JSON, all under /v1):
//   GET    /v1/health
//   GET    /v1/events                          board, newest first, with entry counts
//   POST   /v1/events                          admin: post an event
//   DELETE /v1/events/{id}                     admin: remove an event and its entries
//   GET    /v1/events/{id}/entries             entries for one event, newest first
//   POST   /v1/events/{id}/entries             player: submit an entry (needs X-Install-Token)
//   DELETE /v1/events/{id}/entries/{entryId}   admin, or the tracker that submitted it
//   POST   /v1/report                          section 226: a player's Report a Problem bundle, relayed to Discord
//   POST   /v1/admin/presence-post             section 230: post the hunting count to Discord now, rather than on the hour
//   GET    /v1/world-quests                    section 232: the running World Quest, and the last few
//   POST   /v1/admin/world-quests/poll         section 232: read the channel now, rather than on the cron
//   POST   /v1/admin/world-quests/test         section 234: start a quest of the admin's own, for testing
//   GET    /v1/announcements                   section 239: what PRO announced, newest first
//   POST   /v1/admin/announcements/poll        section 239: read the announcements channel now
//   POST   /v1/admin/announcements             section 239: add one by hand (the seed, and anything PRO did not post)
//   DELETE /v1/admin/announcements/{id}        section 239: remove one
//   DELETE /v1/admin/world-quests/test         section 234: remove every test quest
//   POST   /v1/presence                        section 150: a hunting tracker's anonymous heartbeat
//   GET    /v1/admin/presence                  admin with status permission: how many are hunting, plus a week of history
//   GET    /v1/admin/logins                    master only: the delegated admin logins (never a digest)
//   POST   /v1/admin/logins                    master only: create a login - or reset one, by posting its name again
//   POST   /v1/admin/logins/{id}/revoke        master only: revoke a login
//
// Player identity is a per-install token the tracker generates once and sends
// as X-Install-Token. Only its SHA-256 hash is stored, next to each entry, so
// the same tracker can delete its own entries and nobody can read anyone's
// token out of the database. There are no accounts and no passwords.
//
// Presence (section 150) is deliberately thinner still: a tracker with a
// hunt running posts a random id made fresh at each app start - not the
// install token, not a name, not what it hunts - every five minutes. The
// table holds that id and a last-seen time, rows older than fifteen minutes
// are pruned on every write, and the only reader is the admin route, which
// answers with a count. Nothing in it can be joined to an entry or a person.
//
// Admin logins (section 153) let the master token delegate: a named login
// with a password can post and remove events (and, per login, read the
// presence count) without the master token ever leaving the owner's hands.
// The Worker never sees a password. The tracker derives
//   verifier = PBKDF2-HMAC-SHA256(password,
//              salt = SHA-256("ProTracker events admin login|" + lowercase username),
//              210000 iterations, 32 bytes, lowercase hex)
// on the admin's machine and sends that; D1 stores only SHA-256(verifier).
// A stolen database therefore still costs the full 210k-iteration derive per
// password guess, and this Worker's CPU budget never pays for a single one.
// Ten wrong passwords in a row lock a login for fifteen minutes; revocation
// is immediate; posting an existing name again resets its password,
// permission and lock. No ADMIN_TOKEN configured still means no admin at
// all - logins included.
//
// History (section 151) keeps only numbers: once per ten-minute bucket the
// live count is written to presence_samples (bucket, time, trackers) - from
// whichever heartbeat arrives first in that bucket, and from the optional
// cron trigger, which also records the quiet buckets. Samples are kept for
// eight days; the admin route summarises the last seven (peak, average when
// anyone is hunting, average per hour of the day). No id ever reaches it.

const SCHEMA_VERSION = 2;

// §234. The last MIGRATION_GUIDE.md section this file carries, reported by
// /v1/health. SCHEMA_VERSION answers "can an older tracker still read this
// server", which is a different question and has not moved since §143 - it is
// no use at all for "did my redeploy actually land". That question now has an
// answer, because the tracker and the Worker ship separately and a client
// built for a route the deployed Worker has never heard of fails with a flat
// 404 that says nothing about which half is behind. Bump this in any section
// that changes this file.
const WORKER_SECTION = 241;
const MAX_BODY_BYTES = 16 * 1024;
const MAX_EVENTS_RETURNED = 200;
const MAX_ENTRIES_RETURNED = 1000;

// Field limits - the tracker enforces the same ones on its side.
const LIMITS = {
  title: 80,
  message: 75,        // the board card's own "max 75 characters" rule
  postedBy: 32,
  username: 32,
  pokemonName: 40,
  itemReward: 60,
  pokemonReward: 40,
  installToken: { min: 32, max: 128 },
};

// Submission caps per install token: enough for any honest player, small
// enough that one misbehaving tracker cannot fill the board.
const CAPS = {
  entriesPerEventPerInstall: 20,
  entriesPerHourPerInstall: 30,
};

// Presence window and retention (section 150), in minutes.
// Section 226. Report a Problem bundles. Deliberately small: the tracker
// sends at most its own screenshot, the PRO client screenshot, the tracking
// check and the log. Discord's own ceiling for a plain webhook has
// historically sat below the limit it advertises to people, so these stay
// well under it - a report that will not fit is not worth failing over when
// the files are already sitting in the player's Downloads folder either way.
const REPORT_MAX_FILES = 4;
const REPORT_MAX_FILE_BYTES = 4 * 1024 * 1024;
const REPORT_MAX_TOTAL_BYTES = 8 * 1024 * 1024;
const REPORT_MAX_NOTE = 500;
const REPORT_ALLOWED = /\.(png|txt|log)$/i;

// Per install, so one tracker cannot flood the channel. There are no
// accounts here; this is the same X-Install-Token identity entries use.
const REPORT_CAPS = { perHour: 4, perDay: 12 };

// §229. The shortest gap allowed between two reports from one tracker.
// The caps above bound a day; this bounds a minute, which is the shape
// spam actually takes - four in the same breath is within the hourly cap
// and is still four notifications about one thing. The tracker knows this
// number too and refuses locally before spending an upload on it, but the
// enforcement that matters is here, where the client cannot argue.
const REPORT_MIN_INTERVAL_MINUTES = 10;

// §231. A version string is short and boring by nature; anything longer or
// stranger than this is not one, and is counted as unknown rather than
// given a row of its own in the breakdown.
const MAX_VERSION = 32;
const VERSION_SHAPE = /^[0-9A-Za-z][0-9A-Za-z.+-]{0,31}$/;

const PRESENCE_WINDOW_MINUTES = 10;
const PRESENCE_PRUNE_MINUTES = 15;

// History (section 151): one sample per ten-minute bucket, a week summarised,
// eight days kept so the week is always complete.
const SAMPLE_MINUTES = 10;
const HISTORY_DAYS = 7;
const SAMPLE_RETENTION_DAYS = 8;

// Admin logins (section 153).
const LOGIN_USERNAME = /^[A-Za-z0-9._-]{3,24}$/;
const LOGIN_VERIFIER = /^[0-9a-f]{64}$/;
const LOGIN_LOCK_AFTER_FAILURES = 10;
const LOGIN_LOCK_MINUTES = 15;
const MAX_ADMIN_LOGINS = 50;

const EVENT_TYPES = new Set([
  "Announcement", "Giveaway", "Meetup", "CommunityHunting",
  "CommunityExtermination", "PvpTournament", "DungeonNight",
]);

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (err) {
      // A thrown error is a bug or an outage, never a client's fault: say so
      // without leaking internals.
      console.error("unhandled", err && err.stack ? err.stack : String(err));
      return json({ error: "The events server hit an internal error." }, 500);
    }
  },

  // Section 151, optional: a Cron Trigger (Settings -> Trigger events, e.g.
  // "*/10 * * * *") records a sample every ten minutes whether or not a
  // heartbeat arrives - so quiet stretches show up as real zeros rather
  // than gaps. Without it, samples come from heartbeats alone, and a bucket
  // with no heartbeat is treated as zero by the summary anyway.
  async scheduled(event, env, ctx) {
    try {
      await ensurePresenceTable(env);
      await recordSampleIfDue(env);
    } catch (err) {
      console.error("scheduled sample failed", err && err.stack ? err.stack : String(err));
    }

    // §230: its own try, so a Discord outage cannot cost the sampling above
    // and a sampling failure cannot cost the post. The hourly gate lives
    // inside postPresenceUpdate rather than in the cron expression, so this
    // is correct at whatever interval the trigger fires - the ten minutes
    // §151 suggests included.
    try {
      await postPresenceUpdate(env, false);
    } catch (err) {
      console.error("scheduled presence post failed", err && err.stack ? err.stack : String(err));
    }

    // §232: its own try again. A World Quest fires about once a month, so
    // this poll finds nothing the overwhelming majority of the time - and
    // must cost nothing else when it fails.
    try {
      const quests = await pollWorldQuests(env);
      if (quests.checked && quests.stored > 0) {
        console.log("world quests stored", quests.stored, "unparsed", quests.unparsed);
      }

      // §239. Its own try below rather than sharing this one: an
      // announcements channel that is not set up must not stop the quest
      // poll, and a quest poll that threw must not stop this.
      try {
        const news = await pollAnnouncements(env);
        if (news.checked && news.stored > 0) {
          console.log("announcements stored", news.stored);
        }
      } catch (err) {
        console.error("announcements poll threw", err && err.message ? err.message : String(err));
      }
    } catch (err) {
      console.error("scheduled world quest poll failed", err && err.stack ? err.stack : String(err));
    }
  },
};

async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, "") || "/";
  const method = request.method.toUpperCase();

  if (path === "/v1/health") {
    if (method !== "GET") return methodNotAllowed("GET");
    // adminTokenConfigured says whether this Worker can see a usable
    // ADMIN_TOKEN - the one setup step a browser cannot otherwise verify.
    // It is a yes/no, never the value; a 503 on posting says the same thing.
    const secret = await adminSecret(env);
    // §226: same shape as adminTokenConfigured - a yes/no, never the value.
    const webhook = await reportWebhook(env);
    const presence = await presenceWebhook(env);
    return json({
      ok: true,
      schema: SCHEMA_VERSION,
      // §234. Which build is actually deployed - see WORKER_SECTION.
      section: WORKER_SECTION,
      time: nowIso(),
      adminTokenConfigured: secret !== null,
      reportWebhookConfigured: webhook !== null,
      presenceWebhookConfigured: presence !== null,
      // §232: both halves, because either one missing means no quests.
      worldQuestsConfigured: (await discordBotToken(env)) !== null && questChannelId(env) !== null,
      // §239: the same yes/no for the announcements channel. Both halves,
      // because either one missing means no announcements.
      announcementsConfigured: (await discordBotToken(env)) !== null && announcementsChannelId(env) !== null,
    });
  }

  let m;

  if (path === "/v1/events") {
    if (method === "GET") return listEvents(env);
    if (method === "POST") return postEvent(request, env);
    return methodNotAllowed("GET, POST");
  }

  if ((m = path.match(/^\/v1\/events\/([0-9a-f]{32})$/))) {
    if (method === "DELETE") return deleteEvent(request, env, m[1]);
    return methodNotAllowed("DELETE");
  }

  if ((m = path.match(/^\/v1\/events\/([0-9a-f]{32})\/entries$/))) {
    if (method === "GET") return listEntries(request, env, m[1]);
    if (method === "POST") return postEntry(request, env, m[1]);
    return methodNotAllowed("GET, POST");
  }

  if ((m = path.match(/^\/v1\/events\/([0-9a-f]{32})\/entries\/([0-9a-f]{32})$/))) {
    if (method === "DELETE") return deleteEntry(request, env, m[1], m[2]);
    return methodNotAllowed("DELETE");
  }

  // §207: which counterpart events are running right now. The read is
  // public and unauthenticated on purpose - every tracker asks for it every
  // few minutes and it is not a secret, it is a notice board. The write is
  // ordinary admin, the same level as posting an event.
  if (path === "/v1/active-events") {
    if (method === "GET") return getActiveEvents(env);
    if (method === "PUT") return putActiveEvents(request, env);
    return methodNotAllowed("GET, PUT");
  }

  // §226. Unauthenticated on purpose - the people who need to report a
  // problem are players, not admins, and there is no account to sign in to.
  // What stands in for authentication is the install token (which caps how
  // often one tracker can post), the size caps, and the fact that the only
  // thing this route can do is put a file in one private channel.
  if (path === "/v1/report") {
    if (method === "POST") return postReport(request, env);
    return methodNotAllowed("POST");
  }

  if (path === "/v1/presence") {
    if (method === "POST") return postPresence(request, env);
    return methodNotAllowed("POST");
  }

  if (path === "/v1/admin/presence") {
    if (method === "GET") return getPresence(request, env);
    return methodNotAllowed("GET");
  }

  // §230.
  if (path === "/v1/admin/presence-post") {
    if (method === "POST") return postPresenceNow(request, env);
    return methodNotAllowed("POST");
  }

  // §232. Public read, the same reasoning as /v1/active-events: a quest PRO
  // announced in its own Discord is a notice board, not a secret.
  if (path === "/v1/world-quests") {
    if (method === "GET") return getWorldQuests(env);
    return methodNotAllowed("GET");
  }

  if (path === "/v1/admin/world-quests/poll") {
    if (method === "POST") return pollWorldQuestsNow(request, env);
    return methodNotAllowed("POST");
  }

  // §234. A quest of the admin's own making, so the tracker's World Quest
  // window can be exercised on a day PRO is not running one. POST starts it,
  // DELETE removes every test quest ever started.
  // §239. Public read, the same reasoning as the quests and the active-event
  // list: what PRO announces to everyone is a notice board.
  if (path === "/v1/announcements") {
    if (method === "GET") return getAnnouncements(env);
    return methodNotAllowed("GET");
  }

  if (path === "/v1/admin/announcements/poll") {
    if (method === "POST") return pollAnnouncementsNow(request, env);
    return methodNotAllowed("POST");
  }

  if (path === "/v1/admin/announcements") {
    if (method === "POST") return addAnnouncement(request, env);
    return methodNotAllowed("POST");
  }

  if ((m = path.match(/^\/v1\/admin\/announcements\/([A-Za-z0-9_-]{1,64})$/))) {
    if (method === "DELETE") return deleteAnnouncement(request, env, m[1]);
    return methodNotAllowed("DELETE");
  }

  if (path === "/v1/admin/world-quests/test") {
    if (method === "POST") return startTestQuest(request, env);
    if (method === "DELETE") return clearTestQuests(request, env);
    return methodNotAllowed("POST, DELETE");
  }

  if (path === "/v1/admin/logins") {
    if (method === "GET") return listAdminLogins(request, env);
    if (method === "POST") return saveAdminLogin(request, env);
    return methodNotAllowed("GET, POST");
  }

  if ((m = path.match(/^\/v1\/admin\/logins\/(\d{1,10})\/revoke$/))) {
    if (method === "POST") return revokeAdminLogin(request, env, Number(m[1]));
    return methodNotAllowed("POST");
  }

  return json({ error: "No such route." }, 404);
}

// ------------------------------------------------------- world quests

// §232. PRO announces a World Quest in its own Discord, and that channel is
// followed into a channel on this server. This reads those posts and turns
// them into records the tracker can ask for.
//
// Two facts about World Quests shape everything here. They happen about once
// a month, and one post is the only example this parser was written against.
// Together that means a parser tuned tightly to one layout could break in
// September and nobody would find out until October. So it degrades instead
// of failing: whatever it manages to read is stored, the raw text is stored
// beside it either way, and a message that looks like a quest but yields
// nothing gets announced as a parse failure rather than dropped. A half-read
// quest you can see beats a silent miss you cannot.
//
// Reading messages needs a BOT TOKEN, which a webhook is not - webhooks only
// write. That token is a Cloudflare secret like every other credential here,
// and the bot only ever needs View Channel and Read Message History on the
// one channel.
//
// The end time is derived, not parsed. The post prints "Sunday, August 16,
// 2026 7:40" with no timezone, which is not an instant. The message's own
// Discord timestamp is UTC and exact, and the post says how long the quest
// runs, so the end is the message time plus the duration. The printed string
// is kept for display and never used for arithmetic.

const QUEST_CHECK_KEY = "world_quest_seen";
const QUEST_FETCH_LIMIT = 10;
const QUEST_MAX_RAW = 4000;
const QUEST_DEFAULT_HOURS = 24;

// §234. Every message id a test quest has ever been given starts with this,
// which is how the clear route finds them and how anyone reading the table
// can tell one from a quest PRO actually announced. A real Discord snowflake
// is all digits, so nothing PRO posts can ever collide with it.
const QUEST_TEST_PREFIX = "test-";

// §239. PRO's own #announcements, FOLLOWED into the admin's server rather than
// read from PRO's. Discord relays a followed channel as a webhook message
// carrying an embed, so the same flattening questText does is needed here -
// and the real staff author is in the EMBED's author, not the message's, which
// is the webhook.
const ANNOUNCE_CHECK_KEY = "announcements_seen";
const ANNOUNCE_FETCH_LIMIT = 25;
const ANNOUNCE_MAX_BODY = 4000;
const ANNOUNCE_MAX_AUTHOR = 80;
const ANNOUNCE_KEEP = 60;
const ANNOUNCE_MANUAL_PREFIX = "manual-";

// Where an announcement image may come from. A feed the client renders
// pictures out of is a feed that can point the client at any URL on the
// internet, so the hosts are named rather than trusted: Discord's own CDN,
// which is where a relayed embed's image lives, and PRO's forum, which is
// where its own uploads live.
const ANNOUNCE_IMAGE_HOSTS = [
  "cdn.discordapp.com",
  "media.discordapp.net",
  "pokemonrevolution.net",
];

// §234. Random characters appended to the timestamp in a test quest's id. The
// timestamp alone is not unique: two starts inside the same millisecond built
// the same id, the second INSERT was refused as a duplicate, and the route
// still answered 201 describing a quest that was never stored - the console
// would have reported a Corsola quest while the table held a Rattata one. The
// suffix removes the collision; startTestQuest also checks that the row was
// actually new, so the route can never again claim to have stored something it
// did not.
const QUEST_TEST_ID_SUFFIX = 8;

// §234. The share of the community goal that earns the first Mysterious
// Ticket, and the share that earns the second. Checked against the August
// quest, which printed a goal of 148800 and a per-player figure of 744:
// 148800 * 0.005 is exactly 744, and 3% of the same goal is 4464. The tracker
// derives the second tier the same way, so a test quest only needs the goal.
const QUEST_FIRST_TIER_SHARE = 0.005;
const QUEST_SECOND_TIER_SHARE = 0.03;

// §234. What a test quest carries in the fields the parser would have read
// off an announcement. The reward is the real one; the tier is left empty
// because a test quest is not drawn from a real post and inventing a tier
// would be inventing data.
const QUEST_TEST_REWARD = "Mysterious Ticket";
const QUEST_TEST_MAX_NAME = 40;
const QUEST_TEST_MAX_IVS = 100_000_000;

// "A World Quest started" is the line that makes a message worth parsing.
// Kept deliberately loose - it has to survive PRO rewording the rest.
const QUEST_MARKER = /world\s+quest/i;

// Mentioning a World Quest is not the same as announcing one. "A World Quest
// has ended!" mentions one, has no Pokemon in it, and would otherwise be
// reported as a parse failure every single month - an alarm that cries wolf
// on a schedule is worse than no alarm. A message only counts as a start if
// it either yielded a Pokemon or says so.
const QUEST_START_MARKER = /\bstart/i;

// §232. Where quest notices go. A separate secret so they can land in the
// world-quests channel rather than beside the hourly hunting count, and it
// falls back to the presence webhook when unset - one channel is a fine
// place to start, and a parse failure reaching the wrong channel still
// beats it reaching none.
async function questWebhook(env) {
  let value = env.QUEST_WEBHOOK;

  if (value && typeof value === "object" && typeof value.get === "function") {
    try {
      value = await value.get();
    } catch (err) {
      console.error("QUEST_WEBHOOK binding could not be read", err && err.message ? err.message : String(err));
      value = null;
    }
  }

  if (typeof value === "string") {
    const url = value.trim();
    if (/^https:\/\/(discord\.com|discordapp\.com)\/api\/webhooks\//.test(url)) return url;
  }

  return presenceWebhook(env);
}

async function discordBotToken(env) {
  let value = env.DISCORD_BOT_TOKEN;

  if (value && typeof value === "object" && typeof value.get === "function") {
    try {
      value = await value.get();
    } catch (err) {
      console.error("DISCORD_BOT_TOKEN binding could not be read", err && err.message ? err.message : String(err));
      return null;
    }
  }

  return typeof value === "string" && value.trim().length >= 20 ? value.trim() : null;
}

// §239. The channel PRO's #announcements is FOLLOWED into. A plain variable
// like the quest channel's and for the same reason: a channel id is useless
// without the bot token.
function announcementsChannelId(env) {
  const value = typeof env.ANNOUNCEMENTS_CHANNEL_ID === "string" ? env.ANNOUNCEMENTS_CHANNEL_ID.trim() : "";
  return /^\d{5,25}$/.test(value) ? value : null;
}

function questChannelId(env) {
  const value = typeof env.WORLD_QUEST_CHANNEL_ID === "string" ? env.WORLD_QUEST_CHANNEL_ID.trim() : "";
  return /^\d{5,25}$/.test(value) ? value : null;
}

// A followed channel can relay a post as plain content OR as an embed, and
// PRO's own bot may change which without warning. Everything readable is
// flattened into one block of text so the field reader below does not have
// to care which arrived.
function questText(message) {
  const parts = [];

  if (typeof message.content === "string" && message.content.trim()) {
    parts.push(message.content);
  }

  for (const embed of Array.isArray(message.embeds) ? message.embeds : []) {
    if (typeof embed.title === "string" && embed.title.trim()) parts.push(embed.title);
    if (typeof embed.description === "string" && embed.description.trim()) parts.push(embed.description);

    for (const field of Array.isArray(embed.fields) ? embed.fields : []) {
      const name = typeof field.name === "string" ? field.name : "";
      const value = typeof field.value === "string" ? field.value : "";
      if (name || value) parts.push(`${name}: ${value}`);
    }
  }

  return parts.join("\n");
}

// "Pokemon: Mareanie" out of the block, tolerating the bold markers Discord
// leaves in the raw text and any amount of space around the colon.
function questField(text, label) {
  const pattern = new RegExp(
    `^[*_\\s>]*${label.replace(/[.*+?^${}()|[\\]\\\\]/g, "\\\\$&")}[*_\\s]*:[*_\\s]*(.+?)\\s*$`,
    "im"
  );

  const m = text.match(pattern);
  if (!m) return null;

  // Strip Discord's own decoration from the value, and any trailing link
  // list the post appends to the same line.
  const value = m[1].replace(/\*\*/g, "").replace(/[*_`]/g, "").trim();
  return value.length ? value : null;
}

function questNumber(text, label) {
  const raw = questField(text, label);
  if (raw === null) return null;

  const digits = raw.replace(/[,\s]/g, "").match(/^-?\d+/);
  if (!digits) return null;

  const n = Number(digits[0]);
  return Number.isFinite(n) ? n : null;
}

// "24 hours, or until submission goal" -> 24. Anything that does not state
// hours falls back to the 24 every quest so far has run for, which is
// recorded as a fallback rather than presented as read.
function questHours(duration) {
  if (typeof duration !== "string") return { hours: QUEST_DEFAULT_HOURS, assumed: true };

  const h = duration.match(/(\d+(?:\.\d+)?)\s*h/i);
  if (h) return { hours: Number(h[1]), assumed: false };

  const d = duration.match(/(\d+(?:\.\d+)?)\s*d(?:ay)/i);
  if (d) return { hours: Number(d[1]) * 24, assumed: false };

  return { hours: QUEST_DEFAULT_HOURS, assumed: true };
}

/// Returns a quest record, or null when the message is not one. `startedIso`
/// is the message's own Discord timestamp, which is the only trustworthy
/// clock in the whole message.
function parseQuest(message) {
  const text = questText(message);
  if (!text || !QUEST_MARKER.test(text)) return null;

  const pokemon = questField(text, "Pokemon") || questField(text, "Pokémon");
  const duration = questField(text, "Duration");
  const { hours, assumed } = questHours(duration);

  // Mentions a quest, names no Pokemon and does not claim to be starting
  // one: an ending, a reminder, a correction. Not this parser's business.
  if (!pokemon && !QUEST_START_MARKER.test(text)) return null;

  const startedIso = typeof message.timestamp === "string" ? message.timestamp : null;
  const startedMs = startedIso ? Date.parse(startedIso) : NaN;

  const endsIso = Number.isFinite(startedMs)
    ? new Date(startedMs + hours * 3600 * 1000).toISOString()
    : null;

  return {
    messageId: String(message.id || ""),
    pokemon,
    totalIvs: questNumber(text, "Total Individual Values"),
    singleIvs: questNumber(text, "Single Individual Values"),
    averageSubmissions: questNumber(text, "Average submissions"),
    lowestTier: questField(text, "Lowest Tier"),
    reward: questField(text, "Reward"),
    duration,
    // What the post printed, for display only - it carries no timezone and
    // is never used to work out whether a quest is still running.
    endTimeText: questField(text, "End Time"),
    startedUtc: startedIso,
    endsUtc: endsIso,
    durationHours: hours,
    durationAssumed: assumed,
    raw: text.slice(0, QUEST_MAX_RAW),
    // The one field that decides whether this was worth storing at all.
    parsed: Boolean(pokemon),
  };
}

const questsReady = new WeakSet();

async function ensureQuestTable(env) {
  if (questsReady.has(env.DB)) return;

  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS world_quests (
       message_id    TEXT PRIMARY KEY,
       pokemon       TEXT,
       total_ivs     INTEGER,
       single_ivs    INTEGER,
       avg_subs      INTEGER,
       lowest_tier   TEXT,
       reward        TEXT,
       duration      TEXT,
       end_time_text TEXT,
       started_utc   TEXT,
       ends_utc      TEXT,
       parsed        INTEGER NOT NULL DEFAULT 0,
       raw           TEXT,
       seen_utc      TEXT NOT NULL
     )`
  ).run();

  await env.DB.prepare(
    `CREATE INDEX IF NOT EXISTS idx_quests_started ON world_quests (started_utc DESC)`
  ).run();

  questsReady.add(env.DB);
}

/// Returns true when this message was new. The `after` cursor already keeps
/// Discord from handing back a message twice, but announcing off the back of
/// that alone means one lost cursor write turns into a re-announcement of a
/// month-old quest. Asking the table is one extra read on a message that
/// arrives about once a month, and it makes "new" mean new.
async function storeQuest(env, quest) {
  const existing = await env.DB.prepare(
    `SELECT 1 AS present FROM world_quests WHERE message_id = ?`
  ).bind(quest.messageId).first("present");

  if (existing) return false;

  await env.DB.prepare(
    `INSERT INTO world_quests
       (message_id, pokemon, total_ivs, single_ivs, avg_subs, lowest_tier, reward,
        duration, end_time_text, started_utc, ends_utc, parsed, raw, seen_utc)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(message_id) DO NOTHING`
  ).bind(
    quest.messageId,
    quest.pokemon,
    quest.totalIvs,
    quest.singleIvs,
    quest.averageSubmissions,
    quest.lowestTier,
    quest.reward,
    quest.duration,
    quest.endTimeText,
    quest.startedUtc,
    quest.endsUtc,
    quest.parsed ? 1 : 0,
    quest.raw,
    nowIso()
  ).run();

  return true;
}

function questOut(row) {
  return {
    messageId: row.message_id,
    pokemon: row.pokemon,
    totalIvs: row.total_ivs,
    singleIvs: row.single_ivs,
    averageSubmissions: row.avg_subs,
    lowestTier: row.lowest_tier,
    reward: row.reward,
    duration: row.duration,
    endTimeText: row.end_time_text,
    startedUtc: row.started_utc,
    endsUtc: row.ends_utc,
    parsed: Boolean(row.parsed),
  };
}

/// §232. What the tracker asks for: the quest running right now if there is
/// one, and the handful before it. Public and unauthenticated, the same
/// reasoning as /v1/active-events - a quest PRO announced in its own Discord
/// is not a secret, it is a notice board.
async function getWorldQuests(env) {
  await ensureQuestTable(env);

  const { results } = await env.DB.prepare(
    `SELECT * FROM world_quests ORDER BY started_utc DESC LIMIT 12`
  ).all();

  const rows = (results || []).map(questOut);
  const now = Date.now();

  const active = rows.find(
    (q) => q.parsed && q.endsUtc && Date.parse(q.endsUtc) > now && Date.parse(q.startedUtc) <= now
  ) || null;

  return json({ active, recent: rows, asOfUtc: nowIso() });
}

/// Reads the channel and stores anything new. Returns a small summary; the
/// scheduled handler logs it and nothing else depends on it.
async function pollWorldQuests(env) {
  const token = await discordBotToken(env);
  const channel = questChannelId(env);

  if (token === null || channel === null) {
    return { checked: false, reason: "not configured" };
  }

  await ensureQuestTable(env);
  await ensureSettingsTable(env);

  const seenRow = await env.DB.prepare(
    `SELECT value FROM settings WHERE key = ?`
  ).bind(QUEST_CHECK_KEY).first("value");

  let response;

  try {
    const url =
      `https://discord.com/api/v10/channels/${channel}/messages?limit=${QUEST_FETCH_LIMIT}` +
      (seenRow ? `&after=${encodeURIComponent(seenRow)}` : "");

    response = await fetch(url, {
      headers: { Authorization: `Bot ${token}`, "User-Agent": "ProTracker (world quests, v1)" },
    });
  } catch (err) {
    console.error("world quest poll failed", err && err.message ? err.message : String(err));
    return { checked: false, reason: "fetch failed" };
  }

  if (!response.ok) {
    // 401 means the token is wrong, 403 means the bot cannot see the
    // channel, 404 means the id is. All three are setup problems and all
    // three are worth saying out loud rather than retrying quietly forever.
    console.error("world quest poll rejected", response.status);
    return { checked: false, reason: `discord returned ${response.status}` };
  }

  let messages;

  try {
    messages = await response.json();
  } catch {
    return { checked: false, reason: "unreadable response" };
  }

  if (!Array.isArray(messages)) return { checked: false, reason: "unexpected response" };

  // Oldest first, so the newest id is the last thing remembered.
  messages.sort((a, b) => String(a.id).localeCompare(String(b.id)));

  let stored = 0;
  let unparsed = 0;
  let newest = seenRow || null;
  const announce = [];

  for (const message of messages) {
    if (!message || !message.id) continue;

    newest = String(message.id);

    const quest = parseQuest(message);
    if (!quest) continue;

    // Already known: nothing to store, and nothing to say about it again.
    if (!(await storeQuest(env, quest))) continue;

    stored += 1;

    if (quest.parsed) announce.push(quest);
    else unparsed += 1;
  }

  if (newest && newest !== seenRow) {
    await env.DB.prepare(
      `INSERT INTO settings (key, value, updated_utc, updated_by)
       VALUES (?, ?, ?, ?)
       ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc`
    ).bind(QUEST_CHECK_KEY, newest, nowIso(), "scheduled").run();
  }

  for (const quest of announce) {
    await announceQuest(env, quest);
  }

  if (unparsed > 0) {
    // The important one. A message that says "World Quest" and yields no
    // Pokemon means the format moved, and with a monthly cadence that has
    // to surface this month rather than next.
    await announceQuestProblem(env, unparsed);
  }

  return { checked: true, stored, unparsed };
}

async function announceQuest(env, quest) {
  const webhook = await questWebhook(env);
  if (webhook === null) return;

  const fields = [];
  const add = (name, value) => {
    if (value !== null && value !== undefined && String(value).length) {
      fields.push({ name, value: String(value), inline: true });
    }
  };

  add("Lowest tier", quest.lowestTier);
  add("Reward", quest.reward);
  add("Total IVs", quest.totalIvs === null ? null : quest.totalIvs.toLocaleString("en-US"));
  add("Single IVs", quest.singleIvs);
  add("Average submissions", quest.averageSubmissions);
  add("Ends", quest.endTimeText);

  try {
    await fetch(webhook, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        username: "Pro Tracker",
        embeds: [
          {
            title: `World Quest: ${quest.pokemon}`,
            description: quest.durationAssumed
              ? "The post did not state a duration, so this assumes 24 hours."
              : undefined,
            color: 0x584a86,
            fields,
            timestamp: quest.startedUtc || nowIso(),
          },
        ],
      }),
    });
  } catch (err) {
    console.error("world quest announce failed", err && err.message ? err.message : String(err));
  }
}

async function announceQuestProblem(env, count) {
  const webhook = await questWebhook(env);
  if (webhook === null) return;

  try {
    await fetch(webhook, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        username: "Pro Tracker",
        embeds: [
          {
            title: "A World Quest post did not parse",
            description:
              `${count} message${count === 1 ? "" : "s"} mentioned a World Quest but no Pokemon could be read from ` +
              "it. The raw text is stored - see /v1/world-quests - and the parser probably needs updating.",
            color: 0x8a5a12,
            timestamp: nowIso(),
          },
        ],
      }),
    });
  } catch (err) {
    console.error("world quest problem notice failed", err && err.message ? err.message : String(err));
  }
}

// ------------------------------------------------------ presence updates

// §230. The hourly note in the online-users channel: how many trackers are
// hunting right now.
//
// This costs nothing new to collect. §150 already has every hunting tracker
// posting an anonymous heartbeat every five minutes, and §151 already
// records the live count into presence_samples once per ten-minute bucket.
// All that was missing was somewhere for it to go on its own.
//
// It says a count and nothing else, because a count is all that exists. A
// heartbeat carries a random id made fresh at each app start - not the
// install token, not a name, not what is being hunted - and rows older than
// fifteen minutes are pruned. There is no way to turn any of this into a
// person, which is exactly why it is safe to post in public.
//
// The webhook is a second secret, not the reports one: these belong in a
// different channel, and a channel people can read is a very different
// thing from the private one screenshots go to.
const PRESENCE_POST_KEY = "presence_post";
const PRESENCE_POST_INTERVAL_MINUTES = 60;

// Same shape as adminSecret and reportWebhook.
async function presenceWebhook(env) {
  let value = env.PRESENCE_WEBHOOK;

  if (value && typeof value === "object" && typeof value.get === "function") {
    try {
      value = await value.get();
    } catch (err) {
      console.error("PRESENCE_WEBHOOK binding could not be read", err && err.message ? err.message : String(err));
      return null;
    }
  }

  if (typeof value !== "string") return null;

  const url = value.trim();

  return /^https:\/\/(discord\.com|discordapp\.com)\/api\/webhooks\//.test(url) ? url : null;
}

// The live count over the presence window - the same number /v1/admin/presence
// answers with, read the same way.
async function liveTrackerCount(env) {
  const row = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM presence WHERE last_seen_utc >= ?`
  ).bind(minutesAgoIso(PRESENCE_WINDOW_MINUTES)).first();

  return Number(row && row.n) || 0;
}

// The highest sample in the last day, and the sample closest to an hour ago.
// Both come from presence_samples, so both are only as good as the cron that
// fills it - a missing sample means the field is simply left out rather than
// reported as a zero that never happened.
async function presenceContext(env) {
  const dayAgo = bucketUtc(Date.now() - 86400000);

  const peakRow = await env.DB.prepare(
    `SELECT MAX(trackers) AS peak FROM presence_samples WHERE bucket_utc >= ?`
  ).bind(dayAgo).first("peak");

  const hourBucket = bucketUtc(Date.now() - 3600000);

  const hourRow = await env.DB.prepare(
    `SELECT trackers FROM presence_samples WHERE bucket_utc = ?`
  ).bind(hourBucket).first("trackers");

  return {
    peakDay: peakRow === null || peakRow === undefined ? null : Number(peakRow),
    hourAgo: hourRow === null || hourRow === undefined ? null : Number(hourRow),
  };
}

// { at, trackers } from the settings row, or nulls when there has never
// been a post.
async function lastPresencePost(env) {
  await ensureSettingsTable(env);

  const row = await env.DB.prepare(
    `SELECT value FROM settings WHERE key = ?`
  ).bind(PRESENCE_POST_KEY).first("value");

  if (!row) return { at: null, trackers: null };

  try {
    const parsed = JSON.parse(row);
    return {
      at: typeof parsed.at === "string" ? parsed.at : null,
      trackers: typeof parsed.trackers === "number" ? parsed.trackers : null,
    };
  } catch {
    return { at: null, trackers: null };
  }
}

async function rememberPresencePost(env, trackers) {
  await env.DB.prepare(
    `INSERT INTO settings (key, value, updated_utc, updated_by)
     VALUES (?, ?, ?, ?)
     ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc`
  ).bind(
    PRESENCE_POST_KEY,
    JSON.stringify({ at: nowIso(), trackers }),
    nowIso(),
    "scheduled"
  ).run();
}

/// Posts the count, or explains why it did not. `force` skips the hourly
/// gate and the quiet-hours rule, which is what the admin route uses so
/// setup can be checked without waiting an hour to find out.
async function postPresenceUpdate(env, force) {
  const webhook = await presenceWebhook(env);
  if (webhook === null) return { posted: false, reason: "no webhook configured" };

  await ensurePresenceTable(env);

  const last = await lastPresencePost(env);

  if (!force && last.at) {
    const elapsedMs = Date.now() - Date.parse(last.at);
    const windowMs = PRESENCE_POST_INTERVAL_MINUTES * 60 * 1000;

    // A clock that moved backwards should not silence the channel for as
    // long as the jump, so a negative elapsed time counts as due.
    if (elapsedMs >= 0 && elapsedMs < windowMs) {
      return { posted: false, reason: "not due yet" };
    }
  }

  const trackers = await liveTrackerCount(env);

  // Nobody hunting, and nobody hunting last time either. An empty week
  // would otherwise be 168 identical messages saying nothing happened. The
  // FIRST zero after any other number still posts, because a drop to none
  // is news; it is only the repeat that is noise.
  if (!force && trackers === 0 && last.trackers === 0) {
    await rememberPresencePost(env, 0);
    return { posted: false, reason: "still quiet" };
  }

  const context = await presenceContext(env);

  const fields = [
    { name: "Hunting now", value: String(trackers), inline: true },
  ];

  if (context.hourAgo !== null) {
    fields.push({ name: "An hour ago", value: String(context.hourAgo), inline: true });
  }

  if (context.peakDay !== null) {
    // §231: a rolling 24 hours, which "today" only matches near midnight.
    fields.push({ name: "Peak (24h)", value: String(context.peakDay), inline: true });
  }

  // §231. The whole reason the heartbeat carries a version: seeing at a
  // glance how much of the count is on the current build. Left out entirely
  // when nobody is hunting, where it would just say "unknown x0".
  if (trackers > 0) {
    const versions = await presenceVersions(env);

    if (versions.length > 0) {
      fields.push({
        name: "Versions",
        value: versions.map((v) => `${v.version} x${v.trackers}`).join("\n"),
        inline: false,
      });
    }
  }

  const body = {
    username: "Pro Tracker",
    embeds: [
      {
        title: trackers === 1 ? "1 tracker hunting" : `${trackers} trackers hunting`,
        color: 0x2c6e52,
        fields,
        footer: { text: `Counted over the last ${PRESENCE_WINDOW_MINUTES} minutes` },
        timestamp: nowIso(),
      },
    ],
  };

  let sent;

  try {
    sent = await fetch(webhook, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
  } catch (err) {
    console.error("presence post failed", err && err.message ? err.message : String(err));
    return { posted: false, reason: "delivery failed" };
  }

  if (!sent.ok) {
    console.error("presence post rejected", sent.status);
    return { posted: false, reason: `delivery rejected (${sent.status})` };
  }

  // Only remembered once it arrived, so a Discord outage delays the next
  // post rather than skipping an hour of them.
  await rememberPresencePost(env, trackers);

  return { posted: true, trackers };
}

// §230: the same post, on demand, so the setup can be checked without
// waiting up to an hour to find out whether it works. Gated behind the same
// permission that reads the count in the first place - a login without it
// cannot see the number, so it has no business publishing it either.
/// §232. Reads the channel now rather than waiting for the cron - the only
/// way to check the token, the channel id and the bot's permissions without
/// waiting for PRO to run a quest, which is about a month away.
async function pollWorldQuestsNow(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  const result = await pollWorldQuests(env);

  return result.checked
    ? json({ ok: true, stored: result.stored, unparsed: result.unparsed })
    : json({ error: `Nothing was checked: ${result.reason}.` }, 503);
}

// §234. Builds a quest of the admin's own and stores it exactly where a real
// one goes, so every step after this - the tracker's fetch, the countdown, the
// species gate on the preview detector - runs against the same row shape a
// real announcement produces. There is no separate table and no test flag the
// client has to understand; the only thing that marks it is its message id.
//
// It deliberately does NOT announce to Discord. The webhook exists to tell the
// admin that PRO started a quest or that a post failed to parse; a quest the
// admin just started themselves is not news, and a channel that cries wolf
// during testing is worse than one that says nothing.
async function startTestQuest(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  const body = await readJson(request);
  if (body.error) return body.error;

  const pokemon = text(body.value.pokemon, QUEST_TEST_MAX_NAME);
  if (!pokemon) return bad("pokemon is required.");

  const totalIvs = nonNegativeInt(body.value.totalIvs);
  if (totalIvs <= 0) return bad("totalIvs must be a positive number.");
  if (totalIvs > QUEST_TEST_MAX_IVS) return bad(`totalIvs must be ${QUEST_TEST_MAX_IVS} or less.`);

  await ensureQuestTable(env);

  const startedMs = Date.now();

  // Ceiling, not rounding: "at least 0.5%" means the first whole IV total AT
  // or above the share, so a fractional threshold always goes up. A goal small
  // enough to make the share less than one IV still needs one, or a tier no
  // catch can miss reads as a tier already met before anything is submitted.
  const singleIvs = Math.max(1, Math.ceil(totalIvs * QUEST_FIRST_TIER_SHARE));

  const quest = {
    messageId: `${QUEST_TEST_PREFIX}${startedMs}-${newId().slice(0, QUEST_TEST_ID_SUFFIX)}`,
    pokemon,
    totalIvs,
    singleIvs,
    // Not invented. A test quest has no announcement behind it, so the fields
    // that would have been read off one are left empty and the tracker shows
    // a dash for them.
    averageSubmissions: 0,
    lowestTier: "",
    reward: QUEST_TEST_REWARD,
    duration: `${QUEST_DEFAULT_HOURS} hours`,
    endTimeText: "",
    startedUtc: new Date(startedMs).toISOString(),
    endsUtc: new Date(startedMs + QUEST_DEFAULT_HOURS * 3600 * 1000).toISOString(),
    raw: "",
    parsed: true,
  };

  // storeQuest answers whether the row was new. It always should be - the id
  // carries random characters - but a route that reports success for a write
  // that did not happen is exactly the failure this was written to end.
  const stored = await storeQuest(env, quest);

  if (!stored) {
    return bad("That test quest id already exists - try again.", 409);
  }

  return json({
    ok: true,
    quest: questOut({
      message_id: quest.messageId,
      pokemon: quest.pokemon,
      total_ivs: quest.totalIvs,
      single_ivs: quest.singleIvs,
      avg_subs: quest.averageSubmissions,
      lowest_tier: quest.lowestTier,
      reward: quest.reward,
      duration: quest.duration,
      end_time_text: quest.endTimeText,
      started_utc: quest.startedUtc,
      ends_utc: quest.endsUtc,
      parsed: 1,
    }),
    // The two tiers, so the console can show what it just created rather than
    // recomputing the arithmetic a second time and risking disagreeing with
    // the Worker about it.
    firstTierIvs: singleIvs,
    secondTierIvs: Math.max(1, Math.ceil(totalIvs * QUEST_SECOND_TIER_SHARE)),
  }, 201);
}

// §234. Removes every test quest, not just the running one. A quest lasts a
// day and testing makes several, so "clear" that left yesterday's behind
// would need a second button the moment it was used twice. Real quests cannot
// match the prefix - a Discord message id is all digits - so this can never
// delete one PRO announced.
async function clearTestQuests(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  await ensureQuestTable(env);

  const result = await env.DB.prepare(
    `DELETE FROM world_quests WHERE message_id LIKE ?`
  ).bind(`${QUEST_TEST_PREFIX}%`).run();

  return json({ ok: true, removed: changes(result) });
}

// ------------------------------------------------------------- announcements
// §239. Replaces the client's own read of PRO's "Update Logs" forum topic.
// That read parsed themed forum HTML - the class it lived in said so itself,
// "a strictly less stable foundation than the RSS feed was" - and it only ever
// saw client changelogs. PRO's #announcements carries the server news as well:
// maintenance, PvP bans, Trade Zone changes, when the World Quest starts.

const announcementsReady = new WeakSet();

async function ensureAnnouncementsTable(env) {
  if (announcementsReady.has(env.DB)) return;

  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS announcements (
       id         TEXT PRIMARY KEY,
       author     TEXT,
       body       TEXT,
       image_url  TEXT,
       link       TEXT,
       posted_utc TEXT NOT NULL,
       source     TEXT NOT NULL,
       seen_utc   TEXT NOT NULL
     )`
  ).run();

  await env.DB.prepare(
    `CREATE INDEX IF NOT EXISTS idx_announcements_posted ON announcements (posted_utc DESC)`
  ).run();

  announcementsReady.add(env.DB);
}

/// A followed channel relays the post as a webhook message whose embed holds
/// the real thing, so the author is looked for in the embed first and the
/// message's own author - the webhook - only as a fallback.
function announcementAuthor(message) {
  for (const embed of Array.isArray(message.embeds) ? message.embeds : []) {
    const name = embed && embed.author && typeof embed.author.name === "string" ? embed.author.name : "";
    if (name.trim()) return text(name, ANNOUNCE_MAX_AUTHOR);
  }

  const own = message.author && typeof message.author.username === "string" ? message.author.username : "";
  return text(own, ANNOUNCE_MAX_AUTHOR);
}

/// The first image the post carries: an attachment if there is one, otherwise
/// the embed's image or thumbnail. Refused unless it is https and on a host
/// this is willing to point a client at.
function announcementImage(message) {
  const candidates = [];

  for (const a of Array.isArray(message.attachments) ? message.attachments : []) {
    if (a && typeof a.url === "string") candidates.push(a.url);
  }

  for (const embed of Array.isArray(message.embeds) ? message.embeds : []) {
    if (!embed) continue;
    if (embed.image && typeof embed.image.url === "string") candidates.push(embed.image.url);
    if (embed.thumbnail && typeof embed.thumbnail.url === "string") candidates.push(embed.thumbnail.url);
  }

  for (const candidate of candidates) {
    const safe = safeImageUrl(candidate);
    if (safe) return safe;
  }

  return "";
}

/// https only, and only from a host named in ANNOUNCE_IMAGE_HOSTS. Anything
/// else comes back empty rather than being passed along for the client to go
/// and fetch.
function safeImageUrl(value) {
  if (typeof value !== "string" || value.length > 600) return "";

  let url;

  try {
    url = new URL(value);
  } catch {
    return "";
  }

  if (url.protocol !== "https:") return "";

  const host = url.hostname.toLowerCase();

  for (const allowed of ANNOUNCE_IMAGE_HOSTS) {
    if (host === allowed || host.endsWith("." + allowed)) return url.toString();
  }

  return "";
}

// §241 strips the pings HERE rather than before the insert, so the table keeps
// what PRO actually posted, rows stored before the rule existed get it too, and
// changing the rule later needs a redeploy rather than a reseed.
function announcementOut(row) {
  return {
    id: row.id,
    author: row.author || "",
    body: multilineText(stripMentions(row.body || ""), ANNOUNCE_MAX_BODY),
    imageUrl: row.image_url || "",
    link: row.link || "",
    postedUtc: row.posted_utc,
    source: row.source,
  };
}

/// §239. What the tracker asks for. Public and unauthenticated, the same
/// reasoning as the quests: PRO announced it to everyone.
async function getAnnouncements(env) {
  await ensureAnnouncementsTable(env);

  const { results } = await env.DB.prepare(
    `SELECT * FROM announcements ORDER BY posted_utc DESC LIMIT ?`
  ).bind(ANNOUNCE_KEEP).all();

  return json({
    announcements: (results || []).map(announcementOut),
    asOfUtc: nowIso(),
  });
}

/// Returns true when the row was new - the same reasoning as storeQuest's.
async function storeAnnouncement(env, row) {
  const existing = await env.DB.prepare(
    `SELECT 1 AS present FROM announcements WHERE id = ?`
  ).bind(row.id).first("present");

  if (existing) return false;

  await env.DB.prepare(
    `INSERT INTO announcements (id, author, body, image_url, link, posted_utc, source, seen_utc)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(id) DO NOTHING`
  ).bind(
    row.id, row.author, row.body, row.imageUrl, row.link, row.postedUtc, row.source, nowIso()
  ).run();

  return true;
}

/// §239. Reads the followed channel and stores anything new. A post with no
/// text at all - PRO posts bare images sometimes - is still stored when it has
/// an image, because the image IS the announcement.
async function pollAnnouncements(env) {
  const token = await discordBotToken(env);
  const channel = announcementsChannelId(env);

  if (token === null || channel === null) {
    return { checked: false, reason: "not configured" };
  }

  await ensureAnnouncementsTable(env);
  await ensureSettingsTable(env);

  const seenRow = await env.DB.prepare(
    `SELECT value FROM settings WHERE key = ?`
  ).bind(ANNOUNCE_CHECK_KEY).first("value");

  let response;

  try {
    const url =
      `https://discord.com/api/v10/channels/${channel}/messages?limit=${ANNOUNCE_FETCH_LIMIT}` +
      (seenRow ? `&after=${encodeURIComponent(seenRow)}` : "");

    response = await fetch(url, {
      headers: { Authorization: `Bot ${token}`, "User-Agent": "ProTracker (announcements, v1)" },
    });
  } catch (err) {
    console.error("announcements poll failed", err && err.message ? err.message : String(err));
    return { checked: false, reason: "fetch failed" };
  }

  if (!response.ok) {
    console.error("announcements poll rejected", response.status);
    return { checked: false, reason: `discord returned ${response.status}` };
  }

  let messages;

  try {
    messages = await response.json();
  } catch {
    return { checked: false, reason: "unreadable response" };
  }

  if (!Array.isArray(messages)) return { checked: false, reason: "unexpected response" };

  messages.sort((a, b) => String(a.id).localeCompare(String(b.id)));

  let stored = 0;
  let newest = seenRow || null;

  for (const message of messages) {
    if (!message || !message.id) continue;

    newest = String(message.id);

    const body = multilineText(questText(message), ANNOUNCE_MAX_BODY);
    const imageUrl = announcementImage(message);

    // Nothing to show at all. A join notice or a bare reaction is not an
    // announcement.
    if (!body && !imageUrl) continue;

    const postedUtc = typeof message.timestamp === "string" ? message.timestamp : nowIso();

    if (await storeAnnouncement(env, {
      id: String(message.id),
      author: announcementAuthor(message),
      body,
      imageUrl,
      link: "",
      postedUtc,
      source: "discord",
    })) {
      stored += 1;
    }
  }

  if (newest && newest !== seenRow) {
    await env.DB.prepare(
      `INSERT INTO settings (key, value, updated_utc, updated_by)
       VALUES (?, ?, ?, ?)
       ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc`
    ).bind(ANNOUNCE_CHECK_KEY, newest, nowIso(), "scheduled").run();
  }

  await trimAnnouncements(env);

  return { checked: true, stored };
}

/// Keeps the newest ANNOUNCE_KEEP. The window shows a list; a year of history
/// nobody scrolls to is a table that only grows.
async function trimAnnouncements(env) {
  await env.DB.prepare(
    `DELETE FROM announcements WHERE id NOT IN (
       SELECT id FROM announcements ORDER BY posted_utc DESC LIMIT ?
     )`
  ).bind(ANNOUNCE_KEEP).run();
}

async function pollAnnouncementsNow(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  const result = await pollAnnouncements(env);

  return result.checked
    ? json({ ok: true, stored: result.stored })
    : json({ error: `Nothing was checked: ${result.reason}.` }, 503);
}

/// §239. Adds one by hand. Written for the seed - the channel was followed on
/// the 7th and Discord relays nothing posted before that, so the last post PRO
/// made was not going to arrive on its own - and useful afterwards for
/// anything the poll could not read.
async function addAnnouncement(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  const body = await readJson(request);
  if (body.error) return body.error;
  const b = body.value;

  const message = multilineText(b.body, ANNOUNCE_MAX_BODY);
  const imageUrl = safeImageUrl(typeof b.imageUrl === "string" ? b.imageUrl : "");

  if (!message && !imageUrl) {
    return bad("An announcement needs body text, an image, or both. An image URL must be https and on Discord's CDN or PRO's forum.");
  }

  // An image that was offered and refused is a setup mistake worth saying out
  // loud, not something to drop on the floor.
  if (typeof b.imageUrl === "string" && b.imageUrl.trim() && !imageUrl) {
    return bad("That image URL was refused. It must be https and on Discord's CDN or PRO's forum.");
  }

  const postedUtc = typeof b.postedUtc === "string" && !Number.isNaN(Date.parse(b.postedUtc))
    ? new Date(b.postedUtc).toISOString()
    : nowIso();

  await ensureAnnouncementsTable(env);

  const row = {
    id: `${ANNOUNCE_MANUAL_PREFIX}${Date.now()}-${newId().slice(0, 8)}`,
    author: text(b.author, ANNOUNCE_MAX_AUTHOR),
    body: message,
    imageUrl,
    link: safeImageUrl(typeof b.link === "string" ? b.link : "") || "",
    postedUtc,
    source: "manual",
  };

  if (!(await storeAnnouncement(env, row))) {
    return bad("That announcement id already exists - try again.", 409);
  }

  await trimAnnouncements(env);

  return json({ ok: true, announcement: announcementOut({
    id: row.id, author: row.author, body: row.body, image_url: row.imageUrl,
    link: row.link, posted_utc: row.postedUtc, source: row.source,
  }) }, 201);
}

async function deleteAnnouncement(request, env, id) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  await ensureAnnouncementsTable(env);

  const result = await env.DB.prepare(`DELETE FROM announcements WHERE id = ?`).bind(id).run();

  return json({ deleted: changes(result) > 0 });
}

async function postPresenceNow(request, env) {
  const auth = await requireAdmin(request, env, { status: true });
  if (auth.failure) return auth.failure;

  const result = await postPresenceUpdate(env, true);

  return result.posted
    ? json({ ok: true, trackers: result.trackers })
    : json({ error: `Nothing was posted: ${result.reason}.` }, 503);
}

// ----------------------------------------------------------------- reports

// §226. "Report a Problem" could do exactly one thing before this: write its
// files into the player's Downloads folder and ask them to send them over.
// §73 looked at fixing that, settled on mailing them from a throwaway Gmail,
// and wrote down its own reason for going that way - "this project has no
// existing backend to build it on top of". That reason expired when this
// Worker shipped. The App Password was never filled in and the sender was
// never wired to the button, so nothing is being replaced here: this is the
// first delivery path the feature has ever actually had.
//
// The shape matters more than the destination. A webhook URL is a write
// credential - anyone holding it can post into that channel forever - so
// putting one inside a client anybody can decompile would be the same
// mistake as shipping the mail password there. The client posts here
// instead, and only this Worker knows where the channel is.
//
// Nothing about a report is stored. The table below keeps one row per
// delivered report: the hash of the sending install token, and when. That is
// the whole rate limiter. The screenshots, the log and the note go straight
// through to Discord and are never written to D1.

const reportsReady = new WeakSet();

async function ensureReportsTable(env) {
  if (reportsReady.has(env.DB)) return;

  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS reports (
       id TEXT PRIMARY KEY,
       sender_hash TEXT NOT NULL,
       sent_at_utc TEXT NOT NULL
     )`
  ).run();

  await env.DB.prepare(
    `CREATE INDEX IF NOT EXISTS idx_reports_sender ON reports (sender_hash, sent_at_utc)`
  ).run();

  reportsReady.add(env.DB);
}

// Same shape as adminSecret: a plain Worker secret, or a Secrets Store
// binding under the same name.
async function reportWebhook(env) {
  let value = env.REPORT_WEBHOOK;

  if (value && typeof value === "object" && typeof value.get === "function") {
    try {
      value = await value.get();
    } catch (err) {
      console.error("REPORT_WEBHOOK binding could not be read", err && err.message ? err.message : String(err));
      return null;
    }
  }

  if (typeof value !== "string") return null;

  const url = value.trim();

  // It has to be a Discord webhook over https. Without this check a
  // mistyped binding would quietly turn this route into an open relay that
  // posts players' screenshots at whatever the typo pointed to.
  return /^https:\/\/(discord\.com|discordapp\.com)\/api\/webhooks\//.test(url) ? url : null;
}

async function postReport(request, env) {
  const webhook = await reportWebhook(env);

  if (webhook === null) {
    // Not an error the player caused, and the tracker treats it as "keep
    // the files locally and say so" rather than showing anything alarming.
    return json({ error: "This server has no report webhook configured." }, 503);
  }

  const token = installToken(request);
  if (!token) return bad("X-Install-Token header is required.", 401);

  const declared = Number(request.headers.get("Content-Length") || 0);
  if (declared > REPORT_MAX_TOTAL_BYTES) {
    return json({ error: "That report is too large to send." }, 413);
  }

  let form;
  try {
    form = await request.formData();
  } catch {
    // §227. This is not a hypothetical. The tracker's first release of this
    // used MultipartFormDataContent.Add(content, name, fileName), which
    // sets FileNameStar and therefore writes a filename*= parameter - and
    // the parser behind formData() rejects the ENTIRE body over one, not
    // just the part. It does the same for an unquoted name=. The symptom is
    // a flat 400 from a route that is working perfectly, which is a bad
    // afternoon unless the message says so.
    console.error("report form parse failed", request.headers.get("Content-Type") || "(no content-type)");
    return bad(
      "The report body could not be read as a form. A filename* parameter or an unquoted name= will fail here; both parameters must be quoted strings."
    );
  }

  const note = text(form.get("note"), REPORT_MAX_NOTE);
  const version = text(form.get("version"), 40);
  const platform = text(form.get("platform"), 40);

  const files = [];
  let total = 0;

  for (const entry of form.getAll("files")) {
    if (typeof entry === "string") continue;
    if (files.length >= REPORT_MAX_FILES) break;

    // Only the last path segment, so a crafted name cannot describe a path.
    const name = String(entry.name || "").split(/[\\/]/).pop().slice(0, 80);

    if (!REPORT_ALLOWED.test(name)) continue;
    if (entry.size > REPORT_MAX_FILE_BYTES) continue;

    // §227: skip the one that does not fit, not everything after it. The
    // first version broke here, so a single large file silently cost every
    // smaller file behind it - including the log, which is usually the one
    // worth having.
    if (total + entry.size > REPORT_MAX_TOTAL_BYTES) continue;

    total += entry.size;

    files.push({ name, blob: entry });
  }

  if (files.length === 0) {
    // §227: naming what did arrive turns "it said 400" into an answer. The
    // tracker logs this line verbatim, so it reaches whoever is reading a
    // report about reports.
    const seen = form
      .getAll("files")
      .map((f) => (typeof f === "string" ? "(text part)" : String(f.name || "(no filename)")))
      .slice(0, 6)
      .join(", ");

    return bad(`A report needs at least one png, txt or log file. Parts under "files": ${seen || "none"}.`);
  }

  await ensureReportsTable(env);

  const hash = await sha256Hex(token);

  // §229: the tightest gate first, so its message is the one a player
  // sees when more than one applies.
  const last = await env.DB.prepare(
    `SELECT sent_at_utc FROM reports WHERE sender_hash = ? ORDER BY sent_at_utc DESC LIMIT 1`
  ).bind(hash).first("sent_at_utc");

  if (last) {
    const elapsedMs = Date.now() - Date.parse(last);
    const windowMs = REPORT_MIN_INTERVAL_MINUTES * 60 * 1000;

    // A clock that jumped backwards would otherwise lock a tracker out for
    // as long as the jump; a negative elapsed time is treated as "fine".
    if (elapsedMs >= 0 && elapsedMs < windowMs) {
      const waitSeconds = Math.ceil((windowMs - elapsedMs) / 1000);
      const waitMinutes = Math.max(1, Math.ceil(waitSeconds / 60));

      return json(
        {
          error: `Reports are limited to one every ${REPORT_MIN_INTERVAL_MINUTES} minutes. Try again in about ${waitMinutes} minute${waitMinutes === 1 ? "" : "s"}.`,
          retryAfterSeconds: waitSeconds,
        },
        429,
        { "Retry-After": String(waitSeconds) }
      );
    }
  }

  const hourAgo = new Date(Date.now() - 3600 * 1000).toISOString();
  const perHour = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM reports WHERE sender_hash = ? AND sent_at_utc >= ?`
  ).bind(hash, hourAgo).first("n");
  if (perHour >= REPORT_CAPS.perHour) {
    return json({ error: "Too many reports from this tracker in the last hour." }, 429);
  }

  const dayAgo = new Date(Date.now() - 24 * 3600 * 1000).toISOString();
  const perDay = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM reports WHERE sender_hash = ? AND sent_at_utc >= ?`
  ).bind(hash, dayAgo).first("n");
  if (perDay >= REPORT_CAPS.perDay) {
    return json({ error: "Too many reports from this tracker today." }, 429);
  }

  // The first eight characters of the hash. Enough that two reports from
  // the same tracker can be recognised as the same tracker; not enough,
  // and not the kind of thing, to identify a person.
  const sender = hash.slice(0, 8);

  const relay = new FormData();

  relay.append(
    "payload_json",
    JSON.stringify({
      username: "Pro Tracker reports",
      embeds: [
        {
          title: "Report a Problem",
          description: note || "(no note)",
          color: 0x584a86,
          fields: [
            { name: "Version", value: version || "unknown", inline: true },
            { name: "Platform", value: platform || "unknown", inline: true },
            { name: "Tracker", value: sender, inline: true },
          ],
          timestamp: nowIso(),
        },
      ],
    })
  );

  files.forEach((f, i) => relay.append(`files[${i}]`, f.blob, f.name));

  let relayed;

  try {
    relayed = await fetch(webhook, { method: "POST", body: relay });
  } catch (err) {
    console.error("report relay failed", err && err.message ? err.message : String(err));
    return json({ error: "The report could not be delivered." }, 502);
  }

  if (!relayed.ok) {
    // The status is worth a log line - a 413 here means the caps above are
    // still above whatever Discord currently allows a webhook to carry.
    console.error("report relay rejected", relayed.status);
    return json({ error: "The report could not be delivered." }, 502);
  }

  // Counted only once it actually arrived, so a delivery that failed does
  // not spend the player's allowance for the hour.
  await env.DB.prepare(
    `INSERT INTO reports (id, sender_hash, sent_at_utc) VALUES (?, ?, ?)`
  ).bind(newId(), hash, nowIso()).run();

  // Rows are only ever read through the two windows above, so nothing older
  // than a day needs keeping.
  await env.DB.prepare(
    `DELETE FROM reports WHERE sent_at_utc < ?`
  ).bind(new Date(Date.now() - 48 * 3600 * 1000).toISOString()).run();

  return json({ ok: true, files: files.length });
}

// -------------------------------------------------------------- presence

// The table is created on first use as well as by schema.sql, so a database
// set up before section 150 needs no manual step. Remembered per database
// binding, so the statement runs once per isolate, not once per heartbeat.
const presenceReady = new WeakSet();

async function ensurePresenceTable(env) {
  if (presenceReady.has(env.DB)) return;
  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS presence (run_id TEXT PRIMARY KEY, last_seen_utc TEXT NOT NULL, version TEXT)`
  ).run();

  // §231: databases created before §231 already have the table, so
  // CREATE TABLE IF NOT EXISTS above leaves them without the column. SQLite
  // has no ADD COLUMN IF NOT EXISTS, and the error it raises for a column
  // that is already there is the expected outcome on every run after the
  // first - so it is swallowed rather than treated as a fault.
  try {
    await env.DB.prepare(`ALTER TABLE presence ADD COLUMN version TEXT`).run();
  } catch {
    // Already has it.
  }
  await env.DB.prepare(`CREATE INDEX IF NOT EXISTS idx_presence_seen ON presence (last_seen_utc)`).run();
  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS presence_samples (bucket_utc TEXT PRIMARY KEY, sampled_at_utc TEXT NOT NULL, trackers INTEGER NOT NULL)`
  ).run();
  presenceReady.add(env.DB);
}

async function postPresence(request, env) {
  const body = await readJson(request);
  if (body.error) return body.error;

  const runId = typeof body.value.runId === "string" ? body.value.runId.trim().toLowerCase() : "";
  if (!/^[0-9a-f]{32}$/.test(runId)) return bad("runId must be 32 hex characters.");

  // §231: optional, and anything that is not a plausible version string is
  // stored as nothing rather than refused - a heartbeat is not worth losing
  // over the shape of a field that only feeds a count. Clients older than
  // §231 send none at all and land in the same bucket.
  // text() TRUNCATES to the length it is given rather than refusing, so
  // reading at the cap would quietly turn a 64-character piece of junk into
  // a legal-looking 32-character version and give it a row in the
  // breakdown. Read one character past the cap and reject anything that
  // reaches it - a string that needed truncating was never a version.
  const version = text(body.value.version, MAX_VERSION + 1);
  const storedVersion =
    version.length > 0 && version.length <= MAX_VERSION && VERSION_SHAPE.test(version)
      ? version
      : null;

  await ensurePresenceTable(env);

  // Prune and upsert in one batch: the table never holds more than the
  // trackers seen in the last fifteen minutes.
  await env.DB.batch([
    env.DB.prepare(`DELETE FROM presence WHERE last_seen_utc < ?`).bind(minutesAgoIso(PRESENCE_PRUNE_MINUTES)),
    env.DB.prepare(
      `INSERT INTO presence (run_id, last_seen_utc, version) VALUES (?, ?, ?)
       ON CONFLICT(run_id) DO UPDATE SET last_seen_utc = excluded.last_seen_utc, version = excluded.version`
    ).bind(runId, nowIso(), storedVersion),
  ]);

  // Section 151: the first heartbeat in a ten-minute bucket writes that
  // bucket's sample. One small read per heartbeat; one write per bucket.
  await recordSampleIfDue(env);

  return json({ ok: true });
}

async function getPresence(request, env) {
  // Section 153: the count stays the owner's unless a login was explicitly
  // created with the status permission.
  const auth = await requireAdmin(request, env, { status: true });
  if (auth.failure) return auth.failure;

  await ensurePresenceTable(env);

  const row = await env.DB.prepare(`SELECT COUNT(*) AS n FROM presence WHERE last_seen_utc >= ?`)
    .bind(minutesAgoIso(PRESENCE_WINDOW_MINUTES))
    .first();

  // Section 151: the last week of samples, summarised here rather than
  // shipped raw - at most 1008 small rows, and the tracker only needs the
  // shape of the week, not the rows.
  const nowMs = Date.now();
  const { results: samples } = await env.DB.prepare(
    `SELECT bucket_utc, trackers FROM presence_samples WHERE bucket_utc >= ? ORDER BY bucket_utc`
  ).bind(bucketUtc(nowMs - HISTORY_DAYS * 86400000)).all();

  return json({
    activeTrackers: Number(row && row.n) || 0,
    windowMinutes: PRESENCE_WINDOW_MINUTES,
    asOfUtc: nowIso(),
    history: summariseHistory(samples, nowMs),
    versions: await presenceVersions(env), // §231
  });
}

/// §231. Which builds the currently-counted trackers are running, commonest
/// first. Read over the same window as the count itself, so the two can
/// never disagree. A row with no version is every client older than §231 -
/// and, before §231 set one, every client full stop.
async function presenceVersions(env) {
  const { results } = await env.DB.prepare(
    `SELECT version, COUNT(*) AS n FROM presence
     WHERE last_seen_utc >= ?
     GROUP BY version
     ORDER BY n DESC, version ASC`
  ).bind(minutesAgoIso(PRESENCE_WINDOW_MINUTES)).all();

  return (results || []).map((r) => ({
    version: r.version ? String(r.version) : "unknown",
    trackers: Number(r.n) || 0,
  }));
}

function minutesAgoIso(minutes) {
  return new Date(Date.now() - minutes * 60 * 1000).toISOString();
}

// "2026-09-01T06:10" - the ten-minute bucket a moment belongs to, UTC.
function bucketUtc(ms) {
  const d = new Date(ms);
  d.setUTCSeconds(0, 0);
  d.setUTCMinutes(Math.floor(d.getUTCMinutes() / SAMPLE_MINUTES) * SAMPLE_MINUTES);
  return d.toISOString().slice(0, 16);
}

// Writes the current bucket's sample if nobody has yet: the live count over
// the presence window, which is what the admin route shows. Prunes samples
// past retention in the same batch.
async function recordSampleIfDue(env) {
  const bucket = bucketUtc(Date.now());
  const existing = await env.DB.prepare(`SELECT 1 AS present FROM presence_samples WHERE bucket_utc = ?`).bind(bucket).first();
  if (existing) return;

  const row = await env.DB.prepare(`SELECT COUNT(*) AS n FROM presence WHERE last_seen_utc >= ?`)
    .bind(minutesAgoIso(PRESENCE_WINDOW_MINUTES))
    .first();

  await env.DB.batch([
    env.DB.prepare(`DELETE FROM presence_samples WHERE bucket_utc < ?`).bind(bucketUtc(Date.now() - SAMPLE_RETENTION_DAYS * 86400000)),
    env.DB.prepare(`INSERT OR IGNORE INTO presence_samples (bucket_utc, sampled_at_utc, trackers) VALUES (?, ?, ?)`)
      .bind(bucket, nowIso(), Number(row && row.n) || 0),
  ]);
}

// The week in numbers. A bucket without a sample counts as zero (nobody
// hunting sends no heartbeat, and the cron is optional), so the averages
// divide by the buckets in the covered span - from the earliest sample to
// now, capped at the week - not by the samples that happen to exist.
function summariseHistory(samples, nowMs) {
  const bucketsPerDay = (24 * 60) / SAMPLE_MINUTES;
  const byHourSum = new Array(24).fill(0);
  let peak = null;
  let activeSum = 0;
  let activeBuckets = 0;

  for (const s of samples) {
    const n = Number(s.trackers) || 0;
    byHourSum[Number(s.bucket_utc.slice(11, 13))] += n;
    if (n > 0) {
      activeSum += n;
      activeBuckets += 1;
    }
    if (peak === null || n > peak.trackers) peak = { trackers: n, atUtc: s.bucket_utc + ":00Z" };
  }

  const firstMs = samples.length ? Date.parse(samples[0].bucket_utc + ":00Z") : nowMs;
  const coveredDays = Math.min(HISTORY_DAYS, Math.max(SAMPLE_MINUTES / (24 * 60), (nowMs - firstMs) / 86400000));
  const bucketsPerHourOfDay = Math.max(1, coveredDays * (60 / SAMPLE_MINUTES));

  return {
    days: HISTORY_DAYS,
    sampleMinutes: SAMPLE_MINUTES,
    sampleCount: samples.length,
    coveredDays: Math.round(coveredDays * 100) / 100,
    coveredBuckets: Math.max(1, Math.round(coveredDays * bucketsPerDay)),
    activeBuckets,
    peak,
    averageWhenActive: activeBuckets ? Math.round((activeSum / activeBuckets) * 10) / 10 : 0,
    byHourUtc: byHourSum.map((sum) => Math.round((sum / bucketsPerHourOfDay) * 100) / 100),
  };
}

// ---------------------------------------------------------------- events

// Both lists order by the server's timestamp, with rowid (insertion order)
// as the tiebreaker: two rows written in the same millisecond still come
// back in one fixed order on every call.
async function listEvents(env) {
  const { results } = await env.DB.prepare(
    `SELECT e.*, (SELECT COUNT(*) FROM entries n WHERE n.event_id = e.id) AS entry_count
     FROM events e
     ORDER BY e.posted_at_utc DESC, e.rowid DESC
     LIMIT ?`
  ).bind(MAX_EVENTS_RETURNED).all();

  return json({ schema: SCHEMA_VERSION, events: results.map(eventOut) });
}

async function postEvent(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  const body = await readJson(request);
  if (body.error) return body.error;
  const b = body.value;

  const type = typeof b.type === "string" && EVENT_TYPES.has(b.type) ? b.type : null;
  if (!type) return bad(`type must be one of ${[...EVENT_TYPES].join(", ")}.`);

  const title = text(b.title, LIMITS.title);
  const message = text(b.message, LIMITS.message);
  const postedBy = text(b.postedBy, LIMITS.postedBy);

  if (!title) return bad("title is required.");
  if (!message) return bad("message is required.");
  if (!postedBy) return bad("postedBy is required.");

  const event = {
    id: newId(),
    type,
    title,
    message,
    posted_by: postedBy,
    posted_at_utc: nowIso(),
    pokemon_name: text(b.pokemonName, LIMITS.pokemonName),
    item_reward: text(b.itemReward, LIMITS.itemReward),
    pokemon_reward: text(b.pokemonReward, LIMITS.pokemonReward),
    poke_dollars: nonNegativeInt(b.pokeDollars),
    allow_pokemon_submissions: b.allowPokemonSubmissions === false ? 0 : 1,
    allow_view_entries: b.allowViewEntries === false ? 0 : 1,
  };

  await env.DB.prepare(
    `INSERT INTO events (id, type, title, message, posted_by, posted_at_utc, pokemon_name,
                         item_reward, pokemon_reward, poke_dollars, allow_pokemon_submissions, allow_view_entries)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).bind(
    event.id, event.type, event.title, event.message, event.posted_by, event.posted_at_utc,
    event.pokemon_name, event.item_reward, event.pokemon_reward, event.poke_dollars,
    event.allow_pokemon_submissions, event.allow_view_entries
  ).run();

  return json({ event: eventOut({ ...event, entry_count: 0 }) }, 201);
}

async function deleteEvent(request, env, id) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  const existing = await env.DB.prepare(`SELECT id FROM events WHERE id = ?`).bind(id).first();
  if (!existing) return json({ error: "No such event." }, 404);

  // Entries go with their event, explicitly - not left to a cascade setting.
  const [entriesResult] = await env.DB.batch([
    env.DB.prepare(`DELETE FROM entries WHERE event_id = ?`).bind(id),
    env.DB.prepare(`DELETE FROM events WHERE id = ?`).bind(id),
  ]);

  return json({ deleted: true, entriesDeleted: changes(entriesResult) });
}

// --------------------------------------------------------------- entries

async function listEntries(request, env, eventId) {
  const event = await env.DB.prepare(`SELECT id, allow_view_entries FROM events WHERE id = ?`).bind(eventId).first();
  if (!event) return json({ error: "No such event." }, 404);

  const { results } = await env.DB.prepare(
    `SELECT id, event_id, username, pokemon_name, submitted_at_utc, submitter_hash
     FROM entries WHERE event_id = ?
     ORDER BY submitted_at_utc DESC, rowid DESC
     LIMIT ?`
  ).bind(eventId, MAX_ENTRIES_RETURNED).all();

  // "mine" lets a tracker mark the entries it submitted itself. Optional:
  // with no token every entry is simply not yours.
  const token = installToken(request);
  const myHash = token ? await sha256Hex(token) : null;

  return json({
    schema: SCHEMA_VERSION,
    entries: results.map((r) => entryOut(r, myHash)),
  });
}

async function postEntry(request, env, eventId) {
  const token = installToken(request);
  if (!token) return bad("X-Install-Token header is required.", 401);

  const body = await readJson(request);
  if (body.error) return body.error;
  const b = body.value;

  const username = text(b.username, LIMITS.username);
  const pokemonName = text(b.pokemonName, LIMITS.pokemonName);
  if (!username) return bad("username is required.");

  const event = await env.DB.prepare(`SELECT id, allow_pokemon_submissions FROM events WHERE id = ?`).bind(eventId).first();
  if (!event) return json({ error: "No such event." }, 404);
  if (!event.allow_pokemon_submissions) return json({ error: "This event does not take submissions." }, 403);

  const hash = await sha256Hex(token);

  const perEvent = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM entries WHERE event_id = ? AND submitter_hash = ?`
  ).bind(eventId, hash).first("n");
  if (perEvent >= CAPS.entriesPerEventPerInstall) {
    return json({ error: `This tracker has already submitted ${CAPS.entriesPerEventPerInstall} entries to this event.` }, 429);
  }

  const hourAgo = new Date(Date.now() - 3600 * 1000).toISOString();
  const perHour = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM entries WHERE submitter_hash = ? AND submitted_at_utc >= ?`
  ).bind(hash, hourAgo).first("n");
  if (perHour >= CAPS.entriesPerHourPerInstall) {
    return json({ error: "Too many submissions in the last hour - try again later." }, 429);
  }

  const entry = {
    id: newId(),
    event_id: eventId,
    username,
    pokemon_name: pokemonName,
    submitted_at_utc: nowIso(),
    submitter_hash: hash,
  };

  await env.DB.prepare(
    `INSERT INTO entries (id, event_id, username, pokemon_name, submitted_at_utc, submitter_hash)
     VALUES (?, ?, ?, ?, ?, ?)`
  ).bind(entry.id, entry.event_id, entry.username, entry.pokemon_name, entry.submitted_at_utc, entry.submitter_hash).run();

  return json({ entry: entryOut(entry, hash) }, 201);
}

async function deleteEntry(request, env, eventId, entryId) {
  const row = await env.DB.prepare(
    `SELECT id, submitter_hash FROM entries WHERE id = ? AND event_id = ?`
  ).bind(entryId, eventId).first();
  if (!row) return json({ error: "No such entry." }, 404);

  let allowed = await isAdmin(request, env);

  if (!allowed) {
    const token = installToken(request);
    if (token) {
      const hash = await sha256Hex(token);
      allowed = constantTimeEqual(hash, row.submitter_hash);
    }
  }

  if (!allowed) return json({ error: "Only the admin or the tracker that submitted this entry can remove it." }, 403);

  await env.DB.prepare(`DELETE FROM entries WHERE id = ?`).bind(entryId).run();

  return json({ deleted: true });
}

// ---------------------------------------------------- admin logins (§153)

// Created on first use like the presence tables, so no statement is run by
// hand. Remembered per database binding.
const adminLoginsReady = new WeakSet();

async function ensureAdminLoginsTable(env) {
  if (adminLoginsReady.has(env.DB)) return;
  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS admin_logins (
       id INTEGER PRIMARY KEY AUTOINCREMENT,
       username TEXT NOT NULL UNIQUE COLLATE NOCASE,
       verifier_digest TEXT NOT NULL,
       can_view_status INTEGER NOT NULL DEFAULT 0,
       created_utc TEXT NOT NULL,
       last_used_utc TEXT,
       revoked INTEGER NOT NULL DEFAULT 0,
       failed_attempts INTEGER NOT NULL DEFAULT 0,
       locked_until_utc TEXT
     )`
  ).run();
  adminLoginsReady.add(env.DB);
}

// ---------------------------------------------------------- active events
//
// §207. One row, holding the list of counterpart events the tracker should
// currently consider. Kept as a settings table rather than a column
// somewhere so the next small piece of published configuration has a home
// that already exists.

const settingsReady = new WeakSet();

const ACTIVE_EVENTS_KEY = "active_events";
// §219: six, matching ActiveEvents.MaxActive in the tracker. This is
// the binding one - the worker truncates before it stores, so the
// client cap is a courtesy and this is the rule.
const MAX_ACTIVE_EVENTS = 6;
const MAX_EVENT_NAME = 40;

async function ensureSettingsTable(env) {
  if (settingsReady.has(env.DB)) return;
  await env.DB.prepare(
    `CREATE TABLE IF NOT EXISTS settings (
       key TEXT PRIMARY KEY,
       value TEXT NOT NULL,
       updated_utc TEXT NOT NULL,
       updated_by TEXT
     )`
  ).run();
  settingsReady.add(env.DB);
}

async function getActiveEvents(env) {
  await ensureSettingsTable(env);

  const row = await env.DB.prepare(
    `SELECT value, updated_utc, updated_by FROM settings WHERE key = ?`
  ).bind(ACTIVE_EVENTS_KEY).first();

  let events = [];

  if (row && row.value) {
    try {
      const parsed = JSON.parse(row.value);
      if (Array.isArray(parsed)) events = parsed.filter((e) => typeof e === "string");
    } catch (err) {
      // A row that will not parse is the same as no row: the tracker's
      // job is to keep hunting, not to care that this went wrong.
      console.error("active_events row did not parse", String(err));
    }
  }

  return json({
    schema: SCHEMA_VERSION,
    events,
    updatedUtc: row ? row.updated_utc : null,
    updatedBy: row ? row.updated_by || "" : "",
  });
}

async function putActiveEvents(request, env) {
  const auth = await requireAdmin(request, env);
  if (auth.failure) return auth.failure;

  // readJson answers { value } or { error } - it never hands back the parsed
  // body directly. §207 read body.events off the WRAPPER, which is always
  // undefined, so every publish failed with "events must be an array of
  // names" no matter what was sent. Same three lines as every other handler
  // in this file.
  const body = await readJson(request);
  if (body.error) return body.error;

  const b = body.value;

  if (!Array.isArray(b.events)) return bad("events must be an array of names.");

  // Trimmed, de-duplicated case-insensitively, blanks dropped, capped. The
  // Worker deliberately does NOT check these against the counterpart
  // catalog: it has never seen that file, and a name it does not recognise
  // is a tracker-side problem to report rather than a reason to refuse a
  // write from the person who runs the game's events.
  const seen = new Set();
  const events = [];

  for (const raw of b.events) {
    if (typeof raw !== "string") continue;

    const name = raw.trim();
    if (!name || name.length > MAX_EVENT_NAME) continue;

    const key = name.toLowerCase();
    if (seen.has(key)) continue;

    seen.add(key);
    events.push(name);

    if (events.length >= MAX_ACTIVE_EVENTS) break;
  }

  await ensureSettingsTable(env);

  // One clock reading for the row and the reply, so the console shows the
  // stamp the next GET will hand back rather than one a few ms later.
  const stamp = nowIso();
  const who = auth.master ? "master" : auth.username || "admin";

  await env.DB.prepare(
    `INSERT INTO settings (key, value, updated_utc, updated_by)
     VALUES (?, ?, ?, ?)
     ON CONFLICT(key) DO UPDATE SET value = excluded.value,
                                    updated_utc = excluded.updated_utc,
                                    updated_by = excluded.updated_by`
  ).bind(ACTIVE_EVENTS_KEY, JSON.stringify(events), stamp, who).run();

  return json({ schema: SCHEMA_VERSION, events, updatedUtc: stamp, updatedBy: who });
}

// A username + verifier pair from the headers, checked against the table.
// Unknown name, revoked login and wrong password all answer with the same
// words - only a lock says anything more, because the person it talks to is
// the login's rightful owner. Wrong passwords count per login and the tenth
// in a row locks it for fifteen minutes; a right password resets the count.
async function loginAuth(env, username, verifier) {
  const wrong = { failure: json({ error: "Admin sign-in missing or wrong." }, 401) };
  if (!LOGIN_USERNAME.test(username) || !LOGIN_VERIFIER.test(verifier)) return wrong;

  await ensureAdminLoginsTable(env);

  const row = await env.DB.prepare(
    `SELECT id, username, verifier_digest, can_view_status, revoked, failed_attempts, locked_until_utc
     FROM admin_logins WHERE username = ?`
  ).bind(username).first();

  if (!row || row.revoked) return wrong;

  if (row.locked_until_utc && row.locked_until_utc > nowIso()) {
    return { failure: json({ error: `This login is locked for ${LOGIN_LOCK_MINUTES} minutes after too many wrong passwords.` }, 401) };
  }

  const digest = await sha256Hex(verifier);

  if (!constantTimeEqual(digest, row.verifier_digest)) {
    const failures = (Number(row.failed_attempts) || 0) + 1;
    const lock = failures >= LOGIN_LOCK_AFTER_FAILURES
      ? new Date(Date.now() + LOGIN_LOCK_MINUTES * 60 * 1000).toISOString()
      : null;
    await env.DB.prepare(`UPDATE admin_logins SET failed_attempts = ?, locked_until_utc = ? WHERE id = ?`)
      .bind(lock ? 0 : failures, lock, row.id).run();
    return wrong;
  }

  await env.DB.prepare(`UPDATE admin_logins SET failed_attempts = 0, locked_until_utc = NULL, last_used_utc = ? WHERE id = ?`)
    .bind(nowIso(), row.id).run();

  return { master: false, loginId: Number(row.id), username: row.username, canViewStatus: !!row.can_view_status };
}

async function listAdminLogins(request, env) {
  const auth = await requireAdmin(request, env, { master: true });
  if (auth.failure) return auth.failure;

  await ensureAdminLoginsTable(env);

  const { results } = await env.DB.prepare(
    `SELECT id, username, can_view_status, created_utc, last_used_utc, revoked
     FROM admin_logins ORDER BY revoked, username`
  ).all();

  return json({ logins: results.map(loginOut) });
}

async function saveAdminLogin(request, env) {
  const auth = await requireAdmin(request, env, { master: true });
  if (auth.failure) return auth.failure;

  const body = await readJson(request);
  if (body.error) return body.error;
  const b = body.value;

  const username = typeof b.username === "string" ? b.username.trim() : "";
  const verifier = typeof b.verifier === "string" ? b.verifier.trim().toLowerCase() : "";
  const canViewStatus = b.canViewStatus === true ? 1 : 0;

  if (!LOGIN_USERNAME.test(username)) return bad("username must be 3-24 letters, digits, dots, dashes or underscores.");
  if (!LOGIN_VERIFIER.test(verifier)) return bad("verifier must be 64 hex characters - the tracker derives it from the password.");

  await ensureAdminLoginsTable(env);

  const existing = await env.DB.prepare(`SELECT id FROM admin_logins WHERE username = ?`).bind(username).first();

  if (!existing) {
    const count = await env.DB.prepare(`SELECT COUNT(*) AS n FROM admin_logins`).first("n");
    if (count >= MAX_ADMIN_LOGINS) return bad(`No more than ${MAX_ADMIN_LOGINS} admin logins.`, 429);
  }

  // Posting an existing name again is the reset path: new password, fresh
  // permission, lock and failure count cleared, revocation lifted.
  await env.DB.prepare(
    `INSERT INTO admin_logins (username, verifier_digest, can_view_status, created_utc, revoked, failed_attempts, locked_until_utc)
     VALUES (?, ?, ?, ?, 0, 0, NULL)
     ON CONFLICT(username) DO UPDATE SET
       verifier_digest = excluded.verifier_digest,
       can_view_status = excluded.can_view_status,
       revoked = 0,
       failed_attempts = 0,
       locked_until_utc = NULL`
  ).bind(username, await sha256Hex(verifier), canViewStatus, nowIso()).run();

  const row = await env.DB.prepare(
    `SELECT id, username, can_view_status, created_utc, last_used_utc, revoked FROM admin_logins WHERE username = ?`
  ).bind(username).first();

  return json({ login: loginOut(row) }, existing ? 200 : 201);
}

async function revokeAdminLogin(request, env, id) {
  const auth = await requireAdmin(request, env, { master: true });
  if (auth.failure) return auth.failure;

  await ensureAdminLoginsTable(env);

  const result = await env.DB.prepare(`UPDATE admin_logins SET revoked = 1 WHERE id = ?`).bind(id).run();
  if (!changes(result)) return json({ error: "No such login." }, 404);

  const row = await env.DB.prepare(
    `SELECT id, username, can_view_status, created_utc, last_used_utc, revoked FROM admin_logins WHERE id = ?`
  ).bind(id).first();

  return json({ login: loginOut(row) });
}

function loginOut(r) {
  return {
    id: Number(r.id),
    username: r.username,
    canViewStatus: !!r.can_view_status,
    createdUtc: r.created_utc,
    lastUsedUtc: r.last_used_utc || null,
    revoked: !!r.revoked,
  };
}

// ------------------------------------------------------------------ auth

// Who is asking (§153): { master: true } for the ADMIN_TOKEN, a login
// object from loginAuth for a username + verifier pair, and { failure }
// otherwise - with no ADMIN_TOKEN configured the answer is always the 503,
// logins included: a Worker without its secret has no admin of any kind.
// The admin secret, or null when it is not configured. Two shapes are
// accepted: a plain Worker secret (Settings -> Variables and Secrets), which
// arrives as a string, and an account-level Secrets Store binding (Settings
// -> Bindings -> Secrets Store), which arrives as an object with get(). A
// value shorter than 16 characters counts as not configured.
async function adminSecret(env) {
  let value = env.ADMIN_TOKEN;

  if (value && typeof value === "object" && typeof value.get === "function") {
    try {
      value = await value.get();
    } catch (err) {
      console.error("ADMIN_TOKEN binding could not be read", err && err.message ? err.message : String(err));
      return null;
    }
  }

  return typeof value === "string" && value.length >= 16 ? value : null;
}

async function adminAuth(request, env) {
  const secret = await adminSecret(env);
  if (secret === null) {
    return { failure: json({ error: "The events server has no ADMIN_TOKEN configured (add it under the Worker's Settings -> Variables and Secrets, then check on the Deployments tab that the newest version is the active one), so nothing can be posted yet." }, 503) };
  }

  const header = request.headers.get("Authorization") || "";
  const m = header.match(/^Bearer\s+(.+)$/i);

  if (m) {
    // Compare digests, not strings: same length every time, constant-time loop.
    const a = await sha256Hex(m[1].trim());
    const b = await sha256Hex(secret);
    return constantTimeEqual(a, b)
      ? { master: true, canViewStatus: true }
      : { failure: json({ error: "Admin token missing or wrong." }, 401) };
  }

  const username = (request.headers.get("X-Admin-User") || "").trim();
  const verifier = (request.headers.get("X-Admin-Verifier") || "").trim().toLowerCase();

  if (username || verifier) return loginAuth(env, username, verifier);

  return { failure: json({ error: "Admin token missing or wrong." }, 401) };
}

// The gate the admin routes stand behind. opts.master keeps a route to the
// ADMIN_TOKEN itself (managing logins); opts.status wants the per-login
// status permission, which the master always has.
async function requireAdmin(request, env, opts = {}) {
  const auth = await adminAuth(request, env);
  if (auth.failure) return auth;

  if (opts.master && !auth.master) {
    return { failure: json({ error: "Only the master admin token can manage admin logins." }, 403) };
  }

  if (opts.status && !auth.canViewStatus) {
    return { failure: json({ error: "This admin login is not allowed to read the tracker count - the master token, or a login created with that permission, is needed." }, 403) };
  }

  return auth;
}

// The yes/no shape deleteEntry wants: any admin credential, no detail.
async function isAdmin(request, env) {
  return !(await adminAuth(request, env)).failure;
}

function installToken(request) {
  const t = (request.headers.get("X-Install-Token") || "").trim();
  if (t.length < LIMITS.installToken.min || t.length > LIMITS.installToken.max) return null;
  if (!/^[A-Za-z0-9._-]+$/.test(t)) return null;
  return t;
}

// ---------------------------------------------------------------- helpers

function eventOut(r) {
  return {
    id: r.id,
    type: r.type,
    title: r.title,
    message: r.message,
    postedBy: r.posted_by,
    postedAtUtc: r.posted_at_utc,
    pokemonName: r.pokemon_name || "",
    itemReward: r.item_reward || "",
    pokemonReward: r.pokemon_reward || "",
    pokeDollars: Number(r.poke_dollars) || 0,
    allowPokemonSubmissions: !!r.allow_pokemon_submissions,
    allowViewEntries: !!r.allow_view_entries,
    entryCount: Number(r.entry_count) || 0,
  };
}

function entryOut(r, myHash) {
  return {
    id: r.id,
    eventId: r.event_id,
    username: r.username,
    pokemonName: r.pokemon_name || "",
    submittedAtUtc: r.submitted_at_utc,
    mine: !!myHash && constantTimeEqual(myHash, r.submitter_hash),
  };
}

async function readJson(request) {
  const declared = Number(request.headers.get("Content-Length") || 0);
  if (declared > MAX_BODY_BYTES) return { error: json({ error: "Request body too large." }, 413) };

  let raw;
  try {
    raw = await request.text();
  } catch {
    return { error: bad("Request body could not be read.") };
  }
  if (raw.length > MAX_BODY_BYTES) return { error: json({ error: "Request body too large." }, 413) };

  try {
    const value = raw.trim() === "" ? {} : JSON.parse(raw);
    if (!value || typeof value !== "object" || Array.isArray(value)) return { error: bad("Request body must be a JSON object.") };
    return { value };
  } catch {
    return { error: bad("Request body is not valid JSON.") };
  }
}

// §239. Like text() below, but keeps the line breaks.
//
// text() replaces every control character with a space, newlines included,
// which is right for a title or a username and wrong for a post: PRO's
// announcements are multi-line, and flattening them turns a readable notice
// into a wall. Carriage returns are normalised away, runs of blank lines are
// collapsed to one, and every other control character still goes.
// §241. PRO opens most posts with a ping - "Servers up, @everyone" - and tags
// its donator roles the same way. In a tracker window those tokens are noise:
// nobody is being pinged by a desktop app, and the role does not exist there.
//
// They arrive in two shapes and both have to go. Discord sends a resolved
// mention as markup - <@&123> for a role, <@123> for a user, <#123> for a
// channel - and sends anything it could not resolve as the literal text the
// author typed, which is what @everyone and @here always are. A followed
// channel makes this worse, because the role ids in a relayed post belong to
// PRO's server and resolve to nothing in yours.
//
// The rule is deliberately narrow about what counts as a plain mention: the @
// must not follow a word character, so support@pokemonrevolution.net keeps its
// address, and the name must start with a letter, so "@2026" is left alone.
//
// A line that contained no mention is returned byte for byte.
//
// The punctuation left behind is only removed where the mention actually was.
// A first attempt cleaned the ends of any line it had touched, and ate the
// colon off "@everyone Coin Shop items this month:" - the colon that introduces
// the list underneath it. So the head and the tail are tested separately,
// against the ORIGINAL line, and only the end a mention really sat on is
// tidied.
//
// The boundary before the @ is a LOOKBEHIND, not a consumed character, and that
// is load-bearing. Consuming it worked on one mention and quietly failed on the
// second of two in a row: "@silver @gold" ate the space between them as part of
// the first match, so the second had no preceding character left to match and
// survived. A lookbehind consumes nothing, so adjacent mentions all match.
const MENTION_NAME = "@[A-Za-z][A-Za-z0-9_.-]{1,31}";
const MENTION_ANY = "(?:<@[!&]?\\d+>|<#\\d+>|" + MENTION_NAME + ")";
const MENTION_MARKUP = /<@[!&]?\d+>|<#\d+>/g;
// The trailing [ \t]* keeps "**@everyone The WQ starts**" from becoming
// "** The WQ starts**": the space the mention was followed by goes with it.
const MENTION_PLAIN = new RegExp("(?<![\\w@#])" + MENTION_NAME + "[ \\t]*", "g");
const MENTION_HEAD = new RegExp("^\\s*" + MENTION_ANY);
const MENTION_TAIL = new RegExp("(?<![\\w@#])" + MENTION_ANY + "\\s*$");

function stripMentions(v) {
  if (typeof v !== "string") return "";

  const lines = v.replace(/\r\n?/g, "\n").split("\n");
  const out = [];

  for (const line of lines) {
    const cut = line.replace(MENTION_MARKUP, " ").replace(MENTION_PLAIN, "");

    if (cut === line) {
      out.push(line);
      continue;
    }

    let s = cut
      .replace(/[ \t]+/g, " ")
      .replace(/ +([,.!?;:])/g, "$1")
      .trim();

    if (MENTION_HEAD.test(line)) s = s.replace(/^[,;:-]+\s*/, "").trim();
    if (MENTION_TAIL.test(line)) s = s.replace(/\s*[,;:-]+$/, "").trim();

    out.push(s);
  }

  return out.join("\n");
}

function multilineText(v, max) {
  if (typeof v !== "string") return "";

  const cleaned = v
    .replace(/\r\n?/g, "\n")
    .replace(/[\u0000-\u0009\u000b-\u001f\u007f]/g, " ")
    .replace(/[ \t]+\n/g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();

  return cleaned.length <= max ? cleaned : cleaned.slice(0, max);
}

function text(v, max) {
  if (typeof v !== "string") return "";
  const t = v.replace(/[\u0000-\u001f\u007f]/g, " ").trim();
  return t.length <= max ? t : t.slice(0, max);
}

function nonNegativeInt(v) {
  const n = typeof v === "number" ? v : typeof v === "string" ? Number(v) : 0;
  if (!Number.isFinite(n) || n <= 0) return 0;
  return Math.min(Math.floor(n), 999_999_999_999);
}

function newId() {
  return crypto.randomUUID().replace(/-/g, "");
}

function nowIso() {
  return new Date().toISOString();
}

function changes(result) {
  return result && result.meta && typeof result.meta.changes === "number" ? result.meta.changes : 0;
}

async function sha256Hex(s) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function constantTimeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function json(obj, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      ...extraHeaders,
    },
  });
}

function bad(message, status = 400) {
  return json({ error: message }, status);
}

function methodNotAllowed(allow) {
  return json({ error: "Method not allowed." }, 405, { Allow: allow });
}
