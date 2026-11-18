# Sourced by scripts/smoke-ios.sh (it needs that script's helpers, $udid, $device, $backend, $work, $bundle and the running
# app): photo and video backups on the iPhone (task 23). Makes test media on the Mac (JPEGs with an EXIF date and GPS, an H.264
# video: a Swift script, ImageIO and AVFoundation), adds them with `xcrun simctl addmedia`, shows the permission's explanation and
# iOS's question, grants limited and then full access in the simulator's TCC database (simctl cannot tap the question, and its
# `privacy grant photos` leaves PhotoKit's read/write status undetermined), makes albums as a person would in the
# Photos app, makes a Photos folder in the web UI's API, picks an album from the iPhone's live album list (over the hub), backs it
# up, checks the snapshot's tree, sidecars (source ios-photokit) and byte-identical originals with async-cli (offline, from the
# repository and the folder's recovery key), sees a favourite change read no original again, a new photo start a backup by itself
# (PHPhotoLibraryChangeObserver), a photo taken out of the album stay in the older snapshot, a restore of that snapshot put it
# back into its album, then wipes the simulator's library and restores everything into it, and again without duplicates.
# Deleting a photo needs iOS's confirmation, which simctl cannot answer: "taken out of the album" stands for it.
# shellcheck shell=bash disable=SC2015,SC2154

section "photos and videos (task 23)"
note() { printf '  \033[33m·\033[0m %s\n' "$1"; }
enc() { python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$1"; }
photos() { ctl photos; }
access_is() { photos | jq -e --arg a "$1" '.access == $a' >/dev/null; }
library_empty() { ctl photos-assets | jq -e 'length == 0' >/dev/null; }
media_dir="$work/media"
rm -rf "$media_dir"; mkdir -p "$media_dir"
cat >"$work/make-media.swift" <<'SWIFT'
import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let out = URL(fileURLWithPath: CommandLine.arguments[1])

func jpeg(_ name: String, _ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ date: String, gps: (Double, Double)?) {
    let ctx = CGContext(data: nil, width: 96, height: 64, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
    ctx.setFillColor(red: r, green: g, blue: b, alpha: 1)
    ctx.fill(CGRect(x: 0, y: 0, width: 96, height: 64))
    let dest = CGImageDestinationCreateWithURL(out.appendingPathComponent(name) as CFURL, UTType.jpeg.identifier as CFString, 1, nil)!
    var props: [CFString: Any] = [kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: date]]
    if let (lat, lon) = gps {
        props[kCGImagePropertyGPSDictionary] = [kCGImagePropertyGPSLatitude: lat, kCGImagePropertyGPSLatitudeRef: "N",
                                                kCGImagePropertyGPSLongitude: lon, kCGImagePropertyGPSLongitudeRef: "E"]
    }
    CGImageDestinationAddImage(dest, ctx.makeImage()!, props as CFDictionary)
    guard CGImageDestinationFinalize(dest) else { fatalError("jpeg \(name)") }
}

jpeg("ios-cam-1.jpg", 0.8, 0.1, 0.1, "2026:03:09 12:00:00", gps: (50.4501, 30.5234))
jpeg("ios-cam-2.jpg", 0.1, 0.8, 0.1, "2026:03:10 12:00:00", gps: (50.4501, 30.5234))
jpeg("ios-trip-1.jpg", 0.1, 0.1, 0.8, "2026:03:11 12:00:00", gps: nil)
jpeg("ios-cam-3.jpg", 0.8, 0.8, 0.1, "2026:03:12 12:00:00", gps: nil)

// A one-second H.264 video.
let url = out.appendingPathComponent("ios-clip.mov")
let writer = try! AVAssetWriter(outputURL: url, fileType: .mov)
let input = AVAssetWriterInput(mediaType: .video, outputSettings: [AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: 128, AVVideoHeightKey: 96])
let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32ARGB, kCVPixelBufferWidthKey as String: 128, kCVPixelBufferHeightKey as String: 96])
writer.add(input)
writer.startWriting()
writer.startSession(atSourceTime: .zero)
for i in 0..<30 {
    while !input.isReadyForMoreMediaData { usleep(1000) }
    var buffer: CVPixelBuffer?
    CVPixelBufferPoolCreatePixelBuffer(nil, adaptor.pixelBufferPool!, &buffer)
    CVPixelBufferLockBaseAddress(buffer!, [])
    memset(CVPixelBufferGetBaseAddress(buffer!), Int32(i * 8), CVPixelBufferGetDataSize(buffer!))
    CVPixelBufferUnlockBaseAddress(buffer!, [])
    adaptor.append(buffer!, withPresentationTime: CMTime(value: CMTimeValue(i), timescale: 30))
}
input.markAsFinished()
let done = DispatchSemaphore(value: 0)
writer.finishWriting { done.signal() }
done.wait()
guard writer.status == .completed else { fatalError("video: \(String(describing: writer.error))") }
SWIFT
if xcrun swift "$work/make-media.swift" "$media_dir" >"$logs/make-media.log" 2>&1; then
  pass "test media made: $(cd "$media_dir" && ls | tr '\n' ' ')"
