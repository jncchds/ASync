# Sourced by scripts/smoke-android.sh (it needs that script's helpers, $device, $backend, $work, $CLI and the running app): contacts
# and calendar backups (task 20). Seeds the providers directly (not through ASync's own import) with contacts in two accounts (the
# phone itself and a test account) and events in two local calendars, shows the permission's explanation and allows reading in the
# system's dialog, makes a Contacts and a Calendars folder in the web UI's API, picks one account and one calendar from the phone's
# live lists, backs both up, checks the snapshots' files with async-cli (offline, from a copy of the repository, with the recovery key)
# byte for byte against the expected vCard and iCalendar text, changes one contact and sees the content trigger start a backup by
# itself, downloads an account as one .vcf and a calendar as one .ics from the web snapshot browser, then wipes the account and the
# calendar on the phone and restores them (WRITE asked only then), and a second restore skips everything.
# shellcheck shell=bash disable=SC2015,SC2154

section "contacts and calendars (task 20)"
declare -F bgstate >/dev/null || bgstate() { ctl bg; }
declare -F bg_has >/dev/null || bg_has() { bgstate | jq -e "$1" >/dev/null; }
declare -F idle >/dev/null || idle() { bg_has 'all(.folders[]; .activity == "Idle")'; }
perms=(READ_CONTACTS WRITE_CONTACTS READ_CALENDAR WRITE_CALENDAR)
grant() { for p in "$@"; do adb shell pm grant "$pkg" "android.permission.$p" 2>/dev/null || true; done; }
revoke() { for p in "$@"; do adb shell pm revoke "$pkg" "android.permission.$p" 2>/dev/null || true; done; }
pim() { ctl "bg-pim $1"; }

# Seed with the app's test helper (it writes the providers itself, with the permissions granted for that), then take them back.
grant "${perms[@]}"
seed=$(ctl bg-pim-seed)
jq -e '.carol > 0 and .work > 0 and .standup > 0' <<<"$seed" >/dev/null \
  && pass "seeded: Ann and Bob on the phone, Carol (with a photo) and Дмитро in smoke@example.com; Standup (weekly, a moved occurrence, a reminder) and Review in Smoke Work, Holiday in Smoke Home" \
  || fail "seed" "$seed"
carol=$(jq -r .carol <<<"$seed"); dmytro=$(jq -r .dmytro <<<"$seed"); work_cal=$(jq -r .work <<<"$seed")
standup=$(jq -r .standup <<<"$seed"); review=$(jq -r .review <<<"$seed")
revoke "${perms[@]}"   # revoking kills the app's process
sleep 2
front_app

smoke_account="ua.pp.chds.async.smoke:smoke@example.com"
mk_folder() { api POST /api/folders "{\"name\":\"$1\",\"mode\":\"Backup\",\"encryption\":\"Transport\",\"kind\":\"$2\",\"storageBackendId\":$backend,\"rootPath\":\"$3\",\"intervalMinutes\":60,\"retention\":{\"last\":10}}"; }
created=$(mk_folder "Phone contacts" Contacts phone-contacts); cf=$(jq -r .id <<<"$created"); contacts_key=$(jq -r .recoveryKey <<<"$created")
created=$(mk_folder "Phone calendars" Calendars phone-calendars); kf=$(jq -r .id <<<"$created"); calendars_key=$(jq -r .recoveryKey <<<"$created")
[[ "$cf" =~ ^[0-9]+$ && "$kf" =~ ^[0-9]+$ ]] && pass "a Contacts and a Calendars folder made in the web UI ($cf, $kf)" || fail "pim folders" "$created"
api PUT "/api/folders/$cf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\"}" >/dev/null
api PUT "/api/folders/$kf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\"}" >/dev/null
wait_state '(.folders | any(.name == "Phone contacts")) and (.folders | any(.name == "Phone calendars"))' 30 \
  && pass "both are on the phone's dashboard" || fail "pim folders on the phone" "$(state)"

