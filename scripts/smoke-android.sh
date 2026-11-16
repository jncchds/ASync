#!/usr/bin/env bash
# Smoke test of the Android app on an emulator (or a device adb sees) against a throw-away server stack (Docker Compose
# project async-android-smoke, port ASYNC_SMOKE_PORT, default 5193; the emulator reaches it at 10.0.2.2). The app is
# driven through its automation receiver (src/ASync.App.Android/Automation.cs: only the adb shell may send to it,
# through the DUMP permission); the system's folder picker (DocumentsUI) is driven with uiautomator like a person would: the
# test only says where the picker opens, then taps "Use this folder" and "Allow". It checks:
#   install; the device-code flow (code shown, cancelled); a pasted/opened async://pair link fills the form; the QR
#   scanner opens (camera permission granted by the test) and Back closes it; enrolling by code with a device key made in
#   the Android Keystore (non-exportable, TEE/StrongBox) whose P1363-converted signatures the server accepts; the store's
#   files sealed with the Keystore AES key (no user name readable); a TwoWay folder attached in the web UI appears; the
#   folder chosen through the real SAF picker (persisted permission, content URI on the server); the local tree contract
#   on that SAF tree (selftest); sync both ways with a second device (async-cli), including an edit, a delete and a large
#   file with live progress; pause/resume; the Back button closing settings; a Backup folder on a SAF tree backed up and
#   restored by async-cli, byte for byte; the app restarted (identity and folders survive, the device signs in again).
#
# Needs: an Android SDK (ANDROID_HOME or ~/Android/Sdk: platform-tools, emulator), an AVD (AVD=name, default async-smoke)
# or a running emulator/device, docker, jq, curl, python3, a .NET SDK with the android workload (DOTNET=…) unless APK=… is
# given, and the normal .NET SDK for async-cli.
#   scripts/smoke-android.sh
#   APK=artifacts/packages/android/ASync-0.1.4-android.apk CLI=path/to/async-cli scripts/smoke-android.sh   # built ones
#     (build first on a small machine: compiling next to a running emulator can starve it)
#   SCREENSHOTS=docs/screenshots scripts/smoke-android.sh   # also android-*-light.png / -dark.png of the main screens
#   NO_BUILD=1 IMAGE=async-desktop-smoke-async-server:latest …   # reuse a server image instead of building one
#   KEEP=1 …                                                # leave the stack, the emulator and artifacts/android-smoke
# The checks read `condition && pass … || fail …`: pass never fails, so fail runs only when the condition does.
# shellcheck disable=SC2015
set -euo pipefail
cd "$(dirname "$0")/.."

port="${ASYNC_SMOKE_PORT:-5193}"
project=async-android-smoke
server="http://localhost:$port"
phone_server="http://10.0.2.2:$port"
work="$PWD/artifacts/android-smoke"
pkg=ua.pp.chds.async
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
export PATH="$sdk/platform-tools:$sdk/emulator:$PATH"
avd="${AVD:-async-smoke}"
shots="${SCREENSHOTS:-}"
[[ -n "$shots" ]] && shots="$(cd "$shots" && pwd)"
export ASYNC_PORT="$port"

