#!/usr/bin/env bash
#
# Deterministic fixture bring-up for the parity baseline.
#
# Rebuilds the exact starting state used by both `capture` and `verify`:
#   1. reset + reseed identity columns   -> ClearDatabase.sql (DBCC CHECKIDENT RESEED)
#   2. supplemental reset (app.Emails / meetings.Countries not covered by ClearDatabase.sql)
#   3. populate domain data + permissions -> SUT ParityFixture (clock frozen at 2020-06-15)
#   4. reference data                      -> SeedCountries.sql
#
# The SUT harness (step 3) creates the sign-in identities the API baseline authenticates as, with
# real password hashes and correct roles: testAdmin@mail.com/testAdminPass (Administrator) and
# adamSmith@mail.com/adamSmithPass (Member; regular registration confirmation assigns UserRole.Member).
#
# Determinism relies on: reseeded identity columns and the frozen clock. The SUT's runtime GUIDs are
# random but are normalized to stable tokens downstream by the parity tool.
#
# Env:
#   PARITY_DB_CONTAINER  docker container running SQL Server (default: mymeetings-sql)
#   PARITY_DB_SA_PASSWORD sa password (default: Test@12345)
#   MyMeetings_SUTDatabaseConnectionString  connection string for the SUT harness
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DB_CONTAINER="${PARITY_DB_CONTAINER:-mymeetings-sql}"
SA_PASSWORD="${PARITY_DB_SA_PASSWORD:-Test@12345}"
DB_NAME="MyMeetings"
CONN="${MyMeetings_SUTDatabaseConnectionString:-Server=localhost,1433;Database=MyMeetings;User=sa;Password=${SA_PASSWORD};Encrypt=False;}"

DB_DIR="$REPO_ROOT/src/Database/CompanyName.MyMeetings.Database/Scripts"
SUT_PROJ="$REPO_ROOT/src/Tests/SUT/CompanyName.MyMeetings.SUT.csproj"

# Locate a usable sqlcmd inside the container (path differs between mssql-tools and mssql-tools18).
find_sqlcmd() {
  for p in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd sqlcmd; do
    if docker exec "$DB_CONTAINER" test -x "$p" 2>/dev/null || docker exec "$DB_CONTAINER" sh -c "command -v $p" >/dev/null 2>&1; then
      echo "$p"; return 0
    fi
  done
  echo "ERROR: sqlcmd not found in container $DB_CONTAINER" >&2; return 1
}
SQLCMD="$(find_sqlcmd)"

run_sql_file() {
  local file="$1"
  echo "[fixture] applying $(basename "$file")"
  # Strip a leading UTF-8 BOM which sqlcmd (via stdin) otherwise reports as a syntax error.
  sed '1s/^\xEF\xBB\xBF//' "$file" \
    | docker exec -i "$DB_CONTAINER" "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -d "$DB_NAME" -b -I
}

run_sql() {
  docker exec -i "$DB_CONTAINER" "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -d "$DB_NAME" -b -Q "$1"
}

echo "[fixture] container=$DB_CONTAINER sqlcmd=$SQLCMD"

# 1. pre-clean FK children that ClearDatabase.sql does not delete (so its DELETE FROM meetings.Meetings
#    does not hit FK_meetings_MeetingCommentingConfigurations_Meetings), plus tables it leaves
#    untouched (app.Emails accumulates; meetings.Countries is reseeded below).
run_sql "DELETE FROM [meetings].[MeetingCommentingConfigurations];
DELETE FROM [meetings].[MemberSubscriptions];
DELETE FROM [app].[Emails];
DELETE FROM [meetings].[Countries];"

# 2. reset via the repo's ClearDatabase.sql (best effort: it also reseeds the event-store IDENTITYs;
#    the parity extractor additionally dense-ranks those columns so seed drift never affects hashes).
run_sql_file "$DB_DIR/ClearDatabase.sql" || echo "[fixture] ClearDatabase.sql reported non-fatal issues (continuing)"

# 3. populate domain data through the SUT harness (frozen clock, real auth path).
echo "[fixture] running SUT ParityFixture ..."
MyMeetings_SUTDatabaseConnectionString="$CONN" \
  dotnet test "$SUT_PROJ" --no-build \
    --filter 'FullyQualifiedName~ParityFixture' \
    --logger 'console;verbosity=minimal'

# 4. reference data (countries lookup used by group/proposal endpoints).
run_sql_file "$DB_DIR/Seeds/0001_SeedCountries.sql"

echo "[fixture] done."
