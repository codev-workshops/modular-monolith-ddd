# Parity

A CLI that captures and verifies **parity hashes** for the MyMeetings modular
monolith. It exists to gate the microservice extraction: before a module is
extracted we `capture` a baseline from the monolith, and after extraction we
`verify` that the extracted service still produces the identical hashes for that
module's database schema, API responses and contract shapes.

## Dimensions

| Dimension | What is hashed | Source |
|-----------|----------------|--------|
| `db`  | Per-schema structure: tables (columns, PK/unique, foreign keys, check constraints, indexes) and views (columns + normalized definition). One hash per schema. **Structure only — never row data.** | Live SQL Server (`INFORMATION_SCHEMA` / `sys.*`) |
| `api` | A fixed "golden dataset" of read requests. Each response is normalized (status code + body with volatile fields redacted) and hashed. One hash per request. | A running host (default `http://127.0.0.1:5000`) |
| `dto` | Each module's contract surface: its `*IntegrationEvent` types and its `*Dto` query-result types (full name + every public property name/type). One hash per module. | Compiled module assemblies (reflection) |

Every hash is SHA-256 over canonical (key-sorted) JSON, so baselines are stable
across machines, culture and run order.

Schema/module/assembly mapping lives in `Modules.cs` and is the single source of
truth shared by all three dimensions.

## Usage

```bash
# Capture the baseline (writes tools/parity/baseline/{db,api,dto}.json)
dotnet run --project tools/parity -- capture

# Verify everything against the baseline (exit 1 on any mismatch)
dotnet run --project tools/parity -- verify

# Gate a single extraction track
dotnet run --project tools/parity -- verify --module Registrations

# Only the dimensions that don't need a running host
dotnet run --project tools/parity -- verify --dimensions db,dto

# Dump the full pre-hash material to diff a mismatch by hand
dotnet run --project tools/parity -- verify --details ./parity-details
```

### Options

- `-d, --dimensions <db,api,dto>` — default: all three.
- `-m, --module <name>` — `Meetings | Administration | Payments | Registrations | UserAccess | App`.
- `--baseline <dir>` — default `tools/parity/baseline`.
- `--config <file>` — API golden-dataset config, default `tools/parity/config/endpoints.json`.
- `--details <dir>` — also write the full canonical model per entry (not hashed).
- `--connection-string <cs>` — or env `PARITY_CONNECTION_STRING`.
  Default `Server=localhost,1433;Database=MyMeetings;User Id=sa;Password=Test@12345;Encrypt=False;TrustServerCertificate=True;`.
- `--api-base-url <url>` — or env `PARITY_API_BASE_URL`.
- env `PARITY_API_BEARER` — optional bearer token sent with API requests.

Exit codes: `0` pass, `1` verify mismatch, `2` usage error, `3` runtime error.

## Prerequisites

- The database must be created, migrated and seeded (`~/mymeetings-db-setup.sh`
  on the dev VM) for the `db` and `api` dimensions.
- For the `api` dimension the host must be running (see repo `run` instructions).

## Notes on the API golden baseline

The committed API baseline was captured against the monolith **before** the
Phase‑0 edge-auth change. With authentication not yet moved to the gateway, the
authenticated endpoints currently return their standard error contract; those
responses are captured verbatim (with `traceId`/`exceptionDetails` redacted) so
that an extracted service must reproduce the *same* behavior. When Phase‑0
deliberately changes edge auth, re-run `capture --dimensions api` to re-baseline
those endpoints — the `db` and `dto` baselines are unaffected. Within an
extraction track (no intentional API change) the `api` gate catches unintended
drift.

## How this gates extraction

For each track the exit criteria are: `db`, `api` and `dto` hashes for that
module all match the baseline. Run:

```bash
dotnet run --project tools/parity -- verify --module <Track>
```

as the parity gate in that track's PR / CI.
