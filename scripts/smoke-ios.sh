#!/usr/bin/env bash
# Smoke test of the iOS app on a simulator, on a Mac (CI: .github/workflows/ios.yml on GitHub's macOS runners).
#
# The server runs natively here, with PostgreSQL from Homebrew: GitHub's macOS runners have no Docker. This is the one
# exception to "the server only in Docker", for CI on macOS; everywhere else the server runs with docker compose.
#
# The app is driven through its automation socket (src/ASync.App.iOS/Automation.cs, only on with ASYNC_APP_AUTOMATION=<port>,
# which only `simctl launch` can set), the simulator through `xcrun simctl` (no paid account, no XCUITest). It checks:
#   the app installs and starts; an async://pair link fills the enrollment form (and, last, one opened through iOS: openURL of the app's own scheme, since `simctl openurl` makes iOS ask "Open in ASync?", which simctl cannot tap); the device-code
#   flow (the code shown, approved through the web UI's API, enrolled); signing out deletes the device key from the
#   Keychain; enrolling by code with a new key (the Secure Enclave on a device, a Keychain key on the simulator) whose
#   DER signatures, converted to P1363, the server accepts; the server lists the device as iOS; the store's files sealed
#   (ioskc:1:, nothing readable); a TwoWay folder attached in the web UI at ~/Documents/Sync synced both ways with a second
#   device (async-cli), an edit and a delete included; the web UI's browsing listing the app's Documents and refusing
#   the rest; a folder chosen as a security-scoped bookmark (as the document picker makes one) synced, its location on the
#   server a bookmark: path; the local tree contract on bookmarked directories (selftest); the real document picker shown
#   (and cancelled); a new folder added from the app ("added from" the device on the server) synced up; background work
#   (task 22): the folders' BGTask requests (the simulator refuses BGTaskScheduler requests, so the task's own code path is
#   run with the app in the background, and a server change arrives), the reminders pending in the notification center
#   (moved by the setting, off with Never), no APNs registration without the entitlement, the conditions from iOS and the
#   web UI's (a charging-only folder makes the processing task need power), a silent push handled in the app, and an
#   `xcrun simctl push` to the suspended app; the app restarted (identity and folders survive, it signs in again).
#
# Needs: macOS with Xcode and a simulator runtime, the .NET SDK with the ios workload, jq, python3, Homebrew's PostgreSQL
# (PG_BIN=…/bin, default: brew --prefix postgresql@16). From the repository root:
#   scripts/smoke-ios.sh
#   APP=artifacts/packages/ios/simulator/ASync.app scripts/smoke-ios.sh   # a built app (scripts/package-ios.sh --simulator)
#   SCREENSHOTS=docs/screenshots scripts/smoke-ios.sh                     # also ios-*-light.png / -dark.png
#   KEEP=1 …                                                              # leave the server, the simulator and the work dir
# Logs (server, app, PostgreSQL) end up in artifacts/ios-smoke/logs.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" == Darwin ]] || { echo "error: this needs a Mac (the iOS simulator)" >&2; exit 2; }

root=$PWD
work="$root/artifacts/ios-smoke"
logs="$work/logs"
port="${ASYNC_SMOKE_PORT:-5194}"
pg_port="${ASYNC_SMOKE_PG_PORT:-5434}"
server="http://127.0.0.1:$port"
bundle=ua.pp.chds.async
shots="${SCREENSHOTS:-}"
[[ -n "$shots" ]] && { mkdir -p "$shots"; shots=$(cd "$shots" && pwd); }
failures=0
auto_port="${ASYNC_SMOKE_AUTOMATION_PORT:-47821}"

pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$1"; [[ -n "${2:-}" ]] && printf '%s\n' "$2" | sed 's/^/      /'; failures=$((failures + 1)); }
section() { printf '\n\033[1m── %s\033[0m\n' "$1"; }
need() { command -v "$1" >/dev/null || { echo "error: $1 not found" >&2; exit 2; }; }
need jq; need python3; need xcrun; need dotnet

