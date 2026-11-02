# Panduan Setup DBSync Platform — Langkah demi Langkah

Panduan ini membawa Anda dari nol sampai seluruh aplikasi (API, Worker, Frontend, dan opsional CDC) berjalan dan sudah terbukti menyinkronkan data. Ikuti urutannya. Setiap bagian punya **cek keberhasilan**.

> Perintah ditulis untuk **Windows + PowerShell** (sesuai lingkungan XAMPP). Padanan Linux disebutkan bila berbeda.

## Daftar isi
1. [Gambaran komponen](#1-gambaran-komponen)
2. [Prasyarat](#2-prasyarat)
3. [Setup development (tanpa Docker untuk aplikasi)](#3-setup-development)
4. [Menyiapkan user database sumber/target](#4-menyiapkan-user-database-sumbertarget)
5. [Tur pertama: sinkronisasi pertama Anda](#5-tur-pertama)
6. [Mode Real-Time CDC (Kafka + Debezium)](#6-mode-real-time-cdc)
7. [Deployment server / production](#7-deployment-production)
8. [Verifikasi akhir (checklist)](#8-verifikasi-akhir)
9. [Troubleshooting](#9-troubleshooting)
10. [Batasan yang perlu Anda ketahui](#10-batasan)

---

## 1. Gambaran komponen

| Komponen | Folder | Port dev | Fungsi |
|---|---|---|---|
| Frontend (Nuxt) | `frontend/` | 13000 | UI dashboard |
| API (ASP.NET Core) | `backend/src/DBSync.API` | 18080 | REST + SignalR + auth |
| Worker (.NET) | `backend/src/DBSync.Worker` | – | Eksekusi sync, scheduler (Quartz), CDC consumer |
| Database konfigurasi | PostgreSQL (Docker) | 15432 | Konfigurasi, event, checkpoint, audit |
| Kafka + Kafka Connect/Debezium | Docker | 19092 / 18091 | **Hanya** untuk mode CDC |

Untuk **mode Scheduled**, Anda hanya butuh: PostgreSQL + API + (Worker atau `Worker:Embedded=true`) + Frontend. Kafka tidak diperlukan.

### Daftar port default (sengaja di rentang yang jarang dipakai)

| Layanan | Port host |
|---|---|
| Frontend (Nuxt dev) | 13000 |
| API | 18080 |
| PostgreSQL konfigurasi (Docker) | 15432 |
| Kafka (listener host) | 19092 |
| Kafka Connect / Debezium | 18091 |
| MySQL demo | 13307 |
| PostgreSQL demo | 15434 |
| SQL Server demo | 11444 |
| Nginx production (HTTP / HTTPS) | 18088 / 18443 |

Semua bisa diubah: compose (ports:), Urls di appsettings API, `--port` di rontend/package.json, Cors:Origins, dan NUXT_PUBLIC_API_BASE. Cek port bebas: `Get-NetTCPConnection -State Listen | ? LocalPort -eq 18080`

## 2. Prasyarat

| Software | Versi | Cek |
|---|---|---|
| .NET SDK | 10.x | `dotnet --version` |
| Node.js | 22 LTS atau lebih baru | `node -v` |
| PostgreSQL (konfigurasi DBSync) | 15+ (diuji 18) | `psql --version` |
| Docker Desktop | opsional; wajib bila memakai CDC | `docker --version` |

Database yang akan disinkronkan (salah satu/lebih): MySQL/MariaDB, PostgreSQL, SQL Server. Anda bisa memakai database yang sudah ada atau container demo (bagian 6.1).

## 3. Setup development

### 3.1 Buat database konfigurasi PostgreSQL
Login sebagai superuser PostgreSQL, lalu:

```powershell
$env:PGPASSWORD = "<password postgres>"
psql -h localhost -p 5432 -U postgres -c "CREATE USER dbsync WITH PASSWORD 'dbsync_dev_password';"
psql -h localhost -p 5432 -U postgres -c "CREATE DATABASE dbsync OWNER dbsync;"
```

Bila PostgreSQL belum berjalan di Windows: buka *Services* → start `postgresql-x64-<versi>` (perlu admin), **atau** gunakan Docker:

```powershell
docker compose -f infrastructure/docker/docker-compose.yml up -d postgres
```
(Container ini sudah membuat user/db `dbsync` dengan password `dbsync_dev_password` dan **mendengarkan di port 15432** — bukan 5432 — agar tidak bentrok dengan PostgreSQL lain di mesin Anda. Default `ConnectionStrings:ConfigDb` pada appsettings memakai port 15432. Jika memakai PostgreSQL native di 5432, set `$env:ConnectionStrings__ConfigDb` ke port tersebut.)

✅ Cek: `psql -h localhost -U dbsync -d dbsync -c "select 1"` mengembalikan `1`.

### 3.2 Konfigurasi rahasia
`backend/src/DBSync.API/appsettings.Development.json` dan `…/DBSync.Worker/appsettings.Development.json` sudah berisi nilai **khusus development** (master key dev, JWT key dev, admin `admin` / `Admin#12345`). **Jangan dipakai di server.**

Untuk kunci sendiri:

```powershell
./infrastructure/scripts/generate-secrets.ps1
```
Hasilnya (`DBSYNC_MASTER_KEY`, `DBSYNC_JWT_KEY`) bisa dipasang lewat environment variable (tanpa mengubah file):

```powershell
$env:Security__MasterKeys__v1 = "<DBSYNC_MASTER_KEY>"
$env:Jwt__SigningKey          = "<DBSYNC_JWT_KEY>"
$env:Seed__AdminPassword      = "<password admin awal>"
$env:ConnectionStrings__ConfigDb = "Host=localhost;Port=15432;Database=dbsync;Username=dbsync;Password=<pw>"
```

> ⚠️ **Master key harus sama** untuk API dan Worker, dan harus Anda backup terpisah. Jika hilang, password koneksi database yang tersimpan tidak bisa didekripsi. Mengganti key setelah ada koneksi tersimpan membuat koneksi lama tak terbaca — gunakan versi kunci (`v2`) untuk rotasi: tambah `Security__MasterKeys__v2`, set `Security__CurrentKeyVersion=v2`; kunci `v1` tetap dibutuhkan untuk mendekripsi data lama.

Port PostgreSQL non-default (mis. 55432): ubah `Port=` pada connection string.

### 3.3 Build & jalankan backend
```powershell
cd backend
dotnet build DBSync.sln
dotnet test tests/DBSync.UnitTests          # 22 test harus lulus

# Terminal 1 — API (Development: worker + scheduler ikut berjalan di proses yang sama)
dotnet run --project src/DBSync.API
```
Saat pertama kali jalan, API membuat seluruh tabel konfigurasi, 5 role, dan user admin.

✅ Cek: 
```powershell
Invoke-RestMethod http://localhost:18080/health        # Healthy
Invoke-RestMethod http://localhost:18080/health/db     # status: ok
```
Log harus memuat `User admin awal 'admin' dibuat` dan `RunQueueWorker ... dimulai`.

**Worker terpisah (disarankan di production):** set `Worker__Embedded=false` pada API, lalu di terminal lain:
```powershell
dotnet run --project src/DBSync.Worker
```
Worker memakai database konfigurasi & master key yang sama. Anda boleh menjalankan lebih dari satu Worker: run diambil memakai `FOR UPDATE SKIP LOCKED` + lease + fencing token, jadi tidak ada run yang dijalankan dobel.

### 3.4 Build & jalankan frontend
```powershell
cd frontend
npm install
npm run dev            # http://localhost:13000
```
Alamat API diatur lewat `NUXT_PUBLIC_API_BASE` (default `http://localhost:18080`):
```powershell
$env:NUXT_PUBLIC_API_BASE = "http://localhost:18080"
```
CORS API mengizinkan `http://localhost:13000` (ubah di `Cors:Origins`).

✅ Cek: buka http://localhost:13000 → login `admin` / `Admin#12345` → Dashboard tampil dan badge **Live (SignalR)** hijau. **Segera ganti password** di *System Settings*.

## 4. Menyiapkan user database sumber/target

DBSync **tidak boleh** memakai `root` / `postgres` / `sa`. Buat user khusus. Hak minimum:

**MySQL / MariaDB**
```sql
CREATE USER 'dbsync'@'%' IDENTIFIED BY '<pw>';
GRANT SELECT, INSERT, UPDATE, DELETE ON hrga.* TO 'dbsync'@'%';
-- hanya untuk CDC:
GRANT SELECT, RELOAD, SHOW DATABASES, REPLICATION SLAVE, REPLICATION CLIENT ON *.* TO 'dbsync'@'%';
```
**PostgreSQL**
```sql
CREATE ROLE dbsync LOGIN PASSWORD '<pw>';
GRANT USAGE ON SCHEMA public TO dbsync;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO dbsync;
-- hanya untuk CDC:
ALTER ROLE dbsync REPLICATION;
```
**SQL Server**
```sql
CREATE LOGIN dbsync WITH PASSWORD = '<pw>';  USE hrga;  CREATE USER dbsync FOR LOGIN dbsync;
ALTER ROLE db_datareader ADD MEMBER dbsync;  ALTER ROLE db_datawriter ADD MEMBER dbsync;
-- hanya jika tabel punya kolom IDENTITY yang nilainya ikut disinkronkan:
GRANT ALTER ON SCHEMA::dbo TO dbsync;
```

Aturan struktur (DBSync **tidak** membuat tabel/DDL otomatis):
- Tabel harus **sudah ada di kedua sisi**, dengan **primary key** (composite key didukung).
- Kolom bernama sama dipetakan otomatis; yang berbeda dipetakan manual di *Column Mapping*.
- Tipe ENUM/SET/ARRAY/spatial/XML ditandai **tidak didukung** — nonaktifkan kolom tsb di mapping.

## 5. Tur pertama

Contoh ini memakai database demo yang sama dengan yang dipakai saat pengujian (MariaDB ⇄ PostgreSQL). Ganti dengan database Anda sendiri bila perlu.

1. **Connections → Add Connection.** Isi Endpoint A (mis. MySQL `hrga`) → **Test Connection** harus hijau → Simpan. Ulangi untuk Endpoint B (mis. PostgreSQL `hrga_replica`).
   ✅ Kolom *Status* menjadi `CONNECTED`.
2. **Create Group** (wizard):
   - Langkah 1: nama, pilih A dan B, klik **Test** keduanya → Lanjut (group dibuat sebagai `DRAFT`).
   - Langkah 2 **Pilih Tabel**: tabel BOTH + `VALID/WARNING` bisa dicentang; tabel tanpa PK ditolak dengan alasan. Klik *Tambahkan* atau *Pilih semua yang memenuhi syarat*.
   - Langkah 3 mapping kolom (opsional, ada auto-map).
   - Langkah 4: arah (`A→B`, `B→A`, atau dua arah), mode **Scheduled**, strategi konflik (default **Manual**), interval.
   - Langkah 5 **Validasi**: tidak boleh ada baris `error`. Warning (mis. presisi, FK) boleh.
   - Langkah 6 **Aktifkan**.
3. Buka detail group → tab **Jobs → Initial Sync** → pilih sumber (A) → **Jalankan**.
   ✅ Progress bar bergerak; selesai dengan status `SUCCESS`; data muncul di database tujuan. Status tabel menjadi `COMPLETED`.
   > Sinkronisasi terjadwal/CDC **melewati** tabel yang initial sync-nya belum selesai.
4. Ubah data di salah satu sisi (INSERT/UPDATE/DELETE), lalu tab Jobs → **Sync Now** (atau tunggu interval).
   ✅ Perubahan muncul di sisi lain. Run kedua tanpa perubahan harus menunjukkan 0 insert/update/delete (bukti tidak ada loop A→B→A).
5. **Uji konflik:** ubah baris yang sama di A dan B dengan nilai berbeda → *Sync Now* → menu **Conflict Management** → *Tinjau* → pilih versi A atau B → versi itu disebarkan ke sisi lain.
6. Lihat **Sync History**, **Event Queue & Failed**, **Audit Logs** (butuh role Auditor/Administrator).

### Cara kerja deteksi perubahan (mode Scheduled)
DBSync menyimpan *hash baseline* per baris (tabel `sync_row_states`). Tiap siklus ia membandingkan hash A, hash B, dan baseline: hanya A berubah → A→B; hanya B berubah → B→A; keduanya berubah berbeda → konflik; baris ada di baseline tapi hilang di satu sisi → DELETE. Konsekuensi: tidak butuh kolom `updated_at`, DELETE terdeteksi tanpa tombstone, tetapi tiap siklus **membaca seluruh tabel** (hash dihitung di DBSync). Cocok untuk tabel kecil–menengah; untuk tabel sangat besar gunakan mode CDC.

## 6. Mode Real-Time CDC

CDC memakai Debezium → Kafka → consumer DBSync (di Worker). Perlu Docker.

### 6.1 Jalankan infrastruktur
```powershell
docker compose -f infrastructure/docker/docker-compose.yml up -d            # postgres, kafka, kafka-connect
docker compose -f infrastructure/docker/docker-compose.yml --profile demo up -d   # + MySQL/PG/SQL Server demo (opsional)
```
✅ Cek: `Invoke-RestMethod http://localhost:18091/` mengembalikan versi Kafka Connect; `docker ps` menampilkan 3+ container `Up`.

> Database demo: MySQL `localhost:13307` (db `hrga`, user `dbsync`/`dbsync_demo_pw`), PostgreSQL `localhost:15434` (db `hrga_replica`, user `dbsync`/`dbsync_demo_pw`). Alamat database bisa berbeda antara DBSync (di host) dan Debezium (di container). Isi **Host/Port** koneksi dengan alamat dari sisi host (mis. `localhost` / `3307`), lalu tambahkan opsi koneksi `cdcHost` dan `cdcPort` (mis. `dbsync-demo-mysql` / `3306`, nama container di jaringan Docker) via API (`connectionOptions`). Bila opsi ini kosong, Debezium memakai Host/Port yang sama. Bila port host bentrok, ubah `DBSYNC_PG_PORT` (PostgreSQL konfigurasi) atau port demo di compose.

### 6.2 Aktifkan CDC pada DBSync
Set pada **API dan Worker**:
```powershell
$env:Cdc__Enabled = "true"
$env:Cdc__KafkaBootstrap = "localhost:19092"            # dilihat dari host
$env:Cdc__KafkaBootstrapForConnect = "kafka:19092"     # dilihat dari container Connect
$env:Cdc__KafkaConnectUrl = "http://localhost:18091"
```
Restart API/Worker.

### 6.3 Prasyarat CDC per vendor
- **MySQL:** `log_bin=ON`, `binlog_format=ROW`, `binlog_row_image=FULL`, user punya hak replikasi (lihat bagian 4). Container demo sudah benar. XAMPP/MariaDB default biasanya `log_bin=OFF` — aktifkan di `my.ini` (`log-bin=mysql-bin`, `binlog_format=ROW`, `server-id=1`) lalu restart.
- **PostgreSQL:** `wal_level=logical` (restart), user `REPLICATION`, plus `GRANT CREATE ON DATABASE <db> TO dbsync;` dan user harus **pemilik tabel** (`ALTER TABLE ... OWNER TO dbsync`) karena Debezium membuat publication sendiri (tanpa ini connector gagal: *Unable to create filtered publication*). Pantau replication slot agar tidak menahan WAL (lihat tab CDC pada group).
- **SQL Server:** `EXEC sys.sp_cdc_enable_db;` lalu per tabel `EXEC sys.sp_cdc_enable_table @source_schema=N'dbo', @source_name=N'employees', @role_name=NULL;` dan **SQL Server Agent harus Running**. Tidak tersedia di semua edisi.

### 6.4 Buat group CDC
1. Buat group seperti bagian 5 tetapi pilih mode **Real-Time CDC**.
2. Saat **Validasi/Activate**, DBSync memeriksa prasyarat di atas; item merah berisi petunjuk perbaikan. Activate membuat connector Debezium `dbsync-<connectionId>` (satu per koneksi).
3. **Urutan benar:** connector dibuat saat *Activate* (mulai menangkap perubahan) → **baru jalankan Initial Sync**. Perubahan yang terjadi saat initial sync ditangkap CDC dan diselaraskan oleh baseline hash. Event CDC untuk tabel yang initial sync-nya belum selesai diabaikan.
4. Tab **CDC** pada detail group menampilkan status connector (`RUNNING`) dan hasil prasyarat.

✅ Cek: ubah satu baris di sumber → muncul di tujuan dalam beberapa detik → *Event Queue* menampilkan event `SUCCESS` dan **tidak** ada event balik (echo diredam oleh baseline).

Jaminan: *at-least-once + idempotent*. Offset Kafka di-commit hanya setelah event tersimpan/diterapkan; event gagal permanen masuk **Dead Letter** (bisa di-retry dari UI). Group yang di-**pause** tidak kehilangan event — disimpan `PENDING` dan diproses setelah resume.

## 7. Deployment production

1. Siapkan server (Linux/Windows) dengan Docker. Buat `.env` dari `.env.example` — isi `DBSYNC_PG_PASSWORD`, `DBSYNC_MASTER_KEY`, `DBSYNC_JWT_KEY`, `DBSYNC_ADMIN_PASSWORD`, `DBSYNC_PUBLIC_URL` (mis. `https://dbsync.perusahaan.local`). `.env` **tidak boleh** masuk repository.
2. Sertifikat TLS: taruh `fullchain.pem` dan `privkey.pem` di `infrastructure/nginx/certs/`.
3. Jalankan:
```bash
docker compose --env-file .env -f infrastructure/docker/docker-compose.yml -f infrastructure/docker/docker-compose.prod.yml up -d --build
```
   Hasil: proxy Nginx (host port 18088 untuk HTTP dan 18443 untuk HTTPS; ubah via `DBSYNC_HTTP_PORT`/`DBSYNC_HTTPS_PORT` dan samakan redirect di `infrastructure/nginx/nginx.conf`) → frontend + API (`/api`, `/hubs`), Worker terpisah, PostgreSQL/Kafka **tidak** diekspos.
4. Login → ganti password admin → buat user per role (Administrator, Operator, Viewer, Auditor).
5. **Backup:** (a) database `dbsync` (`pg_dump`) terjadwal, (b) master key & JWT key di tempat terpisah, (c) konfigurasi connector Kafka Connect. **Uji restore** berkala.
6. Worker di jaringan cabang: jalankan `DBSync.Worker` di jaringan yang bisa menjangkau database, dengan akses keluar ke PostgreSQL konfigurasi (via VPN/TLS) dan master key yang sama. Jangan membuka port database ke internet.
7. Tanpa Docker: `dotnet publish backend/src/DBSync.API -c Release` dan `…/DBSync.Worker`, jalankan sebagai Windows Service (`sc create`/NSSM) atau systemd; frontend: `npm run generate` lalu sajikan `.output/public` lewat IIS/Nginx dengan fallback ke `index.html`.

## 8. Verifikasi akhir

- [ ] `dotnet test` → 22 lulus
- [ ] Login UI berhasil, badge Live hijau
- [ ] Test Connection hijau untuk tiap vendor yang dipakai
- [ ] Initial sync `SUCCESS`; jumlah baris sumber = tujuan
- [ ] INSERT, UPDATE, DELETE tersinkron; run kedua = 0 perubahan
- [ ] Konflik terdeteksi & bisa diselesaikan
- [ ] Viewer tidak bisa Sync Now / melihat Audit (HTTP 403)
- [ ] `Audit Logs` tidak memuat password
- [ ] (CDC) event tiba < beberapa detik, tidak ada loop
- [ ] Backup & restore database `dbsync` sudah dicoba

## 9. Troubleshooting

| Gejala | Penyebab & solusi |
|---|---|
| API gagal start: `Jwt:SigningKey wajib…` | Isi `Jwt__SigningKey` (≥32 karakter). |
| `Security:MasterKeys:v1 belum dikonfigurasi` | Set `Security__MasterKeys__v1` (base64 32 byte). |
| `password authentication failed` saat start | Connection string `ConfigDb` salah / user `dbsync` belum dibuat (3.1). |
| Login selalu gagal | Admin hanya dibuat saat tabel `users` kosong dan `Seed__AdminPassword` terisi. Reset: hapus isi tabel `users` & restart. |
| UI: "Tidak dapat terhubung ke API" | API tidak jalan / `NUXT_PUBLIC_API_BASE` salah / origin tidak ada di `Cors:Origins`. |
| Test connection gagal MySQL `Access denied` | Host pengguna (`'user'@'host'`) tidak cocok; buat user untuk `%` atau host yang tepat. |
| SQL Server `certificate chain` | Centang *Trust server certificate* pada koneksi (dev) atau pasang sertifikat valid. |
| Tabel tidak bisa dipilih | Lihat alasan di kolom Struktur: tanpa PK / tidak ada di salah satu sisi / tipe tak didukung. |
| Run `QUEUED` terus | Tidak ada Worker hidup (cek *Worker Status*) atau masih ada run lain pada group yang sama. |
| Run berhenti saat Worker dimatikan | Normal: lease kedaluwarsa (60 dtk) lalu run dikembalikan ke antrean; Initial Sync lanjut dari checkpoint. |
| Event `DEAD_LETTER` | Buka *Event Queue → Dead letter*: lihat `errorMessage` (mis. string terlalu panjang, NOT NULL, FK). Perbaiki penyebab, klik **Retry**. |
| `TYPE_INCOMPATIBLE` / `UNSUPPORTED_TYPE` | Perbaiki di Column Mapping (nonaktifkan kolom) atau ubah tipe di database. |
| CDC: connector tidak muncul | `Cdc:Enabled=true`? Kafka Connect hidup di `:18091`? Lihat pesan error di tab CDC. |
| CDC PostgreSQL: disk WAL membesar | Replication slot tertinggal; pastikan connector `RUNNING` atau hapus slot yang tak dipakai. |
| Sequence/auto-increment tujuan tertinggal | DBSync menyalin nilai kunci apa adanya, tidak memajukan sequence. Setelah cut-over: `SELECT setval(...)` (PG) / `ALTER TABLE … AUTO_INCREMENT` (MySQL). |

## Catatan operasional (hasil audit)

- **Form koneksi:** setelah host, port, dan username terisi, daftar database dimuat otomatis dari server dan bisa dipilih dari dropdown (tombol *Muat database* untuk memuat ulang). Password boleh kosong (mis. MariaDB XAMPP `root`); pada edit, password kosong berarti *tidak diubah* (centang *Setel password menjadi kosong* bila memang ingin mengosongkan).
- **Rate limit login:** maks. 10 percobaan login/refresh per menit per IP (respons `429`). Di belakang proxy, pastikan IP asli diteruskan (`X-Forwarded-For`; compose produksi sudah menyetel `ASPNETCORE_FORWARDEDHEADERS_ENABLED`).
- **Header keamanan:** API mengirim `nosniff`, `X-Frame-Options: DENY`, CSP ketat, dan tidak menampilkan header `Server`; Nginx produksi menambahkan CSP untuk frontend.
- **Retensi data:** worker membersihkan harian: event `SUCCESS/SKIPPED` > 30 hari, `processed_events` > 14 hari (harus lebih panjang dari retensi topic Kafka, default 7 hari), run > 90 hari, audit > 365 hari. Atur lewat `Retention:EventDays|ProcessedEventDays|RunDays|AuditDays`. Event `DEAD_LETTER`, `CONFLICT`, dan `RETRY` tidak pernah dihapus otomatis.
- **Satu run aktif per grup** dijamin oleh indeks unik di database; klik ganda *Sync Now* atau dua scheduler tidak membuat run ganda.
- **Menghentikan/menghapus grup CDC** memperkecil atau menghapus connector Debezium otomatis. **Replication slot PostgreSQL tidak dihapus otomatis** (agar tidak ada risiko kehilangan posisi); bila connector sudah tidak dipakai lagi, hapus slot manual agar WAL tidak menumpuk: `SELECT pg_drop_replication_slot('<nama_slot>');` (lihat `SELECT * FROM pg_replication_slots;`).
- **Kolom generated/computed** (SQL Server `AS (...)`, PostgreSQL generated) tidak ditulis ke sisi yang menghitungnya dan tidak ikut perbandingan perubahan.
- **Spasi di akhir teks** dianggap perubahan untuk `VARCHAR/TEXT`; hanya kolom `CHAR(n)` bertipe tetap yang diabaikan padding-nya.
- **Data awal (Initial Sync) — cek dulu, lalu pilih cara.** Di tab *Job* ada tiga cara: **Dua arah — saling melengkapi** (disarankan untuk grup dua arah), **Salin A → B**, atau **Salin B → A**. Tombol *Cek data A vs B* membandingkan kedua database tanpa mengubah apa pun dan menampilkan, per tabel: baris yang hanya ada di A, hanya di B, identik, dan berbeda (klik angkanya untuk melihat barisnya, termasuk kolom mana yang tidak sama).
- **Model untuk baris yang ID-nya sama tapi isinya berbeda** (mode dua arah): *Putuskan manual* (jadi konflik, aman), *Saling mengisi kolom kosong* (kolom kosong di satu sisi diisi dari sisi lain; kolom yang sama-sama terisi tapi beda mengikuti pilihan cadangan: konflik / pakai A / pakai B), *Pakai A untuk semua*, atau *Pakai B untuk semua*. Di menu **Konflik** tersedia tombol *Pakai A/B untuk semua* bila konfliknya banyak.
- **Mode satu arah tidak menimpa diam-diam:** untuk grup dua arah, bawaannya "biarkan data yang sudah ada di tujuan". Menimpa harus dipilih eksplisit dan jumlah baris yang tertimpa ditampilkan lebih dulu.
- **NULL ke kolom NOT NULL ber-DEFAULT:** bila kolom di satu database boleh NULL dan di database lain wajib diisi dengan DEFAULT literal (mis. `int NOT NULL DEFAULT 0`), NULL otomatis diganti nilai default itu (dan dianggap setara saat membandingkan). Ubah ke *Ketat* di **Mapping Kolom → Jika NULL** bila Anda lebih suka baris seperti itu gagal. Tanpa DEFAULT, baris berisi NULL akan gagal; isi nilainya di sumber atau jadikan kolom tujuan nullable.
- **Tipe kolom berbeda** (mis. teks di A, datetime di B) dibandingkan dengan tipe bersama (tanggal/angka), sehingga `2023-01-10 00:00:00` dan `2023-01-10T00:00:00` tidak dianggap berbeda.- **Dead letter & perbaikan:** event gagal yang penyebabnya sudah diperbaiki bisa dikembalikan sekaligus lewat tombol *Coba ulang semua* di menu Antrean & Gagal. Event lama otomatis dianggap selesai bila barisnya sudah identik di kedua sisi, dan otomatis dicoba ulang saat mapping kolom tabel diubah.
- **CDC SQL Server (teruji):** `sp_cdc_enable_db` + `sp_cdc_enable_table ... @role_name=NULL`, SQL Server Agent berjalan. User DBSync cukup `db_datareader` + `db_datawriter`; pemeriksaan Agent memakai izin tingkat server dan bila tidak tersedia ditandai "tidak dapat dipastikan" (bukan gagal).
- **SQL Server:** user cukup `db_datareader` + `db_datawriter` (+ `ALTER` pada schema bila ada kolom IDENTITY); tidak butuh `VIEW DATABASE PERFORMANCE STATE`.
## 10. Batasan

Sesuai spesifikasi V1: tanpa DDL otomatis, topologi pairwise (dua endpoint), tabel wajib PK, mode HYBRID ditolak (aturan kepemilikan belum dirancang), tanpa klaim *exactly-once* lintas database (yang dijamin: *at-least-once* + idempoten + baseline).

Hal praktis lain:
- Mode Scheduled membaca seluruh tabel tiap siklus (lihat 5); baseline disimpan di memori selama siklus.
- Perbandingan nilai lintas vendor dinormalisasi (boolean, desimal, tanggal ms, JSON). Perbedaan presisi (mis. `datetime2` vs `datetime`) dapat memicu satu siklus pantulan; validasi memberi peringatan `PRECISION_LOSS`/`TIMEZONE`.
- Skema database konfigurasi dibuat dengan `EnsureCreated`. Sebelum upgrade versi yang mengubah skema, siapkan EF Core Migrations.
- Pengujian yang sudah dijalankan pada pengembangan ini: unit test; end-to-end **MariaDB 10.4 ⇄ PostgreSQL 18** (initial sync, INSERT/UPDATE/DELETE dua arah, konflik, loop prevention, RBAC, penyamaran secret). **Belum diuji dalam lingkungan pengembangan ini:** connector SQL Server, jalur CDC (Kafka/Debezium), tampilan UI di browser. Jalankan langkah 8 untuk vendor/mode yang akan Anda pakai sebelum production.
