// Runs worker.js locally against an in-memory SQLite database that answers
// D1's prepare/bind/all/first/run/batch calls, with the real schema.sql.
// Needs Node 22 or newer (node:sqlite). No install step:
//
//     node --no-warnings test-local.mjs
//
// Every route, every auth case and every cap is exercised; the script exits
// non-zero on the first difference from the documented behaviour.

import { DatabaseSync } from "node:sqlite";
import { createHash, pbkdf2Sync } from "node:crypto";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const worker = (await import(join(here, "worker.js"))).default;

// ---- a D1 look-alike over node:sqlite ------------------------------------
function makeD1() {
  const db = new DatabaseSync(":memory:");
  db.exec(readFileSync(join(here, "schema.sql"), "utf8"));

  const statement = (sql, args = []) => ({
    bind: (...a) => statement(sql, a),
    all: async () => ({ results: db.prepare(sql).all(...args).map((r) => ({ ...r })), success: true }),
    first: async (col) => {
      const row = db.prepare(sql).get(...args);
      if (row === undefined) return null;
      return col === undefined ? { ...row } : row[col];
    },
    run: async () => {
      const r = db.prepare(sql).run(...args);
      return { success: true, meta: { changes: Number(r.changes) } };
    },
  });

  return {
    prepare: (sql) => statement(sql),
    batch: async (stmts) => {
      const out = [];
      for (const s of stmts) out.push(await s.run());
      return out;
    },
    _db: db,
  };
}

// ---- tiny test harness ----------------------------------------------------
let passed = 0, failed = 0;
function ck(label, want, got) {
  const ok = JSON.stringify(want) === JSON.stringify(got);
  if (ok) passed++; else failed++;
  console.log(`  ${ok ? "PASS" : "FAIL"}  ${label.padEnd(78)} ${ok ? JSON.stringify(got) : "got=" + JSON.stringify(got) + " want=" + JSON.stringify(want)}`);
}

const ADMIN = "correct-horse-battery-staple-0123456789abcdef";
const env = { DB: makeD1(), ADMIN_TOKEN: ADMIN };
const base = "https://events.example";

async function call(method, path, { body, admin, token, env: e = env, headers = {} } = {}) {
  const h = { ...headers };
  if (body !== undefined) h["Content-Type"] = "application/json";
  if (admin) h["Authorization"] = `Bearer ${admin}`;
  if (token) h["X-Install-Token"] = token;
  const req = new Request(base + path, { method, headers: h, body: body === undefined ? undefined : (typeof body === "string" ? body : JSON.stringify(body)) });
  const res = await worker.fetch(req, e);
  let data = null;
  try { data = await res.json(); } catch { /* non-JSON */ }
  return { status: res.status, data, headers: res.headers };
}

const tokenA = "a".repeat(40);
const tokenB = "b".repeat(40);

console.log("1. Health and routing"); console.log("=".repeat(100));
let r = await call("GET", "/v1/health");
ck("health 200 ok schema 1, admin token visible", [200, true, 1, true], [r.status, r.data.ok, r.data.schema, r.data.adminTokenConfigured]);
r = await call("GET", "/v1/health", { env: { DB: env.DB } });
ck("health with no ADMIN_TOKEN reports adminTokenConfigured=false (and never a value)", [false, false], [r.data.adminTokenConfigured, JSON.stringify(r.data).includes(ADMIN)]);
r = await call("POST", "/v1/health");
ck("health POST -> 405 with Allow", [405, "GET"], [r.status, r.headers.get("Allow")]);
r = await call("GET", "/v1/nothing");
ck("unknown route -> 404", 404, r.status);
r = await call("GET", "/v1/events/");
ck("trailing slash tolerated (board)", 200, r.status);
r = await call("GET", "/v1/events");
ck("empty board", { schema: 1, events: [] }, r.data);

