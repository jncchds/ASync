#!/usr/bin/env bash
# Smoke test of the agent as a per-user systemd service (Linux): builds async-cli, starts a throw-away server stack
# (Docker Compose project async-agent-smoke, port ASYNC_SMOKE_PORT, default 5191), enrolls a device, then
#   installs the user unit async-agent.service (`async-cli service install`), checks that it is active (Type=notify),
#   that `async-cli status/sync/pause/resume` go through its control channel, that it syncs a folder by itself and on
#   request, that a second agent (`async-cli run`) is refused, that it logs to the journal, that it survives
#   `systemctl --user restart`, and that `async-cli service uninstall` removes it cleanly.
# Needs a user systemd manager (a desktop session, or `loginctl enable-linger`), docker, curl and jq. From the repo root:
#   scripts/smoke-agent-service.sh
#   KEEP=1 scripts/smoke-agent-service.sh   # leave the stack and artifacts/agent-smoke for a look afterwards
# Refuses to run when an async-agent.service of the user is already installed (it would replace it).
set -euo pipefail
cd "$(dirname "$0")/.."

port="${ASYNC_SMOKE_PORT:-5191}"
project=async-agent-smoke
server="http://localhost:$port"
work="$PWD/artifacts/agent-smoke"
cfg="$work/config"
unit=async-agent.service
unit_file="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$unit"
export ASYNC_PORT="$port"

failures=0
pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$1"; [[ -n "${2:-}" ]] && printf '%s\n' "$2" | sed 's/^/      /'; failures=$((failures + 1)); }
check() { local what="$1"; shift; if "$@" >/dev/null 2>&1; then pass "$what"; else fail "$what"; fi; }
section() { printf '\n\033[1m── %s\033[0m\n' "$1"; }

if [[ -e "$unit_file" ]]; then
  echo "error: $unit_file already exists (a real agent?). Uninstall it first: async-cli service uninstall" >&2
  exit 2
fi
systemctl --user show-environment >/dev/null 2>&1 || { echo "error: no user systemd manager (log in, or loginctl enable-linger $USER)" >&2; exit 2; }

compose() { docker compose -p "$project" "$@"; }
cli() { "$work/cli/async-cli" --config "$cfg" "$@"; }
cleanup() {
  local status=$?
  # The unit must never stay behind, also when the test fails half-way.
  if [[ -e "$unit_file" ]]; then
    systemctl --user disable --now "$unit" >/dev/null 2>&1 || true
    rm -f "$unit_file"
    systemctl --user daemon-reload || true
    systemctl --user reset-failed "$unit" >/dev/null 2>&1 || true
  fi
  if [[ "${KEEP:-}" == 1 ]]; then
    echo "Kept the stack (remove it with: docker compose -p $project down -v) and $work."
  else
    compose down -v --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$work"
  fi
  exit $status
}
trap cleanup EXIT

# api METHOD PATH [JSON]: the body on stdout; fails on an HTTP error.
api() {
  local args=(-fsS -X "$1" "$server$2" -H "Content-Type: application/json")
  [[ -n "${token:-}" ]] && args+=(-H "Authorization: Bearer $token")
  [[ -n "${3:-}" ]] && args+=(--data "$3")
  curl "${args[@]}"
}

# Waits up to $1 seconds for the command to succeed.
wait_for() {
  local seconds="$1"; shift
  for _ in $(seq 1 $((seconds * 2))); do "$@" >/dev/null 2>&1 && return 0; sleep 0.5; done
  return 1
}

server_has() { api GET "/api/folders/$folder/files" | jq -e --arg n "$1" '.entries | any(.name == $n)'; }
main_pid() { systemctl --user show -p MainPID --value "$unit"; }

rm -rf "$work"
mkdir -p "$work/docs"
started_at=$(date '+%Y-%m-%d %H:%M:%S')
journal() { journalctl --user -u "$unit" --since "$started_at" --no-pager -o cat; }
section "build and server stack"
dotnet publish src/ASync.Cli/ASync.Cli.csproj -c Release -o "$work/cli" --nologo -v quiet
pass "async-cli published to $work/cli"
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build --wait async-server postgres >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
wait_for 120 curl -fsS "$server/api/auth/setup" || { echo "the server did not come up" >&2; exit 1; }
pass "server on $server"

section "account, device, folder"
token=$(api POST /api/auth/register '{"username":"admin","password":"smoke-password-1"}' | jq -r .accessToken)
[[ "$token" != null && -n "$token" ]] && pass "registered admin" || { fail "register admin"; exit 1; }
backend=$(api POST /api/storage-backends '{"name":"smoke","settings":{"type":"Local","path":"/data/agent-smoke"}}' | jq -r .id)
code=$(api POST /api/devices/enrollments '{"reusable":false,"label":"agent smoke"}' | jq -r .code)
cli enroll --server "$server" --code "$code" --name agent-smoke >/dev/null
device=$(jq -r .deviceId "$cfg/identity.json")
pass "enrolled device $device"
folder=$(api POST /api/folders "{\"name\":\"docs\",\"mode\":\"TwoWay\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"docs\",\"intervalMinutes\":60,\"retention\":{\"last\":10}}" | jq -r .id)
api PUT "/api/folders/$folder/devices" "{\"deviceId\":\"$device\",\"localPath\":\"$work/docs\"}" >/dev/null
pass "TwoWay folder $folder attached at $work/docs"
out=$(cli status)
[[ "$out" == *"Agent:   not running"* ]] && pass "without an agent, status works in-process" || fail "in-process status" "$out"