else
  fail "making test media" "$(tail -20 "$logs/make-media.log")"
fi
xcrun simctl addmedia "$udid" "$media_dir/ios-cam-1.jpg" "$media_dir/ios-cam-2.jpg" "$media_dir/ios-trip-1.jpg" "$media_dir/ios-clip.mov" \
  && pass "xcrun simctl addmedia: 3 photos and a video in the simulator's library" || fail "addmedia"

photo_root="iphone-library"
created=$(api POST /api/folders "{\"name\":\"iPhone library\",\"mode\":\"Backup\",\"encryption\":\"Transport\",\"kind\":\"Photos\",\"storageBackendId\":$backend,\"rootPath\":\"$photo_root\",\"intervalMinutes\":60,\"retention\":{\"last\":10}}")
pf=$(jq -r .id <<<"$created"); photo_key=$(jq -r .recoveryKey <<<"$created")
[[ "$pf" =~ ^[0-9]+$ && "$photo_key" != null ]] && pass "a Photos folder made in the web UI (folder $pf, with its recovery key)" || fail "photos folder" "$created"
attached=$(api PUT "/api/folders/$pf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\"}" 2>&1) \
  && pass "the iPhone attached to it (the server takes iOS as a phone with a photo library)" || fail "attach the iPhone" "$attached"
wait_state '.folders | any(.name == "iPhone library")' 30 && pass "it is on the iPhone's dashboard" || fail "photos folder on the iPhone" "$(state)"
p=$(ctl "photos-dialog $pf")
jq -e '.needsPermission and .access == "None" and (.explanation | test("Allow Full Access"))' <<<"$p" >/dev/null \
  && pass "without permission the Photos screen explains why ASync asks" || fail "photos explanation" "$p"
jq -e '.access == "None"' <<<"$(photos)" >/dev/null && pass "and the agent reports no access (nothing asked of PhotoKit)" || fail "agent access" "$(photos)"
shot photos-permission
ctl back >/dev/null