console.log("\n2. Admin gate"); console.log("=".repeat(100));
const draft = { type: "Giveaway", title: "Shiny Wingull giveaway", message: "Post your Summer Wingull, one winner Sunday.", postedBy: "Michael", pokemonName: "Wingull", pokeDollars: 250000 };
r = await call("POST", "/v1/events", { body: draft });
ck("post without token -> 401", 401, r.status);
r = await call("POST", "/v1/events", { body: draft, admin: "wrong-token-wrong-token-wrong" });
ck("post with wrong token -> 401", 401, r.status);
r = await call("POST", "/v1/events", { body: draft, admin: ADMIN, env: { DB: env.DB } });
ck("no ADMIN_TOKEN configured -> 503, never allowed", 503, r.status);
r = await call("POST", "/v1/events", { body: draft, admin: ADMIN, env: { DB: env.DB, ADMIN_TOKEN: "short" } });
ck("ADMIN_TOKEN shorter than 16 chars counts as not configured -> 503", 503, r.status);
// Secrets Store bindings arrive as an object with get(), not a string.
const storeEnv = { DB: env.DB, ADMIN_TOKEN: { get: async () => ADMIN } };
r = await call("GET", "/v1/health", { env: storeEnv });
ck("Secrets Store-shaped binding counts as configured", true, r.data.adminTokenConfigured);
r = await call("POST", "/v1/events", { body: draft, admin: "wrong-token-wrong-token-wrong", env: storeEnv });
ck("...wrong token against it -> 401", 401, r.status);
r = await call("POST", "/v1/events", { body: draft, admin: ADMIN, env: { DB: env.DB, ADMIN_TOKEN: { get: async () => { throw new Error("store down"); } } } });
ck("...a binding whose get() throws counts as not configured -> 503", 503, r.status);
r = await call("POST", "/v1/events", { body: draft, admin: ADMIN, env: { DB: env.DB, ADMIN_TOKEN: { get: async () => "short" } } });
ck("...a stored value under 16 chars -> 503", 503, r.status);
r = await call("POST", "/v1/events", { body: draft, admin: ADMIN });
ck("post with the right token -> 201", 201, r.status);
const ev = r.data.event;
ck("event echoed with server id (32 hex) and UTC time", [true, true], [/^[0-9a-f]{32}$/.test(ev.id), /Z$/.test(ev.postedAtUtc)]);
ck("fields round-trip, flags default true, entryCount 0", ["Giveaway", "Wingull", 250000, true, true, 0], [ev.type, ev.pokemonName, ev.pokeDollars, ev.allowPokemonSubmissions, ev.allowViewEntries, ev.entryCount]);

console.log("\n3. Validation"); console.log("=".repeat(100));
r = await call("POST", "/v1/events", { body: { ...draft, type: "Party" }, admin: ADMIN });
ck("unknown type -> 400", 400, r.status);
r = await call("POST", "/v1/events", { body: { ...draft, title: "   " }, admin: ADMIN });
ck("blank title -> 400", 400, r.status);
r = await call("POST", "/v1/events", { body: "{not json", admin: ADMIN });
ck("invalid JSON -> 400", 400, r.status);
r = await call("POST", "/v1/events", { body: "[]", admin: ADMIN });
ck("JSON array body -> 400", 400, r.status);
r = await call("POST", "/v1/events", { body: JSON.stringify({ ...draft, message: "x".repeat(20000) }), admin: ADMIN });
ck("oversized body -> 413", 413, r.status);
r = await call("POST", "/v1/events", { body: { ...draft, message: "m".repeat(120), title: "\u0007bell\u0000title", allowPokemonSubmissions: false }, admin: ADMIN });
ck("message truncated to 75, control chars scrubbed, flag false honoured", [201, 75, "bell title", false], [r.status, r.data.event.message.length, r.data.event.title, r.data.event.allowPokemonSubmissions]);
const closedEvent = r.data.event;