# The explanation first, then the system's question (reading only).
p=$(ctl "pim-dialog $cf")
jq -e '.needsPermission and .kind == "Contacts"' <<<"$p" >/dev/null && pass "without permission the Contacts screen explains why ASync asks" || fail "contacts explanation" "$p"
shot contacts-permission
ctl pim-allow >/dev/null
sleep 2; dismiss_anr
shot_system contacts-permission-dialog
tap_text '^allow$' 20 && pass "the system's question: Allow" || fail "contacts permission dialog" "$(ui_dump | head -c 600)"
wait_for 30 contains '"access":"Full"' ctl pim-state && pass "ASync may read the contacts now (and not write them)" || fail "contacts access" "$(ctl pim-state)"
adb shell dumpsys package "$pkg" | grep -q 'android.permission.WRITE_CONTACTS: granted=true' && fail "write asked too early" || pass "WRITE_CONTACTS not asked for a backup"
ctl back >/dev/null
ctl "pim-dialog $kf" >/dev/null
ctl pim-allow >/dev/null
sleep 2; dismiss_anr
tap_text '^allow$' 20 && wait_for 30 contains '"access":"Full"' ctl pim-state && pass "and the calendars, the same way" || fail "calendar permission" "$(ctl pim-state)"
ctl back >/dev/null

# The web UI's lists come live from the phone.
lists=$(api GET "/api/devices/$device/collections?kind=Contacts")
jq -e --arg a "$smoke_account" '.live and (.collections | any(.id == $a and .count == 2)) and (.collections | any(.id == "local" and .count >= 2))' <<<"$lists" >/dev/null \
  && pass "the account list comes live from the phone: $(jq -c '[.collections[] | "\(.name) (\(.account)) \(.count)"]' <<<"$lists")" || fail "contact accounts" "$lists"
cals=$(api GET "/api/devices/$device/collections?kind=Calendars")
jq -e --arg w "$work_cal" '.live and (.collections | any(.id == $w and .name == "Smoke Work" and .count == 2 and .color == "#3366cc"))' <<<"$cals" >/dev/null \
  && pass "the calendar list too: $(jq -c '[.collections[] | "\(.name) \(.count) \(.color)"]' <<<"$cals")" || fail "calendars" "$cals"
api PUT "/api/folders/$cf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\",\"collections\":{\"all\":false,\"collections\":[{\"id\":\"$smoke_account\",\"name\":\"smoke@example.com\"}]}}" >/dev/null
api PUT "/api/folders/$kf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\",\"collections\":{\"all\":false,\"collections\":[{\"id\":\"$work_cal\",\"name\":\"Smoke Work\"}],\"pastDays\":null}}" >/dev/null
has_sel() { pim "$1" | jq -e --argjson f "$2" --arg s "$3" '.folders[] | select(.id == $f) | .selection == $s' >/dev/null; }
wait_for 30 has_sel contacts "$cf" "1 account: smoke@example.com" && wait_for 30 has_sel calendars "$kf" "1 calendar: Smoke Work · all events" \
  && pass "one account and one calendar chosen in the web UI; the phone's agent has them" || fail "selection on the phone" "$(pim contacts) $(pim calendars)"

r=$(sync_phone "$cf" 180)
jq -e '.ok and (.message | test("2 contacts in 1 account"))' <<<"$r" >/dev/null && pass "contacts backed up: $(jq -r .message <<<"$r")" || fail "contacts backup" "$r"
r=$(sync_phone "$kf" 180)
jq -e '.ok and (.message | test("2 events in 1 calendar"))' <<<"$r" >/dev/null && pass "calendar backed up: $(jq -r .message <<<"$r")" || fail "calendar backup" "$r"
api GET "/api/folders/$cf" | jq -e '.devices[0].collectionCounts.items == 2 and .devices[0].collectionCounts.collections == 1' >/dev/null \
  && pass "the web UI shows 2 contacts in 1 account for the phone" || fail "counts on the server" "$(api GET "/api/folders/$cf")"