# Access as the person's answer would give it (simctl cannot tap iOS's question; the question itself is shown at the end of
# smoke-ios.sh). On iOS 26 PhotoKit asks TCC for kTCCServicePhotos with auth version 2 and takes "not determined" from a row
# of version 1, which is what `xcrun simctl privacy … grant photos` writes (task 23's Mac runs: tccd answered authValue=1,
# authVersion=2 to the app's first query and granted the version-1 query PhotoKit ignores). So the row is written into the
# simulator's TCC database with auth_version 2, with the simulator shut down: auth_value 2 = full, 3 = limited (simctl refuses
# photos-limited). privacy.log has the TCC rows of each step, privacy-*.log what tccd and the photo daemons said.
tcc_db="$HOME/Library/Developer/CoreSimulator/Devices/$udid/data/Library/TCC/TCC.db"
sqlite3 "$tcc_db" ".schema access" >"$logs/tcc-schema.log" 2>&1 || true
tcc_rows() {
  { echo "── $1"; sqlite3 "$tcc_db" "SELECT service, client, client_type, auth_value, auth_reason, auth_version, csreq IS NOT NULL, indirect_object_identifier, flags FROM access WHERE client = '$bundle'"; } >>"$logs/privacy.log" 2>&1 || true
}
tcc_write() { # AUTH_VALUE RUN: the photos row set while the simulator is shut down, then booted and the app launched
  xcrun simctl shutdown "$udid"
  sqlite3 "$tcc_db" "UPDATE access SET auth_value = $1, auth_reason = 2, auth_version = 2, last_modified = CAST(strftime('%s','now') AS INTEGER) WHERE client = '$bundle' AND service = 'kTCCServicePhotos';
    INSERT OR IGNORE INTO access (service, client, client_type, auth_value, auth_reason, auth_version, flags)
    VALUES ('kTCCServicePhotos', '$bundle', 0, $1, 2, 2, 0);" >>"$logs/privacy.log" 2>&1
  tcc_rows "written: auth_value $1"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b >/dev/null
  launch "$2"
  ctl "notifications off" >/dev/null
  wait_state '.page == "DashboardViewModel" and .state == "Connected"' 60 || true
  echo "app: $(photos | jq -c '{access, authorization, addOnly}')" >>"$logs/privacy.log"
}
tcc_rows "before"
system_log() { # NAME: what tccd and the photo library daemons said in the last minutes
  xcrun simctl spawn "$udid" log show --last 4m --style compact \
    --predicate 'process == "tccd" OR process == "assetsd" OR process == "photolibraryd" OR subsystem == "com.apple.TCC" OR subsystem BEGINSWITH "com.apple.photos"' \
    >"$logs/privacy-$1.log" 2>&1 || true
}
xcrun simctl privacy "$udid" grant photos "$bundle" >>"$logs/privacy.log" 2>&1 || fail "simctl privacy grant photos" "$(tail -3 "$logs/privacy.log")"
tcc_rows "after simctl privacy grant photos"
launch 3
echo "app: $(photos | jq -c '{access, authorization, addOnly}')" >>"$logs/privacy.log"
granted=""
if wait_for 30 access_is Full; then
  granted="xcrun simctl privacy … grant photos"
else
  system_log grant
  note "simctl's grant (a TCC row of auth version 1) left PhotoKit at $(photos | jq -r .authorization): the row with auth version 2"
  tcc_write 2 3b
  wait_for 20 access_is Full && granted="a TCC row of auth version 2 (what PhotoKit asks for on iOS 26)"
fi
if [[ -z "$granted" ]]; then
  system_log csreq
  # iOS's own question, answered through the Simulator app's accessibility (macOS sees the simulated screen's elements).
  xcrun simctl privacy "$udid" reset photos "$bundle" >>"$logs/privacy.log" 2>&1 || true
  open -a Simulator --args -CurrentDeviceUDID "$udid" >>"$logs/privacy.log" 2>&1 || true
  sleep 10
  launch 3c
  ctl "notifications off" >/dev/null
  ctl photos-ask >/dev/null
  sleep 4
  xcrun simctl io "$udid" screenshot --type=png "$logs/photos-question.png" >/dev/null 2>&1 || true
  clicked=$(osascript <<'OSA' 2>&1
tell application "Simulator" to activate
delay 1
tell application "System Events"
  tell process "Simulator"
    set frontmost to true
    repeat with w in windows
      repeat with e in (entire contents of w)
        try
          if (role of e is "AXButton") and ((name of e is "Allow Full Access") or (description of e is "Allow Full Access")) then
            click e
            return "clicked"
          end if
        end try
      end repeat
    end repeat
  end tell
end tell
return "not found"
OSA
)
  echo "osascript: $clicked" >>"$logs/privacy.log"
  sleep 3
  echo "app: $(photos | jq -c '{access, authorization, addOnly}')" >>"$logs/privacy.log"
  wait_for 20 access_is Full && granted="iOS's question answered through the Simulator's accessibility ($clicked)"
