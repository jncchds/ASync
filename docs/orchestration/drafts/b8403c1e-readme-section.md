### ASync in the Files app

<p>
  <img src="docs/screenshots/android-files-root-light.png" alt="ASync's folders in the Files app" width="24%">
  <img src="docs/screenshots/android-files-folder-light.png" alt="A two-way folder in the Files app" width="24%">
  <img src="docs/screenshots/android-files-snapshot-dark.png" alt="A backup snapshot, dark" width="24%">
  <img src="docs/screenshots/android-files-picker-light.png" alt="Picking a file from ASync in another app" width="24%">
</p>

Your ASync folders also show in Android's **Files** app and in every app's file picker ("attach a file", "open", "save
to"), without syncing them to the phone first: files are fetched when you open them.
- **Finding it:** open *Files*, then the menu (☰): **ASync — <your server>** is in the list next to *Downloads* and
  *Internal storage*. In another app, *attach* or *open* a file, then the same menu. It is there once the phone is
  enrolled; if the phone was removed from your account, it says so.
- **Two-way folders:** browse, open, and also save, create, rename, move, copy and delete. Changes go to the server
  exactly as a sync's would: an overwritten file keeps its old content as a version and a deleted one goes to the
  folder's trash (both restorable in the web UI under *Files*), and your other devices pick the change up. If the folder
  is also synced to a folder on this phone, that copy updates by itself right after. A new, empty directory stays on the
  phone until you put a file in it.
- **Mirror folders** are read-only here (the device that uploads them decides what is in them); copy a file out to
  change it.
- **Backup folders:** one directory per snapshot, newest first (named by date, time and short id), with the files as they
  were. Read-only: open a file, or copy it into a two-way folder or onto the phone to restore it. End-to-end encrypted
  backups open once they are unlocked in the app (*Unlock…*); until then the folder says "Unlock it in the ASync app".
- **Large files** stream: anything over 128 MB is read in pieces as the app needs them (videos can be skipped through)
  and never stored on the phone. Smaller files are kept in a 512 MB cache, so opening them again is instant. Large
  transfers show the same notification (with *Cancel*) as a sync. *Only on Wi-Fi* and the other conditions apply to
  background syncs only, not to a file you open yourself.
- **Search** (in the Files app's search box) finds file names in your two-way and mirror folders; *Recent* lists their
  most recently changed files. Backups are not searched. Images get thumbnails.
- **Offline:** the ASync entry needs the server: without a connection the folders cannot be listed, and the Files app
  shows "Cannot reach the ASync server". Files you sync to a folder on the phone are of course still there, in that
  folder. A file saved while the server cannot be reached is not stored: the phone shows a notification saying so.