failures=0
pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✗\033[0m %s\n' "$1"; [[ -n "${2:-}" ]] && printf '%s\n' "$2" | sed 's/^/      /'; failures=$((failures + 1)); }
section() { printf '\n\033[1m── %s\033[0m\n' "$1"; }
need() { command -v "$1" >/dev/null || { echo "error: $1 not found" >&2; exit 2; }; }
for t in adb docker jq curl python3; do need "$t"; done

compose() { docker compose -p "$project" "$@"; }
started_emulator=0
cleanup() {
  local status=$?
  if [[ -z "${KEEP:-}" ]]; then
    compose down -v --remove-orphans >/dev/null 2>&1 || true
    [[ $started_emulator == 1 ]] && adb emu kill >/dev/null 2>&1 || true
  else
    echo "Kept the stack (docker compose -p $project down -v), the emulator and $work."
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
  for _ in $(seq 1 $((seconds * 2))); do "$@" >/dev/null 2>&1 && return 0; sleep 0.5; done
  return 1
}

# ctl COMMAND: one automation command (an ordered broadcast); prints the app's answer.
ctl() {
  local out
  out=$(adb shell "am broadcast -n $pkg/$pkg.Automation -a $pkg.AUTOMATION --es cmd '$1'" 2>&1 | tr -d '\r')
  [[ "$out" == *'data="'* ]] || { echo "error: no answer to \"$1\": $out" >&2; return 1; }
  out="${out#*data=\"}"
  printf '%s\n' "${out%\"}"
}
state() { ctl state; }
has() { state | jq -e "$1" >/dev/null; }
wait_state() { wait_for "${2:-30}" has "$1"; }
folder_json() { state | jq -c --arg n "$1" '.folders[] | select(.name == $n)'; }
server_has() { api GET "/api/folders/$1/files" | jq -e --arg n "$2" '.entries | any(.name == $n)'; }
on_phone() { adb shell "cat '$1'" 2>/dev/null | tr -d '\r'; }
# settle FILE…: waits until the files' modification times stop changing. Android updates a new file's time a few seconds
# after it was closed (the media scanner); a sync in between uploads it a second time (same content, one more version).
settle() {
  local before now
  for _ in $(seq 1 20); do
    before=$(adb shell stat -c %Y "$@" | tr -d '\r'); sleep 4; now=$(adb shell stat -c %Y "$@" | tr -d '\r')
    [[ "$before" == "$now" ]] && return 0
  done
}
top_is() { adb shell dumpsys activity activities | grep -m1 'ResumedActivity' | grep -q "$1"; } # the activity in front

# The screen as uiautomator sees it (the system's own screens: the picker, the permission dialog).
ui_dump() { adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 && adb shell cat /sdcard/ui.xml 2>/dev/null; }
# tap_text REGEX [seconds]: taps the first node whose text or description matches (case-insensitive).
tap_text() {
  local regex="$1" seconds="${2:-20}" xy
  for _ in $(seq 1 "$seconds"); do
    xy=$(ui_dump | python3 -c '
import re, sys, xml.etree.ElementTree as ET
rx = re.compile(sys.argv[1], re.I)
try: root = ET.fromstring(sys.stdin.read())
except ET.ParseError: sys.exit(1)
for n in root.iter("node"):
    if rx.search(n.get("text", "")) or rx.search(n.get("content-desc", "")):
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", n.get("bounds")))
        print((x1 + x2) // 2, (y1 + y2) // 2); break
' "$regex") && [[ -n "$xy" ]] && { read -r x y <<<"$xy"; adb shell input tap "$x" "$y"; return 0; }
    sleep 1
  done
  return 1
}
# The software-rendered emulator is slow enough for "System UI isn't responding": wait it out.
dismiss_anr() { ui_dump | grep -q "isn&apos;t responding\|isn't responding" && tap_text '^Wait$' 3 || true; }

# shot NAME: android-NAME-light.png and -dark.png (only with SCREENSHOTS): the app's theme and the system's night mode.
shot() {
  [[ -z "$shots" ]] && return 0
  local name="$1" mode
  for mode in light dark; do
    if [[ $mode == dark ]]; then adb shell cmd uimode night yes >/dev/null; ctl "theme dark" >/dev/null
    else adb shell cmd uimode night no >/dev/null; ctl "theme light" >/dev/null; fi
    sleep 1.5
    dismiss_anr
    adb exec-out screencap -p > "$shots/android-$name-$mode.png"
  done
  adb shell cmd uimode night no >/dev/null
  ctl "theme light" >/dev/null
}
shot_system() { # a system screen (no in-app theme): light and dark by night mode only
  [[ -z "$shots" ]] && return 0
  adb shell cmd uimode night no >/dev/null; sleep 1.5; dismiss_anr; adb exec-out screencap -p > "$shots/android-$1-light.png"
  adb shell cmd uimode night yes >/dev/null; sleep 1.5; dismiss_anr; adb exec-out screencap -p > "$shots/android-$1-dark.png"
  adb shell cmd uimode night no >/dev/null
}

mkdir -p "$work"
rm -rf "$work/cli-config" "$work/cli-docs" "$work/restored" # the second device starts afresh each run
section "build and start"
if [[ -z "${APK:-}" ]]; then
  scripts/package-android.sh --abis android-x64 --out "$work" >"$work/package.log" 2>&1 || { tail -30 "$work/package.log"; exit 1; }
  APK=$(ls "$work"/ASync-*-android.apk)
fi
pass "APK $(basename "$APK") ($(du -h "$APK" | cut -f1))"
if [[ -z "${CLI:-}" ]]; then
  dotnet publish src/ASync.Cli/ASync.Cli.csproj -c Release -o "$work/cli" --nologo -v quiet >"$work/cli.log" 2>&1 || { cat "$work/cli.log"; exit 1; }
  CLI="$work/cli/async-cli"
fi
cli() { "$CLI" --config "$work/cli-config" "$@"; }
pass "async-cli published"

compose down -v --remove-orphans >/dev/null 2>&1 || true
if [[ -n "${NO_BUILD:-}" ]]; then
  [[ -n "${IMAGE:-}" ]] && docker tag "$IMAGE" "$project-async-server:latest"
  compose up -d --no-build --wait async-server postgres >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
else
  compose up -d --build --wait async-server postgres >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
fi
wait_for 120 curl -fsS "$server/api/auth/setup" || { echo "the server did not come up" >&2; exit 1; }
token=$(api POST /api/auth/register '{"username":"admin","password":"smoke-password-1"}' | jq -r .accessToken)
backend=$(api POST /api/storage-backends '{"name":"smoke","settings":{"type":"Local","path":"/data/android-smoke"}}' | jq -r .id)
pass "server on $server (the phone uses $phone_server), account admin, storage $backend"

if ! adb get-state >/dev/null 2>&1; then
  emulator -avd "$avd" -no-window -no-audio -no-boot-anim -no-snapshot -memory 2048 -partition-size 2048 -gpu swiftshader_indirect -camera-back emulated \
    >"$work/emulator.log" 2>&1 &
  started_emulator=1
  adb wait-for-device
fi
booted() { [[ "$(adb shell getprop sys.boot_completed | tr -d '\r')" == 1 ]]; }
wait_for 300 booted || { echo "the emulator did not boot" >&2; exit 1; }
pass "device: $(adb shell getprop ro.product.model | tr -d '\r'), Android $(adb shell getprop ro.build.version.release | tr -d '\r')"
adb shell settings put global window_animation_scale 0; adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0

adb uninstall "$pkg" >/dev/null 2>&1 || true
adb install -r "$APK" >/dev/null
adb shell pm grant "$pkg" android.permission.CAMERA
adb shell pm grant "$pkg" android.permission.POST_NOTIFICATIONS 2>/dev/null || true
adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null
wait_for 60 has '.page == "EnrollViewModel"' && pass "installed; the app starts on the enroll page" || fail "start" "$(state 2>&1)"
dismiss_anr

section "enroll"
# A freshly installed app's network rules are set up asynchronously; the first connection can be cut ("Software caused
# connection abort"). Give it a moment, and one more try.
sleep 5
code_shown=$(ctl "devicecode $phone_server")
if [[ ! "$code_shown" =~ ^[A-Z0-9]{4}-[A-Z0-9]{4}$ ]]; then echo "    (first try: $code_shown)"; sleep 5; code_shown=$(ctl "devicecode $phone_server"); fi
[[ "$code_shown" =~ ^[A-Z0-9]{4}-[A-Z0-9]{4}$ ]] && pass "device-code flow: the code $code_shown is shown" || fail "device code" "$code_shown"
wait_for 20 bash -c "adb shell dumpsys activity activities | grep -m1 ResumedActivity | grep -qv '$pkg'" \
  && adb logcat -d | grep -q "dat=http://10.0.2.2:$port/" && pass "the approval page opened in the browser" || fail "approval page opened"
adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null # back to the app (as a person would)
wait_for 15 top_is "$pkg/.MainActivity" || true
shot enroll-device-code
ctl cancel >/dev/null
wait_state '.pendingCode == null' 20 && pass "Cancel gives the request up" || fail "cancel" "$(state)"
# A tapped async://pair link (the web UI's QR code opened by another app): Android starts this app with it.
adb shell input keyevent KEYCODE_HOME
adb shell "am start -W -a android.intent.action.VIEW -d 'async://pair?s=http%3A%2F%2F10.0.2.2%3A$port&c=ABCD-EFGH-JKMN'" >/dev/null
wait_state ".enrollServer == \"$phone_server\" and .enrollCode == \"ABCD-EFGH-JKMN\" and .useCode" 20 \
  && pass "an opened async://pair link brings the app up with the server and code filled in" || fail "pair link" "$(state)"
shot enroll
ctl scan >/dev/null
sleep 3
wait_for 15 top_is QrScanActivity && pass "the QR scanner opens (camera preview, ZXing)" || fail "scanner" "$(adb shell dumpsys activity activities | grep -m3 ResumedActivity)"
shot_system scanner
# A "System UI isn't responding" dialog of the slow emulator would take the Back press.
dismiss_anr
adb shell input keyevent KEYCODE_BACK
wait_for 10 top_is "$pkg/.MainActivity" || { dismiss_anr; adb shell input keyevent KEYCODE_BACK; }
wait_for 10 top_is "$pkg/.MainActivity" && has '.page == "EnrollViewModel"' && pass "Back closes the scanner" || fail "scanner back" "$(adb shell dumpsys activity activities | grep -m1 ResumedActivity)"

code=$(api POST /api/devices/enrollments '{"reusable":false,"label":"android smoke"}' | jq -r .code)
answer=$(ctl "enroll $phone_server $code")
wait_state '.page == "DashboardViewModel"' 60 && pass "enrolled by code ($answer)" || fail "enroll" "$answer $(state)"
device=$(api GET /api/devices | jq -r '.[] | select(.revokedAt == null and .platform == "Android") | .id' | head -1)
[[ -n "$device" ]] && pass "the server lists the device as Android ($device)" || fail "device on server" "$(api GET /api/devices)"
wait_state '.state == "Connected"' 60 && pass "signed in: the server accepted the Keystore key's signature" || fail "connected" "$(state)"
keys=$(ctl keyinfo)
jq -e '.algorithm == "EC" and .keySize == 256 and (.exportable | not)' <<<"$keys" >/dev/null && pass "device key: EC P-256 in the Android Keystore, not exportable ($(jq -r .securityLevel <<<"$keys"))" || fail "keystore key" "$keys"
jq -e '(.identityFile | startswith("androidks:identity:")) and (.identityFileHasUsername | not) and .keyReference == "androidkeystore"' <<<"$keys" >/dev/null \
  && pass "identity.json sealed with the Keystore AES key (only a key reference inside, no user name readable)" || fail "sealed store" "$keys"

section "a TwoWay folder on a SAF tree"
adb shell "rm -rf /sdcard/ASyncSmoke && mkdir -p /sdcard/ASyncSmoke/docs /sdcard/ASyncSmoke/photos && echo 'from the phone' > /sdcard/ASyncSmoke/docs/hello-phone.txt"
docs=$(api POST /api/folders "{\"name\":\"Phone docs\",\"mode\":\"TwoWay\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"docs\",\"intervalMinutes\":60}" | jq -r .id)
api PUT "/api/folders/$docs/devices" "{\"deviceId\":\"$device\",\"localPath\":\"choose on the phone\"}" >/dev/null
wait_state '.folders | any(.name == "Phone docs")' 30 && pass "the folder attached in the web UI appears" || fail "folder appears" "$(state)"
shot dashboard-new-folder
ctl "choose $docs content://com.android.externalstorage.documents/document/primary%3AASyncSmoke%2Fdocs" >/dev/null
sleep 3
dismiss_anr
shot_system picker
tap_text '^use this folder$' 30 && pass "the system picker opened at ASyncSmoke/docs: Use this folder" || fail "picker" "$(ui_dump | head -c 600)"
sleep 1
shot_system picker-allow
tap_text '^allow$' 20 && pass "Allow ASync to access files in docs" || fail "allow" "$(ui_dump | head -c 600)"
tree="content://com.android.externalstorage.documents/tree/primary%3AASyncSmoke%2Fdocs"
wait_state ".folders[] | select(.name == \"Phone docs\") | .path == \"$tree\"" 30 && pass "the agent syncs the tree $tree" || fail "set path" "$(state)"
api GET "/api/folders/$docs" | jq -e --arg p "$tree" '.devices[] | select(.localPath == $p)' >/dev/null && pass "the server has the content URI as the local path" || fail "server path"
adb shell dumpsys package "$pkg" 2>/dev/null | grep -q "ASyncSmoke" || adb shell cmd uri grants 2>/dev/null | grep -q ASyncSmoke || true
selftest=$(ctl "selftest $docs")
[[ "$selftest" =~ ^([0-9]+)/([0-9]+)\ passed\ \(SafTree\)$ && "${BASH_REMATCH[1]}" == "${BASH_REMATCH[2]}" ]] \
  && pass "local tree contract on the SAF tree: $selftest" || fail "SAF tree contract" "$selftest"

# A second device: async-cli with its own copy of the folder.
code2=$(api POST /api/devices/enrollments '{"reusable":false,"label":"cli"}' | jq -r .code)
cli enroll --server "$server" --code "$code2" --name android-smoke-cli >/dev/null
cli_device=$(jq -r .deviceId "$work/cli-config/identity.json")
mkdir -p "$work/cli-docs"
echo "from the computer" > "$work/cli-docs/from-cli.txt"
api PUT "/api/folders/$docs/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-docs\"}" >/dev/null
cli sync --folder "$docs" >/dev/null
server_has "$docs" from-cli.txt >/dev/null && pass "async-cli put from-cli.txt on the server" || fail "cli upload"

# The agent also syncs by itself (a new path, the server's change events), so a test waits for the very run it asked
# for: `sync` answers with a ticket, `run <ticket>` with that run's result ({ok, message, skipped}) once it finished.
# wait_run TICKET [seconds]: prints the result.
wait_run() {
  local r
  for _ in $(seq 1 $(( ${2:-120} * 2 ))); do
    r=$(ctl "run $1")
    [[ "$r" != pending ]] && { printf '%s\n' "$r"; return 0; }
    sleep 0.5
  done
  echo timeout; return 1
}
# sync_phone FOLDER-ID [seconds]: Sync now on the phone; prints the requested run's result (again when another device
# held the folder and the run was skipped).
sync_phone() {
  local r
  for _ in 1 2 3; do
    r=$(wait_run "$(ctl "sync $1")" "${2:-120}")
    jq -e '.skipped' <<<"$r" >/dev/null 2>&1 || break
    sleep 3
  done
  printf '%s\n' "$r"
}
last=$(sync_phone "$docs")
jq -e '.ok' <<<"$last" >/dev/null && pass "Sync now: $(jq -r .message <<<"$last")" || fail "phone sync" "$last"
[[ "$(on_phone /sdcard/ASyncSmoke/docs/from-cli.txt)" == "from the computer" ]] && pass "down: from-cli.txt is on the phone" || fail "download to SAF"
server_has "$docs" hello-phone.txt >/dev/null && pass "up: hello-phone.txt is on the server" || fail "upload from SAF"
cli sync --folder "$docs" >/dev/null
[[ "$(cat "$work/cli-docs/hello-phone.txt" 2>/dev/null)" == "from the phone" ]] && pass "and on the other device" || fail "cli download"

# An edit on the computer, a delete on the phone, a large file from the phone with live progress.
echo "edited on the computer" > "$work/cli-docs/from-cli.txt"
cli sync --folder "$docs" >/dev/null
adb shell rm /sdcard/ASyncSmoke/docs/hello-phone.txt
adb shell "dd if=/dev/urandom of=/sdcard/ASyncSmoke/docs/video.bin bs=1048576 count=200 2>/dev/null"
settle /sdcard/ASyncSmoke/docs/video.bin
ticket=$(ctl "sync $docs")
seen=""
for _ in $(seq 1 120); do
  f=$(folder_json "Phone docs")
  if [[ $(jq -r .activity <<<"$f") == Syncing ]]; then seen=1; shot dashboard-syncing; break; fi
  sleep 0.5
done
[[ -n "$seen" ]] && pass "the folder shows Syncing with live progress while it runs" || fail "syncing state"
big=$(wait_run "$ticket" 300)
jq -e '.ok' <<<"$big" >/dev/null && pass "run finished: $(jq -r .message <<<"$big")" || fail "big run" "$big"
[[ "$(on_phone /sdcard/ASyncSmoke/docs/from-cli.txt)" == "edited on the computer" ]] && pass "the edit reached the phone" || fail "edit down"
server_has "$docs" video.bin >/dev/null && pass "the 200 MB file went up in parts" || fail "large upload"
server_has "$docs" hello-phone.txt >/dev/null && fail "the delete did not reach the server" || pass "the delete on the phone reached the server"
phone_sum=$(adb shell sha256sum /sdcard/ASyncSmoke/docs/video.bin | cut -d' ' -f1 | tr -d '\r')
cli sync --folder "$docs" >/dev/null
[[ "$(sha256sum "$work/cli-docs/video.bin" | cut -d' ' -f1)" == "$phone_sum" ]] && pass "video.bin arrived on the computer byte for byte" || fail "large file content"
again=$(sync_phone "$docs" | jq -r .message)
[[ "$again" == "0 up, 0 down, 0 deleted"* ]] && pass "a second run changes nothing (SAF times are not set, nothing looks changed)" || fail "idempotent" "$again"

section "pause, resume, back"
ctl "pause $docs" >/dev/null
wait_state '.folders[] | select(.name == "Phone docs") | .paused' 20 && pass "Pause" || fail "pause" "$(state)"
api GET "/api/folders/$docs" | jq -e --arg d "$device" '.devices[] | select(.deviceId == $d) | .paused' >/dev/null && pass "the server shows it paused" || fail "server paused"
ctl "resume $docs" >/dev/null
wait_state '.folders[] | select(.name == "Phone docs") | .paused | not' 20 && pass "Resume" || fail "resume" "$(state)"
ctl settings >/dev/null
shot settings
adb shell input keyevent KEYCODE_BACK
wait_state '.settings | not' 10 && pass "the Back button closes the settings" || fail "back closes settings" "$(state)"
has '.page == "DashboardViewModel"' && pass "and the app stays open" || fail "app closed on back"

section "a Backup folder on a SAF tree, restored by async-cli"
adb shell "echo 'cat picture' > /sdcard/ASyncSmoke/photos/cat.jpg && mkdir -p /sdcard/ASyncSmoke/photos/2026 && dd if=/dev/urandom of=/sdcard/ASyncSmoke/photos/2026/clip.mp4 bs=1024 count=3000 2>/dev/null"
settle /sdcard/ASyncSmoke/photos/cat.jpg /sdcard/ASyncSmoke/photos/2026/clip.mp4
photos=$(api POST /api/folders "{\"name\":\"Phone photos\",\"mode\":\"Backup\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"photos\",\"intervalMinutes\":60,\"retention\":{\"last\":5}}" | jq -r .id)
api PUT "/api/folders/$photos/devices" "{\"deviceId\":\"$device\",\"localPath\":\"choose on the phone\"}" >/dev/null
wait_state '.folders | any(.name == "Phone photos")' 30 || fail "backup folder appears" "$(state)"
ctl "choose $photos content://com.android.externalstorage.documents/document/primary%3AASyncSmoke%2Fphotos" >/dev/null
sleep 3; dismiss_anr
tap_text '^use this folder$' 30 && tap_text '^allow$' 20 && pass "picked ASyncSmoke/photos" || fail "photos picker" "$(ui_dump | head -c 600)"
wait_state '.folders[] | select(.name == "Phone photos") | .path | startswith("content://")' 30 || fail "photos path" "$(state)"
f=$(sync_phone "$photos" 180)
jq -e '.ok and (.message | startswith("Snapshot"))' <<<"$f" >/dev/null && pass "backed up: $(jq -r .message <<<"$f")" || fail "backup" "$f"
api PUT "/api/folders/$photos/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-photos-unused\"}" >/dev/null
cli snapshots --folder "$photos" | grep -q "files" && pass "async-cli lists the phone's snapshot" || fail "cli snapshots"
rm -rf "$work/restored"
cli restore --folder "$photos" --snapshot latest --target "$work/restored" >/dev/null && pass "async-cli restored it" || fail "cli restore"
[[ "$(cat "$work/restored/cat.jpg")" == "cat picture" ]] \
  && [[ "$(sha256sum "$work/restored/2026/clip.mp4" | cut -d' ' -f1)" == "$(adb shell sha256sum /sdcard/ASyncSmoke/photos/2026/clip.mp4 | cut -d' ' -f1 | tr -d '\r')" ]] \
  && pass "the restored files match the phone's byte for byte" || fail "restore content" "$(ls -R "$work/restored" 2>&1)"
shot dashboard

section "restart"
adb shell am force-stop "$pkg"
adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null
wait_state '.page == "DashboardViewModel" and .state == "Connected" and (.folders | length == 2)' 90 \
  && pass "after a restart: still enrolled, signed in again, both folders there" || fail "restart" "$(state 2>&1)"

section "result"
if [[ $failures -eq 0 ]]; then echo "All checks passed."; else echo "$failures check(s) failed."; exit 1; fi
