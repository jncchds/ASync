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
#   Nothing on the phone needs its browser: the device-code flow shows the code to approve on a computer, and a new
#   end-to-end Backup folder is set up in the app (passphrase, recovery key shown once), backed up, and restored byte for
#   byte by async-cli with that passphrase.
#   Browsing from the web UI (task 17): through the API, a clear 409 without All files access; with it, the storage's
#   root listed, Android/ without data and obb, Android/data itself refused.
#   Background work (M4-2): the foreground-service notification during the large transfer; the battery prompt and the
#   system's exemption question; the folders' periodic WorkManager jobs; with the app in the background and its process
#   killed (am kill), a file changed by async-cli arrives through the folder's periodic job, which the system runs in a
#   process it starts (made due in 25 s: enqueued afresh with an initial delay); a Wi-Fi-only folder on mobile data (svc wifi disable:
#   the emulator's cellular network is metered) does not run, its job waits for UNMETERED, and runs once Wi-Fi is back;
#   UnifiedPush with the ntfy app as the distributor and an ntfy server container: the endpoint registered with the
#   server, and a change on another device wakes the killed app, which syncs it down; the local reminder.
#   Documents provider (M4-3), driven through the automation's `docs` commands (ContentResolver/DocumentsContract calls
#   inside the app, as the Files app makes them; files are read by the adb shell with a one-document grant, as another
#   app reads them; the Files
#   app itself for the screenshots): the adb shell refused without MANAGE_DOCUMENTS; the root and the folders; a TwoWay
#   folder listed without .async; a file uploaded by async-cli read byte for byte from the cache; a 200 MB file streamed
#   through a proxy descriptor, not cached; a file created and written through the provider seen by async-cli and in the
#   phone's own synced copy, replaced (a version kept, no conflict copy), renamed, copied into a new directory, deleted
#   into the trash; search, recents, a thumbnail; a Backup folder's snapshots and tree, a file from it byte for byte
#   (cached, then streamed), a write into it refused.
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
#   SKIP_BACKGROUND=1 …                                     # leave out the background-work sections (quicker)
# The checks read `condition && pass … || fail …`: pass never fails, so fail runs only when the condition does.
#   NTFY_APK=path/to/ntfy.apk …   # the ntfy app (F-Droid io.heckel.ntfy) for the UnifiedPush checks; skipped without it
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

