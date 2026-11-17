# Sourced by scripts/smoke-android.sh (it needs that script's helpers, $device, $backend, $work and the running app): photo and
# video backups (task 19). Seeds the emulator's library with test images and a video in two albums (adb push + media scan), shows
# the permission's explanation, allows it in the system's dialog, makes a Photos folder in the web UI's API, picks one album from
# the phone's live album list, backs it up, checks the snapshot's permanent tree and byte-identical originals with async-cli
# (offline, from a copy of the repository, with the folder's recovery key) and the web snapshot browser, adds a photo and sees the
# media content trigger start a backup by itself, then restores into a wiped album and checks it again byte for byte.
# shellcheck shell=bash disable=SC2015,SC2154

section "photos and videos (task 19)"
declare -F bgstate >/dev/null || bgstate() { ctl bg; }          # defined by the background sections (SKIP_BACKGROUND leaves them out)
declare -F bg_has >/dev/null || bg_has() { bgstate | jq -e "$1" >/dev/null; }
media_dir="$work/media"
rm -rf "$media_dir"; mkdir -p "$media_dir"
# Real JPEGs (two with an EXIF location), a copy of one in the other album (Android keeps "a photo in two albums" as two files),
# and a video. PIL makes the JPEGs; the video is random bytes with an .mp4 name (MediaStore indexes it as a video by its type).
python3 - "$media_dir" <<'PY'
import os, sys
from PIL import Image
out = sys.argv[1]
def jpeg(name, colour, gps=None):
    img = Image.new("RGB", (64, 48), colour)
    exif = Image.Exif()
    exif[0x0132] = "2026:03:09 12:00:00"
    if gps:
        exif.get_ifd(0x8825).update({1: "N", 2: (50.0, 27.0, 0.36), 3: "E", 4: (30.0, 31.0, 24.24)})
    img.save(os.path.join(out, name), "JPEG", quality=90, exif=exif)
jpeg("cam-1.jpg", (200, 30, 30), gps=True)
jpeg("cam-2.jpg", (30, 200, 30), gps=True)
jpeg("trip-1.jpg", (30, 30, 200))
jpeg("cam-3.jpg", (200, 200, 30))
with open(os.path.join(out, "clip.mp4"), "wb") as f: f.write(os.urandom(300_000))
PY
cp "$media_dir/cam-1.jpg" "$media_dir/trip-copy.jpg"
adb shell "rm -rf /sdcard/DCIM/SmokeCamera /sdcard/Pictures/SmokeTrip; mkdir -p /sdcard/DCIM/SmokeCamera /sdcard/Pictures/SmokeTrip"
adb push "$media_dir/cam-1.jpg" "$media_dir/cam-2.jpg" "$media_dir/clip.mp4" /sdcard/DCIM/SmokeCamera/ >/dev/null
adb push "$media_dir/trip-1.jpg" "$media_dir/trip-copy.jpg" /sdcard/Pictures/SmokeTrip/ >/dev/null
scan() { for f in "$@"; do adb shell "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file://$f" >/dev/null 2>&1; done; }
scan /sdcard/DCIM/SmokeCamera/cam-1.jpg /sdcard/DCIM/SmokeCamera/cam-2.jpg /sdcard/DCIM/SmokeCamera/clip.mp4 \
  /sdcard/Pictures/SmokeTrip/trip-1.jpg /sdcard/Pictures/SmokeTrip/trip-copy.jpg
