### Contacts and calendars (task 20)

Asked for: "back up the phone's contacts and calendars, choosing what from the web the same way photos choose albums; restore is an
import". Agreed with the person: the model fits iOS (Contacts framework, EventKit) without server or format changes, so the platform part
is behind interfaces with fakes for tests. Code: `ASync.Contracts/Collections.cs`, `ASync.Client.Core/Collections/` (`Contacts.cs`,
`Calendars.cs`: the platform-neutral models and `IContactsSource` / `ICalendarSource`; `VCard.cs`, `ICalendar.cs`: the writers and
readers; `CollectionLayout.cs`; `CollectionTree.cs`; `CollectionRestorer.cs`), the Android head's `AndroidCollections.cs`,
`ASync.Api/Services/DeviceCollectionsService.cs`, `Controllers/CollectionsController.cs`, `SnapshotService.cs`'s `CollectionMerge`, the
web UI's `components/CollectionSelectionFields.tsx`, the app's `CollectionsViewModel` / `Views/CollectionsView.axaml`.

**Model** (migration `ContactsCalendarsCollections`; see "Data model"):
- Two folder kinds, **Contacts** and **Calendars**, Backup only (like Photos: Transport or end-to-end, retention, prune, check, recovery
  key; several phones can share one, each device its own snapshots). Phones only (`FolderService.HasPhotoLibrary`: Android, iOS); a
  computer gets "Only phones back up their contacts / calendars…" (400).
- Per device a **`CollectionSelectionDto`**: `all`, or `collections` = stable ids with the names they had when chosen (contacts: accounts;
  calendars: calendars), and for calendars `pastDays` ("events since": 730 = the last two years by default, null = everything; a
  rolling window, counted back from each run). `AttachDeviceRequest`, `UpdateDeviceFolderRequest`, `DeviceCreateFolderRequest`,
  `DeviceJoinFolderRequest` carry it as `Collections` (null keeps the saved one, everything for a new membership); `FolderDeviceDto` and
  `DeviceFolderDto` show it. `FolderService.ApplyPlace` validates (at least one when not `all`, at most 500, ids up to 300 characters,
  `pastDays` 1–36500) and stores the local path empty.
- **Decision: the selection reuses task 19's column** `FolderDevice.MediaSelectionJson` (jsonb): the folder's kind says which shape it
  holds (`MediaSelectionDto` for Photos, `CollectionSelectionDto` for Contacts and Calendars; `FolderService.Media` / `Collections` read
  it only for their kinds). It generalises cleanly (one membership has one kind), so no new column. The **counts** reuse
  `MediaPhotos` / `MediaVideos` / `MediaBytes` the same way: contacts or events, accounts or calendars, bytes, from
  `SyncReportRequest.Collections` (`CollectionCountsDto`), shown as `FolderDeviceDto.CollectionCounts`.
- **New column, really needed:** the last lists a device reported, for both kinds at once (a phone has both, and a photo album list
  too): `Device.CollectionListsJson` (jsonb, keyed by kind: `{collections, access, items, at}`; at most 1000 entries each; names and
  counts only).

**Lists for the web UI** (`GET /api/devices/{id}/collections?kind=Contacts|Calendars` → `DeviceCollectionsDto {live, collections, access,
reportedAt, message, items}`; people only, owner only (404), rate-limited like browsing): exactly the albums' mechanism (task 19) — the
SignalR client result `collections` (`CollectionsRequest {kind}` → `CollectionsReply`) on the device's newest connection, an asleep phone
woken with the `browse` push first, the same "Allow browsing folders from the web" switch, and the stored list with `live: false` and the
reason when it cannot be asked (offline, not woken, turned off, an older app, no answer). `DeviceBrowseState.Contacts` /
`.Calendars` (`MediaAccess`; null from older apps) say whether to ask. Devices report their lists after every backup of the kind
(`PUT /api/sync/collections`, device only). A `CollectionDto` is `{id, name, count, account, color, readOnly}`: for an account,
`account` is its type for people ("Google", "Phone", "SIM", "WhatsApp"); for a calendar, the account it belongs to and its colour.