server_pid=""
udid=""
cleanup() {
  local status=$?
  [[ -n "$udid" ]] && xcrun simctl spawn "$udid" log show --last 15m --style compact --predicate 'process == "ASync"' >"$logs/simulator-ASync.log" 2>/dev/null || true
  if [[ -n "${container:-}" && -f "$container/Library/Logs/async.log" ]]; then cp "$container/Library/Logs/async.log" "$logs/app-agent.log" || true; fi
  if [[ "${KEEP:-}" == 1 ]]; then
    echo "Kept: the server (pid $server_pid), PostgreSQL in $work/pg, the simulator $udid, $work."
  else
    [[ -n "$server_pid" ]] && kill "$server_pid" 2>/dev/null || true
    [[ -d "$work/pg" ]] && "$PG_BIN/pg_ctl" -D "$work/pg" -m fast stop >/dev/null 2>&1 || true
    if [[ -n "$udid" ]]; then xcrun simctl shutdown "$udid" >/dev/null 2>&1 || true; xcrun simctl delete "$udid" >/dev/null 2>&1 || true; fi
  fi
  exit $status
}
trap cleanup EXIT

wait_for() {
  local seconds="$1"; shift
  for _ in $(seq 1 $((seconds * 2))); do "$@" >/dev/null 2>&1 && return 0; sleep 0.5; done
  return 1
}

api() {
  # The admin's token lasts 15 minutes: sign in again every 10.
  if [[ -n "${token:-}" && $((SECONDS - ${token_at:-0})) -gt 600 ]]; then
    token=$(curl -fsS -X POST "$server/api/auth/login" -H "Content-Type: application/json" \
      --data '{"username":"admin","password":"smoke-password-1"}' | jq -r .accessToken)
    token_at=$SECONDS
  fi
  local args=(-fsS -X "$1" "$server$2" -H "Content-Type: application/json")
  [[ -n "${token:-}" ]] && args+=(-H "Authorization: Bearer $token")
  [[ -n "${3:-}" ]] && args+=(--data "$3")
  curl "${args[@]}"
}

# ctl COMMAND: one automation command; prints the app's one-line answer.
ctl() {
  python3 - "$auto_port" "$1" <<'EOF'
import socket, sys
s = socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=120)
s.sendall((sys.argv[2] + "\n").encode())
data = b""
while not data.endswith(b"\n"):
    chunk = s.recv(65536)
    if not chunk:
        break
    data += chunk
print(data.decode().rstrip("\n"))
EOF
}
state() { ctl state; }
has() { state | jq -e "$1" >/dev/null; }
wait_state() { wait_for "${2:-30}" has "$1"; }
folder_json() { state | jq -c --arg n "$1" '.folders[] | select(.name == $n)'; }
server_has() { api GET "/api/folders/$1/files" | jq -e --arg n "$2" '.entries | any(.name == $n)'; }

# shot NAME: ios-NAME-light.png and -dark.png (only with SCREENSHOTS): the app's theme and the simulator's appearance.
shot() {
  [[ -z "$shots" ]] && return 0
  local name="$1" mode
  for mode in light dark; do
    xcrun simctl ui "$udid" appearance "$mode" >/dev/null 2>&1 || true
    ctl "theme $mode" >/dev/null
    sleep 1.5
    xcrun simctl io "$udid" screenshot --type=png "$shots/ios-$name-$mode.png" >/dev/null 2>&1
  done
  xcrun simctl ui "$udid" appearance light >/dev/null 2>&1 || true
  ctl "theme system" >/dev/null
}
# shot_system NAME: a system screen over the app (the document picker): the simulator's appearance only.
shot_system() {
  [[ -z "$shots" ]] && return 0
  local mode
  for mode in light dark; do
    xcrun simctl ui "$udid" appearance "$mode" >/dev/null 2>&1 || true
    sleep 1.5
    xcrun simctl io "$udid" screenshot --type=png "$shots/ios-$1-$mode.png" >/dev/null 2>&1
  done
  xcrun simctl ui "$udid" appearance light >/dev/null 2>&1 || true
}