ntfy_port="${ASYNC_SMOKE_NTFY_PORT:-8093}"
# An ntfy server for the UnifiedPush checks, at a fixed address on the stack's network: the server container posts to it
# directly, and the emulator reaches it too (its user-mode network connects through the host, which routes to the bridge).
# Not 10.0.2.2 (the emulator's name for the host means nothing to a container) nor host-gateway (the host's firewall may
# drop a container's traffic to the host's own ports).
compose_override="$work/compose.override.yml"
compose() { docker compose -p "$project" -f docker-compose.yml -f "$compose_override" "$@"; }
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
  # The admin's access token lasts 15 minutes and the run is longer: sign in again every 10.
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
# contains TEXT COMMAND…: the command's output contains TEXT (not `cmd | grep -q`: with pipefail, grep leaving early kills
# the writer with SIGPIPE and the pipeline fails although the text was there).
contains() { local text="$1" out; shift; out=$("$@" 2>/dev/null | tr -d '\r') || true; [[ "$out" == *"$text"* ]]; }
logged() { contains "$1" adb logcat -d -s ASync; }
# Browsing the phone's folders through the API (task 17): the HTTP status, the answer in $work/browse.json.
browse_status() { api GET /api/devices >/dev/null; curl -s -o "$work/browse.json" -w "%{http_code}" -H "Authorization: Bearer $token" "$server/api/devices/$device/browse$1"; }
browse_state() { api GET /api/devices | jq -r --arg d "$device" '.[] | select(.id == $d) | .browse'; }
# settle FILE…: waits until the files' modification times stop changing. Android updates a new file's time a few seconds
# after it was closed (the media scanner); a sync in between uploads it a second time (same content, one more version).
settle() {
  local before now
  for _ in $(seq 1 20); do
    before=$(adb shell stat -c %Y "$@" | tr -d '\r'); sleep 4; now=$(adb shell stat -c %Y "$@" | tr -d '\r')
    [[ "$before" == "$now" ]] && break
  done
  # The app leaves files modified in the last 10 seconds for its next run (its settle time, AGENTS.md "Android background
  # work"): a run asked for at once would sync everything but them.
  sleep 11
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
cat >"$compose_override" <<YAML
services:
  ntfy:
    image: binwiederhier/ntfy:v2.11.0
    command: serve
    environment:
      - NTFY_BASE_URL=http://172.31.250.10:$ntfy_port
      - NTFY_LISTEN_HTTP=:$ntfy_port
    networks:
      default:
        ipv4_address: 172.31.250.10
networks:
  default:
    ipam:
      config:
        - subnet: 172.31.250.0/24
YAML
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
# cli_sync: async-cli syncs Phone docs; again while the phone holds the folder's lease (it syncs by itself too).
cli_sync() {
  local out
  for _ in 1 2 3 4 5 6; do
    out=$(cli sync --folder "$docs" 2>&1) || true
    [[ "$out" != *"Another device is syncing"* ]] && return 0
    sleep 5
  done
  echo "$out" >&2
}
pass "async-cli published"

compose down -v --remove-orphans >/dev/null 2>&1 || true
if [[ -n "${NO_BUILD:-}" ]]; then
  [[ -n "${IMAGE:-}" ]] && docker tag "$IMAGE" "$project-async-server:latest"
  compose up -d --no-build --wait async-server postgres ntfy >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
else
  compose up -d --build --wait async-server postgres ntfy >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
fi
wait_for 120 curl -fsS "$server/api/auth/setup" || { echo "the server did not come up" >&2; exit 1; }
token=$(api POST /api/auth/register '{"username":"admin","password":"smoke-password-1"}' | jq -r .accessToken)
token_at=$SECONDS
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
# A phone never needs its own browser: it shows the code to approve on a computer (the web UI there).
sleep 6
top_is "$pkg/.MainActivity" && ! contains "dat=http://10.0.2.2:$port/" adb logcat -d \
  && pass "the phone's browser did not open: the code is approved on a computer" || fail "browser opened on the phone" "$(adb shell dumpsys activity activities | grep -m1 ResumedActivity)"
has '.approveHint | test("On your computer, open http://10.0.2.2:[0-9]+/devices/approve")' \
  && pass "it says where: $(state | jq -r .approveHint)" || fail "approve hint" "$(state)"
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
# Once more when the answer came too late for the broadcast's time limit (a slow emulator).
selftest=$(ctl "selftest $docs" || ctl "selftest $docs")
[[ "$selftest" =~ ^([0-9]+)/([0-9]+)\ passed\ \(SafTree\)$ && "${BASH_REMATCH[1]}" == "${BASH_REMATCH[2]}" ]] \
  && pass "local tree contract on the SAF tree: $selftest" || fail "SAF tree contract" "$selftest"

# A second device: async-cli with its own copy of the folder.
code2=$(api POST /api/devices/enrollments '{"reusable":false,"label":"cli"}' | jq -r .code)
cli enroll --server "$server" --code "$code2" --name android-smoke-cli >/dev/null
cli_device=$(jq -r .deviceId "$work/cli-config/identity.json")
mkdir -p "$work/cli-docs"
echo "from the computer" > "$work/cli-docs/from-cli.txt"
api PUT "/api/folders/$docs/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-docs\"}" >/dev/null
cli_sync
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
cli_sync
[[ "$(cat "$work/cli-docs/hello-phone.txt" 2>/dev/null)" == "from the phone" ]] && pass "and on the other device" || fail "cli download"

# An edit on the computer, a delete on the phone, a large file from the phone with live progress.
echo "edited on the computer" > "$work/cli-docs/from-cli.txt"
cli_sync
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
# The transfer keeper: a dataSync foreground service with a progress notification and Cancel, while the run goes on.
transfer_notice() { contains "channel=async-transfers" adb shell dumpsys notification --noredact; }
wait_for 30 transfer_notice && pass "the foreground-service notification (channel async-transfers) shows during the transfer" \
  || fail "transfer notification" "$(adb shell dumpsys notification --noredact | grep -A3 "pkg=$pkg" | head -20)"
logged "Transfers: running as a foreground service." && pass "WorkManager's foreground service (type dataSync) holds the transfer" \
  || fail "foreground service" "$(adb logcat -d -s ASync | grep -i transfers | tail -5)"
if [[ -n "$shots" ]]; then
  adb shell cmd statusbar expand-notifications; sleep 2
  shot_system transfer-notification
  adb shell cmd statusbar collapse
fi
big=$(wait_run "$ticket" 300)
jq -e '.ok' <<<"$big" >/dev/null && pass "run finished: $(jq -r .message <<<"$big")" || fail "big run" "$big"
[[ "$(on_phone /sdcard/ASyncSmoke/docs/from-cli.txt)" == "edited on the computer" ]] && pass "the edit reached the phone" || fail "edit down"
server_has "$docs" video.bin >/dev/null && pass "the 200 MB file went up in parts" || fail "large upload"
server_has "$docs" hello-phone.txt >/dev/null && fail "the delete did not reach the server" || pass "the delete on the phone reached the server"
phone_sum=$(adb shell sha256sum /sdcard/ASyncSmoke/docs/video.bin | cut -d' ' -f1 | tr -d '\r')
cli_sync
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

section "an end-to-end Backup folder set up on the phone"
# Created and attached in the web UI on a computer; set up (passphrase, recovery key) on the phone, in the app.
secret=$(api POST /api/folders "{\"name\":\"Phone secrets\",\"mode\":\"Backup\",\"encryption\":\"EndToEnd\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"secrets\",\"intervalMinutes\":60}" | jq -r .id)
photos_tree="content://com.android.externalstorage.documents/tree/primary%3AASyncSmoke%2Fphotos"
api PUT "/api/folders/$secret/devices" "{\"deviceId\":\"$device\",\"localPath\":\"$photos_tree\"}" >/dev/null
wait_state ".folders[] | select(.name == \"Phone secrets\") | .needsSetup" 30 && pass "the new end-to-end folder shows \"Not set up\" on the phone" || fail "needs setup" "$(state)"
ctl "setup $secret" >/dev/null
sleep 1
shot e2e-setup
recovery=$(ctl "setup-passphrase correct horse battery staple")
[[ "$recovery" =~ ^[A-Z0-9]{4}(-[A-Z0-9]{4}){3,}$ ]] && pass "set up in the app; the recovery key is shown once" || fail "setup" "$recovery"
shot e2e-recovery-key
ctl setup-done >/dev/null
wait_state '.dialog | not' 10 || fail "dialog closed" "$(state)"
wait_for 30 bash -c "curl -fsS -H 'Authorization: Bearer $token' $server/api/folders/$secret | jq -e .repositoryReady" \
  && pass "the server knows the repository is ready" || fail "repository ready" "$(api GET "/api/folders/$secret" | jq -c '{repositoryReady}')"
wait_state ".folders[] | select(.name == \"Phone secrets\") | (.needsSetup | not) and (.locked | not)" 30 && pass "and the folder is unlocked on the phone" || fail "unlocked" "$(state)"
f=$(sync_phone "$secret" 180)
jq -e '.ok and (.message | startswith("Snapshot"))' <<<"$f" >/dev/null && pass "backed up end-to-end encrypted: $(jq -r .message <<<"$f")" || fail "e2e backup" "$f"
api PUT "/api/folders/$secret/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-secrets-unused\"}" >/dev/null
printf 'correct horse battery staple' > "$work/passphrase"
cli unlock --folder "$secret" --passphrase-file "$work/passphrase" >/dev/null 2>&1 && pass "async-cli unlocks it with the passphrase chosen on the phone" || fail "cli unlock"
rm -rf "$work/restored-secrets" "$work/passphrase"
cli restore --folder "$secret" --snapshot latest --target "$work/restored-secrets" >/dev/null \
  && [[ "$(sha256sum "$work/restored-secrets/2026/clip.mp4" | cut -d' ' -f1)" == "$(adb shell sha256sum /sdcard/ASyncSmoke/photos/2026/clip.mp4 | cut -d' ' -f1 | tr -d '\r')" ]] \
  && pass "and restores the phone's files byte for byte" || fail "e2e restore" "$(ls -R "$work/restored-secrets" 2>&1 | head)"
api DELETE "/api/folders/$secret/devices/$device" >/dev/null || true # the next sections count the phone's folders

section "documents provider: ASync in the Files app"
# Driven through the automation's `docs` commands: ContentResolver/DocumentsContract calls inside the app (its own uid may
# call its provider), the same calls the Files app makes; the Files app itself for the screenshots.
docs_ctl() { ctl "docs $1"; }
# docs_wait COMMAND [seconds]: a slow command (a large read) in the background, polled until it answers.
docs_wait() {
  local t r
  t=$(docs_ctl "start $1")
  for _ in $(seq 1 $(( ${2:-180} * 2 ))); do
    r=$(docs_ctl "result $t")
    [[ "$r" != pending ]] && { printf '%s\n' "$r"; return 0; }
    sleep 0.5
  done
  echo timeout; return 1
}
denied=$(adb shell content query --uri "content://$pkg.documents/root" 2>&1 | tr -d '\r' || true)
[[ "$denied" == *"Permission Denial"* || "$denied" == *"SecurityException"* || "$denied" == *"requires android.permission.MANAGE_DOCUMENTS"* ]] \
  && pass "another caller without MANAGE_DOCUMENTS (the adb shell) is refused" || fail "provider permission" "$denied"
roots=$(docs_ctl roots)
jq -e --arg t "ASync — 10.0.2.2" 'length == 1 and .[0].title == $t and .[0].document_id == "root" and .[0].summary == "admin"' <<<"$roots" >/dev/null \
  && pass "one root: $(jq -r '.[0].title' <<<"$roots") ($(jq -r '.[0].summary' <<<"$roots"))" || fail "roots" "$roots"
top=$(docs_ctl "list root")
jq -e --arg d "f/$docs" --arg p "b/$photos" '(.entries | any(.document_id == $d and .mime_type == "vnd.android.document/directory"))
  and (.entries | any(.document_id == $p))' <<<"$top" >/dev/null \
  && pass "its folders: $(jq -r '[.entries[]._display_name] | join(", ")' <<<"$top")" || fail "root listing" "$top"
listing=$(docs_ctl "list f/$docs")
jq -e '(.entries | any(._display_name == "from-cli.txt" and .mime_type == "text/plain")) and (.entries | any(._display_name == "video.bin"))
  and (.entries | all(._display_name != ".async"))' <<<"$listing" >/dev/null \
  && pass "Phone docs lists $(jq -r '[.entries[]._display_name] | join(", ")' <<<"$listing") (not .async)" || fail "folder listing" "$listing"
enc() { python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"; }
doc_uri() { printf 'content://%s.documents/document/%s' "$pkg" "$(enc "$1")"; }
# shell_read ID: the document as another app reads it (here the adb shell, with a read grant for that one document, as
# DocumentsUI gives the app a person picked a file for): its sha256. Not read inside the app: a process cannot read the
# proxy descriptors it serves (AGENTS.md "Android documents provider", gotcha).
shell_read() { docs_ctl "grant $1" >/dev/null; adb exec-out content read --uri "$(doc_uri "$1")" | sha256sum | cut -d' ' -f1; }
ungranted=$(adb shell content read --uri "$(doc_uri "f/$docs/video.bin")" 2>&1 | tr -d '\r' | head -c 400 || true)
r=$(docs_ctl "open f/$docs/from-cli.txt")
sum=$(shell_read "f/$docs/from-cli.txt")
[[ "$sum" == "$(sha256sum "$work/cli-docs/from-cli.txt" | cut -d' ' -f1)" ]] && [[ "$(jq -r .fd <<<"$r")" == *"/cache/documents/files/"* ]] \
  && pass "a file uploaded by async-cli, read by another process with a grant for it, byte for byte (from the cache)" || fail "read small file" "$r $sum"
[[ "$ungranted" == *"Permission Denial"* || "$ungranted" == *SecurityException* ]] && pass "a document without a grant stays refused to it" || fail "ungranted read" "$ungranted"
# 200 MB is over the cache's 128 MB per file: a proxy descriptor (appfuse), read in ranges, nothing stored.
before=$(docs_ctl cache | jq -r .bytes)
r=$(docs_wait "open f/$docs/video.bin")
sum=$(shell_read "f/$docs/video.bin")
[[ "$sum" == "$(sha256sum "$work/cli-docs/video.bin" | cut -d' ' -f1)" ]] && [[ "$(jq -r .fd <<<"$r")" == *appfuse* ]] \
  && [[ "$(docs_ctl cache | jq -r .bytes)" == "$before" ]] \
  && pass "the 200 MB file streams through a proxy descriptor ($(jq -r .fd <<<"$r")), byte for byte, not cached" || fail "stream large file" "$r"
logged "Transfers: running" && pass "the transfer notification kept the network for it" || pass "(the transfer notification was not needed: $(adb logcat -d -s ASync | grep -c Transfers) transfer lines)"

new=$(docs_ctl "create f/$docs text/plain provider-note.txt")
[[ "$new" == "f/$docs/provider-note.txt" ]] && pass "created $new" || fail "create" "$new"
docs_ctl "write $new written in the Files app" >/dev/null
server_has "$docs" provider-note.txt >/dev/null && pass "the write is on the server at once" || fail "write to server"
cli_sync
[[ "$(cat "$work/cli-docs/provider-note.txt" 2>/dev/null)" == "written in the Files app" ]] && pass "async-cli syncs it down byte for byte" || fail "cli sees the write" "$(cat "$work/cli-docs/provider-note.txt" 2>&1)"
phone_note() { [[ "$(on_phone /sdcard/ASyncSmoke/docs/provider-note.txt)" == "${1:-written in the Files app}" ]]; }
wait_for 90 phone_note && pass "the phone's own synced copy (the SAF folder) got it through the run the write triggered" || fail "local copy" "$(on_phone /sdcard/ASyncSmoke/docs/provider-note.txt)"
docs_ctl "write $new replaced in the Files app" >/dev/null
history=$(api GET "/api/folders/$docs/files/history?path=provider-note.txt")
jq -e '.versions | length == 1' <<<"$history" >/dev/null && pass "replacing it keeps the old content as a version" || fail "version kept" "$history"
wait_for 90 phone_note "replaced in the Files app" && pass "and the phone's copy follows again" || fail "local copy replaced"
conflicts=$(adb shell ls /sdcard/ASyncSmoke/docs 2>/dev/null | tr -d '\r' | grep -c conflict || true)
[[ "$conflicts" == 0 ]] && pass "no conflict copies from the provider's own writes" || fail "conflict copies" "$(adb shell ls /sdcard/ASyncSmoke/docs)"
renamed=$(docs_ctl "rename $new provider-renamed.txt")
[[ "$renamed" == "f/$docs/provider-renamed.txt" ]] && server_has "$docs" provider-renamed.txt >/dev/null && pass "renamed to $renamed" || fail "rename" "$renamed $(adb logcat -d -s ASync | grep Documents | tail -5)"
dir=$(docs_ctl "mkdir f/$docs Scans")
docs_ctl "list f/$docs" | jq -e '.entries | any(._display_name == "Scans" and .mime_type == "vnd.android.document/directory")' >/dev/null \
  && pass "a new directory ($dir) is listed (kept on the phone until a file goes in)" || fail "mkdir" "$dir"
copied=$(docs_ctl "copy $renamed $dir")
[[ "$copied" == "$dir/provider-renamed.txt" ]] && server_has "$docs" Scans >/dev/null && pass "copied into it: $copied" || fail "copy" "$copied"
[[ "$(docs_ctl "delete $renamed")" == ok ]] && ! server_has "$docs" provider-renamed.txt >/dev/null \
  && api GET "/api/folders/$docs/files/trash" | jq -e '[.. | .path? // empty] | any(. == "provider-renamed.txt")' >/dev/null \
  && pass "deleted into the folder's trash (restorable in the web UI)" || fail "delete to trash" "$(api GET "/api/folders/$docs/files/trash")"
found=$(docs_ctl "search provider")
jq -e --arg c "$copied" 'any(.document_id == $c)' <<<"$found" >/dev/null && pass "search finds $(jq -r '[.[]._display_name] | join(", ")' <<<"$found")" || fail "search" "$found"
docs_ctl recent | jq -e --arg c "$copied" 'any(.document_id == $c)' >/dev/null && pass "recent documents list it" || fail "recents" "$(docs_ctl recent)"
adb shell screencap -p /sdcard/ASyncSmoke/docs/screen.png
settle /sdcard/ASyncSmoke/docs/screen.png
sync_phone "$docs" >/dev/null
thumb=$(docs_ctl "thumb f/$docs/screen.png")
[[ "$thumb" =~ ^[0-9]+x[0-9]+$ ]] && pass "a thumbnail of an image: $thumb" || fail "thumbnail" "$thumb"

snaps=$(docs_ctl "list b/$photos")
sid=$(jq -r '.entries[0].document_id' <<<"$snaps" | cut -d/ -f3)
jq -e '(.entries | length) >= 1 and (.entries[0]._display_name | test("^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2} · [0-9a-f]{8}$"))' <<<"$snaps" >/dev/null \
  && pass "the Backup folder shows its snapshots: $(jq -r '[.entries[]._display_name] | join(", ")' <<<"$snaps")" || fail "snapshots" "$snaps"
tree_listing=$(docs_ctl "list s/$photos/$sid")
jq -e '(.entries | any(._display_name == "2026")) and (.entries | any(._display_name == "cat.jpg")) and (.entries | all(((.flags / 2 | floor) % 2) == 0 and ((.flags / 4 | floor) % 2) == 0))' <<<"$tree_listing" >/dev/null \
  && pass "a snapshot's tree, read-only: $(jq -r '[.entries[]._display_name] | join(", ")' <<<"$tree_listing")" || fail "snapshot tree" "$tree_listing"
clip_sum=$(adb shell sha256sum /sdcard/ASyncSmoke/photos/2026/clip.mp4 | cut -d' ' -f1 | tr -d '\r')
r=$(docs_wait "open s/$photos/$sid/2026/clip.mp4")
sum=$(shell_read "s/$photos/$sid/2026/clip.mp4")
[[ "$sum" == "$clip_sum" && "$(jq -r .fd <<<"$r")" == *"/cache/documents/files/"* ]] && pass "a file from the snapshot opens byte for byte (cached)" || fail "snapshot read" "$r $sum"
docs_ctl "cache 1048576" >/dev/null # smaller than the 3 MB clip: now it streams
r=$(docs_wait "open s/$photos/$sid/2026/clip.mp4")
sum=$(adb exec-out content read --uri "$(doc_uri "s/$photos/$sid/2026/clip.mp4")" | sha256sum | cut -d' ' -f1)
[[ "$sum" == "$clip_sum" && "$(jq -r .fd <<<"$r")" == *appfuse* ]] && pass "and streamed (proxy descriptor, chunks located through the index) byte for byte" || fail "snapshot stream" "$r $sum"
docs_ctl "cache 134217728" >/dev/null
w=$(ctl "docs write s/$photos/$sid/cat.jpg nope" 2>&1)
[[ "$w" == error:* ]] && pass "writing into a snapshot is refused ($w)" || fail "snapshot write refused" "$w"

if [[ -n "$shots" ]]; then
  # The Files app (DocumentsUI) itself: the root, a folder, a snapshot, and a picker as another app opens it.
  # Opened at the root (VIEW of a root URI); folders and snapshots are tapped into, as a person would (DocumentsUI does
  # not open a directory document by VIEW).
  files_root() {
    adb shell am force-stop com.google.android.documentsui 2>/dev/null; adb shell am force-stop com.android.documentsui 2>/dev/null
    adb shell "am start -W -a android.intent.action.VIEW -d 'content://$pkg.documents/root/async' -t vnd.android.document/root" >/dev/null 2>&1
    sleep 4; dismiss_anr
  }
  for mode in light dark; do
    adb shell cmd uimode night "$([[ $mode == dark ]] && echo yes || echo no)" >/dev/null
    files_root
    adb exec-out screencap -p > "$shots/android-files-root-$mode.png"
    tap_text '^Phone docs$' 10 && sleep 3 && adb exec-out screencap -p > "$shots/android-files-folder-$mode.png"
    files_root
    tap_text '^Phone photos$' 10 && sleep 3 && tap_text '^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:]+ · [0-9a-f]{8}$' 10 && sleep 3 \
      && adb exec-out screencap -p > "$shots/android-files-snapshot-$mode.png"
  done
  adb shell cmd uimode night no >/dev/null
  for mode in light dark; do
    adb shell cmd uimode night "$([[ $mode == dark ]] && echo yes || echo no)" >/dev/null
    adb shell am force-stop com.google.android.documentsui 2>/dev/null; adb shell am force-stop com.android.documentsui 2>/dev/null
    adb shell "am start -W -a android.intent.action.OPEN_DOCUMENT -c android.intent.category.OPENABLE -t '*/*' --eu android.provider.extra.INITIAL_URI 'content://$pkg.documents/document/$(enc "f/$docs")'" >/dev/null 2>&1
    sleep 4; dismiss_anr
    adb exec-out screencap -p > "$shots/android-files-picker-$mode.png"
  done
  adb shell cmd uimode night no >/dev/null
  # Back to the app: the next section's system dialog must be started from the front.
  adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null
  sleep 2
fi

if [[ -z "${SKIP_BACKGROUND:-}" ]]; then
section "background work: WorkManager, battery, conditions"
stamp=$(date +%s) # file names of this run: a rerun on a kept phone must not find the last run's files
bgstate() { ctl bg; }
bg_has() { bgstate | jq -e "$1" >/dev/null; }
wait_bg() { wait_for "${2:-30}" bg_has "$1"; }
alive() { [[ -n "$(adb shell pidof "$pkg" | tr -d '\r')" ]]; }
# work_id TAG: the WorkManager id of the live work with that tag (folder-<id>, wake, transfers, reminder).
work_id() { bgstate | jq -r --arg t "$1" '[.works[] | select((.tags | split(",") | index($t)) and .state != "CANCELLED" and .state != "SUCCEEDED")][0].id // empty'; }
# job_of WORK-ID: "<namespace> <id>" of the JobScheduler job WorkManager made for that work (the app reads its own jobs'
# extras; dumpsys redacts them). Forcing such a job (cmd jobscheduler run -f) does not help: WorkManager skips periodic
# work that is not due yet; the automation command `bg-due` makes it due instead, and the system runs it.
job_of() { bgstate | jq -r --arg w "$1" '[.jobs[] | select(.work == $w)][0] | if . then "\(.namespace // "") \(.id)" else empty end'; }
# job_dump ID: the dumpsys block of one of the app's jobs.
job_dump() { adb shell dumpsys jobscheduler "$pkg" | tr -d '\r' | awk -v id="/$1:" '/^  JOB /{p = index($0, id) > 0} p'; }
# background_app: Home, then kill the process once the app is idle (`am kill` only kills a cached process: one that holds
# the transfer keeper's foreground service, or runs a job, survives it).
idle() { bg_has '(.keeperRunning | not) and all(.folders[]; .activity == "Idle")'; }
background_app() {
  adb shell input keyevent KEYCODE_HOME
  for _ in $(seq 1 12); do
    wait_for 60 idle || true
    sleep 3; adb shell am kill "$pkg"; sleep 2
    alive || return 0
  done
}
open_app() { adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null; wait_state '.page == "DashboardViewModel"' 60; }

wait_state '.batteryPrompt == true' 20 && pass "the dashboard explains why and offers the battery-optimisation exemption" || fail "battery prompt" "$(state)"
shot battery-prompt
ctl bg-battery >/dev/null
sleep 2; dismiss_anr
shot_system battery-dialog
tap_text '^allow$' 20 && wait_bg '.batteryUnrestricted' 20 \
  && pass "Allow opens the system's question; ASync is exempt now" || fail "battery exemption" "$(ui_dump | head -c 600)"
ctl refresh >/dev/null
wait_state '.batteryPrompt == false' 10 && pass "and the prompt is gone" || fail "prompt gone" "$(state)"
wait_bg ".works | any(.tags == \"folder-$docs\" and .state == \"ENQUEUED\") and any(.tags == \"folder-$photos\")" 30 \
  && pass "each folder has its periodic WorkManager job" || fail "periodic jobs" "$(bgstate)"
docs_work=$(work_id "folder-$docs")
docs_job=$(job_of "$docs_work")
[[ -n "$docs_job" ]] && pass "JobScheduler holds the job of Phone docs (work $docs_work, job $docs_job)" || fail "job in JobScheduler" "$(bgstate | jq -c .jobs)"

# Scheduled work with the app closed: a file changes elsewhere, the folder's periodic work becomes due in 25 s
# (enqueued afresh with an initial delay: WorkManager never runs periodic work early, not even a forced job), the app goes to the
# background and its process is killed; the system starts it for the job.
asleep_text="changed on the computer while the phone slept ($stamp)"
echo "$asleep_text" > "$work/cli-docs/while-asleep.txt"
cli_sync
due=$(ctl "bg-due $docs 25")
background_app
adb logcat -c
alive && fail "am kill left the process running" "$(adb shell pidof "$pkg")" || pass "the work is $due; the app is in the background and its process is gone (am kill)"
asleep_arrived() { [[ "$(on_phone /sdcard/ASyncSmoke/docs/while-asleep.txt)" == "$asleep_text" ]]; }
wait_for 180 asleep_arrived \
  && pass "while-asleep.txt synced down with the app closed" || fail "scheduled sync" "$(adb logcat -d | grep -E 'ASync|WM-' | tail -20)"
# What started the process (for information: the job's service, or a broadcast of WorkManager's own when the job was
# due before the kill took): the scheduled run below is the check.
started=$(adb logcat -d 2>/dev/null | tr -d '\r' | sed -n "s/.*Start proc [0-9]*:$pkg\/[^ ]* for \([a-z]*\) {\([^}]*\)}.*/\1 \2/p" | head -1) || true
echo "    (the app's process was started for: ${started:-nothing since the log was cleared})"
wait_for 60 logged 'Background run of "Phone docs" (scheduled)' && pass "the scheduled run: $(adb logcat -d -s ASync | grep -m1 -o 'Background run of "Phone docs" (scheduled).*')" \
  || fail "background log" "$(adb logcat -d -s ASync | tail -10)"
top_is "$pkg/.MainActivity" && fail "the app came to the front" || pass "without the app coming to the front"

section "background work: a Wi-Fi-only folder on mobile data"
open_app || fail "app opens" "$(state)"
api PUT "/api/folders/$docs/devices" "{\"deviceId\":\"$device\",\"localPath\":\"$tree\",\"conditions\":{\"unmeteredOnly\":true,\"chargingOnly\":false,\"batteryNotLow\":false}}" >/dev/null
wait_state ".folders[] | select(.name == \"Phone docs\") | .unmeteredOnly" 30 && pass "Wi-Fi only, set in the web UI, reaches the phone" || fail "conditions on the phone" "$(state)"
wait_bg ".works | any(.tags == \"folder-$docs\" and .state == \"ENQUEUED\")" 30 || true
docs_work=$(work_id "folder-$docs")
docs_job=$(job_of "$docs_work")
ctl "editconditions $docs" >/dev/null
shot folder-conditions
ctl "editconditions $docs" >/dev/null
adb shell svc data enable
adb shell svc wifi disable
wait_bg '.conditions.metered == true' 60 && pass "Wi-Fi off: the phone is on the emulator's (metered) mobile network" || fail "metered network" "$(bgstate | jq -c .conditions)"
unsatisfied() { job_dump "${docs_job#* }" | grep -m1 'Unsatisfied constraints'; }
unsatisfied_network() { [[ "$(unsatisfied)" == *CONNECTIVITY* ]]; }
wait_for 30 unsatisfied_network \
  && pass "the folder's job waits for an unmetered network ($(unsatisfied | xargs))" \
  || fail "job constraint" "$(job_dump "${docs_job#* }" | grep -iE 'constraint|Network type')"
echo "made while the phone was on mobile data" > "$work/cli-docs/on-mobile-data-$stamp.txt"
cli_sync
adb logcat -c
ctl "bg-wake changed" >/dev/null
wait_for 60 bash -c "adb logcat -d -s ASync | grep 'Background run of \"Phone docs\" (changed): Waiting for Wi-Fi'" \
  && pass "a wake-up on mobile data: the run waits (\"Waiting for Wi-Fi\")" || fail "gate on mobile data" "$(adb logcat -d -s ASync | tail -10)"
sleep 5
adb shell ls /sdcard/ASyncSmoke/docs/on-mobile-data-$stamp.txt >/dev/null 2>&1 && fail "the file came down over mobile data" || pass "nothing was downloaded over mobile data"
wait_state ".folders[] | select(.name == \"Phone docs\") | .waiting | strings | test(\"Wi-Fi\")" 20 && pass "the app shows why it waits" || fail "waiting shown" "$(state)"
shot folder-waiting
adb shell svc wifi enable
# The emulator's Wi-Fi does not always join its network again by itself.
sleep 5; adb shell cmd wifi connect-network AndroidWifi open >/dev/null 2>&1 || true
wait_bg '.conditions.metered == false' 90 && pass "Wi-Fi back" || fail "wifi back" "$(bgstate | jq -c .conditions)"
adb logcat -c
ctl "bg-due $docs 5" >/dev/null
wait_for 120 bash -c "adb shell cat /sdcard/ASyncSmoke/docs/on-mobile-data-$stamp.txt 2>/dev/null | grep -q mobile" \
  && pass "on Wi-Fi the file comes down" || fail "sync on wifi" "$(adb logcat -d -s ASync | tail -10)"
wait_for 60 logged 'Background run of "Phone docs" (scheduled)' && pass "through the folder's job, now that its constraint holds" \
  || fail "job on wifi" || fail "sync on wifi" "$(adb logcat -d -s ASync | tail -10)"
api PUT "/api/folders/$docs/devices" "{\"deviceId\":\"$device\",\"localPath\":\"$tree\",\"conditions\":{\"unmeteredOnly\":false,\"chargingOnly\":false,\"batteryNotLow\":false}}" >/dev/null

section "background work: UnifiedPush wake-ups (ntfy)"
if [[ -z "${NTFY_APK:-}" || ! -f "${NTFY_APK:-}" ]]; then
  echo "    (skipped: set NTFY_APK to the ntfy app's APK)"
else
  adb install -r "$NTFY_APK" >/dev/null && pass "the ntfy app is installed (the UnifiedPush distributor)" || fail "ntfy install"
  adb shell pm grant io.heckel.ntfy android.permission.POST_NOTIFICATIONS 2>/dev/null || true
  # ntfy's default server for UnifiedPush: the ntfy container, set in ntfy's settings as a person would.
  ntfy_server="http://172.31.250.10:$ntfy_port"
  adb shell monkey -p io.heckel.ntfy -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1; sleep 5; dismiss_anr
  tap_text '^(OK|Got it|Continue|Allow)$' 3 || true
  tap_text '^More options$' 10 && tap_text '^Settings$' 10 && tap_text '^Default server$' 15 && sleep 1 \
    && adb shell input text "$ntfy_server" && tap_text '^(OK|Save)$' 10 || true
  sleep 1
  contains "172.31.250.10" ui_dump && pass "ntfy's default server is $ntfy_server" || fail "ntfy default server" "$(ui_dump | head -c 1200)"
  open_app || true
  ctl refresh >/dev/null
  wait_bg '.push.distributors | index("io.heckel.ntfy")' 20 && pass "ASync finds ntfy as a distributor" || fail "distributor found" "$(bgstate | jq -c .push)"
  ctl "bg-push io.heckel.ntfy" >/dev/null
  wait_bg '.push.endpoint and .push.registered' 90 && pass "registered with ntfy; the endpoint went to the server" \
    || fail "push registration" "$(bgstate | jq -c .push) $(adb logcat -d | grep -iE 'unifiedpush|ntfy' | tail -8)"
  api GET /api/devices | jq -e --arg d "$device" '.[] | select(.id == $d) | .push.host == "172.31.250.10" and .push.enabled' >/dev/null \
    && pass "the Devices page shows UnifiedPush through the ntfy server (its host; the endpoint itself is not shown)" || fail "server push target" "$(api GET /api/devices | jq -c '.[].push')"
  ctl settings >/dev/null
  sleep 1
  adb shell input swipe 540 1700 540 600 400; sleep 1
  shot settings-background
  ctl back >/dev/null
  # The real thing: the app closed, another device changes the folder, the server wakes the phone through ntfy.
  background_app
  alive && fail "am kill left the process running" || pass "the app is closed again (process killed)"
  adb logcat -c
  echo "pushed awake" > "$work/cli-docs/pushed-$stamp.txt"
  cli_sync
  wait_for 120 bash -c "adb shell cat /sdcard/ASyncSmoke/docs/pushed-$stamp.txt 2>/dev/null | grep -q awake" \
    && pass "the change reached the closed app through a UnifiedPush wake-up" || fail "push wake" "$(adb logcat -d | grep -E 'ASync|ntfy' | tail -20)"
  wait_for 60 logged "woken by the server (changed)" && pass "logged: woken by the server (changed)" || fail "wake log" "$(adb logcat -d -s ASync | tail -10)"
  api GET /api/devices | jq -e --arg d "$device" '.[] | select(.id == $d) | .push.lastWakeOk == true' >/dev/null \
    && pass "the server recorded the wake-up as delivered" || fail "server wake recorded" "$(api GET /api/devices | jq -c '.[].push')"
  # Browsing an asleep phone from the web UI (task 17): the server wakes it ("browse"), waits for it to connect, then asks.
  # All files access is still off here, so the woken phone's own answer is that refusal (not "did not wake up").
  background_app
  alive && fail "am kill left the process running" || pass "closed once more, to be browsed"
  adb logcat -c
  asleep() { [[ "$(browse_state)" == Wakeable ]]; }
  wait_for 40 asleep && pass "the Devices page says it is asleep and can be woken for browsing" || fail "wakeable" "$(browse_state)"
  t0=$SECONDS
  code=$(browse_status "")
  [[ "$code" == 409 ]] && grep -q "All files access" "$work/browse.json" \
    && pass "browsing it woke the closed app, which connected and answered in $((SECONDS - t0)) s ($(jq -r .message "$work/browse.json" | cut -c1-60)…)" \
    || fail "browse wake" "$code $(cat "$work/browse.json") $(adb logcat -d -s ASync | tail -10)"
  logged "woken by the server for browsing" && pass "logged: woken by the server for browsing (no folder run)" || fail "browse wake log" "$(adb logcat -d -s ASync | tail -10)"
  logged "Background run" && fail "a browse wake-up ran folders" "$(adb logcat -d -s ASync | grep 'Background run')" || pass "no folder ran for it"
  open_app || true
fi
ctl "bg-reminder 3" >/dev/null
r=$(ctl "bg-remind-now $docs")
[[ "$r" == "Phone docs: nothing synced for 3 days" ]] && pass "the local reminder notification: $r" || fail "reminder" "$r"
wait_bg '.works | any(.tags == "reminder" and .state == "ENQUEUED")' 20 && pass "reminders are scheduled (moved on after each successful run)" || fail "reminder scheduled" "$(bgstate | jq -c .works)"

fi # SKIP_BACKGROUND

section "All files access: folders anywhere on the phone"
running() { [[ -n "$(adb shell pidof "$pkg" | tr -d '\r')" ]]; }
front_app() { adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null; wait_state '.page == "DashboardViewModel"' 60; }
# The switch on Android's page sets the uid's mode, which wins over the package's: set (and read) both, as Settings does.
appop() { adb shell appops get --uid "$pkg" MANAGE_EXTERNAL_STORAGE | tr -d '\r' | sed -n 1p; }
set_appop() { adb shell appops set --uid "$pkg" MANAGE_EXTERNAL_STORAGE "$1"; adb shell appops set "$pkg" MANAGE_EXTERNAL_STORAGE "$1"; }
# A test volume: the emulator's virtual SD card (a public volume of its own, mounted at /storage/<id>).
sd=""
if [[ -z "$(adb shell sm list-volumes public | tr -d '\r' | awk '$2 == "mounted"')" ]]; then
  adb shell sm set-virtual-disk true >/dev/null 2>&1 || true
  disk_found() { [[ "$(adb shell sm list-disks | tr -d '\r')" == disk:* ]]; }
  wait_for 30 disk_found && adb shell sm partition "$(adb shell sm list-disks | tr -d '\r' | head -1)" public >/dev/null 2>&1 || true
fi
sd_mounted() { [[ -n "$(adb shell sm list-volumes public | tr -d '\r' | awk '$2 == "mounted" {print $3}')" ]]; }
wait_for 60 sd_mounted && sd="/storage/$(adb shell sm list-volumes public | tr -d '\r' | awk '$2 == "mounted" {print $3; exit}')"
[[ -n "$sd" ]] && pass "a test volume: the virtual SD card at $sd" || echo "    (no virtual SD card: the folder goes to Download)"
target="${sd:-/storage/emulated/0/Download}"
# The background sections leave the app in the background (or its process killed): bring it up, signed in.
# Back to "not granted" first: changing the permission makes Android stop the app's process (also an earlier run's grant,
# which the uid can keep across a reinstall).
set_appop default
sleep 2
front_app
wait_state '.state == "Connected"' 90 || fail "app connected" "$(state)"
# Browsing the phone's folders from the web UI (task 17): without All files access, a clear 409.
no_access_reported() { [[ "$(browse_state)" == NoAllFilesAccess ]]; }
wait_for 20 no_access_reported && pass "the device list says the phone needs All files access to be browsed" || fail "browse state" "$(browse_state)"
code=$(browse_status "")
[[ "$code" == 409 ]] && grep -q "choose the folder in the app, or turn on All files access" "$work/browse.json" \
  && pass "browsing it from the API: 409 \"$(jq -r .message "$work/browse.json" | cut -c1-90)…\"" || fail "browse without access" "$code $(cat "$work/browse.json")"
adb shell "rm -rf '$target/.async' '$target/root-from-phone.txt' '$target/root-from-cli.txt'; echo 'at the root' > '$target/root-from-phone.txt'"
settle "$target/root-from-phone.txt"
rootf=$(api POST /api/folders "{\"name\":\"Phone root\",\"mode\":\"TwoWay\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"root\",\"intervalMinutes\":60}" | jq -r .id)
api PUT "/api/folders/$rootf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"choose on the phone\"}" >/dev/null
wait_state '.folders | any(.name == "Phone root")' 60 || fail "root folder appears" "$(state)"
ctl "storage refresh" >/dev/null
[[ "$(ctl "choose-local $rootf")" == explaining ]] && pass "without All files access, Choose folder… explains why first" || fail "explanation" "$(ctl picker)"
shot allfiles-explain
ctl picker-allow >/dev/null
settings_page() { adb shell dumpsys activity activities | grep -m1 ResumedActivity | grep -q "com.android.settings"; }
wait_for 20 settings_page && pass "Allow all files access opens the system's page for ASync" \
  || fail "system page" "$(adb shell dumpsys activity activities | grep -m1 ResumedActivity)"
sleep 2; dismiss_anr
shot_system allfiles-system
tap_text 'allow access to manage all files' 15 && sleep 2 || true
if contains allow appop; then pass "the person turns it on there ($(appop))"
else echo "    (the switch was not tapped: granted with appops)"; fi
set_appop allow
adb shell input keyevent KEYCODE_BACK
running || front_app
wait_for 10 top_is "$pkg/.MainActivity" || front_app
ctl "storage refresh" >/dev/null
wait_state '.allFiles == true and .folderDialog == "picking"' 20 \
  && pass "back in the app with the permission: the in-app picker replaces the explanation" || fail "picker after grant" "$(state) $(ctl picker)"
picker=$(ctl picker)
jq -e '.path == "/storage/emulated/0" and .breadcrumb == "Internal storage" and (.entries | index("Download")) and (.entries | index("Android")) and (.canGoUp | not)' <<<"$picker" >/dev/null \
  && pass "it opens at the root of the internal storage, Download offered ($(jq -c .entries <<<"$picker"))" || fail "picker root" "$picker"
inside=$(ctl "picker-open Android")
jq -e '(.entries | index("data") | not) and (.entries | index("obb") | not)' <<<"$inside" >/dev/null \
  && pass "Android/data and Android/obb are not offered ($(jq -c .entries <<<"$inside"))" || fail "android data offered" "$inside"
ctl picker-up >/dev/null
shot allfiles-picker
# With the access: the web UI can browse the storage's root, Android/data and obb left out.
available_reported() { [[ "$(browse_state)" == Available ]]; }
wait_for 20 available_reported && pass "with the access the device list says the phone can be browsed" || fail "browse state after grant" "$(browse_state)"
code=$(browse_status "")
jq -e '.path == "/storage/emulated/0" and .parent == null and .crumbs[0].label == "Internal storage"
       and ([.entries[].name] | index("Download")) and ([.places[].label] | index("Internal storage"))' "$work/browse.json" >/dev/null && [[ "$code" == 200 ]] \
  && pass "browsing it from the API lists the storage's root ($(jq -c '[.entries[] | select(.kind == "Directory") | .name] | .[0:6]' "$work/browse.json"))" \
  || fail "browse root" "$code $(head -c 600 "$work/browse.json")"
code=$(browse_status "?path=/storage/emulated/0/Android")
[[ "$code" == 200 ]] && jq -e '[.entries[].name] | (index("data") == null and index("obb") == null)' "$work/browse.json" >/dev/null \
  && pass "and Android/ without data and obb ($(jq -c '[.entries[].name]' "$work/browse.json"))" || fail "browse android" "$code $(cat "$work/browse.json")"
code=$(browse_status "?path=/storage/emulated/0/Android/data")
[[ "$code" == 422 ]] && pass "Android/data itself is refused (422: out of reach)" || fail "browse android/data" "$code $(cat "$work/browse.json")"
storage=$(ctl storage)
if [[ -n "$sd" ]]; then
  jq -e --arg p "$sd" '.volumes | length == 2 and .[0].label == "Internal storage" and .[1].path == $p' <<<"$storage" >/dev/null \
    && pass "the volumes: $(jq -r '[.volumes[] | "\(.label) (\(.path))"] | join(", ")' <<<"$storage")" || fail "volumes" "$storage"
  picked=$(ctl "picker-volume 1")
  [[ "$(jq -r .path <<<"$picked")" == "$sd" ]] && pass "the SD card's root can be chosen: $(jq -r .breadcrumb <<<"$picked")" || fail "sd root" "$picked"
else
  ctl "picker-open Download" >/dev/null
fi
answer=$(ctl picker-use)
wait_state ".folders[] | select(.name == \"Phone root\") | .path == \"$target\"" 30 && pass "Phone root syncs at $target ($answer)" || fail "root path" "$answer $(state)"
api GET "/api/folders/$rootf" | jq -e --arg p "$target" '.devices[] | select(.localPath == $p)' >/dev/null \
  && pass "the server has the real path as the local path (the web UI shows $target)" || fail "server real path" "$(api GET "/api/folders/$rootf")"
selftest=$(ctl "selftest $rootf" || ctl "selftest $rootf")
[[ "$selftest" =~ ^([0-9]+)/([0-9]+)\ passed\ \(GuardedTree\)$ && "${BASH_REMATCH[1]}" == "${BASH_REMATCH[2]}" ]] \
  && pass "local tree contract on the real path: $selftest" || fail "real path tree contract" "$selftest"

# Both ways with async-cli.
mkdir -p "$work/cli-root"
api PUT "/api/folders/$rootf/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-root\"}" >/dev/null
r=$(sync_phone "$rootf")
jq -e '.ok' <<<"$r" >/dev/null && pass "the phone syncs the volume's root: $(jq -r .message <<<"$r")" || fail "root sync" "$r"
server_has "$rootf" root-from-phone.txt >/dev/null && pass "up: root-from-phone.txt is on the server" || fail "root upload"
android_clean() {
  [[ "$(api GET "/api/folders/$rootf/files" | jq -r '[.entries[].name] | index("Android")')" == null ]] && return 0
  api GET "/api/folders/$rootf/files?path=Android" | jq -e '[.entries[].name] | (index("data") == null and index("obb") == null)' >/dev/null
}
android_clean && pass "nothing of Android/data or Android/obb went up" || fail "android data synced" "$(api GET "/api/folders/$rootf/files?path=Android")"
cli_root_sync() { local out; for _ in 1 2 3 4 5 6; do out=$(cli sync --folder "$rootf" 2>&1) || true; [[ "$out" != *"Another device is syncing"* ]] && return 0; sleep 5; done; echo "$out" >&2; }
cli_root_sync
[[ "$(cat "$work/cli-root/root-from-phone.txt" 2>/dev/null)" == "at the root" ]] && pass "and on the other device" || fail "cli got root file"
echo "from the computer, to the root" > "$work/cli-root/root-from-cli.txt"
cli_root_sync
r=$(sync_phone "$rootf")
[[ "$(on_phone "$target/root-from-cli.txt")" == "from the computer, to the root" ]] && pass "down: root-from-cli.txt arrived at $target ($(jq -r .message <<<"$r"))" || fail "root download" "$r"
r=$(sync_phone "$rootf" | jq -r .message)
[[ "$r" == "0 up, 0 down, 0 deleted"* ]] && pass "a second run changes nothing" || fail "root idempotent" "$r"

# The SAF folder is offered its real path and keeps its state when switched.
ctl "storage refresh" >/dev/null
real_docs=/storage/emulated/0/ASyncSmoke/docs
wait_state ".folders[] | select(.name == \"Phone docs\") | .realPathOffer == \"$real_docs\"" 20 \
  && pass "Phone docs (a document tree) is offered its real path $real_docs" || fail "switch offer" "$(folder_json "Phone docs")"
before=$(api GET "/api/folders/$docs/files" | jq -c '[.entries[] | {name, size}] | sort_by(.name)')
switched=$(ctl "switch-real $docs")
wait_state ".folders[] | select(.name == \"Phone docs\") | .path == \"$real_docs\"" 30 && pass "switched: $switched" || fail "switch" "$switched $(state)"
r=$(sync_phone "$docs" 300)
msg=$(jq -r .message <<<"$r")
[[ "$msg" == "0 up, 0 down, 0 deleted"* && "$msg" != *conflict* ]] && pass "its sync state was kept: $msg (no upload again, no conflicts)" || fail "state kept" "$r"
[[ "$(api GET "/api/folders/$docs/files" | jq -c '[.entries[] | {name, size}] | sort_by(.name)')" == "$before" ]] \
  && pass "the server's files are as before" || fail "server files changed" "$(api GET "/api/folders/$docs/files")"
adb shell "ls /sdcard/ASyncSmoke/docs" | tr -d '\r' | grep -q conflict && fail "a conflict copy appeared" || pass "no conflict copies on the phone"

ctl settings >/dev/null
sleep 1
ctl "show StorageCard" >/dev/null
shot allfiles-settings
has '.settings' && pass "Settings shows All files access as allowed" || fail "settings" "$(state)"
ctl "show BrowsingPanel" >/dev/null
shot settings-browsing
ctl back >/dev/null

# Taken away: Android stops the app's process (as when the switch is turned off in Settings); started again, the folders
# at real paths stop safely (nothing deleted anywhere), background runs too.
pid_before=$(adb shell pidof "$pkg" | tr -d '\r')
set_appop deny
sleep 3
[[ "$(adb shell pidof "$pkg" | tr -d '\r')" != "$pid_before" ]] && pass "turned off: Android stops the app's process" || echo "    (the process survived the change)"
running || front_app
wait_state '.allFiles == false and (.folders | any(.name == "Phone root"))' 60 || fail "app after revoke" "$(state)"
r=$(sync_phone "$rootf")
[[ "$(jq -r .ok <<<"$r")" == false && "$(jq -r .message <<<"$r")" == *"All files access was turned off"* ]] \
  && pass "turned off: the folder stops with \"$(jq -r .message <<<"$r" | cut -c1-70)…\"" || fail "revoked run" "$r"
server_has "$rootf" root-from-phone.txt >/dev/null && server_has "$rootf" root-from-cli.txt >/dev/null \
  && pass "nothing was deleted on the server" || fail "deleted remotely" "$(api GET "/api/folders/$rootf/files")"
[[ "$(on_phone "$target/root-from-phone.txt")" == "at the root" ]] && pass "nor on the phone" || fail "deleted locally"
# In the background: Home, the process killed, then a wake-up starts it for WorkManager.
adb shell input keyevent KEYCODE_HOME; sleep 3; adb shell am kill "$pkg"; sleep 2
ctl "bg-wake test" >/dev/null
failed_bg() { ctl bg | jq -e --argjson id "$rootf" '.folders[] | select(.id == $id) | (.lastMessage // "") | test("All files access was turned off")' >/dev/null; }
wait_for 60 failed_bg && pass "a background run (a wake-up) stops the same way" || fail "background revoked" "$(ctl bg | jq -c .folders)"
running && pass "the app does not crash" || fail "app died"
front_app
set_appop allow
ctl "storage refresh" >/dev/null
r=$(sync_phone "$rootf")
jq -e '.ok' <<<"$r" >/dev/null && pass "turned on again: the folder syncs ($(jq -r .message <<<"$r"))" || fail "after re-grant" "$r"
api DELETE "/api/folders/$rootf/devices/$device" >/dev/null || true # the restart check counts the phone's folders

section "restart"
adb shell am force-stop "$pkg"
adb shell am start -W -n "$pkg/$pkg.MainActivity" >/dev/null
wait_state '.page == "DashboardViewModel" and .state == "Connected" and (.folders | length == 2)' 90 \
  && pass "after a restart: still enrolled, signed in again, both folders there" || fail "restart" "$(state 2>&1)"

section "result"
if [[ $failures -eq 0 ]]; then echo "All checks passed."; else echo "$failures check(s) failed."; exit 1; fi
