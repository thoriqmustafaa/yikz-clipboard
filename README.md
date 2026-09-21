# yikz-clipboard

Sinkronisasi clipboard pribadi antar Mac, Android, Windows, dan web, dengan riwayat terenkripsi end-to-end yang tersimpan di server sendiri (7 hari, maksimal 5 GB).

Server: https://clip.yikz.dev

## Struktur repo

| Folder | Isi |
|---|---|
| `server/` | Server Go (relay realtime, riwayat, retensi, hosting rilis) |
| `web/` | Web client Svelte, di-embed ke binary server |
| `apple/` | App macOS (SwiftPM, tanpa Xcode) |
| `android/` | App Android (Kotlin + Jetpack Compose) |
| `windows/` | App Windows (WinUI 3) dan library inti yang bisa dites di Mac |
| `protocol/` | `SPEC.md` (protokol), `UPDATES.md` (rilis dan auto update), test vectors |
| `deploy/` | `docker-compose.yml`, `.env.example`, config nginx |
| `docs/design.md` | Dokumen desain |

## Install

Unduh dari halaman **What's new** di web (setelah login) atau dari [GitHub Releases](https://github.com/thoriqmustafaa/yikz-clipboard/releases). Setelah terpasang, semua app memperbarui dirinya sendiri.

- **macOS**: unzip, pindahkan `YikzClipboard.app` ke `/Applications`, lalu jalankan `xattr -dr com.apple.quarantine /Applications/YikzClipboard.app` sebelum dibuka pertama kali. Riwayat: ⌥⌘V.
- **Android**: buka APK di HP, izinkan "Install unknown apps" untuk file manager dan untuk Yikz Clipboard (dipakai auto update).
- **Windows**: unzip ke folder yang bisa ditulis user, misalnya `%LOCALAPPDATA%\Programs\YikzClipboard`, lalu jalankan `YikzClipboard.exe`. Jangan taruh di Program Files, karena auto update butuh akses tulis.

Semua perangkat harus memakai **password enkripsi yang sama**. Password ini tidak pernah dikirim ke server.

## Merilis versi baru

1. Ubah `VERSION`, misalnya `1.2.0`.
2. Tambahkan bagian baru di atas `CHANGELOG.md` dengan format:

   ```
   ## 1.2.0 (2026-10-01)

   ### macOS
   - Perubahan
   ```

   Heading platform yang tersedia: `All`, `macOS`, `Android`, `Windows`, `Web`, `Server`.
3. Commit dan push ke `main`, lalu buat tag:

   ```bash
   git tag -a v1.2.0 -m v1.2.0 && git push origin v1.2.0
   ```

Workflow `release.yml` akan build semua platform, menandatangani tiap file dengan kunci Ed25519, meng-upload ke server, dan membuat GitHub Release. Semua app menerima notifikasi rilis lewat WebSocket dan memasang update sendiri. Tag harus sama persis dengan `v` + isi `VERSION`, dan `CHANGELOG.md` wajib punya bagian untuk versi itu.

Web dan server **tidak** ikut diperbarui oleh rilis ini. Deploy server dilakukan terpisah (lihat di bawah).

## Deploy server

VPS tidak login ke GHCR, jadi image di-build langsung di VPS:

```bash
git archive --format=tar HEAD Dockerfile .dockerignore VERSION server web protocol | ssh tencent 'rm -rf /opt/yikz-clipboard/src && mkdir -p /opt/yikz-clipboard/src && tar -x -C /opt/yikz-clipboard/src && cd /opt/yikz-clipboard/src && docker build -q --build-arg VERSION=$(cat VERSION) -t ghcr.io/thoriqmustafaa/yikz-clipboard:latest . && cd .. && docker compose up -d && docker image prune -f'
```

Konfigurasi ada di `/opt/yikz-clipboard/.env` di VPS (lihat `deploy/.env.example`). Data dan file rilis tersimpan di volume Docker `yikz-clipboard-data`.

## Kunci dan secrets

| Lokasi | Isi |
|---|---|
| GitHub secrets | `RELEASE_SIGNING_KEY`, `RELEASE_UPLOAD_TOKEN`, `RELEASE_ORIGIN_IP`, `MACOS_CERT_P12_B64`, `MACOS_CERT_PASSWORD`, `ANDROID_KEYSTORE_B64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` |
| `~/.config/yikz-clipboard/` di Mac | Backup kunci rilis, sertifikat signing macOS, keychain signing lokal |
| `~/.android/debug.keystore` | Kunci signing APK. Jika hilang, app Android harus di-uninstall dan dipasang ulang |

`RELEASE_ORIGIN_IP` dipakai agar upload rilis langsung ke VPS tanpa lewat Cloudflare, karena Cloudflare memutus upload file besar.

## Tes

| Bagian | Perintah |
|---|---|
| Server | `cd server && go test -race ./...` |
| Web | `cd web && pnpm install && pnpm check && pnpm test` |
| macOS | `cd apple && swift test` |
| Android | `cd android && ./gradlew :core:test testDebugUnitTest` |
| Windows | `cd windows && dotnet test tests/YikzClipboard.Core.Tests` (app WinUI hanya bisa di-build di Windows atau CI) |
