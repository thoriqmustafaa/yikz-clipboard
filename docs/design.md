# yikz-clipboard: Dokumen Desain

Tanggal: 2026-09-21
Status: draft, menunggu review
Pemakai: pribadi (1 user), tidak dipublikasikan

## 1. Tujuan

1. **Realtime**: copy di satu perangkat, dalam 1 detik sudah ada di clipboard perangkat lain (dan tercatat di clipboard manager lokal seperti Raycast, Win+V, Gboard) walaupun belum di-paste.
2. **History tersinkron**: semua item tersimpan di server (terenkripsi) selama 7 hari dengan batas total 5 GB, bisa dibrowse, dicari, dan di-copy ulang dari perangkat mana pun.
3. **Stabil**: tidak ada putus diam-diam. Setelah sleep/wake atau ganti jaringan, tersambung lagi dalam hitungan detik tanpa campur tangan. Perangkat yang offline tetap kebagian item saat online lagi.
4. **Tanpa batas ukuran praktis**: teks, gambar, dan file berukuran besar tetap bisa dikirim.
5. **Ringan**: server < 30 MB RAM, app hemat baterai dan memori.
6. **UI bagus dan native** di tiap platform, dengan status koneksi dan log viewer bawaan.

### Bukan tujuan

- Mode P2P / WebRTC.
- App iOS (Universal Clipboard Apple sudah menyinkronkan Mac ke iPhone).
- Multi-user publik, registrasi terbuka.
- Pencarian di sisi server (server tidak bisa membaca isi, pencarian dilakukan di client).

## 2. Kondisi sekarang (hasil pengecekan VPS tencent, clip.yikz.dev)

| Temuan | Dampak |
|---|---|
| Container Java memakai ~380 MB dari 2 GB RAM VPS | Paling boros di VPS, sisa RAM tinggal ~93 MB |
| `CC_MAX_CLIPBOARD_SIZE_LIMIT_IN_BYTES=10485760` | Batas 10 MB |
| nginx tanpa `proxy_read_timeout` (default 60 detik) | WebSocket diputus nginx bila 60 detik tanpa trafik, misalnya saat Mac App Nap |
| STOMP + ActiveMQ + seluruh payload dalam satu frame | Data besar harus muat di memori, heartbeat STOMP rapuh |
| Desktop Python (PyInstaller) | Tidak bereaksi pada event sleep/wake dan perubahan jaringan, UI tidak native, log hanya di Console.app |
| Disk VPS: 40 GB, terpakai 28 GB, sisa 11 GB | Cukup untuk history 5 GB, perlu pengaman disk |

## 3. Arsitektur

```
 Mac (Swift)      Windows (C#)      Android (Kotlin)      Browser (Svelte)
      \                 |                  |                   /
       \______ WSS (realtime) + HTTPS (upload/download) ______/
                                |
                          Server Go
                          - auth + perangkat
                          - relay realtime
                          - history terenkripsi (SQLite + blob di disk)
                          - retensi 7 hari / 5 GB
                          - web UI ter-embed
```

Server menyimpan dan meneruskan data yang sudah terenkripsi. Server tidak bisa membaca isi clipboard (enkripsi end-to-end).

### Struktur repo (monorepo baru, private)

```
protocol/   spec protokol + test vectors (JSON) yang wajib lolos di semua platform
server/     Go
web/        Svelte + Vite, hasil build di-embed ke binary server
apple/      Swift package ClipCore + app macOS
android/    Kotlin + Jetpack Compose
windows/    C# .NET + WinUI 3
deploy/     docker-compose.yml, contoh config nginx
```

## 4. Protokol

### 4.1 Autentikasi

- Satu akun (username + password) dibuat lewat env var saat server pertama kali jalan.
- Login `POST /api/login` mengembalikan **device token** berumur panjang. Token disimpan di Keychain (Mac), Credential Locker (Windows), EncryptedSharedPreferences/Keystore (Android).
- Token bisa dicabut per perangkat dari web UI.

### 4.2 Enkripsi end-to-end