wait_run() { # TICKET [seconds]: the requested run's result
  local r
  for _ in $(seq 1 $(( ${2:-120} * 2 ))); do
    r=$(ctl "run $1")
    [[ "$r" != pending ]] && { printf '%s\n' "$r"; return 0; }
    sleep 0.5
  done
  echo timeout; return 1
}
sync_phone() { # FOLDER [seconds]: Sync now; again when another device held the folder
  local r
  for _ in 1 2 3; do
    r=$(wait_run "$(ctl "sync $1")" "${2:-120}")
    jq -e '.skipped' <<<"$r" >/dev/null 2>&1 || break
    sleep 3
  done
  printf '%s\n' "$r"
}

rm -rf "$work"
mkdir -p "$work" "$logs" "$work/data"

section "server (native, PostgreSQL from Homebrew)"
PG_BIN="${PG_BIN:-$(brew --prefix postgresql@16 2>/dev/null)/bin}"
[[ -x "$PG_BIN/initdb" ]] || { echo "error: no PostgreSQL in $PG_BIN (brew install postgresql@16, or PG_BIN=…)" >&2; exit 2; }
"$PG_BIN/initdb" -D "$work/pg" -U async --auth=trust >"$logs/initdb.log" 2>&1
"$PG_BIN/pg_ctl" -D "$work/pg" -l "$logs/postgres.log" -o "-p $pg_port -k /tmp" -w start >/dev/null
"$PG_BIN/createdb" -h 127.0.0.1 -p "$pg_port" -U async async
pass "PostgreSQL $("$PG_BIN/postgres" --version | awk '{print $NF}') on port $pg_port"

dotnet publish src/ASync.Api/ASync.Api.csproj -c Release -o "$work/server" --nologo -v quiet >"$logs/server-build.log" 2>&1 \
  || { cat "$logs/server-build.log"; exit 1; }
dotnet publish src/ASync.Cli/ASync.Cli.csproj -c Release -o "$work/cli" --nologo -v quiet >"$logs/cli-build.log" 2>&1 \
  || { cat "$logs/cli-build.log"; exit 1; }
(
  cd "$work/server"
  export ASPNETCORE_URLS="http://127.0.0.1:$port" ASPNETCORE_ENVIRONMENT=Production
  export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=$pg_port;Database=async;Username=async;Password=async"
  export Storage__LocalRoots="$work/data" Jwt__SigningKey="ios-smoke-signing-key-0123456789-abcdefghijklmnop"
  exec dotnet ASync.Api.dll
) >"$logs/server.log" 2>&1 &
server_pid=$!
wait_for 120 curl -fsS "$server/api/auth/setup" || { tail -50 "$logs/server.log"; echo "the server did not come up" >&2; exit 1; }
token=$(api POST /api/auth/register '{"username":"admin","password":"smoke-password-1"}' | jq -r .accessToken)
token_at=$SECONDS
backend=$(api POST /api/storage-backends "{\"name\":\"smoke\",\"settings\":{\"type\":\"Local\",\"path\":\"$work/data/ios\"}}" | jq -r .id)
pass "server on $server, account admin, storage $backend"

export ASYNC_SECRET_STORE=file # async-cli: no login Keychain on a runner
cli() { "$work/cli/async-cli" --config "$work/cli-config" "$@"; }
cli_sync() {
  local out
  for _ in $(seq 1 24); do
    out=$(cli sync --folder "$1" 2>&1) || true
    [[ "$out" != *"Another device is syncing"* ]] && return 0
    sleep 5
  done
  echo "$out" >&2
}

section "simulator"
APP="${APP:-}"
if [[ -z "$APP" ]]; then
  scripts/package-ios.sh --simulator >"$logs/app-build.log" 2>&1 || { tail -80 "$logs/app-build.log"; exit 1; }
  APP=artifacts/packages/ios/simulator/ASync.app
