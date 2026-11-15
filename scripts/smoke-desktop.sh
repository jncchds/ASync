#!/usr/bin/env bash
# Smoke test of the desktop app (Linux): the real app (async-desktop) on a virtual display (Xvfb) against a throw-away
# server stack (Docker Compose project async-desktop-smoke, port ASYNC_SMOKE_PORT, default 5192), with the real agent as
# a systemd user service that the app installs itself. The app is driven through its automation socket
# (ASYNC_APP_AUTOMATION, see src/ASync.App.Desktop/Automation.cs): typing into its text boxes, clicking its buttons and
# tray menu items. It checks:
#   no agent → "Install and start the agent" → the user unit runs `async-desktop service run`; the device-code flow
#   (code shown, approval page opened, cancel); enrolling by code; a folder attached in the web UI appears live; choosing
#   a local folder (the picker's answer is the only thing the test supplies); sync with live progress; pause/resume per
#   folder and from the tray; a failing folder raises a desktop notification; unlocking an end-to-end folder (initialised
#   by a second device, async-cli) with its passphrase; open folder / web UI; start at login; one instance only; closing
#   the window keeps the app in the tray, Quit ends the app but not the agent; signing out, then enrolling again with the
#   device-code flow approved in the web UI.
# The app runs with a private D-Bus session (no tray host, no notification daemon) and fake xdg-open/notify-send that only
# log, so nothing reaches the real desktop.
# Needs: Xvfb (XVFB=/path/to/Xvfb if not on PATH), dbus-daemon, python3, jq, curl, docker, a user systemd manager.
#   scripts/smoke-desktop.sh
#   SCREENSHOTS=docs/screenshots scripts/smoke-desktop.sh   # also saves desktop-*.png of every screen, light and dark
#   KEEP=1 scripts/smoke-desktop.sh                        # leave the stack and artifacts/desktop-smoke
# Refuses to run when the user already has an async-agent.service (it would replace it).
set -euo pipefail
cd "$(dirname "$0")/.."

port="${ASYNC_SMOKE_PORT:-5192}"
project=async-desktop-smoke
server="http://localhost:$port"
work="$PWD/artifacts/desktop-smoke"
cfg="$work/config"
auto="$work/run/app.sock"
unit=async-agent.service
unit_file="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$unit"
autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/ua.pp.chds.async.desktop"
shots="${SCREENSHOTS:-}"
[[ -n "$shots" ]] && shots="$(cd "$shots" && pwd)"
export ASYNC_PORT="$port"

failures=0
pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$1"; [[ -n "${2:-}" ]] && printf '%s\n' "$2" | sed 's/^/      /'; failures=$((failures + 1)); }
section() { printf '\n\033[1m── %s\033[0m\n' "$1"; }

if [[ -e "$unit_file" ]]; then
  echo "error: $unit_file already exists (a real agent?). Uninstall it first: async-cli service uninstall" >&2
  exit 2
fi
if [[ -e "$autostart" ]]; then
  echo "error: $autostart exists (the real app's start at login); the test would replace it" >&2
  exit 2
fi
systemctl --user show-environment >/dev/null 2>&1 || { echo "error: no user systemd manager" >&2; exit 2; }
xvfb="${XVFB:-$(command -v Xvfb || true)}"
[[ -x "$xvfb" ]] || { echo "error: Xvfb not found (install xvfb, or set XVFB=/path/to/Xvfb)" >&2; exit 2; }

compose() { docker compose -p "$project" "$@"; }
pids=()
cleanup() {
  local status=$?
  [[ -S "$auto" ]] && ctl quit >/dev/null 2>&1 || true
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done
  if [[ -e "$unit_file" ]]; then
    systemctl --user disable --now "$unit" >/dev/null 2>&1 || true
    rm -f "$unit_file"
    systemctl --user daemon-reload || true
    systemctl --user reset-failed "$unit" >/dev/null 2>&1 || true
  fi
  rm -f "$autostart"
  if [[ "${KEEP:-}" == 1 ]]; then
    echo "Kept the stack (remove it with: docker compose -p $project down -v) and $work."
  else
    compose down -v --remove-orphans >/dev/null 2>&1 || true
    chmod -R u+rwX "$work" 2>/dev/null || true
    rm -rf "$work"
  fi
  exit $status
}
trap cleanup EXIT

api() {
  local args=(-fsS -X "$1" "$server$2" -H "Content-Type: application/json")
  [[ -n "${token:-}" ]] && args+=(-H "Authorization: Bearer $token")
  [[ -n "${3:-}" ]] && args+=(--data "$3")
  curl "${args[@]}"
}