front_app
shot pim-dashboard
ctl "pim-dialog $cf" >/dev/null
ctl "pim-choose smoke@example.com" >/dev/null
shot contacts-accounts
ctl back >/dev/null
ctl "pim-dialog $kf" >/dev/null
ctl "pim-choose Smoke Work" >/dev/null
shot calendars-calendars
ctl back >/dev/null

# async-cli, offline: the files byte for byte.
server_container=$(compose ps -q async-server)
restore_offline() { # root key target
  rm -rf "$work/$1-repo" "$3"
  docker cp "$server_container:/data/android-smoke/$1" "$work/$1-repo" >/dev/null
  printf '%s' "$2" > "$work/$1-key"
  ASYNC_RECOVERY_KEY="" "$CLI" restore --repo "$work/$1-repo" --key-file "$work/$1-key" --snapshot latest --target "$3" >"$work/$1-restore.log" 2>&1
}
restore_offline phone-contacts "$contacts_key" "$work/pim-contacts" && restore_offline phone-calendars "$calendars_key" "$work/pim-calendars" \
  && pass "async-cli restored both snapshots offline (copy of the repository + recovery key)" || fail "cli pim restore" "$(cat "$work"/phone-*-restore.log)"
adir="smoke@example.com~$(printf '%s' "$smoke_account" | sha256sum | cut -c1-8)"
files=$(cd "$work/pim-contacts" && find . -type f | sed 's|^\./||' | LC_ALL=C sort | tr '\n' ' ')
[[ "$files" == "$adir/Carol Smoke~$carol.vcf $adir/account.json $adir/Дмитро Коваль~$dmytro.vcf " ]] \
  && pass "the contacts tree: <account>~<hash>/account.json and <name>~<id>.vcf: $files" || fail "contacts tree" "$files"
crlf() { sed 's/$/\r/'; }
expect_dmytro=$(printf 'BEGIN:VCARD\nVERSION:4.0\nPRODID:-//ASync//Contacts 1//EN\nUID:android:raw:%s\nFN:Дмитро Коваль\nN:Коваль;Дмитро;;;\nTEL;TYPE=cell:+380 44 000 0000\nEND:VCARD\n' "$dmytro" | crlf)
[[ "$(cat "$work/pim-contacts/$adir/Дмитро Коваль~$dmytro.vcf")" == "$expect_dmytro" ]] \
  && pass "Дмитро's vCard is exactly the expected vCard 4.0 (Unicode name, cell number)" || fail "dmytro vcard" "$(cat "$work/pim-contacts/$adir/Дмитро Коваль~$dmytro.vcf" | cat -A | head -20)"
carol_file="$work/pim-contacts/$adir/Carol Smoke~$carol.vcf"
expect_carol=$(printf 'BEGIN:VCARD\nVERSION:4.0\nPRODID:-//ASync//Contacts 1//EN\nUID:android:raw:%s\nFN:Carol Smoke\nN:Smoke;Carol;;;\nTEL;TYPE=cell:+1 555 0200\nEMAIL;TYPE=work:carol@example.com\n' "$carol" | crlf)
head -c "${#expect_carol}" "$carol_file" | cmp -s - <(printf '%s' "$expect_carol") && tr -d '\r\n ' <"$carol_file" | grep -q 'PHOTO:data:image/jpeg;base64,/9j/' \
  && tail -c 11 "$carol_file" | grep -q 'END:VCARD' \
  && pass "Carol's vCard: the expected fields, then her photo embedded as a JPEG" || fail "carol vcard" "$(head -c 600 "$carol_file" | cat -A)"
jq -e --arg a "$smoke_account" '.format == 1 and .source == "android-contacts" and .kind == "contacts" and .id == $a and .name == "smoke@example.com"' \
  "$work/pim-contacts/$adir/account.json" >/dev/null && pass "account.json says which account it was" || fail "account.json" "$(cat "$work/pim-contacts/$adir/account.json")"
