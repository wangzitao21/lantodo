#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# An isolated Compose project owns every resource cleaned up by this test.
project="lantodo-smoke-$$"
export LANTODO_BIND_IP=127.0.0.1
export LANTODO_PORT=42859
export LANTODO_WEB_PORT=18089
export LANTODO_ADMIN_TOKEN=synthetic-docker-test-token-not-a-real-password
cleanup() { docker compose -p "$project" down --volumes >/dev/null; }
trap cleanup EXIT
docker compose -p "$project" up -d --build
ready() {
  for attempt in $(seq 1 20); do
    if docker compose -p "$project" exec -T lantodo dotnet LanTodo.Nas.dll status > /dev/null; then return; fi
    sleep 1
  done
  docker compose -p "$project" logs --tail 30
  return 1
}
ready
test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18089/api/status)" = 401
curl -fsS -H "Authorization: Bearer $LANTODO_ADMIN_TOKEN" http://127.0.0.1:18089/api/status > /dev/null
curl -fsS http://127.0.0.1:18089/app.js > /dev/null
before=$(docker compose -p "$project" exec -T lantodo dotnet LanTodo.Nas.dll status)
docker compose -p "$project" exec -T lantodo dotnet LanTodo.Nas.dll backup
docker compose -p "$project" restart
ready
after=$(docker compose -p "$project" exec -T lantodo dotnet LanTodo.Nas.dll status)
test "$before" = "$after"
echo "PASS Docker: non-root service, local administration, backup volume and restart identity."
