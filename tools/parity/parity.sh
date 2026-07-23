#!/usr/bin/env bash
#
# Parity baseline orchestrator — single entrypoint for `capture` and `verify`.
#
#   ./tools/parity/parity.sh capture [--only db,api,contracts]
#   ./tools/parity/parity.sh verify  [--only db,api,contracts]
#
# What it does, in order (identical for capture and verify so the two are comparable):
#   1. Bring up the deterministic starting DB.
#        PARITY_COMPOSE=1 (default): `docker compose up` the `mymeetingsdb` (host port 1445) and
#        `migrator` services from docker-compose.yml, wait for the DB, wait for migrations, then apply
#        the `registrations.v_UserRegistrations` workaround view (see NOTE below).
#        PARITY_COMPOSE=0: reuse an already-running SQL Server via PARITY_DB_CONTAINER + the connection
#        string env vars (e.g. the blueprint's `mymeetings-sql` on port 1433).
#   2. Build the parity tool and the API (DEBUG — so HasPermissionAuthorizationHandler enforces real
#      permissions instead of short-circuiting to `true`).
#   3. Reset + reseed the fixture deterministically (fixture.sh): ClearDatabase.sql -> SUT ParityFixture
#      (clock frozen at 2020-06-15) -> SeedCountries.sql.
#   4. Run the parity tool, which snapshots the DB and DTO contracts, then starts the DEBUG API itself
#      (frozen clock) to capture the API golden dataset, and writes/compares the hashed baseline.
#
# NOTE (repo bug): migration 0001 creates the registration read-model view in the wrong schema
# (users.v_UserRegistrations); the application queries registrations.v_UserRegistrations. The migrator
# alone therefore yields a DB the app cannot run against. We create the correct view after migrating,
# mirroring the repo's own dev-DB setup helper.
set -euo pipefail

MODE="${1:-}"
shift || true
if [[ "$MODE" != "capture" && "$MODE" != "verify" ]]; then
  echo "usage: $0 {capture|verify} [--only db,api,contracts]" >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

SA_PASSWORD="${PARITY_DB_SA_PASSWORD:-Test@12345}"
DB_NAME="MyMeetings"
USE_COMPOSE="${PARITY_COMPOSE:-1}"

find_sqlcmd_in() {
  local container="$1"
  for p in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd sqlcmd; do
    if docker exec "$container" sh -c "command -v $p" >/dev/null 2>&1; then echo "$p"; return 0; fi
  done
  echo "ERROR: sqlcmd not found in $container" >&2; return 1
}

if [[ "$USE_COMPOSE" == "1" ]]; then
  PORT="${PARITY_DB_PORT:-1445}"
  CONN="Server=localhost,${PORT};Database=${DB_NAME};User=sa;Password=${SA_PASSWORD};Encrypt=False;"

  echo "[parity.sh] bringing up docker-compose services mymeetingsdb + migrator (port ${PORT}) ..."
  docker compose up -d --build mymeetingsdb migrator
  DB_CONTAINER="$(docker compose ps -q mymeetingsdb)"
  [[ -n "$DB_CONTAINER" ]] || { echo "ERROR: mymeetingsdb container not found" >&2; exit 1; }
  SQLCMD="$(find_sqlcmd_in "$DB_CONTAINER")"

  echo "[parity.sh] waiting for SQL Server + database '${DB_NAME}' ..."
  for _ in $(seq 1 90); do
    if docker exec "$DB_CONTAINER" "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -h -1 \
         -Q "SET NOCOUNT ON; SELECT 1 FROM sys.databases WHERE name='${DB_NAME}'" 2>/dev/null | grep -q 1; then
      DB_READY=1; break
    fi
    sleep 2
  done
  if [[ "${DB_READY:-0}" != "1" ]]; then
    echo "[parity.sh] database not created by the image in time; creating it explicitly ..."
    sed '1s/^\xEF\xBB\xBF//' src/Database/CompanyName.MyMeetings.Database/Scripts/CreateDatabase_Linux.sql \
      | docker exec -i "$DB_CONTAINER" "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -b
  fi

  echo "[parity.sh] waiting for the migrator to finish (DbUp) ..."
  MIG_CONTAINER="$(docker compose ps -q migrator)"
  for _ in $(seq 1 90); do
    if docker logs "$MIG_CONTAINER" 2>&1 | grep -qiE "Migration successful"; then MIG_OK=1; break; fi
    if docker exec "$DB_CONTAINER" "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -d "$DB_NAME" -h -1 \
         -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM app.MigrationsJournal" 2>/dev/null | grep -qE "1[0-9]"; then
      MIG_OK=1; break
    fi
    sleep 2
  done
  [[ "${MIG_OK:-0}" == "1" ]] || { echo "ERROR: migrations did not complete; see: docker logs $MIG_CONTAINER" >&2; exit 1; }

  echo "[parity.sh] applying registrations.v_UserRegistrations workaround view ..."
  docker exec "$DB_CONTAINER" "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -d "$DB_NAME" -b -Q \
    "IF OBJECT_ID(N'registrations.v_UserRegistrations', N'V') IS NOT NULL DROP VIEW [registrations].[v_UserRegistrations]; EXEC(N'CREATE VIEW [registrations].[v_UserRegistrations] AS SELECT [UserRegistration].[Id],[UserRegistration].[Login],[UserRegistration].[Email],[UserRegistration].[FirstName],[UserRegistration].[LastName],[UserRegistration].[Name],[UserRegistration].[StatusCode],[UserRegistration].[Password] FROM [registrations].[UserRegistrations] AS [UserRegistration]');"
else
  DB_CONTAINER="${PARITY_DB_CONTAINER:-mymeetings-sql}"
  PORT="${PARITY_DB_PORT:-1433}"
  CONN="${PARITY_CONNECTION_STRING:-Server=localhost,${PORT};Database=${DB_NAME};User=sa;Password=${SA_PASSWORD};Encrypt=False;}"
  echo "[parity.sh] PARITY_COMPOSE=0: using existing DB container '${DB_CONTAINER}' (port ${PORT})."
fi

export PARITY_DB_CONTAINER="$DB_CONTAINER"
export MyMeetings_SUTDatabaseConnectionString="$CONN"
export PARITY_CONNECTION_STRING="$CONN"

echo "[parity.sh] building parity tool + API (Debug) ..."
dotnet build tools/parity/src/CompanyName.MyMeetings.Parity -v q
dotnet build src/API/CompanyName.MyMeetings.API/CompanyName.MyMeetings.API.csproj \
  -c Debug -p:NuGetAudit=false -p:TreatWarningsAsErrors=false -v q

echo "[parity.sh] rebuilding deterministic fixture ..."
bash tools/parity/fixture.sh

echo "[parity.sh] running parity ${MODE} ..."
rm -rf tools/parity/.work
dotnet run --project tools/parity/src/CompanyName.MyMeetings.Parity --no-build -- "$MODE" "$@"