cdir="Smoke Work~$work_cal"
files=$(cd "$work/pim-calendars" && find . -type f | sed 's|^\./||' | LC_ALL=C sort | tr '\n' ' ')
[[ "$files" == "$cdir/Review~$review.ics $cdir/Standup~$standup.ics $cdir/calendar.json " ]] \
  && pass "the calendars tree: <calendar>~<id>/calendar.json and <title>~<id>.ics: $files" || fail "calendars tree" "$files"
expect_review=$(printf 'BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//ASync//Calendars 1//EN\nCALSCALE:GREGORIAN\nBEGIN:VEVENT\nUID:android:event:%s\nDTSTAMP:20261120T140000Z\nDTSTART:20261120T140000Z\nDTEND:20261120T150000Z\nSUMMARY:Review\n' "$review" | crlf)
review_file="$work/pim-calendars/$cdir/Review~$review.ics"
head -c "${#expect_review}" "$review_file" | cmp -s - <(printf '%s' "$expect_review") && tail -c 30 "$review_file" | grep -q 'END:VEVENT' \
  && pass "Review.ics starts exactly as expected (UTC times, no zone): $(sed -n '11,$p' "$review_file" | tr -d '\r' | tr '\n' ' ')" || fail "review ics" "$(cat -A "$review_file")"
st="$work/pim-calendars/$cdir/Standup~$standup.ics"
u=$(tr -d '\r' <"$st" | sed -e ':a' -e 'N' -e '$!ba' -e 's/\n //g')
for line in "BEGIN:VTIMEZONE" "TZID:Europe/Kyiv" "DTSTART;TZID=Europe/Kyiv:20261102T100000" "DURATION:PT15M" "RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=10" \
            "DESCRIPTION:Weekly\\, in Kyiv time" "TRIGGER:-PT10M" "RECURRENCE-ID;TZID=Europe/Kyiv:20261109T100000" "DTSTART;TZID=Europe/Kyiv:20261109T110000" \
            "SUMMARY:Standup (moved)"; do
  grep -qxF -- "$line" <<<"$u" || { fail "standup ics: no $line" "$u"; break; }
done && pass "Standup.ics: its zone (VTIMEZONE), local start, rule, reminder and the moved occurrence (RECURRENCE-ID) in the same file"
jq -e --arg w "$work_cal" '.format == 1 and .source == "android-calendar" and .id == $w and .name == "Smoke Work" and .color == "#3366cc"' \
  "$work/pim-calendars/$cdir/calendar.json" >/dev/null && pass "calendar.json: name, colour, account" || fail "calendar.json" "$(cat "$work/pim-calendars/$cdir/calendar.json")"

# The web snapshot browser: an account as one .vcf, a calendar as one .ics.
sid=$(api GET "/api/folders/$cf/snapshots" | jq -r '.[0].id')
curl -fsS -H "Authorization: Bearer $token" "$server/api/folders/$cf/snapshots/$sid/merged?path=$(enc "$adir")" -o "$work/account.vcf" \
  && [[ $(grep -c '^BEGIN:VCARD' "$work/account.vcf") -eq 2 ]] && pass "the web UI downloads the account as one .vcf (2 cards)" || fail "merged vcf" "$(head -c 300 "$work/account.vcf")"
sid=$(api GET "/api/folders/$kf/snapshots" | jq -r '.[0].id')
curl -fsS -H "Authorization: Bearer $token" "$server/api/folders/$kf/snapshots/$sid/merged?path=$(enc "$cdir")" -o "$work/calendar.ics" \
  && [[ $(grep -c '^BEGIN:VCALENDAR' "$work/calendar.ics") -eq 1 && $(grep -c '^BEGIN:VEVENT' "$work/calendar.ics") -eq 3 && $(grep -c '^BEGIN:VTIMEZONE' "$work/calendar.ics") -eq 1 ]] \
  && grep -q '^X-WR-CALNAME:Smoke Work' "$work/calendar.ics" && pass "and the calendar as one .ics (one VCALENDAR, its zone once, 3 VEVENTs)" || fail "merged ics" "$(head -c 400 "$work/calendar.ics")"

