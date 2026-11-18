# Sourced by scripts/smoke-ios.sh (it needs that script's helpers, $udid, $device, $backend, $work, $bundle, $APP, the running
# app, the TwoWay folder "Phone docs" ($sync_id) shared with async-cli and the Backup folder "Picked" ($picked_id)): the Files
# app location (task 25, the File Provider extension). Checks the extension is embedded; the app as far as here ran as a build without the app group
# (ASYNC_NO_APP_GROUP=1: no domain, the store in the app, Settings' line); launched with it (ad hoc signing on the simulator: are
# the app group and the Keychain group honoured?) the store moves into the group and the device stays enrolled, the domain "ASync —
# <server host>" registered; then, as any app reaches a provider (the domain's user-visible URLs and NSFileCoordinator, which make
# the system call the extension): the root lists the folders; a TwoWay folder lists its files without .async; a 5 MB file
# fetched through the extension is byte-identical with async-cli's copy; a file written into the TwoWay folder reaches the server
# and the other device; a Mirror folder refuses writes (nothing reaches the server); a Backup folder lists its snapshot and a
# file of it reads back; a change made on the server signals the working set and the new content arrives; the extension's
# memory footprint (its own measure and the Mac's `footprint` of the process). Screenshots: the Files app at the ASync root, a
# folder, a snapshot, and the Settings line without the group.
# shellcheck shell=bash disable=SC2015,SC2154

section "the Files app location (File Provider, task 25)"
note() { printf '  \033[33m·\033[0m %s\n' "$1"; }
fps() { ctl fp-state; }
fp_has() { fps | jq -e "$1" >/dev/null; }
fp_ls_has() { ctl "fp-ls $1" | jq -e --arg n "$2" 'type == "array" and any(.name == $n)' >/dev/null; }
b64() { python3 -c 'import base64,sys;print(base64.b64encode(sys.argv[1].encode()).decode())' "$1"; }

appex="$APP/PlugIns/ASyncFileProvider.appex"
if [[ -d "$appex" ]]; then
  pass "the extension is embedded: PlugIns/ASyncFileProvider.appex ($(du -sh "$appex" | cut -f1); $(/usr/libexec/PlistBuddy -c 'Print :NSExtension:NSExtensionPointIdentifier' "$appex/Info.plist" 2>/dev/null))"
  codesign -d --entitlements - "$appex" >"$logs/fileprovider-entitlements.txt" 2>&1 || true
  codesign -d --entitlements - "$APP" >"$logs/app-entitlements.txt" 2>&1 || true
else
  fail "no PlugIns/ASyncFileProvider.appex in $APP" "$(ls "$APP/PlugIns" 2>&1)"
fi

# So far the app ran as a build without the app group (the unsigned .ipa): no domain, the store in the app's container, and
# Settings says why.
state_fp=$(fps)
printf '%s\n' "$state_fp" >"$logs/fp-state-0.json"
jq -e '(.shared | not) and .note == "The Files app location needs a signed build" and (.domains | length == 0) and (.store | test("/Data/Application/"))' \
  <<<"$state_fp" >/dev/null && pass "without the app group: no domain, the store stays in the app, Settings says \"$(jq -r .note <<<"$state_fp")\"" \
  || fail "the unsigned state" "$state_fp"
if [[ -n "$shots" ]]; then
  ctl settings >/dev/null
  sleep 2
  ctl "show FilesLocationNote" >/dev/null
  sleep 1
  shot files-settings-unsigned
  ctl back >/dev/null
fi
folders_before=$(state | jq '.folders | length')

# With the app group (as signed builds have it): the store moves, the device stays enrolled, the domain is registered.
no_app_group=""
launch 25
ctl "notifications off" >/dev/null
wait_state '.page == "DashboardViewModel" and .state == "Connected"' 60 && wait_state ".folders | length == $folders_before" 30 \
  && pass "launched with the app group: still enrolled, the same $folders_before folders" || fail "enrolled after the move" "$(state)"
