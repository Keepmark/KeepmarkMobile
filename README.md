<p align="center">
  <img src="docs/img/keepmark.png" width="128" height="128" alt="Keepmark" />
</p>

<h1 align="center">Keepmark for Android</h1>

<p align="center">
  <b>Your bookmarks on your phone, synced and end-to-end encrypted.</b><br />
  An independent app compatible with xBrowserSync.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Android-10%2B-1f6f5c" alt="Android 10+" />
  <img src="https://img.shields.io/badge/encryption-end--to--end-243878" alt="End-to-end encrypted" />
  <img src="https://img.shields.io/badge/languages-en%20%C2%B7%20it%20%C2%B7%20fr%20%C2%B7%20es%20%C2%B7%20de%20%C2%B7%20ja%20%C2%B7%20pt--BR%20%C2%B7%20ru%20%C2%B7%20zh-4a5170" alt="Languages" />
</p>

<p align="center">
  <img src="docs/img/ecosystem.png" alt="Keepmark in a browser and on Android: the same encrypted bookmarks everywhere" />
</p>

---

Keepmark keeps the same bookmarks on your browsers and your phone. Everything
is encrypted on the device before it leaves: the service that stores your
data sees only ciphertext, and your password never leaves the phone.

It works with **any xBrowserSync service** (the official one, a community
host or your own) and with the official xBrowserSync apps. A sync created in
Keepmark opens there, and the other way round. Nothing to migrate.

## Highlights

- **Private by design.** PBKDF2-SHA256 and AES-GCM on the device. The local
  database is encrypted too (SQLCipher), and the credentials live in the
  Android KeyStore. No telemetry, HTTPS-only services.
- **Never lose a bookmark silently.** Conflicts are never resolved behind
  your back: you pick download, upload or merge, and merge shows a preview
  first. Before overwriting your bookmarks Keepmark saves an encrypted
  safety backup you can restore; single deletes have Undo, and deleting a
  folder with 5 or more bookmarks asks first.
- **Made for the phone.** Fast full-text search, pinned bookmarks, recently
  added bookmarks on Home, a clickable breadcrumb, folders first, swipe for
  details and one menu for every action.
- **Share to Keepmark** from any app: the add form opens already filled in.
- **App lock** with fingerprint or device PIN, with a relock time you choose.

## Features

| | |
|---|---|
| **Sync** | Automatic or manual, with clear status and honest conflict handling |
| **Browse** | Search, pinned and recently added sections, breadcrumb, folder tree picker |
| **Edit** | Add, edit, move and delete bookmarks and folders, with undo; new folder right from the picker; "already saved" hint when you add a page you have |
| **Backup** | Encrypted export and import, compatible with Keepmark for browsers |
| **Safety backups** | Automatic snapshot before every overwrite (newest 5 kept) |
| **Duplicates** | Find links saved more than once; one copy of each always survives |
| **Move service** | Move your bookmarks to another service, into a new or existing sync |
| **Look and feel** | Light, dark or system theme; English, Italiano, Français, Español, Deutsch, 日本語, Português (Brasil), Русский, 简体中文 |
| **Logs** | Built-in viewer with share export, sync IDs and secrets redacted |

With **Keepmark Server** you also get revision-based conflict detection,
faster "nothing changed" checks and the option to delete your sync from the
server. On other services Keepmark falls back automatically.

## Screenshots

<p align="center">
  <img src="docs/img/screens/01-browse.png" width="24%" alt="Browse folders" />
  <img src="docs/img/screens/02-folder.png" width="24%" alt="Search" />
  <img src="docs/img/screens/03-connect.png" width="24%" alt="Connect choice" />
  <img src="docs/img/screens/04-duplicates.png" width="24%" alt="Duplicates" />
</p>

<p align="center">
  <img src="docs/img/screens/05-backup.png" width="24%" alt="Encrypted backups" />
  <img src="docs/img/screens/06-actions.png" width="24%" alt="Bookmark actions" />
  <img src="docs/img/screens/07-pinned.png" width="24%" alt="Pinned bookmarks" />
</p>

## The Keepmark family