fi
tcc_rows "granted: ${granted:-no}"
if [[ -n "$granted" ]]; then
  pass "full access: $granted"
else
  system_log end
  fail "photos access (simctl's grant, the TCC row with csreq and the question clicked through the Simulator all left PhotoKit undetermined)" "$(photos) $(cat "$logs/privacy.log")"
  api DELETE "/api/folders/$pf" >/dev/null
  return 0
fi

tcc_write 3 4
if wait_for 20 access_is Partial; then
  pass "limited access (TCC auth_value 3): the agent reports Partial and sees $(ctl photos-assets | jq length) assets (none selected)"
else
  note "limited access could not be set ($(photos | jq -r .authorization)): the limited state is shown forced, for the screenshot"
  ctl "photos-force partial" >/dev/null
fi
ctl "photos-dialog $pf" >/dev/null
p=$(ctl photos-state)
jq -e '.isPartial and .canChooseMore and (.partialText | test("Limit Access"))' <<<"$p" >/dev/null \
  && pass "limited access: the screen says only selected photos are backed up and offers Choose photos…" || fail "limited screen" "$p"
ctl "show PartialNotice" >/dev/null || true
shot photos-limited
ctl "photos-force off" >/dev/null
ctl back >/dev/null

tcc_write 2 5
if wait_for 30 access_is Full; then
  pass "full access again: ASync may read all photos and videos"
else
  fail "photos access" "$(photos) $(cat "$logs/privacy.log")"
  api DELETE "/api/folders/$pf" >/dev/null
  return 0 # the rest needs access
fi
assets=$(ctl photos-assets)
jq -e '([.[].fileName | select(startswith("ios-"))] | sort) == ["ios-cam-1.jpg", "ios-cam-2.jpg", "ios-clip.mov", "ios-trip-1.jpg"]' <<<"$assets" >/dev/null \
  && pass "PhotoKit lists the 4 assets added, with their original names ($(jq length <<<"$assets") with the simulator's own sample photos)" || fail "photokit listing" "$assets"
# Albums, as a person makes them in the Photos app (no confirmation needed): ios-cam-1 in both.
ctl "photos-album-make SmokeCamera ios-cam-1.jpg,ios-cam-2.jpg,ios-clip.mov" >/dev/null
ctl "photos-album-make SmokeTrip ios-trip-1.jpg,ios-cam-1.jpg" >/dev/null

# The web UI's album list: live from the iPhone over the hub.
albums_live() { api GET "/api/devices/$device/albums" | jq -e '.live and (.albums | any(.name == "SmokeCamera" and .photos == 2 and .videos == 1)) and (.albums | any(.name == "SmokeTrip" and .photos == 2))'; }
wait_for 30 albums_live && albums=$(api GET "/api/devices/$device/albums") \
  && pass "the album list comes live from the iPhone: $(jq -c '[.albums[] | "\(.name) \(.photos)+\(.videos)"]' <<<"$albums")" \
  || fail "albums" "$(api GET "/api/devices/$device/albums")"
jq -e '.albums | any(.id == "smart:videos" and .videos == 1)' <<<"$albums" >/dev/null && pass "with the smart albums that have something in them (Videos)" || fail "smart albums" "$albums"
cam_id=$(jq -r '.albums[] | select(.name == "SmokeCamera") | .id' <<<"${albums:-{\}}")
api PUT "/api/folders/$pf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\",\"media\":{\"all\":false,\"albums\":[{\"id\":\"$cam_id\",\"name\":\"SmokeCamera\"}],\"includeVideos\":true,\"includeScreenshots\":true}}" >/dev/null
phone_selection() { photos | jq -e --argjson f "$pf" '.folders[] | select(.id == $f) | .media == "1 album: SmokeCamera"' >/dev/null; }
wait_for 30 phone_selection && pass "SmokeCamera chosen in the web UI; the iPhone's agent has it: \"1 album: SmokeCamera\"" || fail "selection on the iPhone" "$(photos)"