- Kunci = PBKDF2-HMAC-SHA256(password enkripsi, salt dari server, 600.000 iterasi, 32 byte). Dipilih karena tersedia bawaan di semua platform (CryptoKit/CommonCrypto, javax.crypto, .NET, WebCrypto). Kunci diturunkan sekali lalu disimpan di secure storage perangkat. Password enkripsi terpisah dari password login dan tidak pernah dikirim ke server.
- Setiap payload, setiap chunk, dan setiap meta dienkripsi AES-256-GCM dengan nonce acak 12 byte.
- Yang terlihat server hanya: id item, waktu, ukuran, jumlah chunk, id perangkat asal, jenis kasar (`text`, `image`, `files`), status pin. Isi, nama file, MIME, preview, dan thumbnail ikut terenkripsi.

### 4.3 Transport

- WebSocket `wss://clip.yikz.dev/ws` untuk event realtime (pesan kontrol JSON kecil).
- HTTPS untuk data besar: upload dan download chunk. Lebih mudah di-resume dan tidak mengganggu heartbeat WebSocket.

Pesan kontrol utama (WebSocket):

| Pesan | Arah | Isi |
|---|---|---|
| `hello` | client ke server | device id, nama, platform, versi, `last_seq` |
| `welcome` | server ke client | seq terkini, daftar perangkat online |
| `presence` | server ke client | perangkat online/offline |
| `clip` | dua arah | id, seq, asal, jenis, ukuran, jumlah chunk, meta terenkripsi, payload inline bila kecil |
| `clip_deleted` / `clip_pinned` | server ke client | perubahan status item history |
| `ping` / `pong` | dua arah | heartbeat |

Endpoint HTTP:

| Endpoint | Fungsi |
|---|---|
| `POST /api/login` | login, dapat device token |
| `GET /api/devices`, `DELETE /api/devices/{id}` | daftar perangkat, cabut token |
| `GET /api/history?before=<seq>&limit=100` | daftar item (meta terenkripsi, untuk ditampilkan dan dicari di client) |
| `PUT /api/items/{id}/chunks/{n}` | upload chunk (idempoten, bisa diulang) |
| `POST /api/items/{id}/commit` | tandai upload selesai, server lalu menyiarkan `clip` |
| `GET /api/items/{id}/chunks/{n}` | download chunk |
| `GET /api/items/{id}/thumb` | thumbnail terenkripsi (gambar) |
| `POST /api/items/{id}/pin`, `DELETE /api/items/{id}` | pin / hapus item |
| `GET /api/storage` | pemakaian storage, jumlah item |

### 4.4 Heartbeat dan reconnect

- Server mengirim `ping` tiap 20 detik. Client juga mengirim `ping` tiap 20 detik.
- Tidak ada trafik apa pun selama 45 detik: koneksi dianggap mati, langsung ditutup dan reconnect.
- Backoff reconnect: 0,5 s, 1 s, 2 s, 4 s, ... maksimal 30 s, dengan jitter acak.
- **Reset backoff dan reconnect seketika** saat: perangkat bangun dari sleep, jaringan berubah/tersedia lagi, app kembali ke foreground.
- nginx: `proxy_read_timeout 3600s; proxy_send_timeout 3600s; proxy_buffering off; client_max_body_size 8m;` (per chunk).

### 4.5 Sinkron setelah offline

- Server memberi nomor urut (`seq`) setiap item.
- Saat `hello`, client mengirim `last_seq`. Server mengirim `clip` untuk item yang terlewat (hanya meta).
- Hanya item **terbaru** yang otomatis ditaruh ke clipboard, dan hanya bila umurnya < 5 menit. Item lain cukup masuk daftar history di app, agar clipboard tidak "melompat" saat perangkat baru bangun setelah semalaman.

### 4.6 Data besar

- Payload <= 256 KB: dikirim inline di pesan `clip` dan disimpan server apa adanya.
- Payload > 256 KB: dipecah chunk 4 MB, di-upload lewat HTTPS, lalu `commit`.
- Penerima:
  - Total <= batas auto-unduh (default 50 MB, bisa diatur per perangkat): langsung diunduh dan ditaruh di clipboard.
  - Di atas batas: notifikasi "File X (200 MB) dari Android, unduh?". Item tetap bisa diunduh kapan saja dari history selama belum kedaluwarsa, walaupun perangkat asal sudah offline.
- Penerima memverifikasi hash SHA-256 (di meta terenkripsi) sebelum menaruh ke clipboard.
- Upload yang tidak di-commit dalam 1 jam dihapus otomatis.

