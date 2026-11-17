### Backing up photos and videos

<p>
  <img src="docs/screenshots/light-18-photos-albums.png" alt="Choosing which albums of a phone to back up, in the web UI" width="64%">
  <img src="docs/screenshots/phone-18-photos-albums.png" alt="The same on a phone-sized screen, dark theme" width="28%">
</p>
<p>
  <img src="docs/screenshots/android-photos-dashboard-light.png" alt="A Photos folder on the phone" width="24%">
  <img src="docs/screenshots/android-photos-albums-dark.png" alt="Choosing albums on the phone, dark" width="24%">
  <img src="docs/screenshots/android-photos-permission-light.png" alt="Why ASync asks to read photos" width="24%">
</p>

A **Photos** folder backs up a phone's photo library instead of a directory: every photo and video, or only the albums
you choose.

1. *Folders → Add folder → Photos & videos* (or *New folder… → Photos & videos* in the phone's app). It is a backup
   folder like the others: snapshots, how many to keep, standard or end-to-end encryption, a recovery key.
2. Add your phone to it. Instead of a folder on the device you choose **what it backs up**: *All photos and videos*, or
   *Chosen albums*. The album list comes from the phone itself (it is asked live, and woken if it is asleep and has
   UnifiedPush); when the phone is offline you see the list it reported last. You can also leave out videos or
   screenshots, or start from a date. Several phones can share one Photos folder; each keeps its own snapshots.
3. On the phone, ASync asks once for access to photos and videos and says why. Choose *Allow all*: with Android 14's
   *Select photos* only the photos you pick are backed up, and new ones are not seen until you pick them too (the app
   says so and offers *Allow all photos…*). ASync also asks for the photos' location, or Android removes it from what
   ASync reads.

New photos and videos are backed up soon after you take them (within a minute or so, when the folder's conditions, such
as Wi-Fi only, allow), and every folder also runs on its schedule. **Originals are stored as they are**, not re-encoded,
each with a small `.json` file of its details (when it was taken, its size, dimensions, location, albums, favourite).
They are kept in year and month directories, `2026/03/IMG_1234~1000000033.jpg` (the number after `~` keeps two
`IMG_0001.JPG` apart), so a restore with `async-cli restore`, or a download from the web snapshot browser, gives you plain
files you can open anywhere. A photo in two albums, or renamed, is stored once; unchanged photos are not read again.
Deleting a photo on the phone keeps it in the older snapshots for as long as the folder keeps them.

**Getting them back:** *Restore into the library…* on the folder's card in the phone's app puts the newest snapshot back
into the phone's photos, each where it was (DCIM/Camera…), and skips what is still there. On a computer,
`async-cli restore` (or the web UI's *Snapshots*) gives the files and their `.json` details.

Browsing folders and listing albums from the web are one setting on the phone (*Settings → Allow browsing from the web*):
with it off, the web UI shows the last album list the phone reported and you can still choose *All photos and videos*.
