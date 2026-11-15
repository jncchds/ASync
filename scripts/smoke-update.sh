#!/usr/bin/env bash
# Smoke test of the AppImage and its self-update (Linux): builds the desktop app twice, as version A and B
# (scripts/package.sh --only desktop), runs A's AppImage on a virtual display (Xvfb) with B's Velopack release folder
# as its update feed (ASYNC_UPDATE_URL), and drives it through its automation socket (see smoke-desktop.sh). Checks:
#   the app installs the agent as a systemd user service that runs the AppImage file (not its temporary mount); the
#   agent answers with version A; the app finds B on the feed (banner, tray item, Settings); "Install and restart" stops
#   the agent, Velopack replaces the AppImage and restarts the app with --after-update; the restarted app is B, starts
#   the agent again, and the agent is B too (a new process); `ASync.AppImage uninstall` removes the service and keeps the
#   config directory; `uninstall --remove-data` removes that too.
# No server is needed: the agent stays "not enrolled".
# Needs: Xvfb (XVFB=/path/to/Xvfb if not on PATH), dbus-daemon, python3, jq, FUSE (AppImages mount themselves),
# a user systemd manager, vpk (dotnet tool restore, or VPK=…).
#   scripts/smoke-update.sh
#   SCREENSHOTS=docs/screenshots scripts/smoke-update.sh   # also saves desktop-update-*.png (light and dark)
#   A_VERSION=0.1.900 B_VERSION=0.1.901 KEEP=1 scripts/smoke-update.sh
# Refuses to run when the user already has an async-agent.service (it would replace it).
set -euo pipefail
cd "$(dirname "$0")/.."

a="${A_VERSION:-0.1.900}"
b="${B_VERSION:-0.1.901}"
work="$PWD/artifacts/update-smoke"
cfg="$work/config"
auto="$work/run/app.sock"
image="$work/ASync.AppImage"
unit=async-agent.service
unit_file="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$unit"
shots="${SCREENSHOTS:-}"
[[ -n "$shots" ]] && shots="$(cd "$shots" && pwd)"

failures=0
pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$1"; [[ -n "${2:-}" ]] && printf '%s\n' "$2" | sed 's/^/      /'; failures=$((failures + 1)); }
section() { printf '\n\033[1m── %s\033[0m\n' "$1"; }

if [[ -e "$unit_file" ]] || systemctl --user is-enabled "$unit" >/dev/null 2>&1; then
  echo "error: the user already has $unit (a real agent?). Uninstall it first." >&2
  exit 2
fi
systemctl --user show-environment >/dev/null 2>&1 || { echo "error: no user systemd manager" >&2; exit 2; }
xvfb="${XVFB:-$(command -v Xvfb || true)}"
[[ -x "$xvfb" ]] || { echo "error: Xvfb not found (install xvfb, or set XVFB=/path/to/Xvfb)" >&2; exit 2; }

pids=()
cleanup() {
  local status=$?
  [[ -S "$auto" ]] && ctl quit >/dev/null 2>&1 || true
  sleep 0.5
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done
  if [[ -e "$unit_file" ]]; then
    systemctl --user disable --now "$unit" >/dev/null 2>&1 || true
    rm -f "$unit_file"
    systemctl --user daemon-reload || true
    systemctl --user reset-failed "$unit" >/dev/null 2>&1 || true
  fi
  if [[ "${KEEP:-}" == 1 ]]; then echo "Kept $work."; else rm -rf "$work"; fi
  exit $status
}
trap cleanup EXIT

wait_for() {
  local seconds="$1"; shift
  for _ in $(seq 1 $((seconds * 4))); do "$@" >/dev/null 2>&1 && return 0; sleep 0.25; done
  return 1
}

ctl() {
  python3 - "$auto" "$@" <<'PY'
import json, socket, sys
s = socket.socket(socket.AF_UNIX)
s.connect(sys.argv[1])
f = s.makefile('rw')
for cmd in sys.argv[2:]:
    f.write(cmd + "\n"); f.flush()
    line = f.readline()
    if not line and cmd in ("quit", "click InstallUpdate"):
        break
    r = json.loads(line)
    if not r.get("ok"):
        print(r.get("error"), file=sys.stderr); sys.exit(1)
    v = r.get("value")
    print(json.dumps(v) if v is not None else "")
PY
}
state() { ctl state; }
has() { state | jq -e "$1" >/dev/null; }
wait_state() { wait_for "${2:-30}" has "$1"; }

shot() {
  [[ -z "$shots" ]] && return 0
  for theme in light dark; do
    ctl "theme $theme" >/dev/null; sleep 0.4
    ctl "screenshot $shots/desktop-$1-$theme.png" >/dev/null
  done
  ctl "theme system" >/dev/null
  pass "screenshots desktop-$1-{light,dark}.png"
}

start_app() {
  DISPLAY=":$display" DBUS_SESSION_BUS_ADDRESS="$bus" PATH="$work/fakebin:$PATH" ASYNC_APP_AUTOMATION="$auto" ASYNC_APP_TRAY=0 \
    ASYNC_UPDATE_URL="$work/b/velopack" "$image" --config "$cfg" >>"$work/app.log" 2>&1 &
  pids+=($!)
  wait_for 60 test -S "$auto" && wait_for 30 ctl state || { cat "$work/app.log"; fail "the app started"; exit 1; }
}

rm -rf "$work"
mkdir -p "$work/run" "$work/fakebin"
chmod 700 "$work/run"
for tool in xdg-open notify-send; do
  printf '#!/bin/sh\nprintf "%%s\\n" "$*" >> "%s/%s.log"\n' "$work" "$tool" > "$work/fakebin/$tool"
  chmod +x "$work/fakebin/$tool"