### 4.7 Mencegah echo loop dan data sensitif

- Setelah menaruh item diterima ke clipboard, client menyimpan hash-nya. Perubahan clipboard dengan hash sama tidak dikirim balik.
- Item dari password manager tidak dikirim dan tidak masuk history: hormati `org.nspasteboard.ConcealedType` / `TransientType` (Mac), `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory=0` (Windows), `ClipDescription.EXTRA_IS_SENSITIVE` (Android).
- Deduplikasi: item dengan hash sama dengan item terakhir tidak dibuat ulang, hanya diperbarui waktunya.

## 5. History

### 5.1 Penyimpanan di server

- SQLite: tabel `items` (id, seq, device, kind, size, chunks, meta terenkripsi, created_at, pinned) dan tabel perangkat/token.
- Blob: `/data/blobs/<id>/<n>` (chunk terenkripsi) dan `/data/blobs/<id>/thumb`.
- Volume Docker `/data` di-mount ke disk VPS.

### 5.2 Retensi

Dijalankan tiap 15 menit dan setiap kali upload baru:

1. Hapus item non-pin yang lebih tua dari 7 hari.
2. Bila total > 5 GB, hapus item non-pin yang paling lama sampai di bawah 5 GB.
3. Item yang di-pin tidak kena aturan 7 hari, tapi tetap dihitung dalam 5 GB. Total item pin dibatasi 1 GB.
4. Pengaman disk: bila sisa disk VPS < 2 GB, tolak upload > 1 MB dan tampilkan peringatan di semua perangkat.

Semua angka bisa diatur via env: `CC_RETENTION_DAYS=7`, `CC_STORAGE_MAX_GB=5`, `CC_PINNED_MAX_GB=1`, `CC_MIN_FREE_DISK_GB=2`.

### 5.3 Pencarian dan tampilan di client

- Client menarik daftar meta (kecil, tanpa isi besar) lalu mendekripsi dan menyimpannya di cache lokal (SQLite di Android/Windows, SwiftData atau SQLite di Mac).
- Meta berisi: jenis, preview teks (maks 500 karakter), nama file, MIME, ukuran, dimensi gambar, perangkat asal, sumber app bila tersedia.
- Pencarian dan filter jenis (Teks, Link, Gambar, File) dilakukan di cache lokal, jadi instan.
- Thumbnail gambar diunduh lazy saat terlihat di layar. Isi besar baru diunduh saat item dipilih.

### 5.4 UI history (mengikuti pola Raycast Clipboard History)

- Kiri: daftar item dikelompokkan per hari (Hari ini, Kemarin, ...), ikon jenis, preview satu baris.
- Kanan: preview besar (teks, gambar, PDF, ikon file) + informasi: perangkat asal, jenis, ukuran, waktu.
- Atas: kolom filter teks + dropdown jenis.
- Aksi: Copy (taruh ke clipboard), Paste langsung (Mac/Windows), Pin, Hapus, Simpan sebagai file.

## 6. Server (Go)

- Go 1.25+, WebSocket `github.com/coder/websocket`, SQLite `modernc.org/sqlite` (tanpa CGO).
- Chunk dibaca dan ditulis secara streaming, tidak pernah seluruh file di memori.
- Log terstruktur (`log/slog`) ke stdout.
- Docker image distroless, target < 20 MB, RAM idle < 30 MB.
- Env: `CC_USERNAME`, `CC_PASSWORD`, `CC_LISTEN`, `CC_DATA_DIR`, plus env retensi di 5.2.

## 7. Web UI (Svelte)

- Login, status koneksi, daftar perangkat (online, terakhir terlihat, cabut token), pemakaian storage.
- Halaman history dengan layout 5.4.
- Kirim teks/gambar/file dari browser (paste atau drag and drop).
- Mode gelap/terang mengikuti sistem.

## 8. App macOS (Swift + SwiftUI)