# A changed contact starts a backup by itself (the contacts content trigger).
bg_has '.works | any((.tags | split(",") | index("contacts-trigger")) and .state == "ENQUEUED")' \
  && pass "the contacts content trigger is scheduled" || fail "contacts trigger" "$(bgstate | jq -c '[.works[] | {tags, state}]')"
before=$(api GET "/api/folders/$cf/snapshots" | jq length)
adb shell input keyevent KEYCODE_HOME
grant WRITE_CONTACTS
edit=$(ctl bg-pim-edit)
revoke WRITE_CONTACTS; grant READ_CONTACTS
more_snapshots() { [[ $(api GET "/api/folders/$cf/snapshots" | jq length) -gt $before ]]; }
wait_for 240 more_snapshots && logged "Contacts changed: backing up" \
  && pass "Carol's new number ($edit) started a backup by itself: $(api GET "/api/folders/$cf" | jq -r '.devices[0].lastSyncMessage')" \
  || fail "content-trigger run" "$(adb logcat -d -s ASync | grep -i 'contact' | tail -5)"
restore_offline phone-contacts "$contacts_key" "$work/pim-contacts2" && grep -q 'TEL;TYPE=cell:+1 555 0299' "$work/pim-contacts2/$adir/Carol Smoke~$carol.vcf" \
  && cmp -s "$work/pim-contacts/$adir/Дмитро Коваль~$dmytro.vcf" "$work/pim-contacts2/$adir/Дмитро Коваль~$dmytro.vcf" \
  && pass "the new snapshot has the new number; Дмитро's file is byte-identical (deduplicated)" || fail "changed contact" "$(cat "$work/pim-contacts2/$adir/Carol Smoke~$carol.vcf" 2>&1 | head -12)"
wait_for 30 bg_has '.works | any((.tags | split(",") | index("contacts-trigger")) and .state == "ENQUEUED")' \
  && pass "and the next trigger is waiting" || fail "next contacts trigger" "$(bgstate | jq -c '[.works[] | {tags, state}]')"

# Wipe the account and the calendar on the phone, then restore. The wipe trips the triggers: no empty snapshot over the good one.
front_app
wait_for 60 idle || true
grant WRITE_CONTACTS WRITE_CALENDAR
w1=$(ctl "bg-pim-wipe contacts $smoke_account"); w2=$(ctl "bg-pim-wipe calendar $work_cal")
revoke WRITE_CONTACTS WRITE_CALENDAR; grant READ_CONTACTS READ_CALENDAR
sleep 2
front_app
[[ "$(ctl "bg-pim-dump contacts $smoke_account")" == "[]" && "$(ctl "bg-pim-dump calendar $work_cal")" == "[]" ]] \
  && pass "wiped on the phone: $w1 contacts, $w2 events" || fail "wipe" "$(ctl "bg-pim-dump contacts $smoke_account")"
snapshots_before=$(api GET "/api/folders/$cf/snapshots" | jq length)
r=$(sync_phone "$cf" 120)
jq -e '(.ok | not) and (.message | test("No snapshot was made"))' <<<"$r" >/dev/null && [[ $(api GET "/api/folders/$cf/snapshots" | jq length) -eq $snapshots_before ]] \
  && pass "a backup of the emptied account is refused, no empty snapshot: $(jq -r .message <<<"$r" | cut -c1-90)…" || fail "empty account" "$r"
c=$(ctl "pim-restore-choose $cf")
jq -e '.choosingRestore and (.choices[0] | test("this phone")) and (.targets[0] == "The accounts they came from") and (.targets | any(test("smoke@example.com")))' <<<"$c" >/dev/null \
  && pass "Restore into the contacts… offers the snapshots and where to: $(jq -c .targets <<<"$c")" || fail "restore choice" "$c"
