# yikz-clipboard for Android

Native Android client for yikz-clipboard. It follows `../protocol/SPEC.md` (protocol version 1) and passes every vector in `../protocol/vectors/`.

- Application id: `dev.yikz.clipboard`
- minSdk 29, targetSdk 36, compileSdk 36.1 (newest installed stable platform)
- Kotlin 2.4.20, Jetpack Compose with Material 3 and dynamic color
- Default server: `https://clip.yikz.dev` (editable on the sign-in screen)

## Toolchain

| Component | Version | Notes |
|---|---|---|
| Gradle | 9.7.1 | wrapper, official `gradle-9.7.1-bin.zip` from services.gradle.org, pinned with `distributionSha256Sum` |
| Android Gradle Plugin | 9.4.1 | uses AGP built-in Kotlin support for `:app` |
| Kotlin | 2.4.20 | `org.jetbrains.kotlin.jvm` for `:core`, Compose and serialization compiler plugins |
| JDK | 25 locally (Temurin 25.0.2, the machine default), 21 in CI | AGP 9.4.1 and Kotlin 2.4.20 run on both, so no extra JDK was installed |
| Compose BOM | 2026.06.01 | Compose 1.11.4, Material 3 1.4.0 |
| androidx.core | 1.18.0 | |
| androidx.lifecycle | 2.10.0 | |
| androidx.activity | 1.13.0 | |
| DataStore | 1.2.1 | |
| OkHttp | 5.4.0 | |
| kotlinx.serialization / coroutines | 1.11.0 / 1.11.0 | |

All versions live in `gradle/libs.versions.toml`.

Why these library versions: Compose BOM 2026.08 and later (Compose 1.12), core 1.19, lifecycle 2.11 and OkHttp 5.5 all declare `minCompileSdk=37`. Only platforms up to `android-36.1` are installed, so the catalog uses the newest releases that compile against 36.1.

How the wrapper was created: Gradle 9.7.1 was downloaded once to a scratch folder, its SHA-256 checked against `services.gradle.org`, and `gradle wrapper --gradle-version 9.7.1 --distribution-type bin --gradle-distribution-sha256-sum ...` was run in this directory. Global shell config was not touched.

`local.properties` points `sdk.dir` at `~/Library/Android/sdk` and is git-ignored. On another machine, create it or set `ANDROID_HOME`.

## Build and test

```sh
cd android
./gradlew testDebugUnitTest assembleDebug assembleRelease
./gradlew :core:test
./gradlew :app:lintDebug
```

- `:core` registers a `testDebugUnitTest` task that runs its JVM tests, so the single CI command covers both modules.
- The vector directory is passed to the tests via the system property `yikz.vectors.dir`, resolved from the Gradle project dir as `../protocol/vectors`.
- Outputs: `app/build/outputs/apk/debug/app-debug.apk` and `app/build/outputs/apk/release/app-release.apk`. The release build is minified and signed with the release signing config described below.

## Version and signing

- `versionName` is read from the repo root `VERSION` file and `versionCode` is `MAJOR*10000 + MINOR*100 + PATCH` (1.1.0 is 10100), see `../protocol/UPDATES.md` section 1.
- The release signing config uses `YIKZ_KEYSTORE_FILE`, `YIKZ_KEYSTORE_PASSWORD`, `YIKZ_KEY_ALIAS` and `YIKZ_KEY_PASSWORD` when `YIKZ_KEYSTORE_FILE` is set (the release pipeline sets them). Otherwise it uses `~/.android/debug.keystore` with the default debug credentials (store and key password `android`, alias `androiddebugkey`), which is the key of the APK already installed on the owner's phone. If that file does not exist either (fresh CI runner), it falls back to the AGP debug signing config.
- Every APK meant for the owner's phone must be signed with that same key (certificate SHA-256 `A9:67:25:08:03:11:D9:5A:B4:1E:C1:D7:75:7D:83:8C:08:1F:43:46:95:1B:51:C8:65:E0:A1:CF:74:8D:70:BD`), otherwise Android refuses the update. Check with `apksigner verify --print-certs app/build/outputs/apk/release/app-release.apk`.
- CI (`android.yml`) decodes `ANDROID_KEYSTORE_B64` into a temp file on push builds when the secret exists and passes `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD` through; without secrets it still builds and signs with the runner debug key.