| | |
|---|---|
| **Keepmark for Android** | This app (Android 10+) |
| **Keepmark for browsers** | Extension for Firefox and Chromium-based browsers (Chrome, Edge, Brave): [Firefox Add-ons](https://addons.mozilla.org/en-US/firefox/addon/keepmark/), [Chrome Web Store](https://chromewebstore.google.com/detail/jaaekkbgpeiikepahjelfbkfgpjphbhb) |
| **Keepmark Server** | Drop-in xBrowserSync-compatible sync service with opt-in extended features |

All three share the same encryption, the same backup format and the same
wording, so moving between them feels the same.

## Compatibility

- **Services:** any xBrowserSync API service, version 1.1.9 or later.
- **Clients:** syncs open both ways with the official xBrowserSync apps
  (sync format 1.8.0). The encryption is verified byte for byte against the
  official implementation.
- **Device:** Android 10 (API 29) or later.

## Get it

**Google Play (closed testing).** Keepmark is in closed testing on Google
Play and testers are welcome:

1. Join the [keepmark-testers](https://groups.google.com/g/keepmark-testers)
   group with the Google account you use on Play Store.
2. Opt in to the test:
   <https://play.google.com/apps/testing/com.keepmark.mobile>
3. Install from
   [Google Play](https://play.google.com/store/apps/details?id=com.keepmark.mobile).

Please keep the app installed for at least 14 days. Feedback is welcome in
[issues](https://github.com/Keepmark/KeepmarkMobile/issues) or at
keepmark@proton.me.

Release notes for every version are on the
[Releases](https://github.com/Keepmark/KeepmarkMobile/releases) page.
Project home: <https://github.com/Keepmark>.

## Documentation

- [Developer guide](docs/DEVELOPMENT.md): build environment, architecture,
  engineering rules, safety-backup specification.
- [Verification spec](docs/verification-spec.md): behavioural requirements
  for code review.
- [Workflows](docs/WORKFLOWS.md) and [UI terms](docs/UI-TERMS.md): shared
  with Keepmark for browsers.

## Feedback

Questions, bug reports and ideas: [open an issue](../../issues).

## License

Proprietary — © 2026 Yari Melzani, all rights reserved. See
[`LICENSE.md`](LICENSE.md).

Independent implementation, written from scratch; compatible with the
xBrowserSync protocol. Not affiliated with or endorsed by xBrowserSync.

Third-party notices: `Crypto/LzUtf8.cs` is a C# port of `lzutf8` (MIT,
© Rotem Dan, notice kept in the file); BouncyCastle (MIT); SQLCipher Community
(BSD-style), SQLite (public domain), LibTomCrypt (public-domain option). See
[SQLCipher notices](native/sqlcipher/THIRD-PARTY-NOTICES.txt), also bundled in the app.

## Disclaimer

Keepmark is provided "as is", without warranty of any kind, express or
implied, including the implied warranties of merchantability and fitness for
a particular purpose. Use it at your own risk: the authors are not liable for
any loss of data (including bookmarks), damage or other consequences arising
from its use. See `LICENSE.md`. Keep your own backups.

### Connect from your browser

In the Keepmark browser manager, open **Link a phone**, verify your current
password and scan the QR from Android's **Connect → Link existing** screen.
The QR review shows only the service, abbreviated sync ID and clearly spaced
**Connect** / **Cancel** actions. Manual fields and later wizard steps stay
hidden while you review and verify the QR. Tap **Connect**, wait for verification,
then choose the existing sync direction. Camera permission is requested only when scanning; no images
are saved. The same QR button is available before manual target credentials in **Move
service**, followed by the usual review and confirmation. Manual connection
remains available.

The QR contains your sync credentials, including your password. Show it only
to your own device. The browser hides it after 60 seconds or loss of focus;
a captured copy still works. Android clears an unused candidate after 60
seconds. The password is never sent to the server or persisted by this flow.


### Organize several items on Android

Hold an item to select it, then tap others or choose All. Move them together to an
existing or newly created folder, with a short Undo window. Delete shows the affected
folder contents before confirmation and keeps a safety backup in Backup → Safety
backups. A brief touch response and visible checkboxes confirm selection.