fi
runtime=$(xcrun simctl list runtimes available -j | jq -r '[.runtimes[] | select(.platform == "iOS")] | sort_by(.version | split(".") | map(tonumber)) | last | .identifier')
devtype=$(xcrun simctl list devicetypes -j | jq -r '[.devicetypes[] | select(.name | test("^iPhone [0-9]+$"))] | sort_by(.name | ltrimstr("iPhone ") | tonumber) | last | .identifier')
[[ -n "$runtime" && "$runtime" != null && -n "$devtype" && "$devtype" != null ]] || { xcrun simctl list; echo "error: no iOS runtime or iPhone type" >&2; exit 2; }
udid=$(xcrun simctl create "async-smoke-$$" "$devtype" "$runtime")
xcrun simctl boot "$udid"
xcrun simctl bootstatus "$udid" -b >/dev/null
pass "booted $(xcrun simctl list devices -j | jq -r --arg u "$udid" '.devices[][] | select(.udid == $u) | .name') ($devtype, $runtime)"
xcrun simctl install "$udid" "$APP"
pass "installed $(basename "$APP") ($(du -sh "$APP" | cut -f1))"

launch() {
  SIMCTL_CHILD_ASYNC_APP_AUTOMATION="$auto_port" xcrun simctl launch --terminate-running-process \
    --stdout="$logs/app-stdout-$1.log" --stderr="$logs/app-stderr-$1.log" "$udid" "$bundle" >/dev/null
  container=$(xcrun simctl get_app_container "$udid" "$bundle" data)
  wait_for 60 ctl state || { tail -40 "$logs/app-stdout-$1.log" "$logs/app-stderr-$1.log"; echo "the app's automation did not answer" >&2; exit 1; }
}
launch 1
# iOS asks whether ASync may send notifications when the first is due (a folder failing), and simctl cannot answer: the
# question would cover the screenshots. Off, as the Settings switch does.
ctl "notifications off" >/dev/null
pass "the app runs (automation on 127.0.0.1:$auto_port, container $container)"
docs="$container/Documents"

section "enrolling"
wait_state '.page == "EnrollViewModel"' 60 && pass "the enrollment form" || fail "enroll page" "$(state)"
code=$(api POST /api/devices/enrollments '{"reusable":false,"label":"ios smoke"}' | jq -r .code)
link="async://pair?s=$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$server")&c=$code"
ctl "link $link" >/dev/null
wait_state ".enrollCode == \"$code\" and .enrollServer == \"$server\" and .useCode" 20 \
  && pass "an async://pair link fills in the server and the code" || fail "link" "$(state)"
sleep 2 # the first frames after a launch can still be black
shot enroll

answer=$(ctl "devicecode $server")
[[ "$answer" =~ ^[A-Z0-9]{4}-[A-Z0-9]{4}$ ]] && pass "the device-code flow shows a code to approve on a computer ($answer)" || fail "device code" "$answer"
api POST /api/devices/approvals "{\"userCode\":\"$answer\",\"approve\":true}" >/dev/null
wait_state '.page == "DashboardViewModel"' 60 && pass "approved in the web UI: enrolled" || fail "device-code enroll" "$(state)"
wait_state '.state == "Connected"' 60 && pass "signed in with the new key" || fail "connected (device code)" "$(state)"
first_key=$(ctl keyinfo | jq -r .reference)
first_device=$(api GET /api/devices | jq -r '.[] | select(.revokedAt == null and .platform == "IOS") | .id' | head -1)
answer=$(ctl signout)
wait_state '.page == "EnrollViewModel"' 30 && pass "signed out ($answer)" || fail "sign out" "$answer $(state)"
gone=$(ctl "haskey $first_key")
[[ "$gone" == missing* ]] && pass "signing out deleted the device key from the Keychain" || fail "key deleted" "$gone"
api DELETE "/api/devices/$first_device" >/dev/null # and removed in the web UI, as the app says

answer=$(ctl "enroll $server $code")
wait_state '.page == "DashboardViewModel"' 60 && pass "enrolled by code ($answer)" || fail "enroll by code" "$answer $(state)"
wait_state '.state == "Connected"' 60 && pass "signed in: the server accepted the key's signatures (DER → P1363)" || fail "connected" "$(state)"
device=$(api GET /api/devices | jq -r --arg old "$first_device" '.[] | select(.revokedAt == null and .platform == "IOS" and .id != $old) | .id' | head -1)
[[ -n "$device" ]] && pass "the server lists the device as iOS ($device)" || fail "device on server" "$(api GET /api/devices)"
keys=$(ctl keyinfo)
jq -e '(.reference | startswith("ioskey:")) and (.where == "Secure Enclave" or (.simulator and .where == "Keychain (software)"))' <<<"$keys" >/dev/null \
  && pass "device key: $(jq -r .where <<<"$keys") (only its reference in the identity)" || fail "device key" "$keys"
