# Events board backend (Cloudflare Worker + D1)

This folder is the shared events server for the tracker's Events board: one
Worker script (`worker.js`), one database schema (`schema.sql`), and a local
test (`test-local.mjs`). It is not part of the .NET build; nothing here is
compiled into the tracker. MIGRATION_GUIDE.md section 143 has the design and
the reasoning; this file is the setup.

Everything below is done in the Cloudflare dashboard. No command-line tools
are needed. Names in the dashboard move around from time to time, so the
steps say what to look for rather than exactly where it sits.

## What you need before starting

- A Cloudflare account with Workers enabled (the free plan is enough).
- A D1 database. Yours is already created and named `protracker`.
- An admin token you make up. This is the one secret of the whole system:
  whoever knows it can post and remove events. Make it long and random. In
  PowerShell (Windows 10/11, either PowerShell 5 or 7) this prints a fresh
  64-character one:

  ```powershell
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 32; $rng.GetBytes($b); ($b | ForEach-Object { $_.ToString('x2') }) -join ''
  ```

  Keep it in your password manager. Do not put it in the repository, in a
  build, in Discord, or in a screenshot. The Worker refuses to accept
  anything shorter than 16 characters, and while no token is set it answers
  every admin action with "not set up yet" rather than letting anyone in.

## Step 1 - create the Worker and paste the code

1. Dashboard -> Workers & Pages -> Create -> Create Worker (the "Hello World"
   starter). Name it `protracker-events` (any name works; it becomes part of
   the address). Deploy the starter as it is.
2. Open the new Worker -> Edit code. Select everything in the editor's main
   file and replace it with the whole contents of `worker.js`. Deploy.
3. Note the Worker's address, shown on its overview page. It looks like
   `https://protracker-events.YOUR-SUBDOMAIN.workers.dev`.

## Step 2 - connect the database

1. Worker -> Settings -> Bindings -> Add -> D1 database.
2. Variable name: `DB` (exactly, upper case - the code reads `env.DB`).
3. D1 database: `protracker`. Save / Deploy.

## Step 3 - set the admin token

1. Worker -> Settings -> Variables and Secrets -> Add. (This is on the
   Worker itself. The "Secrets Store" entry in the dashboard's sidebar is a
   separate, account-level feature - not this. If you do use it, bind the
   stored secret to the Worker under the name `ADMIN_TOKEN` and the Worker
   reads it the same way.)
2. Type: Secret. Variable name: `ADMIN_TOKEN` (exactly - upper case, one
   underscore). Value: the token you generated.
3. Make sure it actually went live. Adding a secret in Settings creates a
   new *version* of the Worker, and the dashboard does not always make that
   version the active one. Open the **Deployments** tab: the newest row in
   Version History (labelled "Add secret: ADMIN_TOKEN") must be the one shown
   under "Active deployment". If an older version is active, open the row's
   **...** menu and choose **Deploy** (it may read "Promote to 100%"). Until
   the version holding the secret is active, the running Worker cannot see
   it and every post is answered with "no ADMIN_TOKEN configured".

A secret cannot be read back out of the dashboard later, only replaced. That
is the point: if it is ever leaked, come back here and replace it, and every
tracker will simply be asked for the new one the next time an admin posts.

## Step 4 - create the tables

1. Dashboard -> Storage & Databases -> D1 -> `protracker` -> Console (the
   Query tab in D1 Studio).
2. Paste the whole of `schema.sql`, then press Ctrl+A to select all of it
   before pressing Run. Without a selection Studio runs only the statement
   under the cursor - which after a paste is the last one, an index on a
   table that does not exist yet, so you get "no such table: main.entries"
   and "Executed 1/1". With everything selected the summary reads
   "Executed 7/7". Every statement is `IF NOT EXISTS`, so running the file
   again changes nothing.
3. The table list on the left should now show `entries`, `events` and
   `schema_version` (use its refresh icon if it does not), and the
   database's Overview page should say 3 tables.

## Step 5 - check it answers

Open `https://protracker-events.YOUR-SUBDOMAIN.workers.dev/v1/health` in a
browser. The reply is a short JSON line with `"ok":true`, `"schema":1` and
`"adminTokenConfigured":true`. Then open `/v1/events` on the same address:
an empty board looks like `{"schema":1,"events":[]}`.

If `adminTokenConfigured` is `false`, step 3 did not stick (most often the
version holding the secret was never made active on the Deployments tab, or
the name is not exactly `ADMIN_TOKEN`). If
`/v1/events` answers with an error mentioning `DB`, step 2 did not stick; if
it mentions a missing table, step 4 did not.

## Step 6 - point the tracker at it

Open `SharedPokemonLibrary/Data/Events/events-backend.json` in the repository
and set `baseUrl` to the Worker address (https, no trailing slash):

```json
{ "baseUrl": "https://protracker-events.YOUR-SUBDOMAIN.workers.dev" }
```

Build and publish as usual; the csproj already copies every JSON file under
`SharedPokemonLibrary/Data`, so the setting ships with the tracker. With the
value left empty the Events board stays local to each machine, exactly as it
was before section 143.