state_fp=$(fps)
printf '%s\n' "$state_fp" >"$logs/fp-state-1.json"
if jq -e '.shared' <<<"$state_fp" >/dev/null; then
  pass "the app group is there (ad hoc signing on the simulator honours it): store in $(jq -r .store <<<"$state_fp" | sed "s|$HOME|~|")"
  jq -e '.migration == "Moved"' <<<"$state_fp" >/dev/null && [[ ! -e "$container/Library/Application Support/async/identity.json" ]] \
    && pass "task 21's store moved into the group's container (nothing left in the app's)" || fail "store migration" "$state_fp"
  files=$(ctl files)
  jq -e 'map(select(.name == "identity.json")) | length == 1' <<<"$files" >/dev/null && pass "the identity is in the group's store" || fail "identity in the group" "$files"
else
  fail "the simulator gave no app group (the domain cannot be registered)" "$state_fp"
fi

if wait_for 60 fp_has '.domains | any(test("ASync — 127.0.0.1"))'; then
  pass "the domain is registered: $(fps | jq -r '.domains[0]')"
else
  fail "no File Provider domain" "$(fps)"
fi

if wait_for 60 fp_has '.root | type == "string" and startswith("/")'; then
  pass "the domain's root has a user-visible URL ($(fps | jq -r .root | sed "s|$HOME|~|"))"
else
  fail "no user-visible URL for the root" "$(fps)"
fi

# The root: the account's folders (through the extension's enumeration).
if wait_for 90 fp_ls_has "" "Phone docs"; then
  root_listing=$(ctl "fp-ls ")
  pass "the root lists the folders: $(jq -r 'map(.name) | join(", ")' <<<"$root_listing")"
else
  fail "root enumeration" "$(ctl "fp-ls ") | $(ctl fp-log)"
fi

# A TwoWay folder: its files, no .async.
if wait_for 60 fp_ls_has "Phone docs" "from-cli.txt"; then
  docs_listing=$(ctl "fp-ls Phone docs")
  jq -e 'all(.name != ".async")' <<<"$docs_listing" >/dev/null && pass "Phone docs lists its files, .async hidden: $(jq -r 'map(.name) | join(", ")' <<<"$docs_listing")" \
    || fail ".async shown" "$docs_listing"
else
  fail "TwoWay enumeration" "$(ctl "fp-ls Phone docs") | $(ctl fp-log)"
fi

# Fetching: a 5 MB file put there by the other device, read through the extension, compared with async-cli's copy.
head -c 5242880 /dev/urandom >"$work/cli-docs/big.bin"
cli_sync "$sync_id"
ctl fp-signal >/dev/null
wait_for 60 fp_ls_has "Phone docs" "big.bin" || true
read_answer=$(ctl "fp-read Phone docs/big.bin")
read_path=${read_answer% *}
if [[ -f "$read_path" ]] && cmp -s "$read_path" "$work/cli-docs/big.bin"; then
  pass "a 5 MB file fetched through the extension is byte-identical with async-cli's ($read_answer)"
else
  fail "fetch big.bin" "$read_answer | $(ctl fp-log)"
fi
footprint_line=$(fps | jq -r .footprint)
note "the extension's own measure: $footprint_line"
fp_pid=$(pgrep -f 'ASyncFileProvider.appex/ASyncFileProvider' | head -1 || true)
if [[ -n "$fp_pid" ]]; then
  footprint "$fp_pid" >"$logs/fileprovider-footprint.txt" 2>&1 || vmmap --summary "$fp_pid" >"$logs/fileprovider-footprint.txt" 2>&1 || true
  pass "the extension's process ($fp_pid): $(grep -iE 'phys_footprint|Physical footprint|Footprint' "$logs/fileprovider-footprint.txt" | head -2 | tr -s ' ' | tr '\n' ' ')"
else
  note "the extension's process was not found on the Mac (ps: $(pgrep -fl FileProvider | head -3 | tr '\n' ' '))"
fi

# Writing: a file saved into the TwoWay folder through the provider reaches the server and the other device.
answer=$(ctl "fp-write Phone docs/from-files-app.txt $(b64 "saved in the Files app")")
if [[ "$answer" == written ]] && wait_for 60 server_has "$sync_id" from-files-app.txt; then
  cli_sync "$sync_id"
  [[ "$(cat "$work/cli-docs/from-files-app.txt" 2>/dev/null)" == "saved in the Files app" ]] \
    && pass "a file saved through the provider is on the server and on the other device" || fail "written file on the cli" "$(ls "$work/cli-docs")"