console.log("\n4. Entries"); console.log("=".repeat(100));
r = await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "Lugario", pokemonName: "Wingull" } });
ck("submit without install token -> 401", 401, r.status);
r = await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "Lugario", pokemonName: "Wingull" }, token: "short" });
ck("token under 32 chars is ignored -> 401", 401, r.status);
r = await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "  ", pokemonName: "Wingull" }, token: tokenA });
ck("blank username -> 400", 400, r.status);
r = await call("POST", `/v1/events/${"0".repeat(32)}/entries`, { body: { username: "Lugario" }, token: tokenA });
ck("unknown event -> 404", 404, r.status);
r = await call("POST", `/v1/events/${closedEvent.id}/entries`, { body: { username: "Lugario" }, token: tokenA });
ck("event with submissions off -> 403", 403, r.status);
r = await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "Lugario", pokemonName: "Wingull" }, token: tokenA });
ck("submit -> 201, mine=true for the submitter", [201, true, "Lugario", "Wingull"], [r.status, r.data.entry.mine, r.data.entry.username, r.data.entry.pokemonName]);
const entryA = r.data.entry;
r = await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "Someone", pokemonName: "" }, token: tokenB });
ck("second tracker submits (no pokemon) -> 201", [201, ""], [r.status, r.data.entry.pokemonName]);
const entryB = r.data.entry;
r = await call("GET", `/v1/events/${ev.id}/entries`, { token: tokenA });
ck("list: 2 entries, newest first, only A's is mine", [2, entryB.id, [false, true]], [r.data.entries.length, r.data.entries[0].id, r.data.entries.map((e) => e.mine)]);
ck("list never exposes the submitter hash", false, JSON.stringify(r.data).includes("submitter"));
r = await call("GET", `/v1/events/${ev.id}/entries`);
ck("list without a token: nothing is mine", [false, false], r.data.entries.map((e) => e.mine));
r = await call("GET", "/v1/events");
ck("board shows entryCount 2 on the event", 2, r.data.events.find((e) => e.id === ev.id).entryCount);
const dbRow = env.DB._db.prepare("SELECT submitter_hash FROM entries WHERE id = ?").get(entryA.id);
ck("database stores a 64-hex hash, not the token", [true, false], [/^[0-9a-f]{64}$/.test(dbRow.submitter_hash), dbRow.submitter_hash.includes("aaaa")]);

console.log("\n5. Caps"); console.log("=".repeat(100));
for (let i = 0; i < 19; i++) await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "Lugario" }, token: tokenA });
r = await call("POST", `/v1/events/${ev.id}/entries`, { body: { username: "Lugario" }, token: tokenA });
ck("21st entry from one tracker on one event -> 429", 429, r.status);
r = await call("GET", `/v1/events/${ev.id}/entries`);
ck("...leaving exactly 20 from A plus B's", 21, r.data.entries.length);
// per-hour cap: make a fresh event and push tracker B past 30 in the hour (it has 1 already)
r = await call("POST", "/v1/events", { body: { ...draft, title: "Second event" }, admin: ADMIN });
const ev2 = r.data.event;
let last = null;
for (let i = 0; i < 20; i++) last = await call("POST", `/v1/events/${ev2.id}/entries`, { body: { username: "Someone" }, token: tokenB });
ck("...20 on the second event are fine (B: 21 this hour)", 201, last.status);
r = await call("POST", "/v1/events", { body: { ...draft, title: "Third event" }, admin: ADMIN });
const ev3 = r.data.event;
for (let i = 0; i < 9; i++) last = await call("POST", `/v1/events/${ev3.id}/entries`, { body: { username: "Someone" }, token: tokenB });
ck("...up to 30 in the hour still fine", 201, last.status);
r = await call("POST", `/v1/events/${ev3.id}/entries`, { body: { username: "Someone" }, token: tokenB });
ck("31st in the hour -> 429", 429, r.status);