r=$(sync_phone "$pf" 180)
jq -e '.ok and (.message | test("2 photos, 1 video "))' <<<"$r" >/dev/null && pass "backed up: $(jq -r .message <<<"$r")" || fail "photos backup" "$r"
api GET "/api/folders/$pf" | jq -e '.devices[0].mediaCounts.photos == 2 and .devices[0].mediaCounts.videos == 1' >/dev/null \
  && pass "the web UI shows 2 photos, 1 video for the iPhone" || fail "counts on the server" "$(api GET "/api/folders/$pf")"
shot photos-dashboard
ctl "photos-dialog $pf" >/dev/null
ctl "photos-albums SmokeCamera" >/dev/null
ctl "show CloudDownloads" >/dev/null || true
shot photos-albums
ctl back >/dev/null

# async-cli, offline: the repository on the server's storage and the folder's recovery key; the tree and the originals.
rm -rf "$work/photo-restored"
printf '%s' "$photo_key" >"$work/photo-key"
"$work/cli/async-cli" restore --repo "$work/data/ios/$photo_root" --key-file "$work/photo-key" --snapshot latest --target "$work/photo-restored" \
  >"$logs/photo-restore.log" 2>&1 && pass "async-cli restored the snapshot offline (repository + recovery key)" || fail "cli photo restore" "$(cat "$logs/photo-restore.log")"
tree=$(cd "$work/photo-restored" 2>/dev/null && find . -type f | sed 's|^\./||' | sort)
id_re='[0-9A-F-]{36}_L0_001'
printf '%s\n' "$tree" | grep -Eq "^2026/03/ios-cam-1~$id_re\.jpg$" && printf '%s\n' "$tree" | grep -Eq "^2026/03/ios-cam-1~$id_re\.json$" \
  && printf '%s\n' "$tree" | grep -Eq "^[0-9]{4}/[0-9]{2}/ios-clip~$id_re\.mov$" && [[ $(printf '%s\n' "$tree" | wc -l) -eq 6 ]] \
  && ! printf '%s\n' "$tree" | grep -q trip \
  && pass "the tree is <year>/<month>/<name>~<local id>.<ext> plus a .json each, SmokeCamera only: $(printf '%s ' $tree)" || fail "photo tree" "$tree"
c1=$(printf '%s\n' "$tree" | grep -E 'ios-cam-1~.*\.jpg$'); cv=$(printf '%s\n' "$tree" | grep -E 'ios-clip~.*\.mov$')
cmp -s "$work/photo-restored/$c1" "$media_dir/ios-cam-1.jpg" && cmp -s "$work/photo-restored/$cv" "$media_dir/ios-clip.mov" \
  && pass "the originals are byte-identical to the files added (PhotoKit's original resources, as they are)" \
  || fail "photo bytes" "$(ls -l "$work/photo-restored/$c1" "$media_dir/ios-cam-1.jpg" "$work/photo-restored/$cv" "$media_dir/ios-clip.mov" 2>&1)"
side="$work/photo-restored/${c1%.jpg}.json"
jq -e '.format == 1 and .source == "ios-photokit" and (.id | test("^[0-9A-F-]{36}/L0/001$")) and .fileName == "ios-cam-1.jpg"
       and .mime == "image/jpeg" and .width == 96 and .height == 64 and ([.albums[].name] | sort) == ["SmokeCamera", "SmokeTrip"]
       and (.relativePath == null) and (.location.latitude | . > 50.44 and . < 50.46) and (.resources[0].role == "original")' "$side" >/dev/null \
  && pass "its sidecar: source ios-photokit, the local identifier, name, type, size, dates, dimensions, location, both albums" || fail "sidecar" "$(cat "$side" 2>&1)"
jq -e '.durationMs > 900 and .kind == "video"' "$work/photo-restored/${cv%.mov}.json" >/dev/null && pass "the video's sidecar has its duration" || fail "video sidecar" "$(cat "$work/photo-restored/${cv%.mov}.json" 2>&1)"