indexed() { contains "$1" adb shell content query --uri content://media/external/file --projection _display_name; }
wait_for 60 indexed cam-1.jpg && wait_for 30 indexed clip.mp4 && wait_for 30 indexed trip-copy.jpg \
  && pass "seeded the library: SmokeCamera (2 photos, 1 video) and SmokeTrip (2 photos, one a copy of a SmokeCamera photo)" \
  || fail "media scan" "$(adb shell content query --uri content://media/external/file --projection _display_name | tail -5)"

# All files access would let MediaStore show everything: switched off here, so the photo permissions are what counts.
set_appop default
ctl "storage refresh" >/dev/null
adb shell pm revoke "$pkg" android.permission.READ_MEDIA_IMAGES 2>/dev/null || true
adb shell pm revoke "$pkg" android.permission.READ_MEDIA_VIDEO 2>/dev/null || true
adb shell pm revoke "$pkg" android.permission.READ_MEDIA_VISUAL_USER_SELECTED 2>/dev/null || true
front_app
photo_root="phone-library"
created=$(api POST /api/folders "{\"name\":\"Phone library\",\"mode\":\"Backup\",\"encryption\":\"Transport\",\"kind\":\"Photos\",\"storageBackendId\":$backend,\"rootPath\":\"$photo_root\",\"intervalMinutes\":60,\"retention\":{\"last\":10}}")
pf=$(jq -r .id <<<"$created"); photo_key=$(jq -r .recoveryKey <<<"$created")
[[ "$pf" =~ ^[0-9]+$ && "$photo_key" != null ]] && pass "a Photos folder made in the web UI (folder $pf, with its recovery key)" || fail "photos folder" "$created"
api PUT "/api/folders/$pf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\"}" >/dev/null
wait_state '.folders | any(.name == "Phone library")' 30 && pass "it is on the phone's dashboard" || fail "photos folder on the phone" "$(state)"
p=$(ctl "photos-dialog $pf")
jq -e '.needsPermission and .access == "None"' <<<"$p" >/dev/null && pass "without permission the Photos screen explains why ASync asks" || fail "photos explanation" "$p"
shot photos-permission
ctl photos-allow >/dev/null
sleep 2; dismiss_anr
shot_system photos-permission-dialog
tap_text '^allow all$|^allow$' 20 && pass "the system's question: Allow all" || fail "photos permission dialog" "$(ui_dump | head -c 600)"
# ACCESS_MEDIA_LOCATION has no dialog of its own; grant it in case this Android asks for it separately.
adb shell pm grant "$pkg" android.permission.ACCESS_MEDIA_LOCATION 2>/dev/null || true
wait_for 30 contains '"access":"Full"' ctl photos-state && pass "ASync may read all photos and videos now" || fail "photos access" "$(ctl photos-state)"
ctl back >/dev/null

# The web UI's album list: live from the phone, then kept for when it is offline.
albums=$(api GET "/api/devices/$device/albums")
cam_id=$(jq -r '.albums[] | select(.name == "SmokeCamera") | .id' <<<"$albums")
jq -e '.live and (.albums | any(.name == "SmokeCamera" and .photos == 2 and .videos == 1)) and (.albums | any(.name == "SmokeTrip" and .photos == 2))' <<<"$albums" >/dev/null \
  && pass "the album list comes live from the phone: $(jq -c '[.albums[] | select(.name | startswith("Smoke")) | "\(.name) \(.photos)+\(.videos)"]' <<<"$albums")" \
  || fail "albums" "$albums"
api PUT "/api/folders/$pf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\",\"media\":{\"all\":false,\"albums\":[{\"id\":\"$cam_id\",\"name\":\"SmokeCamera\"}],\"includeVideos\":true,\"includeScreenshots\":true}}" >/dev/null
wait_state '.folders[] | select(.name == "Phone library") | .path | test("SmokeCamera")' 30 \
  && pass "SmokeCamera chosen in the web UI; the phone shows \"$(folder_json "Phone library" | jq -r .path)\"" || fail "selection on the phone" "$(folder_json "Phone library")"

r=$(sync_phone "$pf" 180)
jq -e '.ok and (.message | test("2 photos, 1 videos"))' <<<"$r" >/dev/null && pass "backed up: $(jq -r .message <<<"$r")" || fail "photos backup" "$r"
api GET "/api/folders/$pf" | jq -e '.devices[0].mediaCounts.photos == 2 and .devices[0].mediaCounts.videos == 1' >/dev/null \
  && pass "the web UI shows 2 photos, 1 video for the phone" || fail "counts on the server" "$(api GET "/api/folders/$pf")"
shot photos-dashboard
ctl "photos-dialog $pf" >/dev/null
ctl "photos-albums SmokeCamera" >/dev/null
shot photos-albums
ctl back >/dev/null

# async-cli, offline: a copy of the repository and the folder's recovery key; the tree and the originals.
server_container=$(compose ps -q async-server)
rm -rf "$work/photo-repo" "$work/photo-restored"
docker cp "$server_container:/data/android-smoke/$photo_root" "$work/photo-repo" >/dev/null
printf '%s' "$photo_key" > "$work/photo-key"
ASYNC_RECOVERY_KEY="" "$CLI" restore --repo "$work/photo-repo" --key-file "$work/photo-key" --snapshot latest --target "$work/photo-restored" >"$work/photo-restore.log" 2>&1 \
  && pass "async-cli restored the snapshot offline (copy of the repository + recovery key)" || fail "cli photo restore" "$(cat "$work/photo-restore.log")"
tree=$(cd "$work/photo-restored" 2>/dev/null && find . -type f | sed 's|^\./||' | sort)
printf '%s\n' "$tree" | grep -Eq '^[0-9]{4}/[0-9]{2}/cam-1~[0-9]+\.jpg$' && printf '%s\n' "$tree" | grep -Eq '^[0-9]{4}/[0-9]{2}/cam-1~[0-9]+\.json$' \
  && printf '%s\n' "$tree" | grep -Eq '^[0-9]{4}/[0-9]{2}/clip~[0-9]+\.mp4$' && [[ $(printf '%s\n' "$tree" | wc -l) -eq 6 ]] \
  && ! printf '%s\n' "$tree" | grep -q trip \
  && pass "the tree is <year>/<month>/<name>~<id>.<ext> plus a .json each, SmokeCamera only: $(printf '%s ' $tree)" || fail "photo tree" "$tree"
same() { [[ "$(sha256sum "$1" | cut -d' ' -f1)" == "$(adb shell sha256sum "$2" | cut -d' ' -f1 | tr -d '\r')" ]]; }
c1=$(printf '%s\n' "$tree" | grep -E 'cam-1~[0-9]+\.jpg$'); c2=$(printf '%s\n' "$tree" | grep -E 'cam-2~[0-9]+\.jpg$'); cv=$(printf '%s\n' "$tree" | grep -E 'clip~[0-9]+\.mp4$')
same "$work/photo-restored/$c1" /sdcard/DCIM/SmokeCamera/cam-1.jpg && same "$work/photo-restored/$c2" /sdcard/DCIM/SmokeCamera/cam-2.jpg \
  && same "$work/photo-restored/$cv" /sdcard/DCIM/SmokeCamera/clip.mp4 && same "$work/photo-restored/$c1" "/sdcard/DCIM/SmokeCamera/cam-1.jpg" \
  && cmp -s "$work/photo-restored/$c1" "$media_dir/cam-1.jpg" \
  && pass "the originals are byte-identical to the phone's files (EXIF location kept: requireOriginal)" || fail "photo bytes"
side="$work/photo-restored/${c1%.jpg}.json"
jq -e '.format == 1 and .source == "android-mediastore" and (.id | startswith("android:external_primary:")) and .fileName == "cam-1.jpg"
       and .mime == "image/jpeg" and .width == 64 and .height == 48 and (.albums[0].name == "SmokeCamera") and .relativePath == "DCIM/SmokeCamera/"
       and (.location.latitude | . > 50.44 and . < 50.46) and (.resources[0].role == "original")' "$side" >/dev/null \
  && pass "its sidecar: id, name, type, size, dates, dimensions, location, album, relative path" || fail "sidecar" "$(cat "$side" 2>&1)"

# The web UI's snapshot browser shows it and downloads an original.
sid=$(api GET "/api/folders/$pf/snapshots" | jq -r '.[0].id')
year=${c1%%/*}; rest=${c1#*/}; month=${rest%%/*}
api GET "/api/folders/$pf/snapshots/$sid/tree?path=$year/$month" | jq -e --arg n "${c1##*/}" '.entries | any(.name == $n)' >/dev/null \
  && curl -fsS -H "Authorization: Bearer $token" "$server/api/folders/$pf/snapshots/$sid/file?path=$(enc "$c1")" -o "$work/photo-web.jpg" \
  && cmp -s "$work/photo-web.jpg" "$media_dir/cam-1.jpg" && pass "the web snapshot browser lists $year/$month and downloads the original" || fail "web snapshot browser"

# A new photo starts a backup by itself (the MediaStore content trigger), with the app in the background.
bg_has '.works | any((.tags | split(",") | index("media-trigger")) and .state == "ENQUEUED")' \
  && pass "the media content trigger is scheduled" || fail "media trigger" "$(bgstate | jq -c '[.works[] | {tags, state}]')"
before=$(api GET "/api/folders/$pf/snapshots" | jq length)
adb shell input keyevent KEYCODE_HOME
adb push "$media_dir/cam-3.jpg" /sdcard/DCIM/SmokeCamera/ >/dev/null
scan /sdcard/DCIM/SmokeCamera/cam-3.jpg
more_snapshots() { [[ $(api GET "/api/folders/$pf/snapshots" | jq length) -gt $before ]]; }
wait_for 180 more_snapshots && logged "New photos or videos: backing up" \
  && pass "the new photo started a backup by itself: $(api GET "/api/folders/$pf" | jq -r '.devices[0].lastSyncMessage')" \
  || fail "content-trigger run" "$(adb logcat -d -s ASync | grep -i 'photo\|media' | tail -5)"
api GET "/api/folders/$pf" | jq -e '.devices[0].mediaCounts.photos == 3' >/dev/null && pass "and it holds 3 photos now" || fail "trigger counts" "$(api GET "/api/folders/$pf")"
wait_for 30 bg_has '.works | any((.tags | split(",") | index("media-trigger")) and .state == "ENQUEUED")' \
  && pass "and the next trigger is waiting" || fail "next trigger" "$(bgstate | jq -c '[.works[] | {tags, state}]')"
front_app

# Restore into a wiped album: the originals come back where they were, and a second restore skips them all.
adb shell "rm -f /sdcard/DCIM/SmokeCamera/*"
scan /sdcard/DCIM/SmokeCamera/cam-1.jpg /sdcard/DCIM/SmokeCamera/cam-2.jpg /sdcard/DCIM/SmokeCamera/cam-3.jpg /sdcard/DCIM/SmokeCamera/clip.mp4
gone() { ! indexed cam-2.jpg; }
wait_for 60 gone && pass "SmokeCamera wiped on the phone" || fail "wipe" "$(adb shell content query --uri content://media/external/file --projection _display_name | grep cam)"
api PUT "/api/folders/$pf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\",\"paused\":true}" >/dev/null # no backup of the empty album meanwhile
ctl "bg-photos-restore $pf" >/dev/null
restored() { ctl bg-photos | jq -e '.restore.running == false and .restore.done == .restore.total' >/dev/null; }
wait_for 180 restored && p=$(ctl bg-photos) && jq -e '.restore.restored == 4 and .restore.ok' <<<"$p" >/dev/null \
  && pass "restored into the library: $(jq -r .restore.message <<<"$p")" || fail "restore into the library" "$(ctl bg-photos)"
same "$media_dir/cam-1.jpg" /sdcard/DCIM/SmokeCamera/cam-1.jpg && same "$media_dir/cam-3.jpg" /sdcard/DCIM/SmokeCamera/cam-3.jpg \
  && same "$media_dir/clip.mp4" /sdcard/DCIM/SmokeCamera/clip.mp4 && indexed cam-2.jpg \
  && pass "back in DCIM/SmokeCamera, byte for byte, and in MediaStore" || fail "restored bytes" "$(adb shell ls -l /sdcard/DCIM/SmokeCamera)"
ctl "bg-photos-restore $pf" >/dev/null
wait_for 120 restored && p=$(ctl bg-photos) && jq -e '.restore.restored == 0 and .restore.skipped == 4' <<<"$p" >/dev/null \
  && pass "a second restore skips what is there: $(jq -r .restore.message <<<"$p")" || fail "second restore" "$(ctl bg-photos)"

# Tidy up for the restart check (it counts the phone's folders), and give All files access back.
api DELETE "/api/folders/$pf" >/dev/null && pass "the Photos folder deleted in the web UI" || fail "delete photos folder"
wait_state '(.folders | any(.name == "Phone library")) | not' 30 || fail "photos folder gone" "$(state)"
set_appop allow
ctl "storage refresh" >/dev/null
