#!/usr/bin/env bash
# End-to-end parity gate: ensure SQL Server + MyMeetings DB exist, start the API,
# then run `parity verify`. Idempotent — safe to re-run. Any extra arguments are
# forwarded to the verify command (e.g. `--module Registrations`).
#
#   tools/parity/ci-parity.sh                     # verify all dimensions
#   tools/parity/ci-parity.sh --module Payments   # gate one track
#   PARITY_DIMENSIONS=db,dto tools/parity/ci-parity.sh   # skip the api dimension
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

CONN='Server=localhost,1433;Database=MyMeetings;User Id=sa;Password=Test@12345;Encrypt=False;TrustServerCertificate=True;'
MIGRATE_CONN='Server=localhost,1433;Database=MyMeetings;User=sa;Password=Test@12345;Encrypt=False;'
SQLCMD='/opt/mssql-tools18/bin/sqlcmd'
DB=src/Database/CompanyName.MyMeetings.Database
API_URL="${PARITY_API_BASE_URL:-http://127.0.0.1:5000}"
DIMENSIONS="${PARITY_DIMENSIONS:-db,api,dto}"

sacmd() { docker exec -i mymeetings-sql "$SQLCMD" -S localhost -U sa -P 'Test@12345' -C "$@"; }

echo "==> Ensuring SQL Server container"
if ! docker ps -a --format '{{.Names}}' | grep -qx mymeetings-sql; then
  docker run -d --name mymeetings-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=Test@12345 \
    -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
else
  docker start mymeetings-sql >/dev/null
fi

for _ in $(seq 1 60); do
  if sacmd -Q 'SELECT 1' >/dev/null 2>&1; then break; fi
  sleep 2
done

if sacmd -h -1 -Q "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name='MyMeetings'" | grep -q MyMeetings; then
  echo "==> MyMeetings database already present"
else
  echo "==> Creating / migrating / seeding MyMeetings database"
  sed '1s/^\xEF\xBB\xBF//' "$DB/Scripts/CreateDatabase_Linux.sql" | sacmd -b -i /dev/stdin
  ./build.sh MigrateDatabase --DatabaseConnectionString "$MIGRATE_CONN"
  sed '1s/^\xEF\xBB\xBF//' "$DB/Scripts/SeedDatabase.sql" | sacmd -d MyMeetings -b -i /dev/stdin
  sacmd -d MyMeetings -b -Q "IF OBJECT_ID(N'registrations.v_UserRegistrations', N'V') IS NOT NULL DROP VIEW [registrations].[v_UserRegistrations]; EXEC(N'CREATE VIEW [registrations].[v_UserRegistrations] AS SELECT [UserRegistration].[Id],[UserRegistration].[Login],[UserRegistration].[Email],[UserRegistration].[FirstName],[UserRegistration].[LastName],[UserRegistration].[Name],[UserRegistration].[StatusCode],[UserRegistration].[Password] FROM [registrations].[UserRegistrations] AS [UserRegistration]');"
fi

API_PID=""
if [[ ",$DIMENSIONS," == *",api,"* ]]; then
  if curl -s -o /dev/null "$API_URL/api/userAccess/emails"; then
    echo "==> API already reachable at $API_URL"
  else
    echo "==> Starting API host"
    Meetings_ConnectionStrings__MeetingsConnectionString="$CONN" \
    ASPNETCORE_ENVIRONMENT=Development \
      nohup dotnet run --project src/API/CompanyName.MyMeetings.API/CompanyName.MyMeetings.API.csproj \
        -p:NuGetAudit=false --urls "$API_URL" > /tmp/parity-api.log 2>&1 &
    API_PID=$!
    for _ in $(seq 1 60); do
      if curl -s -o /dev/null -w '%{http_code}' "$API_URL/api/userAccess/emails" | grep -q 200; then break; fi
      sleep 2
    done
  fi
fi

cleanup() { [[ -n "$API_PID" ]] && kill "$API_PID" 2>/dev/null || true; }
trap cleanup EXIT

echo "==> Running parity verify (dimensions: $DIMENSIONS)"
dotnet run --project tools/parity -c Release -- verify --dimensions "$DIMENSIONS" "$@"
