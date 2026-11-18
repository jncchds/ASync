# Sourced by scripts/smoke-ios.sh (it needs that script's helpers, $udid, $device, $backend, $work, $bundle and the running
# app): contacts and calendar backups on the iPhone (task 24). Shows the permissions' explanations, grants access (simctl's grant,
# else rows in the simulator's TCC database written with the simulator shut down, as task 23 found for photos), seeds the address
# book and a calendar through the frameworks (three contacts in a group, one with a photo and custom labels; an all-day event, a
# weekly series with one occurrence moved and one deleted, an event with an alarm), checks the framework's label constants against
# the mapping's, makes a Contacts and a Calendars folder in the web UI's API, reads the iPhone's live lists over the hub, backs both
# up, checks the snapshots' .vcf / .ics and account.json / calendar.json (sources ios-contacts / ios-eventkit) offline with async-cli,
# changes a contact and sees the change observer start a backup in which only that card changed, then deletes every contact and
# event and restores them, and a second restore adds nothing. iOS's own questions are shown at the end of smoke-ios.sh.
# shellcheck shell=bash disable=SC2015,SC2154

section "contacts and calendars (task 24)"
note() { printf '  \033[33m·\033[0m %s\n' "$1"; }
pim() { ctl "pim $1"; }
pim_access() { pim "$1" | jq -r .access; }
access_full() { [[ "$(pim_access contacts)" == Full && "$(pim_access calendars)" == Full ]]; }
unfold() { tr -d '\r' <"$1" | sed -e ':a' -e 'N' -e '$!ba' -e 's/\n //g'; }

mk_folder() { api POST /api/folders "{\"name\":\"$1\",\"mode\":\"Backup\",\"encryption\":\"Transport\",\"kind\":\"$2\",\"storageBackendId\":$backend,\"rootPath\":\"$3\",\"intervalMinutes\":60,\"retention\":{\"last\":10}}"; }
created=$(mk_folder "iPhone contacts" Contacts iphone-contacts); cf=$(jq -r .id <<<"$created"); contacts_key=$(jq -r .recoveryKey <<<"$created")
created=$(mk_folder "iPhone calendars" Calendars iphone-calendars); kf=$(jq -r .id <<<"$created"); calendars_key=$(jq -r .recoveryKey <<<"$created")
[[ "$cf" =~ ^[0-9]+$ && "$kf" =~ ^[0-9]+$ ]] && pass "a Contacts and a Calendars folder made in the web UI ($cf, $kf)" || fail "pim folders" "$created"
attach=$(api PUT "/api/folders/$cf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\"}" 2>&1 && api PUT "/api/folders/$kf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\"}" 2>&1) \
  && pass "the iPhone attached to both (the server takes iOS as a phone)" || fail "attach the iPhone" "$attach"
wait_state '(.folders | any(.name == "iPhone contacts")) and (.folders | any(.name == "iPhone calendars"))' 30 \
  && pass "both are on the iPhone's dashboard" || fail "pim folders on the iPhone" "$(state)"

# Without access: the explanations in iOS's words, and the agent asks nothing of the frameworks.
p=$(ctl "pim-dialog $cf")
jq -e '.needsPermission and .kind == "Contacts" and (.note | test("Allow Full Access")) and (.note | test("notes"))' <<<"$p" >/dev/null \
  && pass "without permission the Contacts screen explains why ASync asks (and that notes stay on the iPhone)" || fail "contacts explanation" "$p"
shot contacts-permission
ctl back >/dev/null
p=$(ctl "pim-dialog $kf")
jq -e '.needsPermission and .kind == "Calendars" and (.note | test("Add Events Only"))' <<<"$p" >/dev/null \
  && pass "the Calendars screen too (\"Allow Full Access\", not \"Add Events Only\")" || fail "calendars explanation" "$p"
shot calendars-permission
ctl back >/dev/null
[[ "$(pim_access contacts)" == None && "$(pim_access calendars)" == None ]] && pass "and the agent reports no access to either" || fail "agent access" "$(pim contacts) $(pim calendars)"

