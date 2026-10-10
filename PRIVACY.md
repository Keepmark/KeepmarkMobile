# Keepmark for Android: Privacy Policy

Last updated: 11 October 2026

Keepmark for Android (`com.keepmark.mobile`) is an open-source app that syncs
your bookmarks, end-to-end encrypted, through an xBrowserSync-compatible sync
service that you choose.

## Summary

- Keepmark does not collect, sell or share any personal data.
- There is no analytics, telemetry, advertising, crash reporting or tracking
  of any kind, and no third-party SDK that does so.
- Your bookmarks are encrypted on your phone before they leave it.
- Keepmark does not create accounts and does not ask for your name or email.

## What leaves your device

Only when sync is enabled, Keepmark sends your bookmarks to the sync service
you selected (the official xBrowserSync service, a community host or your own
server). Before sending, your bookmarks are encrypted on the device with
AES-GCM, using a key derived from your password with PBKDF2-SHA256.

The sync service therefore stores only:

- the encrypted bookmark data (ciphertext it cannot read),
- a random sync ID,
- timestamps and version information needed for sync.

Your password and your bookmarks in readable form are never sent anywhere.
The developer of Keepmark does not receive any of this data. The sync service
you choose is operated by its own provider (or by you), and its own privacy
policy applies. As with any server, it can see the IP address your requests
come from; Keepmark does not send it any other information about you or your
device.

Keepmark contacts no other server.

## What stays on your device

- Your bookmarks, in a local database encrypted with SQLCipher.
- The sync ID and the key derived from your password (never the password
  itself), in Android secure storage backed by the Android KeyStore.
- Settings, sync state and a local log of recent events. Sync IDs are
  truncated and secrets are never written to the log.
- Encrypted safety backups taken before destructive operations.

Android cloud backup of the app data is disabled. Backup files and logs leave
the app only when you export or share them yourself, to a destination you
pick. Uninstalling the app deletes all local data.

## Permissions

- Internet and network state: to reach the sync service you choose and to know
  when you are offline.
- Biometrics: for the optional app lock with fingerprint or device PIN. Your
  fingerprint is handled by Android and never reaches Keepmark.

## Children

Keepmark is not directed at children under 13.

## Changes

Changes to this policy are published in this file, in the public repository
https://github.com/Keepmark/KeepmarkMobile.

## Contact

keepmark@proton.me
