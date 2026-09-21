# Releases, Changelog and Auto Update

Status: authoritative addendum to SPEC.md. Protocol version stays 1; everything here is additive.

## 1. Versioning

- One version for the whole product, stored in the file `VERSION` at the repo root (semver `MAJOR.MINOR.PATCH`, no `v` prefix). All platforms read it at build time:
  - macOS: `CFBundleShortVersionString` = VERSION, `CFBundleVersion` = build number below.
  - Android: `versionName` = VERSION, `versionCode` = `MAJOR*10000 + MINOR*100 + PATCH`.
  - Windows: assembly `Version` = VERSION.
  - Server: reported as `server_version` stays the git sha; the product version is exposed in `/api/releases`.
- Build number (macOS `CFBundleVersion`) = the same integer as Android `versionCode`.
- Version comparison: numeric per component. A client updates only when `latest.version > own version`.

## 2. Changelog

- Source of truth: `CHANGELOG.md` at the repo root. Format (strict, parsed by CI):

```
# Changelog

## 1.1.0 (2026-09-22)

### All
- Bullet

### macOS
- Bullet

### Android
- Bullet

### Windows
- Bullet

### Web
- Bullet

### Server
- Bullet
```

- Section headings `### <Platform>` are optional per release. The release notes for a version are the Markdown between its `## x.y.z (date)` heading and the next `## ` heading, without the heading line itself.
- Only a small Markdown subset is used and must be rendered by clients: `###` headings, `-` bullets, `**bold**`, `` `code` ``, and `[text](url)` links.

## 3. Release artifacts

| platform key | file name | contents |
|---|---|---|
| `macos` | `YikzClipboard-<ver>-macos.zip` | `YikzClipboard.app` (universal) zipped with `ditto -c -k --keepParent` |
| `android` | `yikz-clipboard-<ver>.apk` | release APK signed with the project keystore |
| `windows-x64` | `YikzClipboard-<ver>-win-x64.zip` | self-contained app folder contents at zip root |
| `windows-arm64` | `YikzClipboard-<ver>-win-arm64.zip` | same for arm64 |

## 4. Signatures

- Every artifact is signed with the project release key (Ed25519).
- `signature = Ed25519Sign(private_key, SHA-256(file bytes))`: the message signed is the 32 raw digest bytes, not the hex string and not the file.
- Encoding: `sha256` lowercase hex, `signature` standard base64 with padding (64 bytes decoded).
- Release public key (raw 32 bytes, base64), embedded as a constant in every client and in the server:

```
2Ve8Uwt53+AwbuAiK08Xf2gB5EU7pguqXL7I9yeqGGk=
```

- Test vector: `protocol/vectors/release_signature.json` (uses a throwaway key, not the release key). Every client must have a unit test that verifies it, and that verification fails when one byte of the file or signature changes.
- A client MUST verify both the SHA-256 of the downloaded file and the signature against the embedded public key before installing. On failure: delete the file, log an error, do not retry that version until the next check cycle.

## 5. Manifest

```json
{
  "version": "1.1.0",
  "published_at": "2026-09-22T10:00:00.000Z",
  "notes_md": "### macOS\n- Dock icon toggle\n",
  "assets": [
    {
      "platform": "macos",
      "file": "YikzClipboard-1.1.0-macos.zip",
      "size": 2400000,
      "sha256": "<hex>",
      "signature": "<base64>"
    }
  ]
}
```

`notes_md` is the full notes for that version (all platform sections).

## 6. Server

Storage: `$CC_DATA_DIR/releases/<version>/` containing `manifest.json` and the asset files. Release bytes do not count toward the clipboard storage quota. Keep the newest `CC_RELEASES_KEEP` (default 5) versions; delete older ones after a successful publish. The disk guard (`disk_low`) applies to asset uploads.

Config: `CC_RELEASE_TOKEN` (required to enable publishing; if empty, admin endpoints return 404). `CC_RELEASES_KEEP` (default 5).

### Admin (CI) endpoints, `Authorization: Bearer $CC_RELEASE_TOKEN`

| Method and path | Body | Result |
|---|---|---|
| `PUT /api/admin/releases/{version}/assets/{file}` | raw bytes, `application/octet-stream`, streamed to disk, no body size limit other than 1 GiB | `204`. Re-upload overwrites. `{file}` must match `^[A-Za-z0-9._-]{1,128}$`. |
| `PUT /api/admin/releases/{version}` | manifest JSON | Server checks every asset in the manifest exists, its size and SHA-256 match, and its signature verifies with the embedded public key. Then writes `manifest.json` atomically, prunes old versions, and broadcasts `release_available` on the WebSocket. `201` with the manifest. Errors: `400 invalid_request`, `409 missing_asset`, `422 bad_signature`. |