# Access as the person's answer would give it (simctl cannot tap iOS's questions): simctl's grant first, else TCC rows of auth
# version 2 written with the simulator shut down (task 23's finding for PhotoKit). privacy.log has the rows of each step.
tcc_db="$HOME/Library/Developer/CoreSimulator/Devices/$udid/data/Library/TCC/TCC.db"
pim_rows() {
  { echo "── $1"; sqlite3 "$tcc_db" "SELECT service, client, auth_value, auth_reason, auth_version FROM access WHERE client = '$bundle' AND service IN ('kTCCServiceAddressBook', 'kTCCServiceCalendar')"; } >>"$logs/privacy.log" 2>&1 || true
}
pim_tcc() { # "SERVICE=VALUE …" RUN: the rows set while the simulator is shut down, then booted and the app launched
  local run="$1"; shift
  xcrun simctl shutdown "$udid"
  local kv service value
  for kv in "$@"; do
    service=${kv%%=*}; value=${kv#*=}
    sqlite3 "$tcc_db" "DELETE FROM access WHERE client = '$bundle' AND service = '$service';
      INSERT INTO access (service, client, client_type, auth_value, auth_reason, auth_version, flags) VALUES ('$service', '$bundle', 0, $value, 2, 2, 0);" >>"$logs/privacy.log" 2>&1
  done
  pim_rows "written: $*"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b >/dev/null
  launch "$run"
  ctl "notifications off" >/dev/null
  wait_state '.page == "DashboardViewModel" and .state == "Connected"' 60 || true
  echo "app: contacts $(pim contacts | jq -c '{access, authorization}') calendars $(pim calendars | jq -c '{access, authorization}')" >>"$logs/privacy.log"
}
pim_rows "before"
xcrun simctl privacy "$udid" grant contacts "$bundle" >>"$logs/privacy.log" 2>&1 || note "simctl privacy grant contacts: $(tail -1 "$logs/privacy.log")"
xcrun simctl privacy "$udid" grant calendar "$bundle" >>"$logs/privacy.log" 2>&1 || note "simctl privacy grant calendar: $(tail -1 "$logs/privacy.log")"
pim_rows "after simctl privacy grant"
launch 8
ctl "notifications off" >/dev/null
wait_state '.page == "DashboardViewModel" and .state == "Connected"' 60 || true
granted=""
if wait_for 20 access_full; then
  granted="xcrun simctl privacy … grant contacts / calendar"
else
  note "simctl's grants left contacts $(pim contacts | jq -r .authorization), calendars $(pim calendars | jq -r .authorization): TCC rows of auth version 2"
  pim_tcc 8b kTCCServiceAddressBook=2 kTCCServiceCalendar=2
  wait_for 20 access_full && granted="TCC rows of auth version 2"
fi
if [[ -n "$granted" ]]; then
  pass "full access to contacts and calendars: $granted"
else
  fail "contacts / calendars access" "contacts $(pim contacts | jq -c '{access, authorization}') calendars $(pim calendars | jq -c '{access, authorization}') $(cat "$logs/privacy.log")"
  api DELETE "/api/folders/$cf" >/dev/null; api DELETE "/api/folders/$kf" >/dev/null
  return 0
fi

# The framework's own label constants are what the mapping assumes (_$!<Home>!$_ …).
labels=$(ctl pim-labels)
jq -e '.Home == "_$!<Home>!$_" and .Mobile == "_$!<Mobile>!$_" and .iPhone == "iPhone" and .Main == "_$!<Main>!$_" and .HomeFAX == "_$!<HomeFAX>!$_"
       and .HomePage == "_$!<HomePage>!$_" and .Anniversary == "_$!<Anniversary>!$_" and .iCloud == "iCloud" and .Spouse == "_$!<Spouse>!$_"' <<<"$labels" >/dev/null \
  && pass "the framework's label constants are the forms the mapping reads and writes" || fail "label constants" "$labels"
note "label constants: $(jq -c . <<<"$labels")"

seed=$(ctl pim-seed)
jq -e '.contacts == 3 and (.calendar | length > 0) and (.series | length > 0)' <<<"$seed" >/dev/null \
  && pass "seeded through the frameworks: Alice (photo, iPhone and custom labels), Bob, Smoke Bakery in \"Smoke friends\"; a calendar with Smoke holiday (all-day), Smoke standup (weekly, one moved, one deleted), Smoke dentist (an alarm)" \
  || fail "seed" "$seed"
smoke_cal=$(jq -r .calendar <<<"$seed")
monday=$(jq -r .monday <<<"$seed")
day() { python3 -c 'import sys,datetime;print((datetime.date.fromisoformat(sys.argv[1])+datetime.timedelta(days=int(sys.argv[2]))).strftime("%Y%m%d"))' "$monday" "$1"; }

# The web UI's lists come live from the iPhone over the hub.
lists_live() { api GET "/api/devices/$device/collections?kind=Contacts" | jq -e '.live and (.collections | any(.count >= 3))'; }
wait_for 30 lists_live && lists=$(api GET "/api/devices/$device/collections?kind=Contacts") \
  && pass "the account list comes live from the iPhone: $(jq -c '[.collections[] | "\(.name) (\(.account)) \(.count)"]' <<<"$lists")" \
  || fail "contact accounts" "$(api GET "/api/devices/$device/collections?kind=Contacts")"
account=$(jq -r '[.collections[] | select(.count >= 3)][0].id' <<<"${lists:-{\}}")
cals=$(api GET "/api/devices/$device/collections?kind=Calendars")
jq -e --arg c "$smoke_cal" '.live and (.collections | any(.id == $c and .name == "ASync smoke" and .count == 3 and .color == "#8c3d8c")) and (.collections | all(.name != "Birthdays"))' <<<"$cals" >/dev/null \
  && pass "the calendar list too (no Birthdays): $(jq -c '[.collections[] | "\(.name) \(.account) \(.count) \(.color)"]' <<<"$cals")" || fail "calendars" "$cals"
api PUT "/api/folders/$kf/devices" "{\"deviceId\":\"$device\",\"localPath\":\"\",\"collections\":{\"all\":false,\"collections\":[{\"id\":\"$smoke_cal\",\"name\":\"ASync smoke\"}],\"pastDays\":730}}" >/dev/null
has_sel() { pim "$1" | jq -e --argjson f "$2" --arg s "$3" '.folders[] | select(.id == $f) | .selection == $s' >/dev/null; }
wait_for 30 has_sel calendars "$kf" "1 calendar: ASync smoke · events of the last 2 years" \
  && pass "ASync smoke chosen in the web UI; the iPhone's agent has it" || fail "selection on the iPhone" "$(pim calendars | jq -c .folders)"

r=$(sync_phone "$cf" 180)
jq -e '.ok and (.message | test("[0-9]+ contacts in [0-9]+ account"))' <<<"$r" >/dev/null && pass "contacts backed up: $(jq -r .message <<<"$r")" || fail "contacts backup" "$r"
r=$(sync_phone "$kf" 180)
jq -e '.ok and (.message | test("3 events in 1 calendar"))' <<<"$r" >/dev/null && pass "calendar backed up: $(jq -r .message <<<"$r")" || fail "calendar backup" "$r"
api GET "/api/folders/$kf" | jq -e '.devices[0].collectionCounts.items == 3 and .devices[0].collectionCounts.collections == 1' >/dev/null \
  && pass "the web UI shows 3 events in 1 calendar for the iPhone" || fail "counts on the server" "$(api GET "/api/folders/$kf")"
shot contacts-dashboard
ctl "pim-dialog $cf" >/dev/null
shot contacts-accounts
ctl back >/dev/null
ctl "pim-dialog $kf" >/dev/null
shot calendars-calendars
ctl back >/dev/null

# async-cli, offline: the repository on the server's storage and the folders' recovery keys.
restore_offline() { # ROOT KEY TARGET
  rm -rf "$3"
  printf '%s' "$2" >"$work/$1-key"
  "$work/cli/async-cli" restore --repo "$work/data/ios/$1" --key-file "$work/$1-key" --snapshot latest --target "$3" >"$logs/$1-restore.log" 2>&1
}
restore_offline iphone-contacts "$contacts_key" "$work/pim-contacts" && restore_offline iphone-calendars "$calendars_key" "$work/pim-calendars" \
  && pass "async-cli restored both snapshots offline (repository + recovery key)" || fail "cli pim restore" "$(cat "$logs"/iphone-*-restore.log)"
alice=$(find "$work/pim-contacts" -name 'Alice Smoke~*.vcf' | head -1)
adir=$(dirname "${alice:-$work/pim-contacts/none/x}")
if [[ -n "$alice" ]]; then
  u=$(unfold "$alice")
  ok=1
  for line in "VERSION:4.0" "FN:Alice Smoke" "N:Smoke;Alice;;;" "NICKNAME:Al" "BDAY:19850415" "TEL;TYPE=cell:+380 50 111 2233" "X-ABLABEL:iPhone" \
              "X-ABLABEL:Dacha" "EMAIL;TYPE=home:alice@example.com" "ORG:Smoke Labs" "TITLE:Engineer" "IMPP:skype:alice.smoke" \
              "IMPP:x-social-twitter:alicesmoke" "RELATED;TYPE=spouse;VALUE=text:Bob Smoke" "CATEGORIES:Smoke friends"; do
    grep -qxF -- "$line" <<<"$u" || { ok=0; fail "Alice's card: no $line" "$u"; break; }
  done
  ((ok)) && grep -Eq '^UID:[0-9A-F-]{36}(:ABPerson)?$' <<<"$u" && grep -q '^PHOTO:data:image/[a-z]*;base64,' <<<"$u" && ! grep -q '^NOTE' <<<"$u" \
    && pass "Alice's vCard: name, birthday, labels (cell, iPhone, Dacha), email, organisation, Skype, Twitter, spouse, group, photo; no note" \
    || { ((ok)) && fail "Alice's card (UID, photo or note)" "$u"; }
  jq -e '.format == 1 and .source == "ios-contacts" and .kind == "contacts" and (.typeLabel | test("iPhone|iCloud|CardDAV|Exchange|Contacts"))' "$adir/account.json" >/dev/null \
    && pass "account.json: source ios-contacts, $(jq -r '"\(.name) (\(.typeLabel))"' "$adir/account.json")" || fail "account.json" "$(cat "$adir/account.json")"
else
  fail "no Alice in the contacts snapshot" "$(cd "$work/pim-contacts" && find . -type f)"
fi
cdir=$(find "$work/pim-calendars" -mindepth 1 -maxdepth 1 -type d -name 'ASync smoke~*' | head -1)
files=$(cd "${cdir:-/nonexistent}" 2>/dev/null && find . -type f | sed 's|^\./||; s|~[0-9a-f]*\.ics$|.ics|' | LC_ALL=C sort | tr '\n' ' ')
[[ "$files" == "Smoke dentist.ics Smoke holiday.ics Smoke standup.ics calendar.json " ]] \
  && pass "the calendar's tree: ASync smoke~<id>/calendar.json and <title>~<id>.ics" || fail "calendar tree" "$files"
jq -e --arg c "$smoke_cal" '.format == 1 and .source == "ios-eventkit" and .kind == "calendar" and .id == $c and .name == "ASync smoke" and .color == "#8c3d8c"' \
  "$cdir/calendar.json" >/dev/null && pass "calendar.json: source ios-eventkit, name, colour, $(jq -r .account "$cdir/calendar.json")" || fail "calendar.json" "$(cat "$cdir/calendar.json" 2>&1)"
st=$(unfold "$(find "$cdir" -name 'Smoke standup~*.ics' | head -1)")
ok=1
for line in "BEGIN:VTIMEZONE" "TZID:Europe/Kyiv" "DTSTART;TZID=Europe/Kyiv:$(day 0)T100000" "DURATION:PT30M" "EXDATE;TZID=Europe/Kyiv:$(day 14)T100000" \
            "RECURRENCE-ID;TZID=Europe/Kyiv:$(day 7)T100000" "DTSTART;TZID=Europe/Kyiv:$(day 7)T120000" "SUMMARY:Smoke standup (moved)"; do
  grep -qxF -- "$line" <<<"$st" || { ok=0; fail "standup ics: no $line" "$st"; break; }
done
((ok)) && grep -Eq '^RRULE:FREQ=WEEKLY;COUNT=20;BYDAY=MO(;WKST=[A-Z]{2})?$' <<<"$st" \
  && pass "Smoke standup.ics: its zone, local start, rule, the deleted occurrence (EXDATE) and the moved one (RECURRENCE-ID) in one file" \
  || { ((ok)) && fail "standup rule" "$st"; }
grep -qxF "TRIGGER:-PT30M" <<<"$(unfold "$(find "$cdir" -name 'Smoke dentist~*.ics' | head -1)")" && grep -qE '^DTSTART;VALUE=DATE:[0-9]{8}$' <<<"$(unfold "$(find "$cdir" -name 'Smoke holiday~*.ics' | head -1)")" \
  && pass "Smoke dentist has its alarm (TRIGGER:-PT30M), Smoke holiday is a DATE (all-day)" || fail "dentist / holiday" "$(cat "$cdir"/Smoke\ d*.ics "$cdir"/Smoke\ h*.ics 2>&1)"

# A changed contact: the change observer (CNContactStoreDidChange, debounced) starts a backup in which only that card changed.
before=$(api GET "/api/folders/$cf/snapshots" | jq length)
triggered=$(pim contacts | jq .triggered)
ctl pim-edit >/dev/null
more_snapshots() { [[ $(api GET "/api/folders/$cf/snapshots" | jq length) -gt $before ]]; }
if wait_for 90 more_snapshots && [[ $(pim contacts | jq .triggered) -gt $triggered ]]; then
  pass "Bob's new number started a backup by itself (CNContactStoreDidChange): $(api GET "/api/folders/$cf" | jq -r '.devices[0].lastSyncMessage')"
else
  fail "change-observer run" "$(pim contacts | jq -c '{triggered, folders}')"
  sync_phone "$cf" 120 >/dev/null
fi
restore_offline iphone-contacts "$contacts_key" "$work/pim-contacts2"
bob=$(find "$work/pim-contacts2" -name 'Bob Smoke~*.vcf' | head -1)
grep -q 'TEL;TYPE=cell:+380 67 999 0000' "$bob" && cmp -s "$alice" "$work/pim-contacts2/${alice#"$work/pim-contacts/"}" \
  && pass "the new snapshot has Bob's new number; Alice's card (with her photo) is byte-identical" || fail "changed contact" "$(cat "$bob" 2>&1 | head -12)"
last=$(api GET "/api/folders/$cf" | jq -r '.devices[0].lastSyncMessage')
[[ "$last" =~ \ 0\ new,\ 1\ changed ]] && pass "only that card was read and changed: $last" || note "the run's own words: $last"

# Everything deleted (as in another app), then restored; the deletions trip the observers: no empty snapshot over the good one.
w1=$(ctl "pim-wipe contacts"); w2=$(ctl "pim-wipe calendar")
d=$(ctl "pim-dump contacts")
jq -e '.contacts | length == 0' <<<"$d" >/dev/null && [[ "$(ctl "pim-dump calendar" | jq '.events | length')" == 0 ]] \
  && pass "every contact and event deleted ($(jq -r .deleted <<<"$w1") contacts, $(jq -r .deleted <<<"$w2") events)" || fail "wipe" "$d"
sleep 8
snapshots_before=$(api GET "/api/folders/$cf/snapshots" | jq length)
r=$(sync_phone "$cf" 120)
jq -e '(.ok | not) and (.message | test("No snapshot was made"))' <<<"$r" >/dev/null && [[ $(api GET "/api/folders/$cf/snapshots" | jq length) -eq $snapshots_before ]] \
  && pass "a backup of the emptied address book is refused, no empty snapshot" || fail "empty address book" "$r"
c=$(ctl "pim-restore-choose $cf")
jq -e '.choosingRestore and (.targets[0] == "The accounts they came from")' <<<"$c" >/dev/null \
  && pass "Restore into the contacts… offers the snapshots and where to: $(jq -c .targets <<<"$c")" || fail "restore choice" "$c"
ctl "show RestoreChoice" >/dev/null || true
shot contacts-restore
ctl "pim-restore-cancel $cf" >/dev/null
restored() { pim "$1" | jq -e '.restore.running == false and .restore.done == .restore.total and .restore.total > 0' >/dev/null; }
contacts_total=$(jq '.contacts | length' <<<"$(ctl "pim-dump contacts")")
ctl "pim-restore $cf - -" >/dev/null
wait_for 120 restored contacts && p=$(pim contacts) && jq -e '.restore.restored >= 3 and .restore.skipped == 0' <<<"$p" >/dev/null \
  && pass "restored into the address book: $(jq -r .restore.message <<<"$p")" || fail "contacts restore" "$(pim contacts)"
d=$(ctl "pim-dump contacts")
jq -e '(.contacts | any(.given == "Alice" and .photo and (.phones | length == 3)))
       and (.contacts | any(.given == "Bob" and (.phones[0].value == "+380 67 999 0000")))
       and (.contacts | any(.org == "Smoke Bakery"))
       and (.groups | any(.name == "Smoke friends" and .members == 2))' <<<"$d" >/dev/null \
  && pass "Alice (photo, three numbers), Bob (his new number), Smoke Bakery are back, with the group" || fail "restored contacts" "$d"
contacts_after=$(jq '.contacts | length' <<<"$d")
ctl "pim-restore $cf - -" >/dev/null
sleep 1
wait_for 60 restored contacts && p=$(pim contacts) && jq -e '.restore.restored == 0' <<<"$p" >/dev/null && [[ "$(ctl "pim-dump contacts" | jq '.contacts | length')" == "$contacts_after" ]] \
  && pass "a second restore adds nothing: $(jq -r .restore.message <<<"$p")" || fail "second contacts restore" "$(pim contacts)"
note "contacts before the restore: $contacts_total, after: $contacts_after"

ctl "pim-restore $kf - -" >/dev/null
wait_for 120 restored calendars && p=$(pim calendars) && jq -e '.restore.restored == 3' <<<"$p" >/dev/null \
  && pass "restored into ASync smoke: $(jq -r .restore.message <<<"$p")" || fail "calendar restore" "$(pim calendars)"
d=$(ctl "pim-dump calendar")
jq -e --arg ex "Europe/Kyiv;$(day 14)T100000" '(.events | length == 3)
       and (.events | any(.title == "Smoke standup" and .exDate == $ex and (.exceptions | length == 1) and .exceptions[0].title == "Smoke standup (moved)"))
       and (.events | any(.title == "Smoke dentist" and .alarms == [30]))
       and (.events | any(.title == "Smoke holiday" and .allDay))' <<<"$d" >/dev/null \
  && pass "the series is back with its deleted and its moved occurrence, the alarm, the all-day event" || fail "restored events" "$d"
ctl "pim-restore $kf - -" >/dev/null
sleep 1
wait_for 60 restored calendars && p=$(pim calendars) && jq -e '.restore.restored == 0 and .restore.skipped == 3' <<<"$p" >/dev/null \
  && [[ "$(ctl "pim-dump calendar" | jq '.events | length')" == 3 ]] \
  && pass "a second restore skips all three, nothing duplicated: $(jq -r .restore.message <<<"$p")" || fail "second calendar restore" "$(pim calendars)"

# Limited contacts access (iOS 18): the TCC row's value 3, else the screens forced for the screenshot.
if [[ -n "$shots" ]]; then
  pim_tcc 9 kTCCServiceAddressBook=3 kTCCServiceCalendar=2
  if [[ "$(pim_access contacts)" == Partial ]]; then
    pass "limited contacts access (TCC auth_value 3): the agent reports Partial"
  else
    note "limited contacts access could not be set ($(pim contacts | jq -r .authorization)): the state is forced for the screenshot"
    ctl "pim-force contacts partial" >/dev/null
  fi
  p=$(ctl "pim-dialog $cf")
  jq -e '.accessText | test("Limited Access")' <<<"$p" >/dev/null && pass "limited access: the screen says only the shared contacts are backed up" || fail "limited text" "$p"
  shot contacts-limited
  ctl back >/dev/null
  ctl "pim-force contacts off" >/dev/null
  pim_tcc 10 kTCCServiceAddressBook=2 kTCCServiceCalendar=2
fi

api DELETE "/api/folders/$cf" >/dev/null && api DELETE "/api/folders/$kf" >/dev/null && pass "the Contacts and Calendars folders deleted in the web UI" || fail "delete pim folders"
wait_state '((.folders | any(.name == "iPhone contacts")) or (.folders | any(.name == "iPhone calendars"))) | not' 30 || fail "pim folders gone" "$(state)"