console.log("\n6. Deleting"); console.log("=".repeat(100));
r = await call("DELETE", `/v1/events/${ev.id}/entries/${entryA.id}`, { token: tokenB });
ck("another tracker cannot delete A's entry -> 403", 403, r.status);
r = await call("DELETE", `/v1/events/${ev.id}/entries/${entryA.id}`);
ck("no credentials -> 403", 403, r.status);
r = await call("DELETE", `/v1/events/${ev.id}/entries/${entryA.id}`, { token: tokenA });
ck("the submitter deletes its own entry -> 200", 200, r.status);
r = await call("DELETE", `/v1/events/${ev.id}/entries/${entryA.id}`, { token: tokenA });
ck("...and again -> 404", 404, r.status);
r = await call("DELETE", `/v1/events/${ev.id}/entries/${entryB.id}`, { admin: ADMIN });
ck("admin deletes anyone's entry -> 200", 200, r.status);
r = await call("DELETE", `/v1/events/${ev.id}`, { token: tokenA });
ck("a player cannot delete an event -> 401", 401, r.status);
r = await call("DELETE", `/v1/events/${ev.id}`, { admin: ADMIN });
ck("admin deletes the event with its remaining 19 entries", [200, true, 19], [r.status, r.data.deleted, r.data.entriesDeleted]);
r = await call("GET", `/v1/events/${ev.id}/entries`);
ck("its entries are gone (404 on the event)", 404, r.status);
ck("no orphan rows left in the database", 0, env.DB._db.prepare("SELECT COUNT(*) AS n FROM entries WHERE event_id = ?").get(ev.id).n);
r = await call("DELETE", `/v1/events/${ev.id}`, { admin: ADMIN });
ck("deleting it twice -> 404", 404, r.status);
r = await call("GET", "/v1/events");
ck("board now holds the other three events, newest first", ["Third event", "Second event", "bell title"], r.data.events.map((e) => e.title));

console.log("\n7. Bodies exactly as the tracker's EventsSyncService serialises them"); console.log("=".repeat(100));
// System.Text.Json (JsonSerializerDefaults.Web) writes every DTO property,
// camelCased - including the ones the server assigns (id, postedAtUtc,
// entryCount, mine) - so the worker has to ignore those rather than choke.
r = await call("POST", "/v1/events", {
  admin: ADMIN,
  body: {
    id: "", type: "DungeonNight", title: "Client-shaped post", message: "From the tracker", postedBy: "DeepF",
    postedAtUtc: null, pokemonName: "", itemReward: "", pokemonReward: "", pokeDollars: 250000,
    allowPokemonSubmissions: true, allowViewEntries: false, entryCount: 0,
  },
});
const evC = r.data && r.data.event;
ck("event body with server-owned keys -> 201, server picks the id", [201, true, 32], [r.status, !!evC, evC ? evC.id.length : 0]);
ck("...type, pokeDollars and the two flags kept", ["DungeonNight", 250000, true, false, 0], evC ? [evC.type, evC.pokeDollars, evC.allowPokemonSubmissions, evC.allowViewEntries, evC.entryCount] : null);
ck("...postedAtUtc is the server's clock, ISO-8601 UTC", true, !!evC && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/.test(evC.postedAtUtc));
r = await call("POST", `/v1/events/${evC.id}/entries`, {
  token: tokenA,
  body: { id: "", eventId: "", username: "DeepF", pokemonName: "Nidoran M", submittedAtUtc: null, mine: false },
});
const enC = r.data && r.data.entry;
ck("entry body with server-owned keys -> 201, mine=true, server id", [201, true, true, 32], [r.status, !!enC, enC ? enC.mine : null, enC ? enC.id.length : 0]);
ck("...eventId is the URL's event, not the body's empty string", evC.id, enC ? enC.eventId : null);
r = await call("POST", "/v1/events", { admin: ADMIN, body: { type: "giveaway", title: "x", message: "y", postedBy: "z" } });
ck("type is matched exactly as the C# enum name (lower-case -> 400)", 400, r.status);
// Three submissions back to back land in the same millisecond more often
// than not; the list must still come back newest-inserted first, every time.
// (A third tracker: B is at its hourly cap from section 5.)
const burst = [];
for (let i = 0; i < 3; i++) burst.push((await call("POST", `/v1/events/${evC.id}/entries`, { token: "c".repeat(40), body: { username: `Burst ${i}` } })).data.entry.id);
r = await call("GET", `/v1/events/${evC.id}/entries`);
ck("same-millisecond entries list in reverse insertion order (rowid tiebreaker)", [...burst].reverse(), r.data.entries.slice(0, 3).map((e) => e.id));
r = await call("DELETE", `/v1/events/${evC.id}`, { admin: ADMIN });
ck("clean up the client-shaped post", [200, 4], [r.status, r.data.entriesDeleted]);