nginx `client_max_body_size` for `/api/admin/` must allow large bodies (e.g. `location /api/admin/ { client_max_body_size 1g; ... }`).

### Client endpoints, device token (normal auth)

| Method and path | Result |
|---|---|
| `GET /api/releases?limit=20` | `200 {"releases": [manifest, ...]}` newest first. Used by the web changelog page. |
| `GET /api/releases/latest?platform=<key>` | `200 {"version", "published_at", "notes_md", "asset": {..., "url": "/api/releases/<ver>/assets/<file>"}}` for the newest release that has that platform; `204` if none. |
| `GET /api/releases/{version}/assets/{file}` | The file, `application/octet-stream`, `Content-Length`, supports `Range` (use `http.ServeContent`). `404 not_found` if missing. |

### WebSocket

New server to client message:

```json
{"type": "release_available", "version": "1.1.0"}
```

Clients that do not know it must ignore it (SPEC already says unknown types are ignored by clients).

## 7. Client update behavior

- Check on launch (after 10 s), every 6 hours, on `release_available`, and when the user clicks "Check for Updates".
- Settings: "Automatically install updates" (default on). "Check for Updates" button, current version, last check time, and a "What's new" view rendering `notes_md` of the latest release.
- Flow: check, download to a temp/cache location with progress, verify SHA-256 and signature, then install:
  - **macOS**: unzip with `ditto -x -k` into a temp dir, verify the new bundle with `codesign --verify --deep --strict` and that its signing certificate leaf matches the running app's (same designated requirement), then spawn a detached helper shell script that waits for the app's PID to exit, replaces the installed bundle (the running app's `Bundle.main.bundleURL`, typically `/Applications/YikzClipboard.app`) by moving the old one to the trash-like temp dir and moving the new one in place, clears the quarantine xattr, and relaunches with `open`. If auto install is on, install when no History/Settings window is key and no transfer is running; otherwise show "Update ready, Restart to Update" in the menu.
  - **Android**: requires `REQUEST_INSTALL_PACKAGES`. Use `PackageInstaller` sessions with `setRequireUserAction(USER_ACTION_NOT_REQUIRED)` on API 31+ so updates can install silently once the app is the installer of record; when the system still demands confirmation (`STATUS_PENDING_USER_ACTION`), post a notification "Update 1.1.0 ready, tap to install" that launches the confirmation intent. The foreground service restarts after the update via `MY_PACKAGE_REPLACED`.
  - **Windows**: unzip to `%LOCALAPPDATA%\YikzClipboard\updates\<ver>\`, then spawn a detached helper (PowerShell script written to that folder) that waits for the process to exit, replaces the install folder contents (the directory of the running exe), and restarts the exe. Same auto/deferred rules as macOS.
- Never downgrade. Never install an asset whose platform key differs from the running platform/architecture.

## 8. Signing identities (CI secrets, already configured in the GitHub repo)

| Secret | Use |
|---|---|
| `RELEASE_SIGNING_KEY` | Ed25519 private key PEM for artifact signatures |
| `RELEASE_UPLOAD_TOKEN` | Bearer token for the admin release endpoints (same value as server `CC_RELEASE_TOKEN`) |
| `MACOS_CERT_P12_B64`, `MACOS_CERT_PASSWORD` | Self-signed code signing identity "Yikz Clipboard Signing" (PKCS#12, base64). Signing every build with the same identity keeps the designated requirement stable, so Keychain access and the Accessibility permission survive updates. |
| `ANDROID_KEYSTORE_B64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` | The keystore that signed the APK already installed on the owner's phone. Every release APK must be signed with it or updates fail. |

Local copies for local builds live in `~/.config/yikz-clipboard/` on the owner's Mac (`macos-signing.p12`, `macos-cert-password.txt`, `release-signing.pem`, `release-public-key.txt`, `release-upload-token.txt`); the Android keystore is `~/.android/debug.keystore` (store and key password `android`, alias `androiddebugkey`).

## 9. Release pipeline

`.github/workflows/release.yml`, triggered by pushing a tag `v*` whose value equals `v` + `VERSION`:

1. Extract notes for the version from `CHANGELOG.md` (fail if missing).
2. Build in parallel: macOS (macos-latest, sign with the imported identity), Android (assembleRelease with the keystore secrets), Windows x64 and arm64 (windows-latest).
3. A final job downloads all artifacts, renames them per section 3, computes SHA-256 and signatures with `openssl pkeyutl -sign -rawin` using `RELEASE_SIGNING_KEY`, uploads each asset, then PUTs the manifest to `https://clip.yikz.dev`.
4. Also creates a GitHub Release with the notes and attaches the artifacts (for the owner's archive).