wait_for() {
  local seconds="$1"; shift
  for _ in $(seq 1 $((seconds * 4))); do "$@" >/dev/null 2>&1 && return 0; sleep 0.25; done
  return 1
}

# ctl COMMAND...: one automation command each; prints each answer's value; fails on an error answer.
ctl() {
  python3 - "$auto" "$@" <<'PY'
import json, socket, sys
s = socket.socket(socket.AF_UNIX)
s.connect(sys.argv[1])
f = s.makefile('rw')
for cmd in sys.argv[2:]:
    f.write(cmd + "\n"); f.flush()
    line = f.readline()
    if not line and cmd in ("quit", "tray Quit ASync"):
        break  # the app may close before answering
    r = json.loads(line)
    if not r.get("ok"):
        print(r.get("error"), file=sys.stderr); sys.exit(1)
    v = r.get("value")
    print(json.dumps(v) if v is not None else "")
PY
}
state() { ctl state; }
# has JQ: the app's state satisfies the jq expression.
has() { state | jq -e "$1" >/dev/null; }
wait_state() { wait_for "${2:-30}" has "$1"; }
folder_id() { state | jq -r --arg n "$1" '.folders[] | select(.name == $n) | .id'; }
server_has() { api GET "/api/folders/$1/files" | jq -e --arg n "$2" '.entries | any(.name == $n)'; }

# shot NAME [w h]: desktop-NAME-light.png and -dark.png (only with SCREENSHOTS).
shot() {
  [[ -z "$shots" ]] && return 0
  local name="$1"
  if [[ -n "${2:-}" ]]; then ctl "resize $2 $3" >/dev/null; sleep 0.4; fi
  for theme in light dark; do
    ctl "theme $theme" >/dev/null; sleep 0.4
    ctl "screenshot $shots/desktop-$name-$theme.png" >/dev/null
  done
  ctl "theme system" >/dev/null
  if [[ -n "${2:-}" ]]; then ctl "resize 760 720" >/dev/null; sleep 0.3; fi
  pass "screenshots desktop-$name-{light,dark}.png"
}

start_app() {
  DISPLAY=":$display" DBUS_SESSION_BUS_ADDRESS="$bus" PATH="$work/fakebin:$PATH" ASYNC_APP_AUTOMATION="$auto" ASYNC_APP_TRAY="${1:-0}" \
    "$work/app/async-desktop" --config "$cfg" >>"$work/app.log" 2>&1 &
  app_pid=$!
  pids+=("$app_pid")
  wait_for 30 test -S "$auto" && wait_for 30 ctl state || { cat "$work/app.log"; fail "the app started"; exit 1; }
}

rm -rf "$work"
mkdir -p "$work/run" "$work/fakebin" "$work/docs" "$work/vault" "$work/cli2"
chmod 700 "$work/run"
# xdg-open and notify-send only log what the app asked for.
for tool in xdg-open notify-send; do
  printf '#!/bin/sh\nprintf "%%s\\n" "$*" >> "%s/%s.log"\n' "$work" "$tool" > "$work/fakebin/$tool"
  chmod +x "$work/fakebin/$tool"
done

section "build, server stack, display"
dotnet publish src/ASync.App.Desktop/ASync.App.Desktop.csproj -c Release -o "$work/app" --nologo -v quiet
dotnet publish src/ASync.Cli/ASync.Cli.csproj -c Release -o "$work/cli" --nologo -v quiet
pass "async-desktop and async-cli published"
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build --wait async-server postgres >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
wait_for 120 curl -fsS "$server/api/auth/setup" || { echo "the server did not come up" >&2; exit 1; }
pass "server on $server"
token=$(api POST /api/auth/register '{"username":"admin","password":"smoke-password-1"}' | jq -r .accessToken)
backend=$(api POST /api/storage-backends '{"name":"smoke","settings":{"type":"Local","path":"/data/desktop-smoke"}}' | jq -r .id)
pass "account admin, storage back-end $backend"

display=$((90 + RANDOM % 9))
"$xvfb" ":$display" -screen 0 1280x900x24 -nolisten tcp >"$work/xvfb.log" 2>&1 &
pids+=($!)
bus=$(dbus-daemon --session --fork --print-address=1 --print-pid=1 | head -1)
pids+=("$(pgrep -n -x dbus-daemon)")
wait_for 10 test -e "/tmp/.X11-unix/X$display"
pass "Xvfb :$display, private D-Bus session"