console.log("\n8. Presence (section 150)"); console.log("=".repeat(100));
const runA = "a1".repeat(16), runB = "b2".repeat(16);
r = await call("GET", "/v1/admin/presence");
ck("count without the admin token -> 401", 401, r.status);
r = await call("GET", "/v1/admin/presence", { admin: ADMIN });
ck("count starts at 0 with the window and time reported", [200, 0, 10, true], [r.status, r.data.activeTrackers, r.data.windowMinutes, /Z$/.test(r.data.asOfUtc)]);
r = await call("POST", "/v1/presence", { body: { runId: "not-hex" } });
ck("heartbeat with a bad run id -> 400", 400, r.status);
r = await call("GET", "/v1/presence");
ck("GET on the heartbeat route -> 405 with Allow POST", [405, "POST"], [r.status, r.headers.get("Allow")]);
r = await call("POST", "/v1/presence", { body: { runId: runA } });
ck("heartbeat -> 200 ok, no token of any kind needed", [200, true], [r.status, r.data.ok]);
await call("POST", "/v1/presence", { body: { runId: runA.toUpperCase() } });
r = await call("POST", "/v1/presence", { body: { runId: runB } });
ck("second tracker -> 200", 200, r.status);
r = await call("GET", "/v1/admin/presence", { admin: ADMIN });
ck("two trackers, the repeat (upper-cased) heartbeat not double counted", 2, r.data.activeTrackers);
ck("the table holds only run_id and last_seen_utc", ["run_id", "last_seen_utc"], env.DB._db.prepare("PRAGMA table_info(presence)").all().map((c) => c.name));
ck("...and never an install token hash or a name", false, JSON.stringify(env.DB._db.prepare("SELECT * FROM presence").all()).includes("aaaa"));
// age tracker B out of the window but not yet past pruning, then past pruning
env.DB._db.prepare("UPDATE presence SET last_seen_utc = ? WHERE run_id = ?").run(new Date(Date.now() - 12 * 60000).toISOString(), runB);
r = await call("GET", "/v1/admin/presence", { admin: ADMIN });
ck("a tracker silent for 12 minutes is out of the 10-minute count", 1, r.data.activeTrackers);
env.DB._db.prepare("UPDATE presence SET last_seen_utc = ? WHERE run_id = ?").run(new Date(Date.now() - 20 * 60000).toISOString(), runB);
await call("POST", "/v1/presence", { body: { runId: runA } });
ck("a tracker silent for 20 minutes is pruned by the next heartbeat", 1, env.DB._db.prepare("SELECT COUNT(*) AS n FROM presence").get().n);
r = await call("POST", "/v1/presence", { body: "{oops", headers: {} });
ck("invalid JSON heartbeat -> 400", 400, r.status);
// a database created before section 150 has no presence table: the Worker makes it
{
  const oldDb = makeD1();
  oldDb._db.exec("DROP TABLE presence");
  const oldEnv = { DB: oldDb, ADMIN_TOKEN: ADMIN };
  const before = oldDb._db.prepare("SELECT name FROM sqlite_master WHERE name = 'presence'").all().length;
  r = await call("POST", "/v1/presence", { body: { runId: runA }, env: oldEnv });
  const after = oldDb._db.prepare("SELECT name FROM sqlite_master WHERE name = 'presence'").all().length;
  ck("pre-150 database: heartbeat creates the presence table itself", [0, 1, 200], [before, after, r.status]);
}

