### Backing up contacts and calendars

<p>
  <img src="docs/screenshots/light-20-contacts-accounts.png" alt="Choosing which contact accounts of a phone to back up, in the web UI" width="64%">
  <img src="docs/screenshots/phone-20-calendars.png" alt="Choosing calendars on a phone-sized screen, dark theme" width="28%">
</p>
<p>
  <img src="docs/screenshots/android-pim-dashboard-light.png" alt="Contacts and Calendars folders on the phone" width="24%">
  <img src="docs/screenshots/android-contacts-permission-light.png" alt="Why ASync asks to read contacts" width="24%">
  <img src="docs/screenshots/android-calendars-calendars-dark.png" alt="Choosing calendars on the phone, dark" width="24%">
  <img src="docs/screenshots/android-contacts-restore-light.png" alt="Restoring contacts: which snapshot, and into which account" width="24%">
</p>

**Contacts** and **Calendars** folders back up a phone's address book and calendars, chosen the same way as photo albums.

1. *Folders → Add folder → Contacts* or *Calendars* (or *New folder…* in the phone's app). They are backup folders like the
   others: snapshots, how many to keep, standard or end-to-end encryption, a recovery key.
2. Add your phone and choose **what it backs up**: *All contacts* or *Chosen accounts* (your Google account, the phone itself, the
   SIM, an app's account), *All calendars* or *Chosen calendars*, and for calendars *Events since* (the last two years unless you
   choose otherwise; repeating events are kept while they still repeat, future events always). The lists come live from the phone,
   or as it reported them last when it is offline, with how many contacts or events each holds.
3. On the phone, ASync asks once to **read** your contacts or calendars and says why. It asks to **add** to them only when you
   restore.

Changes are backed up soon after you make them (when the folder's conditions allow), and every folder also runs on its schedule.
Each contact is kept as a **vCard 4.0 file with its photo**, by account (`me@gmail.com~1a2b3c4d/Olena Shevchenko~42.vcf`); each event
as an **iCalendar file** with its repetitions, changed or cancelled occurrences, reminders and guests, by calendar
(`Work~4/Standup~118.ics`), with a small `account.json` / `calendar.json` beside them. Unchanged contacts and events are stored once,
and an unchanged contact is not even read again.

**Getting them back:** *Restore into the contacts… / calendars…* on the folder's card asks which snapshot and where to: the accounts
or calendars they came from, or one you choose. Entries still on the phone are skipped, and **nothing on the phone is changed or
deleted**. If an account or calendar is emptied on the phone, no empty snapshot replaces the last good one. On a computer, `async-cli
restore` gives plain `.vcf` and `.ics` files any address book or calendar imports, and the web UI's *Snapshots* downloads them — a
whole account as **one .vcf** and a whole calendar as **one .ics** too.

Listing accounts and calendars from the web is the same phone setting as browsing folders and albums.