To try a build against a test server without rebuilding, put a file with the
same name and shape in `%LOCALAPPDATA%\ProTracker\Database\`; it overrides
the shipped one on that machine only. Delete it to go back.

## Using it from the tracker

- Everyone: Events shows the shared board and refreshes it on open and on
  the Refresh button. View Entries lists the server's entries. Submit
  Pokemon sends the name and the Pokemon to the server; the screenshot stays
  on the submitter's machine (section 143 is text-only online on purpose).
- Admin: Create Event and Remove Event work as before, except that the first
  post or delete in each run asks for the admin token. It is held in memory
  until the tracker closes and is never written anywhere. A wrong token is
  refused by the server, forgotten by the tracker, and asked for again.

The first real post is best made from the tracker's own Create Event window
rather than from a shell, so the token never lands in a command history.

## Checking the server by hand

Reads need no credentials, so a browser or PowerShell is enough:

```powershell
Invoke-RestMethod https://protracker-events.YOUR-SUBDOMAIN.workers.dev/v1/events
```

## Running the tests locally

`test-local.mjs` runs the real `worker.js` against an in-memory SQLite copy
of `schema.sql` and checks every route, every refusal and every cap. It
needs Node 22 or newer and nothing else:

```text
node --no-warnings test-local.mjs
```

The last line reports the count; anything other than `0 failed` means the
Worker should not be deployed.

## Active trackers (section 150)

While a hunt is running, each tracker posts a heartbeat every five minutes
to `/v1/presence`. The heartbeat is a random id made when that tracker
started and nothing else - not its install token, not a name, not what it
is hunting. The server keeps the id and a last-seen time, prunes anything
older than fifteen minutes on every write, and answers the admin route
`/v1/admin/presence` (admin token required) with a count of the ids seen in
the last ten minutes. The Admin Console's Status tab shows that count behind
its Refresh button. The table is created by the Worker on first use, so an
existing database needs nothing run by hand. A player running two trackers
counts twice; your own tracker counts while it hunts.

Section 151 adds a week of history under that number: once per ten-minute
bucket the Worker records the live count (`presence_samples` - a bucket, a
time and a number, nothing else; eight days kept), and the admin route
summarises the last seven days - the peak and when, the average whenever
anyone is hunting, how much of the time anyone was, and the average per hour
of the day. The console shows the peak, the average and the three busiest
hours in your local time, so the number is not just "whoever was on when I
looked". Samples are written by the first heartbeat in each bucket; if you
also want quiet stretches recorded as zeros, add a Cron Trigger to the
Worker (Settings -> Trigger events -> Cron triggers -> Add) with the
schedule `*/10 * * * *` - the Worker's `scheduled` handler writes the same
sample. It is optional: a bucket with no sample already counts as zero.

## Admin logins (section 153)

The `ADMIN_TOKEN` secret is the master credential; section 153 lets it
delegate. In the tracker's Admin Console, the Event Logins tab (master token
required) creates named logins - a username, a password you choose, and a
per-login checkbox for whether it may also read the Active trackers count.
A login can post and remove events (and moderate entries) by signing in with
its username and password where the token used to be pasted; the master
token keeps working everywhere and stays the only thing that can list,
create, reset or revoke logins.

The Worker never sees a password. The tracker derives
`PBKDF2-HMAC-SHA256(password, salt = SHA-256("ProTracker events admin login|"
+ lowercase username), 210000 iterations)` on the admin's machine and sends
the 64-hex result; D1 stores only a SHA-256 digest of that. Ten wrong
passwords in a row lock a login for fifteen minutes (the lock message is the
one answer that admits the name exists - acceptable, since it talks to the
login's owner). Revoking is immediate; posting an existing name again is the
reset path - new password, fresh permission, lock cleared, revocation
lifted. At most 50 logins. A Worker with no `ADMIN_TOKEN` configured still
has no admin at all - logins included - and the table creates itself on
first use, so no D1 statement is run by hand.

On Windows the sign-in window can remember a login on that PC, encrypted to
that Windows account (DPAPI); the tracker then signs in silently until the
login is revoked or reset. The master token is never remembered anywhere.

## What the server stores, and what it does not

- Stored: events (type, title, message, poster name, time, rewards, the two
  flags), entries (username, Pokemon name, time, and a SHA-256 hash of the
  submitting tracker's install token), presence (a random per-run id with a
  last-seen time, gone within fifteen minutes of the last heartbeat),
  presence samples (a ten-minute bucket and a count, kept eight days), and
  admin logins (username, permission flag, timestamps, and a digest of the
  client-derived verifier - nothing reversible to a password).
- Not stored: screenshots, IP addresses, the admin token, or any install
  token. The hash lets a tracker delete its own entries and nothing else.
- Limits: 200 events and 1000 entries per event are returned at most; a
  tracker can hold 20 entries per event and submit 30 per hour; bodies over
  16 KB are refused. The message limit (75) is the card's own rule that the
  tracker already enforces; title 80, names 32, Pokemon 40 and item reward
  60 are server-side ceilings.

## If something goes wrong

- `503` on posting ("no ADMIN_TOKEN configured"): the Worker cannot see the
  secret - the version holding it was never made active (Deployments tab),
  it is named differently, is shorter than 16 characters, or sits on a
  different Worker than the one in `baseUrl`.
  `/v1/health` shows `adminTokenConfigured` so you can check without the
  tracker (step 3, step 5).
- `401` on posting: the token or login typed into the tracker is wrong (or
  the login was revoked, reset, or is locked after ten wrong passwords).
  The tracker asks again on the next attempt.
- `403` on the Event Logins tab or the tracker count: the sign-in works but
  is not allowed that action - manage logins with the master token, and
  grant "may read the status" per login when creating it.
- The board is empty on every tracker but yours: `baseUrl` is still empty in
  the build they run, so they are on their local boards.
- To take the whole thing offline: set `baseUrl` back to `""` and publish,
  or delete the Worker. The database can be deleted from the D1 page; that
  is the only place the shared events live.