## In-app updates

Implements `../protocol/UPDATES.md` section 7.

- `:core` `Updates.kt`: `SemVer` (parse, compare, versionCode), release models, `Releases.parseLatest` (`200` body, `204` or `404` means no release), `UpdatePolicy.decide` (never downgrade, platform must be `android`, sane file name, size, sha256 and signature), `ReleaseVerifier` (SHA-256 of the file, then Ed25519 over the 32 raw digest bytes with the embedded release public key, BouncyCastle lightweight API) and `NotesMarkdown` (the notes subset: `###` headings, `-` bullets, `**bold**`, `` `code` ``, links).
- `:app` `update/UpdateManager.kt`: checks 10 s after the foreground service starts, then whenever 6 hours have passed (polled every 15 minutes and on screen on or leaving Doze), on the `release_available` WebSocket message and on demand from Settings. It downloads the APK into `cache/updates` with the device token, verifies size, SHA-256, signature, package name and versionCode, and installs it with a `PackageInstaller` session (`USER_ACTION_NOT_REQUIRED` on API 31+, `setRequestUpdateOwnership(true)` on API 34+). A failed verification deletes the file and waits for the next check.
- When Android still wants a confirmation (`STATUS_PENDING_USER_ACTION`), the confirmation opens directly if the app is in the foreground, otherwise a high-priority notification "Update x.y.z ready, tap to install" opens it. If "Install unknown apps" is off for this app, Settings shows an explanation with a button to `ACTION_MANAGE_UNKNOWN_APP_SOURCES`, and a notification points there when the app is in the background.
- `BootReceiver` handles `MY_PACKAGE_REPLACED`: it clears the update cache and restarts the foreground service.
- Settings, Updates: current version, "Automatically install updates" (default on; off only notifies), "Check for updates" with status, last check time, and "What's new" with the latest release notes.
- Silent installs need this app to be the installer of record. The first update after installing an APK by hand usually still shows one confirmation; later ones install without it.
- The debug build also runs R8, but only to shrink (`-dontobfuscate`, `-dontoptimize`, see `proguard-debug.pro`). Without it the full Material icon set made the debug APK 66 MB. With it the debug APK is about 11 MB and stack traces stay readable.

CI: `.github/workflows/android.yml` uses Temurin JDK 21, installs `platforms;android-36.1`, runs the same command and uploads both APKs.

## Structure

```
android/
  core/   pure Kotlin/JVM, no Android dependencies, fully unit tested
  app/    Android application
```

### :core (`dev.yikz.clipboard.core`)

| File | Contents |
|---|---|
| `Encoding.kt` | lowercase hex, strict padded base64, RFC 3339 timestamps |
| `Protocol.kt` | protocol constants, kinds, MIME types |
| `Crypto.kt` | PBKDF2-HMAC-SHA256 with NFC, AES-256-GCM seal/open with explicit nonces, AAD builders, subkeys, key check, streaming content digests |
| `Formats.kt` | UUIDv7, token format, chunking math, 500 code point preview, YCF1 streaming writer and reader with name rules |
| `Models.kt`, `WsMessages.kt` | kotlinx.serialization models for every HTTP body and WebSocket message, close codes |
| `Items.kt` | cached item model, meta encrypt/decrypt, integrity verification |
| `Support.kt` | logger interface, reconnect and HTTP backoff, recent-hash echo guard, upload dedupe rules, auto-apply rules |
| `Interfaces.kt` | `SyncApi`, `SyncStore`, `ApiException` |
| `SyncEngine.kt` | welcome handling, initial sync, catch-up, reconcile via `history/index`, `state_rev` tracking, server id change, auto-apply |
| `Connection.kt` | WebSocket lifecycle over an abstract transport: hello, app-level ping every 20 s, dead after 45 s, full jitter backoff, close code handling, network change and foreground probes |
| `Transfers.kt` | inline and chunked upload (3 chunks in parallel, per-chunk retry, `missing_chunks` recovery), streamed chunk download with verification, progress |