# A favourite: PhotoKit moves the asset's modification date, ASync reads no original again, the sidecar has it.
ctl "photos-favourite ios-cam-2.jpg" >/dev/null
sleep 8 # the change observer's own run (5 s quiet) may come first; either way the next run reads nothing
r=$(sync_phone "$pf" 120)
jq -e '.ok and (.message | test(" 0 new, [01] changed"))' <<<"$r" >/dev/null && pass "a favourite reads no original again: $(jq -r .message <<<"$r")" || fail "favourite run" "$r"

# A new photo while the app runs: the change observer starts the Photos folder by itself (debounced).
before=$(api GET "/api/folders/$pf/snapshots" | jq length)
triggered=$(photos | jq .triggered)
xcrun simctl addmedia "$udid" "$media_dir/ios-cam-3.jpg"
sleep 2
ctl "photos-album-add SmokeCamera ios-cam-3.jpg" >/dev/null
three() { api GET "/api/folders/$pf" | jq -e '.devices[0].mediaCounts.photos == 3' >/dev/null; }
wait_for 90 three && [[ $(photos | jq .triggered) -gt $triggered ]] && [[ $(api GET "/api/folders/$pf/snapshots" | jq length) -gt $before ]] \
  && pass "a new photo started a backup by itself (PHPhotoLibraryChangeObserver): $(api GET "/api/folders/$pf" | jq -r '.devices[0].lastSyncMessage')" \
  || fail "change-observer run" "$(photos | jq -c '{triggered, folders}')"
full_snapshot=$(api GET "/api/folders/$pf/snapshots" | jq -r 'sort_by(.time) | last | .id')

# Taken out of the album (deleting needs iOS's confirmation): not in the next snapshot, still in the older one.
ctl "photos-album-remove SmokeCamera ios-cam-2.jpg" >/dev/null
sleep 8
r=$(sync_phone "$pf" 120)
jq -e '.ok and (.message | test("^Snapshot [0-9a-f]+: 2 photos, 1 video"))' <<<"$r" >/dev/null && pass "ios-cam-2 out of SmokeCamera: $(jq -r .message <<<"$r")" || fail "after removal" "$r"
newest=$(api GET "/api/folders/$pf/snapshots" | jq -r 'sort_by(.time) | last | .id')
in_snapshot() { api GET "/api/folders/$pf/snapshots/$1/tree?path=2026/03" | jq -e --arg n "$2" '.nodes | any(.name | startswith($n))' >/dev/null; }
! in_snapshot "$newest" ios-cam-2~ && in_snapshot "$full_snapshot" ios-cam-2~ \
  && pass "the newest snapshot has no ios-cam-2, the older one still has it" || fail "older snapshot keeps it"

c=$(ctl "photos-restore-choose $pf")
jq -e '.choosingRestore and (.choices | length >= 2)' <<<"$c" >/dev/null && pass "Restore into the library… offers the snapshots: $(jq -r '.choices[0]' <<<"$c")" || fail "restore choice" "$c"
ctl "show RestoreChoice" >/dev/null || true
shot photos-restore
ctl "photos-restore-cancel $pf" >/dev/null

restored() { photos | jq -e '.restore.running == false and .restore.done == .restore.total' >/dev/null; }
# The older snapshot while everything is still in the library: nothing is added, ios-cam-2 goes back into its album.
ctl "photos-restore $pf $full_snapshot" >/dev/null
wait_for 120 restored && p=$(photos) && jq -e '.restore.restored == 0 and .restore.skipped == 4' <<<"$p" >/dev/null \
  && pass "restoring the older snapshot adds nothing: $(jq -r .restore.message <<<"$p")" || fail "restore with everything there" "$(photos)"