ctl "show RestoreChoice" >/dev/null
shot contacts-restore
write_granted() { adb shell dumpsys package "$pkg" | grep -q 'android.permission.WRITE_CONTACTS: granted=true'; }
write_granted && fail "WRITE_CONTACTS granted before the restore" || true
ctl "pim-restore-confirm $cf" >/dev/null
sleep 2; dismiss_anr
# Android grants WRITE_CONTACTS without a dialog once READ_CONTACTS (the same permission group) is granted; tap Allow if one shows.
tap_text '^allow$' 5 || true
wait_for 20 write_granted && pass "WRITE_CONTACTS asked only now, for the restore (granted)" || fail "write permission" "$(adb shell dumpsys package "$pkg" | grep CONTACTS)"
restored() { pim "$1" | jq -e '.restore.running == false and .restore.done == .restore.total and .restore.total > 0' >/dev/null; }
wait_for 120 restored contacts && p=$(pim contacts) && jq -e '.restore.restored == 2 and .restore.ok' <<<"$p" >/dev/null \
  && pass "restored into the account: $(jq -r .restore.message <<<"$p")" || fail "contacts restore" "$(pim contacts)"
d=$(ctl "bg-pim-dump contacts $smoke_account")
jq -e 'length == 2 and (.[] | select(.name == "Carol Smoke") | .phones == ["+1 555 0299"] and .emails == ["carol@example.com"] and .photo > 0)
       and any(.[]; .name == "Дмитро Коваль")' <<<"$d" >/dev/null && pass "Carol (new number, email, photo) and Дмитро are back in smoke@example.com" || fail "restored contacts" "$d"
ctl "bg-pim-restore $cf - -" >/dev/null
sleep 1
wait_for 60 restored contacts && p=$(pim contacts) && jq -e '.restore.restored == 0 and .restore.skipped == 2' <<<"$p" >/dev/null \
  && pass "a second restore skips what is there: $(jq -r .restore.message <<<"$p")" || fail "second contacts restore" "$(pim contacts)"
[[ $(ctl "bg-pim-dump contacts local" | jq length) -ge 2 ]] && pass "the phone's own contacts (Ann, Bob) were never touched" || fail "local contacts" "$(ctl "bg-pim-dump contacts local")"

grant WRITE_CALENDAR
ctl "bg-pim-restore $kf - -" >/dev/null
sleep 1
wait_for 120 restored calendars && p=$(pim calendars) && jq -e '.restore.restored == 2 and .restore.ok' <<<"$p" >/dev/null \
  && pass "the calendar restored: $(jq -r .restore.message <<<"$p")" || fail "calendar restore" "$(pim calendars)"
d=$(ctl "bg-pim-dump calendar $work_cal")
jq -e --arg u "android:event:$standup" '(.[] | select(.title == "Standup") | .rRule == "FREQ=WEEKLY;BYDAY=MO;COUNT=10" and .timeZone == "Europe/Kyiv" and .alarms == 1
        and .uid == $u and (.exceptions | length == 1 and .[0].title == "Standup (moved)"))
       and any(.[]; .title == "Review")' <<<"$d" >/dev/null \
  && pass "Standup is back with its rule, zone, reminder, UID and moved occurrence; Review too" || fail "restored events" "$d"
ctl "bg-pim-restore $kf - -" >/dev/null
sleep 1
wait_for 60 restored calendars && p=$(pim calendars) && jq -e '.restore.restored == 0 and .restore.skipped == 2' <<<"$p" >/dev/null \
  && pass "a second calendar restore skips them (by UID)" || fail "second calendar restore" "$(pim calendars)"

# Tidy up for the restart check.
api DELETE "/api/folders/$cf" >/dev/null && api DELETE "/api/folders/$kf" >/dev/null && pass "the Contacts and Calendars folders deleted in the web UI" || fail "delete pim folders"
wait_state '((.folders | any(.name == "Phone contacts")) or (.folders | any(.name == "Phone calendars"))) | not' 30 || fail "pim folders gone" "$(state)"
