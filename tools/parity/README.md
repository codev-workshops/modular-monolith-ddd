# Parity baseline (`tools/parity`)

A re-runnable, hashed **parity baseline** of the source system, so that post-migration output can be
diffed against a frozen source-of-truth. It captures three independent baselines and rolls them up
into a single Merkle-style root hash:

1. **DB data invariants** — every base table + read-model view across all schemas, as canonical
   NDJSON, with row counts and per-object SHA-256 (`parity-baseline/db/`).
2. **API golden dataset** — every reflected endpoint exercised as both seeded roles (Member +
   Administrator), freezing the authorization matrix and canonical response bodies
   (`parity-baseline/api/`).
3. **Front-end DTO contracts** — a JSON Schema per `*Dto` returned by the controllers, serialized
   with the API's Newtonsoft settings so casing/nullability drift is caught (`parity-baseline/contracts/`).

The committed baseline tree lives under [`parity-baseline/`](../../parity-baseline) and the tooling
that produces/checks it lives here.

```
parity-baseline/
  db/<schema>/<object>.ndjson      # one file per table/view, canonical sorted-key JSON lines
  db/manifest.json                 # objects, rowCounts, sha256, orderBy, volatileColumns, per-schema hashes, identity fan-out
  api/golden.json                  # [{id, method, route, role, status, responseType, bodySha256, bodyPath, ...}]
  api/bodies/<id>.json             # canonical response body per (endpoint, role)
  api/volatile-bodies/<id>.json    # bodies captured but excluded from hashing (see below)
  contracts/<Dto>.schema.json      # JSON Schema per DTO
  contracts/manifest.json          # Dto -> endpoints + schema sha256
  baseline.manifest.json           # rootSha256 = SHA-256(sorted child-manifest hashes)
```

## Running

```bash
# Capture (writes/overwrites parity-baseline/):
./tools/parity/parity.sh capture

# Verify (recomputes everything and diffs against the committed baseline):
./tools/parity/parity.sh verify

# Limit to sections:
./tools/parity/parity.sh verify --only db,contracts
```

`verify` exits non-zero and prints the **first divergent leaf** plus a summary if anything drifts.

### What the orchestrator does (identical for capture & verify, so they are comparable)

1. **Environment bring-up.** By default (`PARITY_COMPOSE=1`) it uses the repo's `docker-compose.yml`
   to stand up `mymeetingsdb` (host port **1445** → container 1433) and run the `migrator` service,
   waits for the DB and for DbUp to finish, then applies the `registrations.v_UserRegistrations`
   workaround view (see *Determinism assumptions*). Set `PARITY_COMPOSE=0` to reuse an already-running
   SQL Server via `PARITY_DB_CONTAINER` + the connection-string env vars (e.g. the dev DB on 1433).
2. **Build** the parity tool and the API in **DEBUG** (so real permission checks run — see below).
3. **Fixture** (`fixture.sh`): reset via `ClearDatabase.sql`, populate domain + payments event-store
   state through the SUT `ParityFixture` harness (clock frozen at `2020-06-15`), then seed countries.
4. **Extract**: the tool snapshots the DB and DTO contracts, then starts the DEBUG API itself (with
   the frozen clock) to capture the API golden dataset, writes canonical JSON, and hashes everything.

## Determinism assumptions

- **Frozen clock.** The SUT fixture freezes both module clocks (`SystemClock.Set(2020-06-15)`), and
  the API is launched with `PARITY_FROZEN_CLOCK=2020-06-15T00:00:00.0000000` (an opt-in hook in
  `Program.cs`, no-op in normal operation) so time-relative state — e.g. subscription expiry — is
  reproducible.
- **DEBUG build for real auth.** `HasPermissionAuthorizationHandler` short-circuits to `true` unless
  built in DEBUG; the API is therefore built and run in Debug so the 200/403 authorization matrix is
  real.
- **DB snapshot precedes the API.** The running API's background outbox/inbox processors mutate the
  database continuously, so the DB baseline is captured *before* the API is started.
- **`volatileColumns` allowlist.** Non-deterministic columns (wall-clock timestamps written via
  `GETUTCDATE()`/`SystemClock.Now`, salted password hashes, derived event-store hash columns, and the
  async read-model checkpoint `Position`) are excluded from the hash and declared under
  `volatileColumns` in `db/manifest.json`. Runtime GUIDs are normalized to stable ordinal tokens
  (`#GUID_0001#`), timestamps to `#TS#`, and ProblemDetails trace ids to `#TRACE#`.
- **Excluded plumbing tables.** `*InboxMessages`, `*OutboxMessages`, `*InternalCommands` depend on
  async processing timing (even in row count) and are excluded (listed in the manifest for
  transparency), as is the `GET /api/userAccess/emails` body (async email log — captured under
  `api/volatile-bodies/` but not hashed).
- **Repo bug worked around.** Migration `0001` creates the registrations read-model view in the wrong
  schema (`users.v_UserRegistrations`); the application queries `registrations.v_UserRegistrations`.
  The orchestrator creates the correct view after migrating, mirroring the repo's own dev-DB helper.

## Command endpoints

Only GET endpoints are executed. Command endpoints (POST/PUT/PATCH/DELETE) are recorded in
`golden.json` with their role→permission authorization expectation but **not executed**, because
running mutations would contaminate the deterministic fixture and make reruns diverge. Their
authorization is frozen from the reflected `[HasPermission]` metadata + the seeded role matrix.