section "no agent: the app installs it"
start_app
wait_state '.page == "ServiceSetupViewModel"' 30 && pass "without an agent the app offers to install it" || fail "service page" "$(state)"
has '.tray == "Problem"' && pass "tray: problem (no agent)" || fail "tray problem" "$(state)"
shot service-setup
ctl "click Install" >/dev/null
wait_state '.page == "EnrollViewModel"' 60 && pass "Install and start: the agent runs and answers (enroll page)" || fail "install" "$(state); $(tail -20 "$work/app.log")"
grep -q "async-desktop --config $cfg service run" "$unit_file" && pass "the user unit runs async-desktop service run" || fail "unit ExecStart" "$(cat "$unit_file")"
systemctl --user is-active "$unit" >/dev/null && pass "async-agent.service is active" || fail "unit active"

section "enroll"
ctl "type DeviceName desktop-smoke" "type Server $server" >/dev/null
shot enroll
shot enroll-small 380 720
ctl "click UseDeviceCode" "click StartDeviceCode" >/dev/null
wait_state '.userCode != null' 20 && pass "device-code flow: the code is shown" || fail "device code" "$(state)"
user_code=$(state | jq -r .userCode)
wait_for 10 grep -q "devices/approve?code=$user_code" "$work/xdg-open.log" && pass "the approval page was opened ($user_code)" || fail "approval link opened" "$(cat "$work/xdg-open.log" 2>/dev/null)"
shot enroll-device-code
ctl "click CancelPending" >/dev/null
wait_state '.userCode == null' 10 && pass "Cancel gives up the request" || fail "cancel device code" "$(state)"
# Now with a code from the web UI (Devices → Add device).
code=$(api POST /api/devices/enrollments '{"reusable":false,"label":"desktop smoke"}' | jq -r .code)
ctl "click UseCode" "type Code $code" "click Enroll" >/dev/null
wait_state '.page == "DashboardViewModel" and .device == "desktop-smoke"' 30 && pass "enrolled by code: the dashboard shows desktop-smoke" || fail "enroll by code" "$(state)"
device=$(api GET /api/devices | jq -r '.[] | select(.name == "desktop-smoke" and .revokedAt == null) | .id' | head -1)
[[ -n "$device" ]] && pass "the server lists the device ($device, platform $(api GET /api/devices | jq -r --arg d "$device" '.[] | select(.id == $d) | .platform'))" || fail "device on the server"
wait_state '.agentState == "Connected"' 30 && pass "connected" || fail "connected" "$(state)"
has '.tray == "Ok"' && pass "tray: ok" || fail "tray ok" "$(state)"
shot dashboard-empty

section "a folder attached in the web UI"
echo "hello from the desktop" > "$work/docs/hello.txt"
docs=$(api POST /api/folders "{\"name\":\"Documents\",\"mode\":\"TwoWay\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"docs\",\"intervalMinutes\":60}" | jq -r .id)
api PUT "/api/folders/$docs/devices" "{\"deviceId\":\"$device\",\"localPath\":\"$work/placeholder/Documents\"}" >/dev/null
wait_state '.folders | any(.name == "Documents")' 30 && pass "the folder appears live (no reload)" || fail "folder appears" "$(state)"
ctl "pick-folder $work/docs" "folder Documents | Change folder…" >/dev/null
wait_state ".folders[] | select(.name == \"Documents\") | .localPath == \"$work/docs\"" 30 && pass "choose a local folder: the agent now syncs $work/docs" || fail "set path" "$(state)"
api GET "/api/folders/$docs" | jq -e --arg p "$work/docs" '.devices[] | select(.localPath == $p)' >/dev/null && pass "the server has the new local path" || fail "server path"
wait_for 60 server_has "$docs" hello.txt && pass "the folder synced: hello.txt is on the server" || fail "first sync" "$(state)"

section "sync with live progress"
head -c 150000000 /dev/urandom > "$work/docs/big.bin"
busy_seen=0; progress_seen=""
ctl "folder Documents | Sync now" >/dev/null
for _ in $(seq 1 200); do
  s=$(state)
  if jq -e '.folders[] | select(.name == "Documents") | .busy' <<<"$s" >/dev/null; then
    busy_seen=1
    p=$(jq -r '.folders[] | select(.name == "Documents") | .progress' <<<"$s")
    [[ "$p" == *" of "* ]] && { progress_seen="$p"; break; }
  fi
  sleep 0.1