files=$(ctl files)
jq -e 'map(select(.name == "identity.json" or .name == "token.json")) | length == 2 and all(.start | startswith("ioskc:1:"))' <<<"$files" >/dev/null \
  && ! grep -q admin "$container/Library/Application Support/async/identity.json" \
  && pass "identity.json and token.json sealed with the Keychain AES key (nothing readable)" || fail "sealed store" "$files"

section "a TwoWay folder in the app's Documents"
mkdir -p "$docs/Sync"
echo "from the iPhone" >"$docs/Sync/hello-phone.txt"
sync_id=$(api POST /api/folders "{\"name\":\"Phone docs\",\"mode\":\"TwoWay\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"docs\",\"intervalMinutes\":60}" | jq -r .id)
api PUT "/api/folders/$sync_id/devices" "{\"deviceId\":\"$device\",\"localPath\":\"~/Documents/Sync\"}" >/dev/null
wait_state '.folders | any(.name == "Phone docs")' 30 && pass "the folder attached in the web UI appears" || fail "folder appears" "$(state)"
code2=$(api POST /api/devices/enrollments '{"reusable":false,"label":"cli"}' | jq -r .code)
cli enroll --server "$server" --code "$code2" --name ios-smoke-cli >/dev/null
cli_device=$(jq -r .deviceId "$work/cli-config/identity.json")
mkdir -p "$work/cli-docs"
echo "from the computer" >"$work/cli-docs/from-cli.txt"
api PUT "/api/folders/$sync_id/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-docs\"}" >/dev/null
cli_sync "$sync_id"
last=$(sync_phone "$sync_id")
jq -e '.ok' <<<"$last" >/dev/null && pass "Sync now: $(jq -r .message <<<"$last")" || fail "phone sync" "$last"
[[ "$(cat "$docs/Sync/from-cli.txt" 2>/dev/null)" == "from the computer" ]] && pass "down: from-cli.txt is in On My iPhone › ASync › Sync" || fail "download"
server_has "$sync_id" hello-phone.txt >/dev/null && pass "up: hello-phone.txt is on the server" || fail "upload"
cli_sync "$sync_id"
[[ "$(cat "$work/cli-docs/hello-phone.txt" 2>/dev/null)" == "from the iPhone" ]] && pass "and on the other device" || fail "cli download"
echo "edited on the computer" >"$work/cli-docs/from-cli.txt"
cli_sync "$sync_id"
rm "$docs/Sync/hello-phone.txt"
last=$(sync_phone "$sync_id")
[[ "$(cat "$docs/Sync/from-cli.txt" 2>/dev/null)" == "edited on the computer" ]] && pass "an edit on the computer arrives" || fail "edit" "$last"
cli_sync "$sync_id"
[[ ! -e "$work/cli-docs/hello-phone.txt" ]] && pass "a delete on the iPhone reaches the other device" || fail "delete"
jq -e '.display == "On My iPhone › ASync › Sync"' <<<"$(folder_json "Phone docs")" >/dev/null && pass "shown as On My iPhone › ASync › Sync" || fail "display" "$(folder_json "Phone docs")"
shot dashboard

section "browsing from the web UI"
status=$(curl -s -o "$work/browse.json" -w "%{http_code}" -H "Authorization: Bearer $token" "$server/api/devices/$device/browse")
[[ "$status" == 200 ]] && jq -e '.places[0].label == "On My iPhone › ASync" and (.entries | any(.name == "Sync"))' "$work/browse.json" >/dev/null \
  && pass "lists On My iPhone › ASync with Sync in it" || fail "browse" "$status $(cat "$work/browse.json")"