console.log("\n9. History (section 151)"); console.log("=".repeat(100));
const bucketOf = (ms) => { const d = new Date(ms); d.setUTCSeconds(0, 0); d.setUTCMinutes(Math.floor(d.getUTCMinutes() / 10) * 10); return d.toISOString().slice(0, 16); };
r = await call("GET", "/v1/admin/presence", { admin: ADMIN });
const h0 = r.data.history;
ck("the heartbeats above wrote this bucket's sample (one row, trackers >= 1)", [1, true, bucketOf(Date.now())], [env.DB._db.prepare("SELECT COUNT(*) AS n FROM presence_samples").get().n, h0.sampleCount === 1 && h0.peak.trackers >= 1, env.DB._db.prepare("SELECT bucket_utc FROM presence_samples").get().bucket_utc]);
ck("history shape: days 7, sampleMinutes 10, 24 hourly averages, coverage fields", [7, 10, 24, true], [h0.days, h0.sampleMinutes, h0.byHourUtc.length, h0.coveredDays > 0 && h0.coveredBuckets >= 1 && h0.activeBuckets === 1]);
ck("a second heartbeat in the same bucket writes no second sample", 1, (await call("POST", "/v1/presence", { body: { runId: runA } }), env.DB._db.prepare("SELECT COUNT(*) AS n FROM presence_samples").get().n));
ck("the sample table holds bucket, time and a number - nothing else", ["bucket_utc", "sampled_at_utc", "trackers"], env.DB._db.prepare("PRAGMA table_info(presence_samples)").all().map((c) => c.name));
// a synthetic week: 3 days back, every bucket, 5 trackers during hour 20 UTC, 0 otherwise; plus a peak of 23 at 20:10 two days ago
env.DB._db.exec("DELETE FROM presence_samples");
const now = Date.now();
const ins = env.DB._db.prepare("INSERT OR IGNORE INTO presence_samples (bucket_utc, sampled_at_utc, trackers) VALUES (?, ?, ?)");
for (let ms = now - 3 * 86400000; ms <= now; ms += 600000) {
  const b = bucketOf(ms);
  ins.run(b, b + ":00Z", b.slice(11, 13) === "20" ? 5 : 0);
}
const peakBucket = bucketOf(now - 2 * 86400000).slice(0, 11) + "20:10";
env.DB._db.prepare("UPDATE presence_samples SET trackers = 23 WHERE bucket_utc = ?").run(peakBucket);
r = await call("GET", "/v1/admin/presence", { admin: ADMIN });
const h = r.data.history;
ck("peak 23 at the planted bucket", [23, peakBucket + ":00Z"], [h.peak.trackers, h.peak.atUtc]);
ck("covered about 3 days (not the full week), about 432 ten-minute buckets", [true, true], [Math.abs(h.coveredDays - 3) < 0.02, Math.abs(h.coveredBuckets - 432) <= 1]);
ck("hour 20 UTC averages about 5.x, hour 3 UTC averages 0", [true, 0], [h.byHourUtc[20] >= 5 && h.byHourUtc[20] <= 6.1, h.byHourUtc[3]]);
ck("average when active reflects only the hunting buckets (5s and one 23)", true, h.averageWhenActive > 5 && h.averageWhenActive < 7);
ck("active buckets = 6 per day x 3 days (+/- the edge bucket)", true, Math.abs(h.activeBuckets - 18) <= 1);
// retention: a nine-day-old sample is pruned by the next sample write
env.DB._db.exec("DELETE FROM presence_samples");
ins.run(bucketOf(now - 9 * 86400000), "old", 1);
await call("POST", "/v1/presence", { body: { runId: runB } });
ck("a nine-day-old sample is pruned when the next sample is written", 1, env.DB._db.prepare("SELECT COUNT(*) AS n FROM presence_samples").get().n);
// the optional cron handler records a sample too (fresh bucket via a cleared table)
env.DB._db.exec("DELETE FROM presence_samples");
await worker.scheduled({ cron: "*/10 * * * *" }, env, {});
ck("the scheduled handler writes the current bucket's sample", 1, env.DB._db.prepare("SELECT COUNT(*) AS n FROM presence_samples WHERE bucket_utc = ?").get(bucketOf(Date.now())).n);
r = await call("GET", "/v1/admin/presence", { admin: ADMIN });
ck("no samples older than the week are summarised; response still carries the live count", [true, true], [r.data.history.sampleCount === 1, typeof r.data.activeTrackers === "number"]);