done
[[ $busy_seen == 1 ]] && pass "the folder shows Syncing while it runs" || fail "busy state"
[[ -n "$progress_seen" ]] && pass "live progress from the event stream: $progress_seen" || fail "progress text"
has '.tray == "Syncing"' && pass "tray: syncing" || true
shot dashboard-syncing
wait_state '.folders[] | select(.name == "Documents") | (.busy | not) and .lastOk' 120 && pass "the run finished: $(state | jq -r '.folders[] | select(.name == "Documents") | .lastResult')" || fail "run finished" "$(state)"
server_has "$docs" big.bin >/dev/null && pass "big.bin is on the server" || fail "big.bin uploaded"

section "pause and resume"
ctl "folder Documents | Pause" >/dev/null
wait_state '.folders[] | select(.name == "Documents") | .paused' 20 && pass "Pause (folder card)" || fail "pause" "$(state)"
api GET "/api/folders/$docs" | jq -e '.devices[0].paused' >/dev/null && pass "the server shows it paused" || fail "paused on server"
ctl "folder Documents | Resume" >/dev/null
wait_state '.folders[] | select(.name == "Documents") | .paused | not' 20 && pass "Resume (folder card)" || fail "resume" "$(state)"
ctl "tray Pause all" >/dev/null
wait_state '[.folders[] | .paused] | all' 20 && pass "tray: Pause all" || fail "tray pause all" "$(state)"
ctl "tray Resume all" >/dev/null
wait_state '[.folders[] | .paused] | any | not' 20 && pass "tray: Resume all" || fail "tray resume all" "$(state)"
ctl "folder Documents | Open folder" >/dev/null
wait_for 10 grep -qxE "$work/docs/?" "$work/xdg-open.log" && pass "Open folder opens $work/docs in the file manager" || fail "open folder" "$(cat "$work/xdg-open.log")"

section "end-to-end folder: unlock"
vault=$(api POST /api/folders "{\"name\":\"Vault\",\"mode\":\"Backup\",\"encryption\":\"EndToEnd\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"vault\",\"intervalMinutes\":60,\"retention\":{\"last\":5}}" | jq -r .id)
code2=$(api POST /api/devices/enrollments '{"reusable":false,"label":"second"}' | jq -r .code)
"$work/cli/async-cli" --config "$work/cli2" enroll --server "$server" --code "$code2" --name smoke-cli >/dev/null
cli_device=$(jq -r .deviceId "$work/cli2/identity.json")
mkdir -p "$work/cli-vault"; echo "secret" > "$work/cli-vault/a.txt"
api PUT "/api/folders/$vault/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-vault\"}" >/dev/null
ASYNC_PASSPHRASE="correct horse battery staple" "$work/cli/async-cli" --config "$work/cli2" init-encryption --folder "$vault" >/dev/null
pass "a second device (async-cli) set up the end-to-end folder Vault"
echo "my diary" > "$work/vault/diary.txt"
api PUT "/api/folders/$vault/devices" "{\"deviceId\":\"$device\",\"localPath\":\"$work/vault\"}" >/dev/null
wait_state '.folders[] | select(.name == "Vault") | .locked' 30 && pass "Vault appears, locked on this device" || fail "vault locked" "$(state)"
has '.tray == "Problem"' && pass "tray: problem (a locked folder)" || fail "tray problem for locked" "$(state)"
shot dashboard
ctl "folder Vault | Unlock…" >/dev/null
wait_state '.dialog == "UnlockViewModel"' 10 && pass "the unlock dialog opens" || fail "unlock dialog" "$(state)"
ctl "type Secret wrong passphrase" "click Unlock" >/dev/null
sleep 3
has '.dialog == "UnlockViewModel"' && pass "a wrong passphrase keeps the dialog open" || fail "wrong passphrase" "$(state)"
ctl "type Secret correct horse battery staple" >/dev/null
shot unlock
ctl "click Unlock" >/dev/null
wait_state '.dialog == null and (.folders[] | select(.name == "Vault") | .locked | not)' 60 && pass "unlocked with the passphrase" || fail "unlock" "$(state)"
wait_state '.folders[] | select(.name == "Vault") | .lastOk' 60 && pass "Vault backed up: $(state | jq -r '.folders[] | select(.name == "Vault") | .lastResult')" || fail "vault backup" "$(state)"
if grep -rq "correct horse" "$work/app.log" 2>/dev/null || journalctl --user -u "$unit" --no-pager -o cat 2>/dev/null | grep -q "correct horse"; then
  fail "the passphrase never reaches a log"
else
  pass "the passphrase never reaches a log (app log, journal)"
fi