status=$(curl -s -o "$work/browse2.json" -w "%{http_code}" -H "Authorization: Bearer $token" \
  "$server/api/devices/$device/browse?path=$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$container/Library")")
[[ "$status" == 422 ]] && pass "the app's private Library is refused (422)" || fail "browse refused" "$status $(cat "$work/browse2.json")"

section "a picked folder (security-scoped bookmark)"
mkdir -p "$docs/Picked"
echo "picked" >"$docs/Picked/picked.txt"
picked_id=$(api POST /api/folders "{\"name\":\"Picked\",\"mode\":\"Backup\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"picked\",\"intervalMinutes\":60,\"retention\":{\"last\":5}}" | jq -r .id)
api PUT "/api/folders/$picked_id/devices" "{\"deviceId\":\"$device\",\"localPath\":\"choose on the phone\"}" >/dev/null
wait_state '.folders | any(.name == "Picked")' 30 || fail "picked folder appears" "$(state)"
# The real picker, for a look (simctl cannot tap it): shown, then cancelled.
ctl "choose $picked_id" >/dev/null
wait_state '.picker' 15 && { sleep 2; shot_system folder-picker; pass "Change folder… shows the system's document picker"; } || fail "picker" "$(state)"
ctl picker-cancel >/dev/null
wait_state '.picker | not' 15 || fail "picker closed" "$(state)"
# What the picker answers, made the way the picker makes it: a bookmark of the folder.
location=$(ctl "bookmark ~/Documents/Picked")
ctl "pick $location" >/dev/null
ctl "choose $picked_id" >/dev/null
wait_for 30 bash -c "curl -fsS -H 'Authorization: Bearer $token' $server/api/folders/$picked_id | jq -e --arg d '$device' '.devices[] | select(.deviceId == \$d) | .localPath | startswith(\"bookmark:\")'" \
  && pass "the server has the bookmark as the local path ($location)" || fail "bookmark path" "$(api GET "/api/folders/$picked_id")"
last=$(sync_phone "$picked_id")
jq -e '.ok' <<<"$last" >/dev/null && pass "backed up through the bookmark: $(jq -r .message <<<"$last")" || fail "bookmark backup" "$last"
snapshots=$(api GET "/api/folders/$picked_id/snapshots" || true)
jq -e 'length >= 1' <<<"$snapshots" >/dev/null && pass "a snapshot on the server" || fail "snapshot" "$snapshots"
answer=$(ctl selftest)
[[ "$answer" =~ ^([0-9]+)/([0-9]+)\ passed\ \(BookmarkTree\)$ && "${BASH_REMATCH[1]}" == "${BASH_REMATCH[2]}" ]] \
  && pass "the local tree contract on bookmarked directories: $answer" || fail "selftest" "$answer"

section "adding a folder from the app"
mkdir -p "$docs/Notes"
echo "a note" >"$docs/Notes/note.txt"
answer=$(ctl "add-folder new")
jq -e '.open and .loaded and (.storages | any(.id == '"$backend"'))' <<<"$answer" >/dev/null && pass "New folder… offers the account's storage" || fail "add-folder options" "$answer"
ctl "add-folder-set name Notes" >/dev/null
ctl "add-folder-set mode TwoWay" >/dev/null
ctl "add-folder-set storage $backend" >/dev/null
ctl "add-folder-set local ~/Documents/Notes" >/dev/null
shot add-folder
answer=$(ctl add-folder-submit)
jq -e '.open | not' <<<"$answer" >/dev/null && pass "New folder… created Notes ($(jq -r .message <<<"$answer"))" || fail "add folder" "$answer"
notes=$(api GET /api/folders | jq -r '.[] | select(.name == "Notes") | .id')
api GET "/api/folders/$notes" | jq -e '.addedFromDevice != null' >/dev/null \
  && pass "the server shows it added from the iPhone ($(api GET "/api/folders/$notes" | jq -r .addedFromDevice))" || fail "added from" "$(api GET "/api/folders/$notes")"