console.log("\n10. Admin logins (section 153)"); console.log("=".repeat(100));
// The same derivation the tracker runs (EventsSyncService.DeriveLoginVerifier):
// the Worker only ever sees the result.
function deriveVerifier(username, password) {
  const salt = createHash("sha256").update("ProTracker events admin login|" + username.toLowerCase()).digest();
  return pbkdf2Sync(password, salt, 210000, 32, "sha256").toString("hex");
}
const sha256 = (s) => createHash("sha256").update(s).digest("hex");
const asLogin = (u, v) => ({ headers: { "X-Admin-User": u, "X-Admin-Verifier": v } });

const bobVerifier = deriveVerifier("Bob", "correct horse battery bob");
const annVerifier = deriveVerifier("ann", "anns own long password");

r = await call("GET", "/v1/admin/logins", { admin: ADMIN });
ck("empty login list for the master", [200, []], [r.status, r.data.logins]);
r = await call("GET", "/v1/admin/logins");
ck("list without credentials -> 401", 401, r.status);
r = await call("POST", "/v1/admin/logins", { body: { username: "Bob", verifier: bobVerifier, canViewStatus: false }, admin: ADMIN });
ck("create Bob -> 201, events only", [201, "Bob", false, false], [r.status, r.data.login.username, r.data.login.canViewStatus, r.data.login.revoked]);
ck("the reply carries neither the verifier nor its digest", [false, false], [JSON.stringify(r.data).includes(bobVerifier), JSON.stringify(r.data).includes(sha256(bobVerifier))]);
r = await call("POST", "/v1/admin/logins", { body: { username: "ann", verifier: annVerifier, canViewStatus: true }, admin: ADMIN });
ck("create ann -> 201 with the status permission", [201, true], [r.status, r.data.login.canViewStatus]);

r = await call("POST", "/v1/events", { body: draft, ...asLogin("bob", bobVerifier) });
ck("bob posts an event (username case-insensitive) -> 201", 201, r.status);
const bobEventId = r.data.event.id;
r = await call("GET", "/v1/admin/presence", asLogin("bob", bobVerifier));
ck("bob may not read the tracker count -> 403 naming the permission", [403, true], [r.status, r.data.error.includes("permission")]);
r = await call("GET", "/v1/admin/presence", asLogin("ann", annVerifier));
ck("ann (status permission) reads the tracker count", 200, r.status);
r = await call("GET", "/v1/admin/logins", asLogin("ann", annVerifier));
ck("a login cannot list logins -> 403 (master only)", [403, true], [r.status, r.data.error.includes("master")]);
r = await call("POST", "/v1/admin/logins", { body: { username: "eve", verifier: "0".repeat(64) }, ...asLogin("ann", annVerifier) });
ck("a login cannot create logins -> 403", 403, r.status);
ck("ann's last_used_utc is recorded", true, typeof env.DB._db.prepare("SELECT last_used_utc AS t FROM admin_logins WHERE username = 'ann'").get().t === "string");

// Wrong passwords: nine miss, the tenth locks for fifteen minutes.
for (let i = 0; i < 9; i++) await call("POST", "/v1/events", { body: draft, ...asLogin("bob", "f".repeat(64)) });
ck("nine wrong passwords count, same answer as an unknown name", 9, env.DB._db.prepare("SELECT failed_attempts AS n FROM admin_logins WHERE username = 'Bob'").get().n);
r = await call("POST", "/v1/events", { body: draft, ...asLogin("bob", "f".repeat(64)) });
ck("the tenth wrong password answers 401 and sets the lock", [401, true], [r.status, typeof env.DB._db.prepare("SELECT locked_until_utc AS t FROM admin_logins WHERE username = 'Bob'").get().t === "string"]);
r = await call("POST", "/v1/events", { body: draft, ...asLogin("bob", bobVerifier) });
ck("even the right password is refused while locked, and says why", [401, true], [r.status, r.data.error.includes("locked")]);
env.DB._db.prepare("UPDATE admin_logins SET locked_until_utc = '2000-01-01T00:00:00.000Z' WHERE username = 'Bob'").run();
r = await call("POST", "/v1/events", { body: draft, ...asLogin("bob", bobVerifier) });
ck("after the lock passes the right password works and clears the counters", [201, 0, null], [r.status, env.DB._db.prepare("SELECT failed_attempts AS n FROM admin_logins WHERE username = 'Bob'").get().n, env.DB._db.prepare("SELECT locked_until_utc AS t FROM admin_logins WHERE username = 'Bob'").get().t]);