section "a failing folder notifies"
chmod 000 "$work/docs"
ctl "folder Documents | Sync now" >/dev/null || true
wait_state '.folders[] | select(.name == "Documents") | .lastOk | not' 60 && pass "the folder fails: $(state | jq -r '.folders[] | select(.name == "Documents") | .lastResult' | cut -c1-80)" || fail "failure" "$(state)"
wait_for 10 grep -q "Sync failed: Documents Cannot read" "$work/notify-send.log" && pass "desktop notification: $(grep "Sync failed: Documents Cannot" "$work/notify-send.log" | head -1 | cut -c1-90)" || fail "notification" "$(cat "$work/notify-send.log" 2>/dev/null)"
shot dashboard-failed
server_has "$docs" hello.txt >/dev/null && server_has "$docs" big.bin >/dev/null && pass "nothing was deleted on the server meanwhile" || fail "files deleted while unreadable"
chmod 755 "$work/docs"
ctl "folder Documents | Sync now" >/dev/null
wait_state '.folders[] | select(.name == "Documents") | .lastOk' 60 && pass "syncs again after the fix" || fail "recovery" "$(state)"
wait_for 10 grep -q "Documents syncs again" "$work/notify-send.log" && pass "notification: Documents syncs again" || fail "recovery notification"

section "settings, web UI, start at login"
ctl "click SettingsButton" >/dev/null
wait_state '.settings' 10 && pass "settings open" || fail "settings"
shot settings
ctl "click StartAtLogin" >/dev/null
wait_for 5 test -f "$autostart" && grep -q -- "--minimized" "$autostart" && pass "start at login: $autostart" || fail "autostart on"
ctl "click StartAtLogin" >/dev/null
wait_for 5 test ! -e "$autostart" && pass "start at login off: removed" || fail "autostart off"
ctl "click CloseSettings" >/dev/null
: > "$work/xdg-open.log"
ctl "tray Open the web UI" >/dev/null
wait_for 10 grep -qxE "$server/?" "$work/xdg-open.log" && pass "Open the web UI opens $server" || fail "web UI" "$(cat "$work/xdg-open.log")"
shot dashboard-small 380 820
if [[ -n "$shots" ]]; then
  for st in ok syncing problem; do ctl "tray-icon $st $shots/desktop-tray-$st.png" >/dev/null; done
  pass "tray icons desktop-tray-{ok,syncing,problem}.png"
fi

section "window, tray and single instance"
ctl quit >/dev/null; wait_for 20 bash -c "! kill -0 $app_pid"
rm -f "$auto"
start_app 1   # as if a tray host were there: closing hides the window
ctl close >/dev/null
sleep 1
has '.windowVisible | not' && pass "closing the window keeps the app running in the tray" || fail "hide on close" "$(state)"
DISPLAY=":$display" DBUS_SESSION_BUS_ADDRESS="$bus" "$work/app/async-desktop" --config "$cfg" >>"$work/app.log" 2>&1 && pass "a second start exits at once" || fail "second start"
wait_state '.windowVisible' 10 && pass "… and brings the first one's window back" || fail "single instance show" "$(state)"
ctl "tray Quit ASync" >/dev/null
wait_for 20 bash -c "! kill -0 $app_pid" && pass "Quit ends the app" || fail "quit"
systemctl --user is-active "$unit" >/dev/null && pass "… but not the agent service" || fail "agent still running"

section "sign out, enroll again with an approval in the web UI"
rm -f "$auto"
start_app
wait_state '.page == "DashboardViewModel"' 30 || fail "dashboard after restart" "$(state)"
ctl "click SettingsButton" "click SignOut" >/dev/null
shot settings-sign-out
ctl "click ConfirmSignOut" >/dev/null
wait_state '.page == "EnrollViewModel"' 30 && pass "signed out: back to the enroll page" || fail "sign out" "$(state)"
[[ ! -e "$cfg/identity.json" ]] && pass "the identity is gone from $cfg" || fail "identity removed"
ctl "type DeviceName desktop-smoke-2" "type Server $server" "click UseDeviceCode" "click StartDeviceCode" >/dev/null
wait_state '.userCode != null' 20 || fail "second device code" "$(state)"
user_code=$(state | jq -r .userCode)
api POST /api/devices/approvals "{\"userCode\":\"$user_code\",\"approve\":true}" >/dev/null
wait_state '.page == "DashboardViewModel" and .device == "desktop-smoke-2"' 60 && pass "approved in the web UI: enrolled as desktop-smoke-2" || fail "device-code enrollment" "$(state)"

echo
if [[ $failures -eq 0 ]]; then echo "All desktop checks passed."; else echo "$failures check(s) failed."; exit 1; fi