KDF: `Kdf.derive` uses `javax.crypto` `PBKDF2WithHmacSHA256` only after it passes a built-in self test with the `unicode_nfd_input` vector. If the platform provider fails that test, it falls back to a PBKDF2 implementation over the NFC UTF-8 bytes built on `HmacSHA256`. Both paths are covered by tests.

### :app (`dev.yikz.clipboard`)

| Package | Contents |
|---|---|
| `data` | `HttpApi` and `OkHttpWsTransport` (OkHttp, `pingInterval` disabled), `ClipDatabase` (plain SQLite, implements `SyncStore`, keeps meta encrypted at rest and decrypts into memory), `SecureStore` (AndroidKeyStore AES-256-GCM key wraps the device token and the derived key), `SettingsStore` (DataStore), `AppLog` (1000 entry ring buffer plus rotating file log in `files/logs`) |
| `sync` | `SyncController` (sessions, login, unlock, send, item actions, sign out), `ClipboardBridge` (read and write the system clipboard, origin marker, sensitive flag), `ContentStore` (decrypt and unpack items into `cache/clips/<id>`), `Thumbnails`, `Notifications` |
| `service` | `SyncService` (foreground service owning the connection), `BootReceiver`, `SendTileService`, `LogcatWatcher` (automatic mode) |
| `ui` | `MainActivity`, `ClipboardReadActivity` (invisible reader), `ShareActivity` (share sheet), Compose screens: onboarding, clipboard history with detail sheet, devices with storage card, settings, activity log |

## Foreground service type

The service uses `foregroundServiceType="specialUse"` with `FOREGROUND_SERVICE_SPECIAL_USE` and the `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` property.

- `dataSync` is limited to 6 hours per 24 hours for apps targeting Android 15 and receives `onTimeout`, which would kill an always-on connection every day.
- Android 15 also forbids starting `dataSync` services from `BOOT_COMPLETED`, which breaks start on boot.
- `specialUse` has neither restriction. Its only cost is Play review, which does not apply to a sideloaded personal app.
- `START_STICKY`, a battery optimization exemption, and a start on `BOOT_COMPLETED` and `MY_PACKAGE_REPLACED` keep it running.

## Connection behavior

- App-level JSON `ping` every 20 s, and the connection counts as dead after 45 s without any message. It is then cancelled and reopened. The heartbeat clock is `SystemClock.elapsedRealtime`, so time spent in deep sleep counts.
- Reconnect delay is `uniform(0, min(30 s, 0.5 s * 2^attempt))`, reset after every `welcome`.
- Immediate reconnect with backoff reset when the default network changes or becomes validated (`ConnectivityManager.NetworkCallback`).
- App to foreground, screen on and leaving Doze: reconnect now if disconnected, otherwise send a `ping` and reconnect if nothing arrives within 5 s.
- Close 4001 or an upgrade 401 signs the device out. 4003 asks for an app update. 4002 waits for the app to come to the foreground.

## First run

1. Install `app-release.apk` (or `app-debug.apk`) and open **Yikz Clipboard**.
2. Tap **Get started**. The server defaults to `https://clip.yikz.dev`. Enter the username and login password from `CC_USERNAME` and `CC_PASSWORD`. The device name defaults to the phone model.
3. Enter the encryption password. On the first device of a fresh server (no key check stored yet) you create it and confirm it. On every later device you enter the same password, and a wrong one is rejected before anything is stored. Deriving the key takes a few seconds.
4. Allow notifications and unrestricted battery use on the next screen. Then tap **Finish**. The status notification appears and the connection starts.
5. Optional: in Settings, tap **Add** next to Quick Settings tile (Android 13 and later), or add the **Send clipboard** tile by editing Quick Settings.

Sending from the phone, because Android blocks background clipboard reads:

- Quick Settings tile **Send clipboard**
- **Send clipboard** action on the status notification
- Share sheet: **Send to devices** accepts text, images and any files
- **Send clipboard** button on the Clipboard screen
- Optional automatic mode (Settings, Automatic mode):
  1. From a computer, run `adb shell pm grant dev.yikz.clipboard android.permission.READ_LOGS`. The command is shown in the app with a copy button.
  2. Allow **Display over other apps** from the same card.
  3. Turn on the switch. Android 13 and later may ask once per service start to allow access to device logs.

Receiving: new items from other devices created in the last 5 minutes are put on the clipboard automatically, so they also land in the Gboard clipboard history. Images and files are shared as `content://` URIs from the app's FileProvider. Items over the auto-download limit (50 MB by default, adjustable in Settings) show a notification with a **Download** action.

## SPEC interpretation decisions

1. **Initial sync size.** 11.2 step 3 says "until has_more is false or the client has enough history". The client loads up to 1000 items. Older pages load when you scroll to the end of the list (`history?before=`).
2. **Live items during initial sync.** Nothing from the initial backfill is auto-applied. A live `clip` that arrives while the initial backfill runs is newer than `welcome.current_seq`, so it is treated like a live item received during catch-up and can be applied if it is eligible.
3. **Reconcile race.** `history/index` deletions only remove cached items with `seq <= index.current_seq`, so a live `clip` inserted while the index request is in flight is not dropped. Pin events that arrive during reconcile with a higher `state_rev` are reapplied, and consecutive revisions received meanwhile advance the stored `state_rev`.
4. **Gap detection while catching up.** A `state_rev` gap seen before catch-up finishes does not start a second reconcile, because the catch-up reconcile covers it.
5. **Server id change.** The cache is cleared, then `/api/me` is checked. If the salt differs from the one the key was derived with, or the stored key check differs, the key is discarded and the app asks for the encryption password again. If the new server has no key check yet, this device stores its own.
6. **Clipboard sends and dedupe.** Tile, notification, in-app button and automatic mode all apply the 12.2 rules (recent-hash set, CRLF-normalized text hash, newest cached item), and the app reports "Already synced" instead of uploading again. The share sheet is an explicit choice of content, so it always uploads.
7. **Sync type switches.** The Text, Images and Files switches apply to automatic clipboard writes and to clipboard sends, but not to the share sheet.
8. **Pause.** Pause closes the WebSocket, so nothing is received or auto-applied. Explicit sends still work over HTTP.
9. **Clipboard reading representation (13.1).** One content URI with an image MIME type becomes an `image` item (converted to PNG unless it is already PNG). Other or multiple URIs become a `files` YCF1 archive. Otherwise the text items are joined with newlines (HTML items fall back to their plain text).
10. **Shared images.** A single shared image becomes an `image` item. Several items, or anything that is not an image, become a `files` archive.
11. **File name sanitizing.** Shared file names that break the YCF1 rules get separators and control characters replaced with `_`, are cut to 255 UTF-8 bytes, and get duplicates renamed `name (2).ext` before archiving. On receive, names that collide case-insensitively are renamed the same way.
12. **Echo-loop prevention (12.1).** Every write carries the `ClipDescription` extra `dev.yikz.clipboard.origin`, and content carrying it is never uploaded. The listener ignores the next primary clip change only if it arrives within 2 s of our own write, because background apps may never receive that callback. Hashes of applied, sent and received items go into the 32-entry recent set.
13. **Sensitive content.** `android.content.extra.IS_SENSITIVE` is honored when reading. Items the app writes are never marked sensitive.
14. **Token revocation.** On a 401 or close 4001 the app deletes both the token and the derived key and clears the local cache, then asks you to sign in again. The device id is kept so the next login reuses the same device record.
15. **Re-login key.** A fresh login always asks for the encryption password again, even for the same server, so the stored key always matches the salt in force.
16. **Thumbnail cache.** Sealed thumbnails are cached on disk as received, still encrypted. Decrypted bitmaps are only kept in memory.