done

section "build A ($a) and B ($b)"
scripts/package.sh linux-x64 --version "$a" --out "$work/a" --only desktop --skip-native-packages >"$work/build-a.log" 2>&1 \
  || { tail -30 "$work/build-a.log"; exit 1; }
scripts/package.sh linux-x64 --version "$b" --out "$work/b" --only desktop --skip-native-packages >"$work/build-b.log" 2>&1 \
  || { tail -30 "$work/build-b.log"; exit 1; }
cp "$work/a/velopack/ASync.Desktop-linux-x64.AppImage" "$image"
chmod +x "$image"
pass "A's AppImage ($(du -m "$image" | cut -f1) MB); B's feed: $(ls "$work/b/velopack" | tr '\n' ' ')"

display=$((90 + RANDOM % 9))
"$xvfb" ":$display" -screen 0 1280x900x24 -nolisten tcp >"$work/xvfb.log" 2>&1 &
pids+=($!)
bus=$(dbus-daemon --session --fork --print-address=1 --print-pid=1 | head -1)
pids+=("$(pgrep -n -x dbus-daemon)")
wait_for 10 test -e "/tmp/.X11-unix/X$display"
pass "Xvfb :$display, private D-Bus session"

section "A: install the agent from the AppImage"
start_app
has ".appVersion == \"$a\"" && pass "the app is $a" || fail "app version" "$(state)"
wait_state '.page == "ServiceSetupViewModel"' 30 && pass "no agent yet: the app offers to install it" || fail "service page" "$(state)"
ctl "click Install" >/dev/null
wait_state '.page == "EnrollViewModel"' 60 && pass "the agent runs and answers (enroll page)" || fail "install" "$(state); $(tail -20 "$work/app.log")"
grep -q "^ExecStart=$image --config $cfg service run" "$unit_file" \
  && pass "the user unit runs the AppImage file itself, not its mount" || fail "unit ExecStart" "$(grep ExecStart "$unit_file")"
wait_state ".agentVersion == \"$a\"" 20 && pass "the agent is $a" || fail "agent version" "$(state)"
pid_a=$(state | jq -r .agentPid)

section "A finds B"
wait_state ".update.state == \"Available\" and .update.available == \"$b\"" 60 \
  && pass "update found: $(state | jq -r .update.banner)" || fail "update found" "$(state | jq .update)"
has ".trayItems | index(\"Update to ASync $b…\")" && pass "tray: \"Update to ASync $b…\"" || fail "tray item" "$(state | jq .trayItems)"
ctl "type DeviceName laptop" "resize 760 560" >/dev/null; sleep 0.3
shot update-available
ctl "click SettingsButton" "resize 760 1130" >/dev/null; sleep 0.4
has '.settings' && pass "Settings show the update: $(state | jq -r .update.status)" || fail "settings"
shot update-settings
ctl "click CloseSettings" "resize 760 560" >/dev/null

section "install B"
before=$(sha256sum "$image" | cut -d' ' -f1)
ctl "click InstallUpdate" >/dev/null || true
# The old app exits; Velopack replaces the AppImage and starts it again.
wait_for 30 sh -c "! [ -S '$auto' ] || ! python3 -c 'import socket,sys; s=socket.socket(socket.AF_UNIX); s.connect(sys.argv[1])' '$auto'" || true
wait_for 120 has ".appVersion == \"$b\"" && pass "the app restarted as $b" || fail "app restarted as B" "$(tail -30 "$work/app.log")"
[[ "$(sha256sum "$image" | cut -d' ' -f1)" != "$before" ]] && pass "the AppImage file was replaced in place" || fail "AppImage replaced"
wait_state ".agentVersion == \"$b\"" 60 && pass "the agent runs $b too" || fail "agent version after update" "$(state)"
pid_b=$(state | jq -r .agentPid)
[[ "$pid_b" != "$pid_a" ]] && pass "… in a new process ($pid_a → $pid_b)" || fail "agent restarted"
systemctl --user is-active "$unit" >/dev/null && pass "$unit is active" || fail "unit active"
has ".message | test(\"updated to $b\")" && pass "message: $(state | jq -r .message)" || fail "after-update message" "$(state | jq .message)"
has '.update.state == "UpToDate" or .update.state == "Idle"' || wait_state '.update.state == "UpToDate"' 30 \
  && pass "B finds nothing newer" || fail "B up to date" "$(state | jq .update)"
ctl "type DeviceName laptop" "resize 760 560" >/dev/null; sleep 0.3
shot update-done

section "uninstall"
ctl quit >/dev/null 2>&1 || true
wait_for 10 sh -c "! pgrep -f -- '--config $cfg --after-update' >/dev/null" || true
"$image" --config "$cfg" uninstall >"$work/uninstall.log" 2>&1 && pass "ASync.AppImage uninstall: $(tr '\n' ' ' < "$work/uninstall.log")" \
  || fail "uninstall" "$(cat "$work/uninstall.log")"
[[ ! -e "$unit_file" ]] && ! systemctl --user is-active "$unit" >/dev/null 2>&1 && pass "the service is gone" || fail "service removed"
[[ -d "$cfg" ]] && pass "the config directory is kept" || fail "config kept"
"$image" --config "$cfg" uninstall --remove-data >"$work/uninstall2.log" 2>&1 && [[ ! -e "$cfg" ]] \
  && pass "uninstall --remove-data deletes it" || fail "remove data" "$(cat "$work/uninstall2.log")"

echo
if [[ $failures -eq 0 ]]; then echo "All update checks passed."; else echo "$failures check(s) failed."; exit 1; fi