else
  fail "write through the provider" "$answer | $(ctl fp-log)"
fi

# A Mirror folder (async-cli uploads, the iPhone downloads): read-only in the Files app.
music_id=$(api POST /api/folders "{\"name\":\"Shared music\",\"mode\":\"Mirror\",\"encryption\":\"Transport\",\"kind\":\"Files\",\"storageBackendId\":$backend,\"rootPath\":\"music\",\"intervalMinutes\":60}" | jq -r .id)
mkdir -p "$work/cli-music" && echo "la la" >"$work/cli-music/song.txt"
api PUT "/api/folders/$music_id/devices" "{\"deviceId\":\"$cli_device\",\"localPath\":\"$work/cli-music\",\"direction\":\"Upload\"}" >/dev/null
api PUT "/api/folders/$music_id/devices" "{\"deviceId\":\"$device\",\"localPath\":\"~/Documents/Music\",\"direction\":\"Download\"}" >/dev/null
cli_sync "$music_id"
wait_state '.folders | any(.name == "Shared music")' 30 || fail "the Mirror folder appears" "$(state)"
if wait_for 90 fp_ls_has "Shared music" "song.txt"; then
  answer=$(ctl "fp-write Shared music/not-allowed.txt $(b64 "nope")")
  sleep 15
  if server_has "$music_id" not-allowed.txt >/dev/null 2>&1; then
    fail "a write into the Mirror folder reached the server" "$answer"
  else
    pass "the Mirror folder refuses writes (${answer:0:120}); nothing reached the server"
  fi
else
  fail "Mirror enumeration" "$(ctl "fp-ls Shared music") | $(ctl fp-log)"
fi

# A Backup folder: its snapshots, a file of one read back.
picked_listing=$(ctl "fp-ls Picked")
snapshot_dir=$(jq -r 'if type == "array" then (map(select(.dir)) | .[0].name // "") else "" end' <<<"$picked_listing" 2>/dev/null || true)
if [[ -n "$snapshot_dir" ]] && wait_for 60 fp_ls_has "Picked/$snapshot_dir" "picked.txt"; then
  answer=$(ctl "fp-read Picked/$snapshot_dir/picked.txt")
  [[ "$(cat "${answer% *}" 2>/dev/null)" == picked ]] \
    && pass "the Backup folder lists its snapshot \"$snapshot_dir\" and a file of it reads back" || fail "snapshot file" "$answer"
  answer=$(ctl "fp-write Picked/$snapshot_dir/new.txt $(b64 "nope")")
  note "writing into a snapshot: ${answer:0:120}"
else
  fail "Backup snapshots" "$picked_listing | $(ctl fp-log)"
fi

# Change signalling: a change on the server (another device) reaches the hub, the app signals the working set, the new content
# arrives through the extension.
generation_before=$(fps | jq -r '.generation // 0')
echo "changed on the computer" >"$work/cli-docs/from-cli.txt"
cli_sync "$sync_id"
changed_content() { local a; a=$(ctl "fp-read Phone docs/from-cli.txt"); [[ "$(cat "${a% *}" 2>/dev/null)" == "changed on the computer" ]]; }
if wait_for 90 changed_content; then
  pass "a change on the server was signalled (generation $generation_before → $(fps | jq -r .generation), $(fps | jq -r .signals) signals) and the new content arrived"
else
  fail "change signalling" "$(fps) | $(ctl fp-log)"
fi
printf '%s\n' "$(fps)" >"$logs/fp-state-2.json"
ctl fp-log >"$logs/fileprovider-log-tail.txt" || true

# Screenshots: the Files app at the ASync location, a folder, a snapshot (shareddocuments:// opens it at a path).
if [[ -n "$shots" ]]; then
  ctl "fp-open " >/dev/null; sleep 4; shot_system files-root
  ctl "fp-open Phone docs" >/dev/null; sleep 4; shot_system files-folder
  [[ -n "${snapshot_dir:-}" ]] && { ctl "fp-open Picked/$snapshot_dir" >/dev/null; sleep 4; shot_system files-snapshot; }
  in_front
fi