jq -e --arg a "$cam_id" '[.[] | select(.fileName == "ios-cam-2.jpg")] | length == 1 and (.[0].albums | index($a))' <<<"$(ctl photos-assets)" >/dev/null \
  && pass "and ios-cam-2 is back in SmokeCamera" || fail "back into the album" "$(ctl photos-assets | jq -c '[.[] | {fileName, albums}]')"

# A wiped library (a new iPhone): the simulator's Photos data removed while it is shut down.
sim_data="$HOME/Library/Developer/CoreSimulator/Devices/$udid/data"
xcrun simctl shutdown "$udid"
rm -rf "$sim_data/Media/DCIM" "$sim_data/Media/PhotoData"
xcrun simctl boot "$udid"
xcrun simctl bootstatus "$udid" -b >/dev/null
launch 6
ctl "notifications off" >/dev/null
wait_state '.page == "DashboardViewModel" and .state == "Connected"' 60 || fail "after the wipe" "$(state)"
wait_for 60 library_empty && pass "the simulator's library is empty (Photos data removed, rebooted)" || fail "wipe" "$(ctl photos-assets | head -c 400)"
snapshots_before=$(api GET "/api/folders/$pf/snapshots" | jq length)
r=$(sync_phone "$pf" 120)
jq -e '(.ok | not) and (.message | test("No snapshot was made"))' <<<"$r" >/dev/null && [[ $(api GET "/api/folders/$pf/snapshots" | jq length) -eq $snapshots_before ]] \
  && pass "a backup of the empty library is refused, no empty snapshot: $(jq -r .message <<<"$r")" || fail "empty selection" "$r"
ctl "photos-restore $pf $full_snapshot" >/dev/null
wait_for 180 restored && p=$(photos) && jq -e '.restore.restored == 4 and (.restore.message | test("4 restored"))' <<<"$p" >/dev/null \
  && pass "restored into the library: $(jq -r .restore.message <<<"$p")" || fail "restore into the library" "$(photos)"
a=$(ctl photos-assets)
jq -e 'length == 4 and ([.[].fileName] | sort) == ["ios-cam-1.jpg", "ios-cam-2.jpg", "ios-cam-3.jpg", "ios-clip.mov"]
       and ([.[] | select(.fileName == "ios-cam-1.jpg")][0] | .location.latitude > 50.44 and (.taken > 0))
       and ([.[] | select(.fileName == "ios-cam-2.jpg")][0].favourite)' <<<"$a" >/dev/null \
  && pass "back with their names, dates, location and favourite" || fail "restored assets" "$(jq -c '[.[] | {fileName, taken, location, favourite}]' <<<"$a")"
albums=$(ctl photos | jq -c '[.albums[] | select(.name | startswith("Smoke")) | "\(.name) \(.photos)+\(.videos)"]')
[[ "$albums" == '["SmokeCamera 3+1","SmokeTrip 1+0"]' ]] && pass "into the albums named in the sidecars, each made once: $albums" || fail "restored albums" "$albums"
ctl "photos-restore $pf $full_snapshot" >/dev/null
wait_for 120 restored && p=$(photos) && jq -e '.restore.restored == 0 and .restore.skipped == 4' <<<"$p" >/dev/null \
  && [[ $(ctl photos-assets | jq length) -eq 4 ]] \
  && pass "a second restore skips what is there, nothing duplicated: $(jq -r .restore.message <<<"$p")" || fail "second restore" "$(photos)"
# The album ids are new on a wiped iPhone: the selection is chosen again (here: everything), and the restored originals back up
# without uploading their content again.
ctl "photos-set $pf all" >/dev/null
r=$(sync_phone "$pf" 180)
jq -e '.ok and (.message | test("4 photos|3 photos, 1 video"))' <<<"$r" >/dev/null && pass "the restored library backs up again: $(jq -r .message <<<"$r")" || fail "backup after restore" "$r"

api DELETE "/api/folders/$pf" >/dev/null && pass "the Photos folder deleted in the web UI" || fail "delete photos folder"
wait_state '(.folders | any(.name == "iPhone library")) | not' 30 || fail "photos folder gone" "$(state)"