section "install the user unit"
echo "first file" > "$work/docs/before-install.txt"
out=$(cli service install 2>&1) && pass "async-cli service install" || fail "async-cli service install" "$out"
printf '%s\n' "$out" | sed 's/^/      /'
check "unit file written to $unit_file" test -f "$unit_file"
check "unit is Type=notify" grep -qx "Type=notify" "$unit_file"
check "unit is enabled" systemctl --user is-enabled "$unit"
check "unit is active (READY=1 reached)" systemctl --user is-active "$unit"
first_pid=$(main_pid)
wait_for 30 cli status || true
out=$(cli status 2>&1)
[[ "$out" == *"running as a service, pid $first_pid"* ]] && pass "status goes through the service (pid $first_pid)" || fail "status through the service" "$out"
check "the control socket is private (0600)" bash -c "stat -c %a \"\$(cat '$cfg/agent.endpoint')\" | grep -qx 600"
wait_for 60 server_has before-install.txt && pass "the service synced the folder by itself on start" || fail "first sync by the service" "$(cli status 2>&1)"

section "commands through the control channel"
echo "second file" > "$work/docs/by-request.txt"
out=$(cli sync 2>&1) && pass "async-cli sync runs in the service" || fail "sync through the service" "$out"
printf '%s\n' "$out" | sed 's/^/      /'
check "the requested sync uploaded by-request.txt" server_has by-request.txt
out=$(cli run 2>&1) && fail "a second agent (async-cli run) is refused" "$out" || {
  [[ "$out" == *"already running"* ]] && pass "a second agent (async-cli run) is refused" || fail "second agent refused with the right message" "$out"; }
out=$(cli pause --folder "$folder" 2>&1) && [[ "$out" == *paused* ]] && pass "pause through the service" || fail "pause" "$out"
out=$(cli status 2>&1); [[ "$out" == *"paused"* ]] && pass "status shows the folder paused" || fail "paused in status" "$out"
out=$(cli resume --folder "$folder" 2>&1) && [[ "$out" == *resumed* ]] && pass "resume through the service" || fail "resume" "$out"
echo "third file" > "$work/docs/by-watcher.txt"
wait_for 60 server_has by-watcher.txt && pass "a new file is synced by the file watcher (no command)" || fail "watcher sync"
out=$(journal); [[ "$out" == *"Control channel:"* && "$out" == *'"docs": 1 up'* ]] && pass "the service logs to the journal" || fail "journal" "$out"

section "systemctl --user restart"
systemctl --user restart "$unit"
check "active again after restart" systemctl --user is-active "$unit"
second_pid=$(main_pid)
out=$(journal); [[ "$out" != *"Failed with result"* && "$out" != *"Unhandled exception"* ]] && pass "the old process stopped cleanly (exit 0)" || fail "clean stop" "$(printf '%s\n' "$out" | tail -20)"
[[ "$second_pid" != "$first_pid" && "$second_pid" != 0 ]] && pass "a new process ($first_pid → $second_pid)" || fail "new pid after restart" "$first_pid → $second_pid"
wait_for 30 cli status || true
out=$(cli status 2>&1)
[[ "$out" == *"pid $second_pid"* ]] && pass "the CLI reaches the restarted service" || fail "status after restart" "$out"
echo "fourth file" > "$work/docs/after-restart.txt"
out=$(cli sync 2>&1) || true
wait_for 30 server_has after-restart.txt && pass "syncs after the restart" || fail "sync after restart" "$out"
cli service status | sed 's/^/      /' || true

section "uninstall"
out=$(cli service uninstall 2>&1) && pass "async-cli service uninstall" || fail "uninstall" "$out"
check "unit file removed" test ! -e "$unit_file"
check "unit no longer active" bash -c "! systemctl --user is-active $unit"
check "no process left" bash -c "! kill -0 $second_pid"
out=$(journal); [[ "$out" != *"Failed with result"* && "$out" != *"Unhandled exception"* ]] && pass "stopped cleanly on uninstall" || fail "clean stop on uninstall" "$(printf '%s\n' "$out" | tail -20)"
check "control socket and endpoint file removed" bash -c "test ! -e '$cfg/agent.endpoint'"
out=$(cli status 2>&1)
[[ "$out" == *"Agent:   not running"* ]] && pass "the CLI works in-process again" || fail "in-process after uninstall" "$out"
check "the identity stays" test -f "$cfg/identity.json"

echo
if [[ $failures -eq 0 ]]; then echo "agent service smoke test: all checks passed"; else echo "agent service smoke test: $failures check(s) failed"; exit 1; fi