wait_state '.folders | any(.name == "Notes")' 30 || fail "notes appears" "$(state)"
last=$(sync_phone "$notes")
server_has "$notes" note.txt >/dev/null && pass "synced up: note.txt is on the server" || fail "notes sync" "$last"
shot folders

section "background work (task 22)"
note() { printf '  \033[33m·\033[0m %s\n' "$1"; }
bg() { ctl bg; }
in_front() { xcrun simctl launch "$udid" "$bundle" >/dev/null; wait_for 30 ctl state; } # the running app back to the front
to_back() { xcrun simctl launch "$udid" com.apple.Preferences >/dev/null; } # Settings in front: ASync in the background
b=$(bg)
jq -e '(.requests | map(.id) == ["ua.pp.chds.async.refresh", "ua.pp.chds.async.processing"]) and all(.requests[]; .network and .delay >= 15)' <<<"$b" >/dev/null \
  && pass "the folders ask for a refresh and a processing task (earliest begin $(jq -r '.requests[0].delay' <<<"$b") min)" || fail "BGTask requests" "$b"
if jq -e '.tasks | length >= 1' <<<"$b" >/dev/null; then
  pass "BGTaskScheduler holds them: $(jq -c '[.tasks[] | {id, earliest}]' <<<"$b")"
else
  note "BGTaskScheduler took no request here ($(jq -r '.submitError // "no error"' <<<"$b")): the simulator does not run background tasks; their code path is run below"
fi
jq -e --arg id "reminder-$sync_id" '.reminders | any(.id == $id and .title == "Phone docs: nothing synced for 3 days" and .inSeconds > 255000 and .inSeconds <= 259200)' <<<"$b" >/dev/null \
  && pass "the reminder is pending: \"Phone docs: nothing synced for 3 days\", 3 days after its last successful run" || fail "reminder pending" "$(jq -c .reminders <<<"$b")"
ctl "bg-reminder 1" >/dev/null
jq -e --arg id "reminder-$sync_id" '.reminders | any(.id == $id and .title == "Phone docs: nothing synced for 1 day" and .inSeconds > 86000)' <<<"$(bg)" >/dev/null \
  && pass "choosing 1 day reschedules every folder's reminder" || fail "reminder days" "$(bg | jq -c .reminders)"
ctl "bg-reminder 0" >/dev/null
jq -e '.reminders | length == 0' <<<"$(bg)" >/dev/null && pass "Never: no reminders pending" || fail "reminders off" "$(bg | jq -c .reminders)"
ctl "bg-reminder 3" >/dev/null
jq -e '.push.register == false and (.push.why | test("simulator"))' <<<"$(bg)" >/dev/null \
  && pass "no APNs registration without the push entitlement: $(bg | jq -r .push.why)" || fail "push decision" "$(bg | jq -c .push)"
jq -e '.conditions.metered == false' <<<"$(bg)" >/dev/null && pass "run conditions from iOS: $(bg | jq -c .conditions)" || fail "conditions" "$(bg | jq -c .conditions)"

# Conditions set in the web UI: shown in the app, the processing task then needs external power.
api PUT "/api/folders/$notes/devices" "{\"deviceId\":\"$device\",\"localPath\":\"~/Documents/Notes\",\"conditions\":{\"unmeteredOnly\":true,\"chargingOnly\":true,\"batteryNotLow\":false}}" >/dev/null
wait_state ".folders[] | select(.name == \"Notes\") | .conditions == \"True|True|False\"" 30 && pass "the web UI's conditions reach the app (Wi-Fi only, while charging)" || fail "conditions in app" "$(folder_json Notes)"
processing_needs_power() { bg | jq -e '.requests[] | select(.id == "ua.pp.chds.async.processing") | .power'; }
wait_for 20 processing_needs_power \
  && pass "a charging-only folder: the processing task requires external power" || fail "processing power" "$(bg | jq -c .requests)"
ctl "show EditConditions" >/dev/null || true
shot folder-conditions
ctl "editconditions $notes" >/dev/null
sleep 1
shot conditions-editor
ctl back >/dev/null
ctl settings >/dev/null
ctl "show BackgroundCard" >/dev/null
shot background-settings
ctl back >/dev/null

