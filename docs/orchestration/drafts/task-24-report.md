# Task 24 report: iOS contacts and calendars backup

Branch `task/24-ios-collections` (based on local main 09d7541). No server, Contracts, migration or repository-format change.

## Built
- `IContactsSource` over the Contacts framework (`Shared/IosContacts.cs`, `Collections.cs`): containers as accounts (local → iPhone,
  CardDAV named iCloud/Card → iCloud, Exchange, CardDAV), contacts fetched per container without unifying, every field but notes,
  labels both ways (system `_$!<…>!$_`, plain iPhone / iCloud / Apple Watch, custom), social profiles as `IMPP:x-social-…`, groups
  as CATEGORIES, photo. Source `ios-contacts`.
- `ICalendarSource` over EventKit (`Shared/IosCalendars.cs`, `Shared/IosRecurrence.cs`): occurrences rebuilt into series
  (detached → RECURRENCE-ID exceptions, deleted → inferred EXDATE), rules ↔ RRULE with an occurrence expander, alarms, attendees,
  all-day, zones, 3-year predicate pieces from "since" to 5 years ahead, Birthdays left out. Source `ios-eventkit`.
- Restore: CNSaveRequest (+ groups), EKEventStore.save + occurrence changes/removals; attendees not writable (documented).
- Permissions (Info.plist keys, iOS 17 full access and the 15/16 fallback), platform texts via new optional
  `ICollectionAccess.Note` / `PartialText`, change observers debounced with `PhotoChangeDebouncer`, automation `pim…`.
- Shared changes: `ICalendar.ContentKey` compares a normalised RRULE; `CollectionRestorer` falls back to the one writable
  account/calendar of the same name and type when the id is gone (iOS gives new ids on a new iPhone).
- `scripts/smoke-ios-collections.sh`, `scripts/web-shot.mjs` (web UI screenshots through headless Chrome; ios.yml builds the
  SPA for it), smoke-ios.sh's "task result" race fixed (waits for the task's own new lastTask).

## Decisions
- Contact versions derived from content (card without photo + thumbnail hash) with a first-seen time store, not
  `CNChangeHistoryFetchRequest` (reachable from .NET, but still needs a per-contact comparison and a second state).
- Events always generated and read (dedup), not skipped by `lastModifiedDate` (deleted occurrences don't reliably move it);
  real modification times kept as file times.
- Birthdays calendar not offered; notes not read (entitlement); limited contacts picker not reachable (SwiftUI): Settings text.

## CI
- 44233286871: failed — moved occurrence came with its own eventIdentifier and UID; Apple Watch label is plain; a script grep.
- 44239395512: failed one check — the calendar list count.
- 44245116136: **passed** (125 checks).

## Tests
- ASync.App.Tests 312 passed (IosCollectionsTests new), ASync.Tests 566 passed / 86 skipped (PostgreSQL, back-ends), check-ios.sh clean.

## Open items
- Not tried on a real iPhone, iCloud/Exchange/CardDAV accounts, iOS 15/16 calendar permission path.
- Events beyond five years ahead are not backed up; deleted occurrences within a week of the window's edges are not inferred.