// Revocation and the reset path.
const bobId = env.DB._db.prepare("SELECT id FROM admin_logins WHERE username = 'Bob'").get().id;
r = await call("POST", `/v1/admin/logins/${bobId}/revoke`, { admin: ADMIN });
ck("revoke bob", [200, true], [r.status, r.data.login.revoked]);
r = await call("POST", "/v1/events", { body: draft, ...asLogin("bob", bobVerifier) });
ck("a revoked login is just wrong -> 401", 401, r.status);
r = await call("POST", `/v1/admin/logins/9999/revoke`, { admin: ADMIN });
ck("revoking an unknown id -> 404", 404, r.status);
const bobNewVerifier = deriveVerifier("BOB", "a brand new password");
r = await call("POST", "/v1/admin/logins", { body: { username: "BOB", verifier: bobNewVerifier, canViewStatus: true }, admin: ADMIN });
ck("posting the name again resets it -> 200, revocation lifted, permission updated", [200, false, true], [r.status, r.data.login.revoked, r.data.login.canViewStatus]);
r = await call("POST", "/v1/events", { body: draft, ...asLogin("bob", bobVerifier) });
ck("the old password is gone", 401, r.status);
r = await call("POST", "/v1/events", { body: draft, ...asLogin("Bob", bobNewVerifier) });
ck("the new password works", 201, r.status);

// Validation and the entry-delete path.
r = await call("POST", "/v1/admin/logins", { body: { username: "ab", verifier: "0".repeat(64) }, admin: ADMIN });
ck("two-character username -> 400", 400, r.status);
r = await call("POST", "/v1/admin/logins", { body: { username: "fine.name", verifier: "not hex" }, admin: ADMIN });
ck("non-hex verifier -> 400", 400, r.status);
r = await call("POST", `/v1/events/${bobEventId}/entries`, { body: { username: "Player", pokemonName: "Wingull" }, token: tokenB });
const entryToModerate = r.data.entry.id;
r = await call("DELETE", `/v1/events/${bobEventId}/entries/${entryToModerate}`, asLogin("ann", annVerifier));
ck("a login can remove a wrong entry", [200, true], [r.status, r.data.deleted]);

// A fresh pre-153 database: the table is created on first use.
const bare = { DB: makeD1(), ADMIN_TOKEN: ADMIN };
bare.DB._db.exec("DROP TABLE admin_logins");
r = await call("GET", "/v1/admin/logins", { admin: ADMIN, env: bare });
ck("a database from before this section grows the table on first use", [200, []], [r.status, r.data.logins]);
r = await call("POST", "/v1/events", { body: draft, ...asLogin("ann", annVerifier), env: { DB: bare.DB } });
ck("no ADMIN_TOKEN configured -> 503 for logins too: no master, no admin at all", 503, r.status);
for (let i = 0; i < 50; i++) {
  bare.DB._db.prepare("INSERT INTO admin_logins (username, verifier_digest, can_view_status, created_utc) VALUES (?, 'x', 0, 'now')").run("filler" + i);
}
r = await call("POST", "/v1/admin/logins", { body: { username: "onemore", verifier: "0".repeat(64) }, admin: ADMIN, env: bare });
ck("the 51st login is refused", 429, r.status);
r = await call("POST", "/v1/admin/logins", { body: { username: "filler7", verifier: "0".repeat(64) }, admin: ADMIN, env: bare });
ck("resetting an existing one at the cap still works", 200, r.status);

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