# The silent push's code path in the app (didReceiveRemoteNotification hands the same dictionary over).
answer=$(ctl "bg-wake test")
[[ "$answer" == NewData || "$answer" == NoData ]] && pass "a silent push handled in the app answers its fetch completion ($answer) within 30 s" || fail "bg-wake" "$answer"

# A background task's runs with the app in the background: the task's code path (what the BGTask handler calls), started
# after the app went to the background; a change made on the server meanwhile arrives.
answer=$(ctl "bg-simulate refresh")
note "iOS's own simulated task launch: $answer (it needs a pending request, which the simulator refuses)"
ctl "bg-run refresh 12 $sync_id" >/dev/null
to_back
echo "made on the server while the iPhone app was in the background" >"$work/cli-docs/from-background.txt"
cli_sync "$sync_id"
if wait_for 60 test -f "$docs/Sync/from-background.txt"; then
  pass "with the app in the background, the refresh task's run brought from-background.txt down"
else
  fail "background run" "$(ls "$docs/Sync")"
fi
sleep 3
in_front
jq -e '.lastTask | test("^Refresh at .*: done .*Phone docs")' <<<"$(bg)" >/dev/null && pass "the task reported done: $(bg | jq -r .lastTask)" || fail "task result" "$(bg | jq -r .lastTask)"

# A real (simulated) push to the suspended app: xcrun simctl push, a change waiting on the server.
to_back
sleep 15 # suspended: no hub connection any more
echo "pushed" >"$work/cli-docs/pushed.txt"
cli_sync "$sync_id"
cat >"$work/push.json" <<EOF
{"Simulator Target Bundle": "$bundle", "aps": {"content-available": 1}, "async": "wake", "reason": "changed"}
EOF
xcrun simctl push "$udid" "$bundle" "$work/push.json" >/dev/null 2>&1 || true
if wait_for 45 test -f "$docs/Sync/pushed.txt"; then
  pass "xcrun simctl push woke the suspended app: pushed.txt came down"
elif grep -q "Push: woken" "$logs"/app-stdout-1.log 2>/dev/null; then
  fail "the push reached the app but pushed.txt did not come down" "$(ls "$docs/Sync")"
else
  note "the simulator did not deliver the silent push to the suspended app (no \"Push: woken\" in its log); the push code path ran above (bg-wake)"
fi
in_front
wait_for 30 test -f "$docs/Sync/pushed.txt" && pass "opening the app syncs (pushed.txt is there)" || fail "app opened sync" "$(ls "$docs/Sync")"

section "restart"
launch 2
wait_state '.page == "DashboardViewModel" and .state == "Connected"' 60 && pass "after a restart: still enrolled, signed in again" || fail "restart" "$(state)"
wait_state '.folders | length == 3' 30 && pass "the three folders are back" || fail "folders after restart" "$(state)"

section "a pairing link opened through iOS"
# Last: if iOS asked "Open in ASync?" here, the question would cover the screens after it (simctl cannot tap it).
ctl signout >/dev/null
wait_state '.page == "EnrollViewModel"' 30 || fail "signed out again" "$(state)"
code3=$(api POST /api/devices/enrollments '{"reusable":false,"label":"ios link"}' | jq -r .code)
link="async://pair?s=$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$server")&c=$code3"
ctl "openurl $link" >/dev/null
if wait_state ".enrollCode == \"$code3\" and .enrollServer == \"$server\"" 20; then
  pass "an async://pair link opened through iOS (URL scheme, scene, activation) fills in the form"
else
  xcrun simctl io "$udid" screenshot --type=png "$logs/openurl.png" >/dev/null 2>&1 || true
  fail "link opened through iOS (screen in logs/openurl.png)" "$(state)"
fi

# The very last: iOS's notification question (the dashboard prompt's Allow asks it); simctl cannot answer it.
if [[ -n "$shots" ]]; then
  ctl notify-permission >/dev/null
  sleep 2
  shot_system notification-permission
fi

echo
if ((failures)); then echo "$failures check(s) failed (logs: $logs)"; exit 1; fi
echo "All checks passed."