**What is stored — the permanent format** (`CollectionLayout`, `VCard`, `ICalendar`, `CollectionInfoFile`; **part of the repository
format**: change only with a new `format` in the JSON files and a reader for the old one):

```
Contacts:   <account name>~<8 hex of SHA-256(account id)>/account.json
            <account name>~<…>/<display name>~<contact short id>.vcf       one vCard 4.0 per contact, its photo embedded
Calendars:  <calendar name>~<calendar short id>/calendar.json
            <calendar name>~<…>/<title>~<event short id>.ics              one VCALENDAR per event, its exceptions inside
```

- Names are made safe like a Photos snapshot's (`MediaLayout.Safe`, at most 80 characters; `account`, `calendar`, `contact`, `event`
  when nothing is left); the short id after the last `~` keeps two "John Smith" or two "Lunch" apart, and a renamed contact or event
  keeps its id in its name (a new path, the same chunks: nothing uploaded but the changed card). Android: an account's id is
  `<account type>:<account name>` (`local` for contacts in no account, the phone's own), a contact's short id its raw contact `_id`, a
  calendar's and an event's their `_id`.
- **account.json / calendar.json, format 1** (UTF-8 JSON, indented, fixed field order): `format, source, kind ("contacts" |
  "calendar"), id, name`, then for accounts `type, typeLabel`, for calendars `account, accountType, color (#rrggbb, when known),
  timeZone (when known)`, then `readOnly`. Readers ignore fields they do not know and refuse a higher format.
- **vCard 4.0 (RFC 6350), one card per file.** Properties in this fixed order: `BEGIN, VERSION:4.0, PRODID:-//ASync//Contacts 1//EN,
  UID` (the source's stable id: Android `android:raw:<_id>`), `FN` (the display name, else one made of the parts, else a phone or email,
  else "Unnamed"), `N` (family;given;middle;prefix;suffix — always written), `X-PHONETIC-LAST-NAME / -FIRST-NAME / -MIDDLE-NAME`,
  `NICKNAME`s, `BDAY` / `ANNIVERSARY` (basic ISO form, `19850415`, `--0601` without a year), other dates as `itemN.X-ABDATE` with
  `itemN.X-ABLABEL`, `TEL`s, `EMAIL`s, `ADR`s (pobox;neighbourhood;street;city;region;code;country, the formatted address as `LABEL`),
  `ORG` (company;department) with `TITLE` (grouped `itemN.` when there are several), `URL`s, `IMPP`s (`<scheme>:<handle>`: `skype:`,
  `xmpp:`, …; SIP addresses as `IMPP:sip:…`), `RELATED;VALUE=text`, `NOTE`, `CATEGORIES` (the contact's groups by title, not the
  system groups), `X-ASYNC-STARRED:TRUE`, `X-ASYNC-SOURCE-ID` (the sync server's id), `PHOTO:data:image/<type>;base64,…` (the largest
  photo the phone has: Android's display photo, else the thumbnail), `END`. Types are vCard `TYPE` values (`cell`, `work,fax`,
  `home`, …), `PREF=1` for the primary one, and labels the phone has but vCard has no type for ("Dacha", Android's "Car") follow the
  Apple convention every major address book reads: `item1.TEL:…` + `item1.X-ABLABEL:Dacha`. Text is escaped (`\\ \, \; \n`), lines
  end in CRLF and fold at 75 octets without splitting a UTF-8 sequence. **No `REV`** or other "made at" stamp.
- **iCalendar (RFC 5545), one VCALENDAR per event:** `VERSION:2.0, PRODID:-//ASync//Calendars 1//EN, CALSCALE:GREGORIAN`, a
  `VTIMEZONE` per zone used (sorted by id; generated from the system's zone data for the year of the event's first start: each
  transition that year as `STANDARD` / `DAYLIGHT` with `TZOFFSETFROM/TO`, and a yearly `RRULE` (nth or last weekday of the month) when
  the next year has the same one; no `TZNAME`, which depends on the system's language), then the event's `VEVENT`, then each exception's
  `VEVENT` in the order of the occurrences they replace. A VEVENT: `UID` (the event's UID where it has one — Android's `UID_2445`, set by
  invitations and by ASync's restores — else its id, `android:event:<_id>`), `DTSTAMP` (required by RFC 5545: the last modification
  where the provider knows it, else the creation, else the start — Android keeps neither, so the start: deterministic), `CREATED`,
  `LAST-MODIFIED`, `RECURRENCE-ID` (exceptions: the original occurrence, in the series' zone, or a DATE for all-day series), `DTSTART`,
  `DTEND` or (recurring) `DURATION` (normalised: Android's `P900S` becomes `PT15M`), `SUMMARY`, `LOCATION`, `DESCRIPTION`, `RRULE`,
  `EXRULE`, `RDATE`, `EXDATE` (as the provider stores them; Android's `<tzid>;` prefix becomes `TZID=`), `STATUS` (a cancelled
  occurrence is an exception with `STATUS:CANCELLED`), `TRANSP`, `CLASS`, `ORGANIZER`, `ATTENDEE`s (`CN`, `CUTYPE`, `ROLE`,
  `PARTSTAT`, as stored), `X-ASYNC-COLOR` (the event's own colour), `VALARM`s (`ACTION:DISPLAY`, `DESCRIPTION` = the title,
  `TRIGGER:-PT10M`, and `X-ASYNC-METHOD:email|sms|alarm` for Android's other reminder methods). Times: a timed event in a zone in
  local time with `TZID` (a weekly 10:00 stays 10:00 across daylight saving); all-day events as `VALUE=DATE` (Android keeps them at UTC
  midnight); times without a known zone in UTC (`…Z`).
- **Deterministic:** an unchanged contact or event gives identical bytes (tested), so it is stored once.
- `VCard.Parse` and `ICalendar.Parse` read what the writers write, and the usual vCard 3/4 and iCalendar of other programs well enough to
  import (vCard 3 `ENCODING=b` photos, `TYPE=CELL,VOICE`, several cards or VCALENDARs per file, VEVENTs with RECURRENCE-ID attached to
  their series by UID).

**Running** (`CollectionTree`, `SyncAgent.RunFolderAsync`): the normal `BackupJob` with a read-only tree over the source and the
membership's selection (like `MediaTree`), listed once when first used.
- **Incremental where the provider gives a version:** Android's raw contacts have `VERSION` (the provider bumps it on any change, its
  photo included). A contact whose version is unchanged is listed with the size it had (`GeneratedSizeCache`, `contacts-sizes.json` in
  the folder's state directory) and its contact's `CONTACT_LAST_UPDATED_TIMESTAMP`, so `BackupRunner` takes it from the parent snapshot
  **without generating it or reading its photo**; a new or changed one is generated while listing (to know its size) and kept in memory
  until read (up to 48 MB, generated again beyond). Losing the cache costs one full read. **Events have no version on Android** (no
  modification time either): every event is generated while listing and always read (`ILocalTree.AlwaysRead`), and an unchanged one
  dedups to the stored blob (cheap: provider queries, no files).
- The message has the counts ("Snapshot 1a2b3c4d: 312 contacts in 2 accounts (4.1 MB), …"; "1840 events in 3 calendars"), the report
  carries them, and the lists are reported afterwards.
- **No empty snapshot over a good one** (`BackupJob`): a selection that shows no contacts / events while this device has an earlier
  snapshot (the account removed, the calendar wiped) fails with "No snapshot was made: the last one keeps them".
- **Background (Android):** `BackgroundPlanner.HasPlace` takes both kinds (their periodic jobs with the run conditions);
  `CollectionTriggerWorker` (`contacts-trigger`, `calendars-trigger`) is a one-time WorkManager job with a content-URI trigger on
  `ContactsContract.AUTHORITY_URI` / `CalendarContract.CONTENT_URI` (descendants), a 10-second update delay (an edit, or a sync bringing
  many, is one run), at most 2 minutes, and the network; it runs every unpaused folder of its kind as an automatic run (run conditions
  apply) and appends the next trigger (`BackgroundWork.EnsureCollectionTriggers`: while there are such folders and ASync may read them).

**Android** (`AndroidContactsSource`, `AndroidCalendarSource`, `AndroidCollectionAccess`):
- Contacts are **raw contacts** (one account's record, not the merged contact the Contacts app shows), so every account backs up and
  restores on its own. Accounts are the `(account_type, account_name)` pairs of the raw contacts, plus the Google accounts of the phone
  (they take a restore while they hold none) and the phone itself (`local`, "Phone"); messaging apps' accounts (WhatsApp, Telegram,
  Viber, Skype, …) are read-only (backed up, not restored into). Data rows map both ways: name (and phonetic), nickname, phone (Android's
  20 types to vCard types, or a label), email, postal address, organisation, IM (protocol → scheme), website, event, relation, SIP,
  note, group membership (titles of non-system groups), photo (`DisplayPhoto` by `PHOTO_FILE_ID`, else the row's thumbnail).
- Calendars: `Calendars` rows (read-only below `CAL_ACCESS_CONTRIBUTOR`: holidays, contacts' birthdays, read-only shares), `Events`
  (`deleted = 0`; exceptions are the rows with `ORIGINAL_ID`; "events since" through `LAST_DATE`, null for a never-ending series),
  `Reminders` and `Attendees`.
- **Permissions:** READ_CONTACTS / READ_CALENDAR, explained in the app first (the Contacts or Calendars screen); WRITE_CONTACTS /
  WRITE_CALENDAR asked **only when a restore starts** (and the restore waits for the answer, a minute at most). Request codes 4/5 and
  6/7; answers and coming back to the front raise `Changed`.

**Restore** (`CollectionRestorer`, control op `restore-collections {folderId, snapshot?, target?}`, the card's *Restore into the
contacts… / calendars…*: first the snapshot (as for photos, `collection-snapshots`) and where to — *The accounts / calendars they came
from* (`target` null) or one of the phone's writable accounts / calendars): each directory of the snapshot is imported into the target,
or into the account or calendar its JSON names when the phone still has it (otherwise that directory is reported, not guessed). An
entry already there is skipped — a contact with the same id and name, or the same name, phone numbers and email addresses; an event with
the same UID (or id) and title, or the same title, start, length and rule — and **nothing on the phone is changed or deleted**: a restore
only inserts (Android: a raw contact with its data rows in one batch, the photo as the photo row's blob; an event with its reminders and
attendees, then its exceptions through `Events.CONTENT_EXCEPTION_URI`; the UID goes into `UID_2445`, so a second restore skips by UID).
Not restored: group memberships (the CATEGORIES stay in the file), the starred flag of other accounts' sync adapters, event colours. The
agent runs it; `AgentStatusDto.CollectionRestore` (a `MediaRestoreDto`) is shown on the card with a bar.
- `async-cli restore` writes the tree as it is: plain `.vcf` / `.ics` files any address book or calendar imports, plus the JSON. The
  web snapshot browser lists and downloads them, and **downloads an account as one .vcf, a calendar as one .ics** (done:
  `GET /api/folders/{id}/snapshots/{sid}/merged?path=<dir>`, also through a download link with `merge: true`; `CollectionMerge`: the
  cards one after the other, or one VCALENDAR with each zone once, every VEVENT, and `X-WR-CALNAME` from calendar.json).

**App** (`CollectionsViewModel`, `ShellViewModel.CollectionsDialog`): first, while ASync may not read them, why it asks and what it reads
(never changed or deleted, only to the own server's storage; adding asked only for a restore), *Allow access to contacts / calendars*
and *Open the app's settings*; then *All contacts* / *All calendars* or *Chosen accounts* / *Chosen calendars* (the phone's own list,
`collection-list`, with counts, the account and the calendar's colour; chosen ones the phone no longer lists are kept), and for calendars
*Events since* (the last year, 2 years, 5 years, everything). The card shows the selection, the last backup's counts and bytes, an access
notice and pill, *Back up now*, *Contacts… / Calendars…* and *Restore into the contacts… / calendars…*. *New folder* on a phone offers
*Contacts* and *Calendars* (named and rooted `contacts` / `calendars` unless typed otherwise); *Add to a folder* offers the owner's
Contacts and Calendars folders on a phone only. Control ops (protocol 1): `collection-list {kind}`, `set-collections {folderId,
collections}`, `collection-snapshots`, `restore-collections`, and `join-folder` with `collections` instead of `path`.

**Web UI:** the folder form's type picker has *Contacts* and *Calendars*; the device form of such a folder lists phones only and shows
`CollectionSelectionFields` instead of the path (live pill or "Reported 2 h ago", Refresh, chosen ones no longer listed kept and
marked, read-only calendars marked, *Events since*); the Folders page shows what each phone backs up and the last backup's counts; the
snapshot browser's account / calendar directories have *One .vcf* / *One .ics*.

**iOS mapping notes (later, no server or format change):** `IContactsSource` over the Contacts framework: `CNContainer`s are the accounts
(`identifier` → id; type local / Exchange / CardDAV → typeLabel; iCloud's is CardDAV), `CNContact.identifier` the contact id (`ShortId`
with `:` / `/` → `_`), its `CNLabeledValue`s map to TYPE or X-ABLABEL exactly as here (`CNLabelHome`, `CNLabelPhoneNumberMobile` = `cell`,
custom labels as labels), `imageData` → PHOTO, `CNGroup`s → CATEGORIES. iOS has no per-contact version: `Version` null (every contact
generated and always read, deduplicated) or `CNChangeHistoryFetchRequest` tokens later. iOS 18's limited contacts access =
`MediaAccess.Partial`. `ICalendarSource` over EventKit: `EKCalendar.calendarIdentifier` → id, `EKSource` → the account, `cgColor` → colour,
`allowsContentModifications` false = read-only; `EKEvent.calendarItemExternalIdentifier` → UID (ids are unstable across syncs there, so
UID is the key), `startDate` / `endDate` / `isAllDay` / `timeZone`, `recurrenceRules` → RRULE text, detached occurrences (EventKit lists
them as events with `isDetached`) → exceptions with `occurrenceDate`, `alarms` (relative offset) → alarms, `attendees` (`EKParticipant`
role / status / type) → attendees, `lastModifiedDate` → LastModified (so iOS events are incremental by time). The JSON's `source`
becomes `ios-contacts` / `ios-eventkit`. Change triggers: `CNContactStoreDidChange` and `EKEventStoreChanged` while running, a
BGProcessingTask otherwise.

**Tests:** `ASync.Tests`: `CollectionBackupTests` (fakes in `Client/FakeCollections.cs`: a vCard written exactly as the format says —
Unicode, escaping, folding at 75 octets, custom labels, photo — and read back to the same contact; vCard 3 of other programs; the
contacts tree by account with account.json byte for byte; a second run generating no unchanged contact (no photo read) and seeing a
changed one; without the size cache the same bytes; a deleted contact in the older snapshot; no permission stopping the run with the
reason; an emptied account refused without an empty snapshot; the tree read-only with its counts; restoring into a wiped account,
then skipping, by id on the same phone and by content into another account, an account the phone no longer has reported; a recurring
event with its zone (VTIMEZONE with the EU rules for Europe/Kyiv), rule, exceptions (moved and cancelled), attendees and alarms written
line for line, deterministic, and read back; all-day events as dates and UTC events without a zone; the calendars tree with
calendar.json, "events since" leaving old events out but keeping a never-ending series; unchanged events stored once and a same-size
title change seen; restoring a wiped calendar with the exceptions and the UID, skipping, into another calendar; read-only calendars
not restored into; the selection's rules and descriptions), `ApiCollectionsTests` (PostgreSQL: the selection instead of a path, phones
only, Mirror refused, none chosen refused, contacts without "since", calendars with the default two years and with everything, kept by
an edit, the phone's own change and report counts; a phone creating and joining them; lists offline, reported per kind, live over a
real hub client for both kinds, an older app and browsing turned off → the last list; owner only, people and devices apart, a wrong
kind, a computer). `ASync.App.Tests`: `CollectionsTests` (the cards, the explanation and the read-only question, choosing calendars and
"since" with a vanished one kept, the restore's snapshot and target with WRITE asked only then and its progress, a refused WRITE not
starting it, New folder and Add to a folder on a phone and not on a computer, the views at 360 px). `scripts/smoke-android.sh` (its
contacts and calendars part is `scripts/smoke-android-collections.sh`): see its header and "Milestones".