- Menu bar app (`MenuBarExtra`) berisi status dan 10 item terakhir, plus jendela History (hotkey global, default ⌥⌘V, bisa diganti), Settings, dan Log.
- Deteksi perubahan clipboard: polling `NSPasteboard.changeCount` tiap 300 ms (tidak ada API notifikasi resmi, biayanya sangat kecil).
- WebSocket: `URLSessionWebSocketTask`. Upload/download: `URLSession` background-capable.
- Stabilitas: `NSWorkspace.didWakeNotification`, `willSleepNotification`, `NWPathMonitor`. Aktivitas koneksi ditandai `ProcessInfo.beginActivity` agar tidak terkena App Nap.
- File diterima disimpan di `~/Library/Caches/ClipCascade/` lalu URL-nya ditaruh ke pasteboard (bisa di-paste di Finder). Cache dibersihkan otomatis.
- Launch at login via `SMAppService`.
- Log: `os.Logger` + ring buffer di memori + file log berotasi, ditampilkan di jendela Log dengan filter level dan tombol ekspor.
- Distribusi: build lokal dengan Xcode, tanpa Apple Developer. Universal Clipboard ke iPhone berjalan otomatis.
- Logic protokol, kripto, reconnect, dan cache history ada di Swift package `ClipCore` agar bisa dites terpisah.

## 9. App Android (Kotlin + Jetpack Compose)

- Foreground service dengan notifikasi status (tersambung / reconnecting), tipe `dataSync` atau `specialUse` sesuai aturan Android 14+.
- WebSocket OkHttp dengan `pingInterval(20s)`, `ConnectivityManager.NetworkCallback` untuk reconnect saat jaringan berubah. Upload/download besar via WorkManager.
- Menerima: otomatis, `ClipboardManager.setPrimaryClip` dari service (masuk ke riwayat Gboard).
- Mengirim (Android melarang baca clipboard di background):
  - Quick Settings tile "Kirim clipboard"
  - Tombol di notifikasi foreground service
  - Share target (teks, gambar, file dari app mana pun)
  - Opsional: mode otomatis via izin `READ_LOGS` (diberikan lewat adb sekali) seperti versi sekarang
- Layar History (layout 5.4 versi mobile: daftar, tap untuk preview, tahan untuk pin/hapus).
- Minta pengecualian battery optimization saat setup.
- Log viewer di dalam app.

## 10. App Windows (C# .NET + WinUI 3)

- Tray app (H.NotifyIcon) plus jendela History (hotkey global), Settings, dan Log bergaya Fluent.
- Deteksi clipboard: `AddClipboardFormatListener` (event-based, bukan polling).
- Stabilitas: `SystemEvents.PowerModeChanged` (resume), `NetworkChange.NetworkAvailabilityChanged`.
- Item diterima otomatis masuk riwayat Win+V.
- Autostart via registry `Run` atau startup task.
- Installer: MSIX sideload atau zip portable.

## 11. Deploy dan migrasi

1. Jalankan server baru di VPS tencent pada port lain dengan subdomain `clip2.yikz.dev`, volume `/data`, config nginx dengan timeout panjang.
2. Uji semua perangkat di `clip2`.
3. Setelah stabil, arahkan `clip.yikz.dev` ke server baru, matikan container Java (membebaskan ~350 MB RAM).

Perbaikan cepat sementara untuk server lama: tambahkan `proxy_read_timeout 3600s; proxy_send_timeout 3600s;` di config nginx `clip.yikz.dev`.

## 12. Urutan pengerjaan

| Fase | Isi | Selesai bila |
|---|---|---|
| 0 | Spec protokol + test vectors kripto dan format pesan | Test vectors lengkap untuk enkripsi, chunk, meta, dan pesan kontrol |
| 1 | Server Go + web UI (termasuk history) + docker | Dua tab browser saling sinkron teks dan file 200 MB, history tampil, retensi teruji, RAM server < 30 MB |
| 2 | App macOS | Sleep 1 jam lalu wake: tersambung < 5 detik, copy dari browser langsung muncul di Raycast, jendela History berfungsi |
| 3 | App Android | Sinkron dua arah dengan Mac, bertahan semalaman tanpa putus permanen, layar History berfungsi |
| 4 | App Windows | Sinkron dengan Mac dan Android, masuk riwayat Win+V, jendela History berfungsi |
| 5 | Migrasi ke `clip.yikz.dev`, matikan server lama | Semua perangkat memakai server baru |

## 13. Keputusan terbuka

- Batas default auto-unduh file (usul 50 MB).
- Hotkey default jendela History (usul ⌥⌘V, agar tidak bentrok dengan Raycast).
