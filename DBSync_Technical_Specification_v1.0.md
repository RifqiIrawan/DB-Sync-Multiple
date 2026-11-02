# DBSync Platform

## Dokumen Spesifikasi Teknis dan Arsitektur Sistem

  Atribut                      Nilai
  ---------------------------- ------------------------------------------------------
  Nama proyek                  DBSync Platform
  Nama dokumen                 Technical Specification & System Architecture
  Versi dokumen                1.0
  Status                       Baseline rancangan / siap menjadi acuan implementasi
  Tanggal                      2 November 2026
  Backend                      ASP.NET Core / C# / .NET 10 LTS
  Frontend                     Nuxt / Vue 3 / TypeScript
  Database konfigurasi         PostgreSQL
  Database yang disinkronkan   MySQL, PostgreSQL, SQL Server
  Mode sinkronisasi            Scheduled Incremental Sync dan Real-Time CDC
  Topologi                     Pairwise, dua database per synchronization group
  Deployment                   Web application dan background worker

------------------------------------------------------------------------

# Daftar Isi

1.  [Ringkasan Eksekutif](#1-ringkasan-eksekutif)
2.  [Tujuan dan Ruang Lingkup](#2-tujuan-dan-ruang-lingkup)
3.  [Konsep dan Terminologi](#3-konsep-dan-terminologi)
4.  [Kebutuhan Fungsional](#4-kebutuhan-fungsional)
5.  [Kebutuhan Non-Fungsional](#5-kebutuhan-non-fungsional)
6.  [Arsitektur Sistem](#6-arsitektur-sistem)
7.  [Struktur Repository](#7-struktur-repository)
8.  [Desain Database Konfigurasi](#8-desain-database-konfigurasi)
9.  [Desain Connector Database](#9-desain-connector-database)
10. [Desain Synchronization Engine](#10-desain-synchronization-engine)
11. [Initial Synchronization](#11-initial-synchronization)
12. [Scheduled Incremental
    Synchronization](#12-scheduled-incremental-synchronization)
13. [Real-Time CDC](#13-real-time-cdc)
14. [Bidirectional Synchronization dan Loop
    Prevention](#14-bidirectional-synchronization-dan-loop-prevention)
15. [Conflict Detection dan
    Resolution](#15-conflict-detection-dan-resolution)
16. [Data Type Mapping](#16-data-type-mapping)
17. [API Specification](#17-api-specification)
18. [Frontend Specification](#18-frontend-specification)
19. [Security Architecture](#19-security-architecture)
20. [Reliability, Recovery, dan
    Observability](#20-reliability-recovery-dan-observability)
21. [Deployment Architecture](#21-deployment-architecture)
22. [Testing Strategy](#22-testing-strategy)
23. [Roadmap Implementasi](#23-roadmap-implementasi)
24. [Acceptance Criteria](#24-acceptance-criteria)
25. [Risiko dan Mitigasi](#25-risiko-dan-mitigasi)
26. [Keputusan Arsitektur](#26-keputusan-arsitektur)
27. [Glosarium](#27-glosarium)
28. [Referensi Teknis](#28-referensi-teknis)

------------------------------------------------------------------------

# 1. Ringkasan Eksekutif

DBSync adalah aplikasi web untuk mengelola sinkronisasi data antara dua
database atau lebih melalui pasangan koneksi yang disebut
**Synchronization Group**. Setiap group menghubungkan tepat dua endpoint
database, yaitu Endpoint A dan Endpoint B.

DBSync mendukung database:

-   MySQL
-   PostgreSQL
-   Microsoft SQL Server

Sistem menyediakan dua engine sinkronisasi:

1.  **Scheduled Incremental Sync** --- mengambil perubahan data
    berdasarkan jadwal dan mekanisme change detection yang tersedia.
2.  **Real-Time Change Data Capture (CDC)** --- menangkap perubahan dari
    transaction log atau mekanisme CDC database, kemudian meneruskannya
    ke database tujuan.

Aplikasi dibangun dengan pendekatan **Modular Monolith untuk API dan
domain**, disertai background worker yang dapat dipisahkan
deployment-nya. Arsitektur ini menjaga kompleksitas awal tetap
terkendali, tetapi tetap memungkinkan pemisahan service ketika beban
meningkat.

## 1.1 Sasaran Produk

-   Mengelola banyak koneksi database dari satu dashboard.
-   Menghubungkan database berbeda vendor.
-   Memilih seluruh tabel yang tersedia atau hanya tabel tertentu.
-   Menjalankan initial synchronization hanya pada tabel terpilih.
-   Menjalankan sinkronisasi satu arah atau dua arah sesuai konfigurasi.
-   Mendukung sinkronisasi terjadwal dan CDC.
-   Mendeteksi serta menangani konflik perubahan.
-   Menyediakan retry, checkpoint, audit, monitoring, dan recovery.
-   Dapat diakses melalui browser dalam jaringan yang diizinkan.

## 1.2 Prinsip Utama

1.  **Data safety first** --- keselamatan data lebih penting daripada
    kecepatan.
2.  **No silent data loss** --- event yang belum berhasil tidak boleh
    dibuang tanpa jejak.
3.  **Idempotent processing** --- event yang diproses ulang tidak boleh
    menggandakan efek.
4.  **Explicit configuration** --- tabel dan arah sinkronisasi harus
    jelas.
5.  **Schema-aware** --- struktur dan tipe data harus divalidasi sebelum
    sinkronisasi.
6.  **Least privilege** --- akun database hanya memperoleh hak minimum
    yang diperlukan.
7.  **Observable by design** --- setiap proses memiliki status, metrik,
    dan audit.
8.  **No unsupported exactly-once claim** --- jaminan lintas dua
    database menggunakan durable event, deduplication, checkpoint, dan
    recovery; bukan mengklaim transaksi atomik lintas database.

------------------------------------------------------------------------

# 2. Tujuan dan Ruang Lingkup

## 2.1 Ruang Lingkup V1

  -----------------------------------------------------------------------
  Area                                Cakupan
  ----------------------------------- -----------------------------------
  Database                            MySQL, PostgreSQL, SQL Server

  Topologi                            Pairwise

  Endpoint                            Tepat dua endpoint per group

  Arah                                A ke B, B ke A, atau dua arah

  Tabel                               Semua tabel yang memenuhi syarat
                                      atau tabel terpilih

  Initial sync                        Hanya tabel terpilih

  Scheduled                           Interval terkonfigurasi

  CDC                                 Debezium dan Kafka

  Conflict                            Manual sebagai default; strategi
                                      otomatis opsional

  Monitoring                          Dashboard, event, run history,
                                      worker health

  Security                            Authentication, RBAC, secret
                                      encryption, TLS

  Deployment                          Docker Compose untuk development;
                                      Linux/Windows server sesuai
                                      kebutuhan
  -----------------------------------------------------------------------

## 2.2 Di Luar Cakupan V1

-   Sinkronisasi DDL otomatis.
-   Multi-master mesh bebas dengan topologi siklus.
-   Sinkronisasi tabel tanpa primary key.
-   Sinkronisasi file atau object storage.
-   Sinkronisasi lintas lebih dari dua endpoint dalam satu group.
-   Distributed transaction atomik antara database sumber dan tujuan.
-   Arbitrary transformation engine yang kompleks.
-   Konflik otomatis berbasis business rule khusus yang belum
    dikonfigurasi.
-   Jaminan zero-downtime schema migration untuk semua vendor.

Fitur di luar cakupan dapat dipertimbangkan pada versi berikutnya
setelah core engine stabil.

------------------------------------------------------------------------

# 3. Konsep dan Terminologi

  -----------------------------------------------------------------------
  Istilah                             Definisi
  ----------------------------------- -----------------------------------
  Connection                          Informasi koneksi ke satu database

  Endpoint                            Peran koneksi dalam sebuah
                                      synchronization group

  Endpoint A                          Database pertama dalam group

  Endpoint B                          Database kedua dalam group

  Synchronization Group               Konfigurasi pasangan database yang
                                      akan disinkronkan

  Table Rule                          Aturan sinkronisasi untuk pasangan
                                      tabel

  Initial Sync                        Pemindahan baseline data awal

  Incremental Sync                    Pemindahan perubahan setelah
                                      baseline

  CDC                                 Change Data Capture dari log atau
                                      fitur database

  Event                               Representasi perubahan data yang
                                      dinormalisasi

  Checkpoint                          Posisi terakhir yang telah
                                      diamankan/diproses

  Idempotency                         Kemampuan memproses ulang tanpa
                                      menggandakan efek

  Conflict                            Perubahan yang tidak dapat
                                      digabungkan secara aman

  Dead Letter                         Event yang gagal diproses dan
                                      membutuhkan tindakan

  Snapshot                            Pembacaan data pada kondisi
                                      konsisten

  Tombstone                           Representasi penghapusan data

  Provenance                          Informasi asal-usul perubahan

  Schema Mapping                      Pemetaan schema, tabel, dan kolom
                                      antar database
  -----------------------------------------------------------------------

## 3.1 Model Pairwise

Setiap synchronization group memiliki dua endpoint:

``` text
Synchronization Group: HRGA-CENTRAL

Endpoint A: MySQL / Database HRGA
Endpoint B: PostgreSQL / Database HRGA_REPLICA

A_TO_B: Enabled
B_TO_A: Enabled
```

Jika terdapat database C, buat group lain secara eksplisit. Jangan
otomatis membuat mesh.

**Aturan keselamatan:** V1 sebaiknya menolak atau memberi peringatan
keras jika satu database dipakai dalam beberapa group aktif yang
membentuk jalur propagasi melingkar. Provenance lintas group harus diuji
sebelum skenario tersebut diizinkan.

## 3.2 Arah Sinkronisasi

Pilihan arah:

-   `A_TO_B`
-   `B_TO_A`
-   `BIDIRECTIONAL`

Arah juga dapat diatur per tabel jika kebutuhan bisnis mengharuskannya.
Kebijakan grup dan kebijakan tabel harus mempunyai prioritas yang jelas.

------------------------------------------------------------------------

# 4. Kebutuhan Fungsional

## 4.1 Database Connection Manager

Sistem harus dapat:

-   Menambah koneksi MySQL, PostgreSQL, dan SQL Server.
-   Mengubah nama koneksi dan parameter koneksi.
-   Mengetes koneksi.
-   Menampilkan status koneksi.
-   Menampilkan database dan schema yang tersedia.
-   Membaca daftar tabel dan struktur kolom.
-   Menyimpan kredensial secara terenkripsi.
-   Menghapus koneksi jika tidak sedang digunakan.
-   Menampilkan waktu pengujian koneksi terakhir.

## 4.2 Synchronization Group

Sistem harus dapat:

-   Membuat group.
-   Memilih Endpoint A dan Endpoint B.
-   Memvalidasi bahwa kedua endpoint berbeda.
-   Menentukan arah sinkronisasi.
-   Menentukan mode Scheduled atau CDC.
-   Memilih kebijakan konflik.
-   Mengaktifkan, pause, resume, dan menghentikan group.
-   Memvalidasi konfigurasi sebelum aktivasi.

## 4.3 Table Discovery dan Mapping

Sistem harus dapat:

-   Membaca tabel dari Endpoint A dan B.
-   Membandingkan struktur tabel.
-   Memilih tabel yang akan disinkronkan.
-   Memetakan nama schema dan tabel yang berbeda.
-   Memetakan kolom.
-   Memeriksa primary key.
-   Memeriksa tipe data yang kompatibel.
-   Memeriksa constraint dan foreign key.
-   Menandai tabel yang tidak memenuhi syarat.
-   Menampilkan perbedaan struktur sebelum group diaktifkan.

## 4.4 Initial Synchronization

Sistem harus dapat:

-   Menjalankan initial sync untuk tabel terpilih.
-   Memilih strategi insert/upsert.
-   Menjalankan proses dalam batch.
-   Menampilkan progress.
-   Menyimpan checkpoint.
-   Melanjutkan proses setelah worker restart.
-   Menampilkan jumlah record dibaca, ditulis, dilewati, dan gagal.
-   Memvalidasi hasil.
-   Membatalkan proses dengan status yang konsisten.

## 4.5 Scheduled Synchronization

Sistem harus dapat:

-   Menentukan interval sinkronisasi.
-   Menjalankan sinkronisasi manual.
-   Memproses perubahan secara incremental jika sumber menyediakan
    mekanisme yang andal.
-   Menyimpan checkpoint per tabel dan arah.
-   Menjalankan retry.
-   Mencegah dua job yang sama berjalan bersamaan.
-   Menampilkan histori eksekusi.

## 4.6 Real-Time CDC

Sistem harus dapat:

-   Memeriksa prasyarat CDC.
-   Membuat dan mengelola connector.
-   Memantau status connector.
-   Membaca event dari Kafka.
-   Menormalisasi event.
-   Menerapkan perubahan ke target.
-   Menyimpan offset/checkpoint.
-   Menangani event duplikat.
-   Menangani event yang tidak kompatibel.
-   Memantau lag dan retensi log sumber.

## 4.7 Conflict Management

Sistem harus dapat:

-   Mendeteksi perubahan bersamaan.
-   Menyimpan detail konflik.
-   Menampilkan nilai dari kedua endpoint.
-   Menawarkan pilihan resolusi.
-   Menyimpan siapa yang menyelesaikan konflik.
-   Menyebarkan hasil resolusi secara aman.
-   Menyimpan histori resolusi.

## 4.8 Monitoring dan Audit

Sistem harus menyediakan:

-   Dashboard status seluruh group.
-   Status koneksi database.
-   Status worker.
-   Jumlah event pending, success, retry, failed, conflict, dan dead
    letter.
-   Latency dan lag CDC.
-   Riwayat sinkronisasi.
-   Audit aktivitas user.
-   Filter log berdasarkan waktu, group, tabel, dan status.

------------------------------------------------------------------------

# 5. Kebutuhan Non-Fungsional

## 5.1 Reliability

-   Event harus disimpan secara durable sebelum sumber checkpoint
    bergerak melewatinya.
-   Pemrosesan event harus idempotent.
-   Retry menggunakan exponential backoff dan jitter.
-   Worker dapat restart dan melanjutkan pekerjaan.
-   Checkpoint disimpan secara durable.
-   Event gagal permanen masuk dead-letter handling.
-   Pause tidak menghapus antrean.
-   Perubahan schema yang tidak kompatibel menghentikan tabel terkait,
    bukan merusak seluruh group.

## 5.2 Performance

Target awal untuk pengujian, bukan jaminan kapasitas:

  Parameter             Target awal
  --------------------- ----------------------------------------
  Batch size            500 record
  Parallel table jobs   2
  Scheduled interval    Minimum 1 menit, jika sumber mendukung
  CDC latency           P95 \< 5 detik pada test environment
  API sederhana         P95 \< 500 ms
  Automatic retry       3 percobaan, dapat dikonfigurasi
  Worker recovery       Resume dari durable checkpoint

Target harus divalidasi dengan load test berdasarkan ukuran record,
jumlah tabel, indeks, jaringan, dan kapasitas server.

## 5.3 Scalability

-   API dan worker dapat di-deploy terpisah.
-   Worker dapat ditambah secara horizontal dengan distributed
    lock/lease.
-   Pemrosesan per group atau partition dapat diskalakan.
-   Event store dan log mempunyai retensi.
-   Kafka topic dan partition dirancang berdasarkan kebutuhan ordering.
-   Database konfigurasi mempunyai indeks dan strategi backup.

## 5.4 Maintainability

-   Modular project structure.
-   Connector abstraction.
-   Unit dan integration tests.
-   Structured logging.
-   API versioning.
-   Database migration.
-   Dokumentasi deployment dan troubleshooting.

## 5.5 Compatibility

-   Mendukung versi database yang masih didukung vendor dan telah diuji.
-   Versi server, connector, dan fitur CDC dicatat dalam compatibility
    matrix.
-   Tipe data yang tidak didukung harus ditandai sebelum aktivasi.
-   Perubahan versi connector diuji melalui integration test.

------------------------------------------------------------------------

# 6. Arsitektur Sistem

## 6.1 Gaya Arsitektur

Gunakan **Modular Monolith + Background Workers**.

API dan domain berada dalam satu solution yang modular. Pekerjaan berat
dijalankan melalui worker. CDC infrastructure dijalankan terpisah.

Keuntungan:

-   Lebih sederhana untuk V1.
-   Batas modul jelas.
-   Tidak perlu mengoperasikan banyak microservices sejak awal.
-   Worker dapat dipisah ketika beban meningkat.
-   Connector baru dapat ditambahkan tanpa mengubah seluruh domain.

## 6.2 Diagram Komponen

``` mermaid
flowchart TD
    U[Browser / Administrator] --> FE[Nuxt Frontend]
    FE --> API[ASP.NET Core API]

    API --> APP[Application Layer]
    APP --> DOM[Domain Layer]
    APP --> CFG[(PostgreSQL Configuration DB)]
    APP --> Q[Durable Job / Event Queue]

    Q --> SCH[Scheduler Worker]
    Q --> SW[Scheduled Sync Worker]

    SRC[Source Databases] --> CDC[Debezium Kafka Connect]
    CDC --> K[(Apache Kafka)]
    K --> CW[CDC Consumer Worker]

    SW --> CON[Database Connector Abstractions]
    CW --> CON
    CON --> DST[Target Databases]

    API --> OBS[Logs / Metrics / Audit]
    SCH --> OBS
    SW --> OBS
    CW --> OBS
```

## 6.3 Tanggung Jawab Komponen

  Komponen                Tanggung jawab
  ----------------------- ------------------------------------
  Nuxt Frontend           Dashboard dan konfigurasi
  ASP.NET Core API        REST API, validasi, autentikasi
  Domain                  Entity dan business rules
  Application             Use case dan orchestration
  Infrastructure          PostgreSQL, encryption, repository
  Connector Abstraction   Kontrak akses database
  Database Connectors     Implementasi driver tiap vendor
  Scheduler Worker        Mengatur jadwal
  Scheduled Worker        Incremental sync
  CDC Consumer            Memproses event CDC
  Kafka                   Event streaming durable
  Observability           Log, metrics, tracing, alert

## 6.4 Prinsip Pemisahan

-   Frontend tidak boleh membuka koneksi langsung ke database bisnis.
-   API tidak boleh menjalankan proses sinkronisasi panjang secara
    synchronous.
-   Worker tidak boleh mengambil konfigurasi rahasia dari frontend.
-   Connector harus menggunakan parameterized query.
-   Setiap group memiliki isolation boundary untuk job dan checkpoint.
-   Semua perubahan konfigurasi penting dicatat ke audit log.

------------------------------------------------------------------------

# 7. Struktur Repository

Nama repository: `dbsync-platform`

``` text
dbsync-platform/
│
├── frontend/
│   ├── app/
│   │   ├── assets/
│   │   ├── components/
│   │   │   ├── common/
│   │   │   ├── dashboard/
│   │   │   ├── database/
│   │   │   ├── synchronization/
│   │   │   ├── conflicts/
│   │   │   └── monitoring/
│   │   ├── composables/
│   │   ├── layouts/
│   │   ├── middleware/
│   │   ├── pages/
│   │   │   ├── dashboard/
│   │   │   ├── connections/
│   │   │   ├── sync-groups/
│   │   │   ├── table-mapping/
│   │   │   ├── scheduler/
│   │   │   ├── conflicts/
│   │   │   ├── history/
│   │   │   ├── logs/
│   │   │   └── users/
│   │   ├── stores/
│   │   ├── types/
│   │   └── utils/
│   ├── public/
│   ├── nuxt.config.ts
│   └── package.json
│
├── backend/
│   ├── DBSync.sln
│   ├── src/
│   │   ├── DBSync.API/
│   │   ├── DBSync.Application/
│   │   ├── DBSync.Domain/
│   │   ├── DBSync.Infrastructure/
│   │   ├── DBSync.Connectors.Abstractions/
│   │   ├── DBSync.Connectors.MySql/
│   │   ├── DBSync.Connectors.PostgreSql/
│   │   ├── DBSync.Connectors.SqlServer/
│   │   ├── DBSync.SyncEngine/
│   │   ├── DBSync.Scheduler/
│   │   └── DBSync.Worker/
│   ├── tests/
│   │   ├── DBSync.UnitTests/
│   │   ├── DBSync.IntegrationTests/
│   │   ├── DBSync.ConnectorTests/
│   │   └── DBSync.EndToEndTests/
│   └── Directory.Build.props
│
├── infrastructure/
│   ├── docker/
│   │   ├── docker-compose.yml
│   │   ├── docker-compose.dev.yml
│   │   └── docker-compose.prod.yml
│   ├── kafka/
│   │   └── connectors/
│   ├── postgres/
│   │   └── init/
│   ├── nginx/
│   └── scripts/
│
├── database/
│   ├── migrations/
│   ├── seeds/
│   └── scripts/
│
├── docs/
│   ├── 01-project-overview.md
│   ├── 02-system-architecture.md
│   ├── 03-database-design.md
│   ├── 04-api-specification.md
│   ├── 05-sync-engine.md
│   ├── 06-cdc-architecture.md
│   ├── 07-security.md
│   ├── 08-deployment.md
│   ├── 09-testing.md
│   └── 10-troubleshooting.md
│
├── .github/
│   └── workflows/
├── .env.example
├── .gitignore
├── README.md
└── CHANGELOG.md
```

## 7.1 Backend Project Responsibility

  -----------------------------------------------------------------------
  Project                             Isi
  ----------------------------------- -----------------------------------
  `DBSync.API`                        Controllers/endpoints,
                                      authentication, middleware

  `DBSync.Application`                Commands, queries, DTO, validators,
                                      use cases

  `DBSync.Domain`                     Entities, value objects, enums,
                                      domain rules

  `DBSync.Infrastructure`             EF Core, PostgreSQL, repositories,
                                      secret protection

  `DBSync.Connectors.Abstractions`    Interface dan model connector

  `DBSync.Connectors.MySql`           MySqlConnector implementation

  `DBSync.Connectors.PostgreSql`      Npgsql implementation

  `DBSync.Connectors.SqlServer`       Microsoft.Data.SqlClient
                                      implementation

  `DBSync.SyncEngine`                 Snapshot, event pipeline, apply,
                                      conflict

  `DBSync.Scheduler`                  Job scheduling

  `DBSync.Worker`                     Background host dan worker
                                      lifecycle
  -----------------------------------------------------------------------

## 7.2 Contoh Struktur Sync Engine

``` text
DBSync.SyncEngine/
├── InitialSync/
│   ├── InitialSyncService.cs
│   ├── SnapshotManager.cs
│   ├── BatchProcessor.cs
│   └── InitialSyncValidator.cs
├── Incremental/
│   ├── IncrementalSyncService.cs
│   ├── ChangeDetector.cs
│   └── CheckpointManager.cs
├── Bidirectional/
│   ├── BidirectionalSyncService.cs
│   ├── EventOriginTracker.cs
│   └── LoopPreventionService.cs
├── Conflict/
│   ├── ConflictDetector.cs
│   ├── ConflictResolver.cs
│   └── Strategies/
├── Queue/
│   ├── SyncQueueService.cs
│   ├── RetryProcessor.cs
│   └── DeadLetterProcessor.cs
├── Pipeline/
│   ├── ChangeEvent.cs
│   ├── EventNormalizer.cs
│   └── EventApplier.cs
└── Monitoring/
    ├── SyncMetrics.cs
    └── SyncHealthService.cs
```

------------------------------------------------------------------------

# 8. Desain Database Konfigurasi

PostgreSQL digunakan sebagai database internal DBSync. Database ini
menyimpan konfigurasi, metadata, job, checkpoint, event, konflik, dan
audit.

Untuk V1 dapat digunakan satu tenant default. Struktur dapat menyediakan
`tenant_id` sejak awal jika produk nantinya akan mendukung multi-tenant.

## 8.1 Daftar Tabel

  -----------------------------------------------------------------------
  Modul                               Tabel
  ----------------------------------- -----------------------------------
  Authentication                      `users`, `roles`, `user_roles`,
                                      `refresh_tokens`

  Connections                         `db_connections`,
                                      `connection_secrets`

  Synchronization                     `sync_groups`, `sync_endpoints`

  Schema                              `schema_snapshots`,
                                      `sync_table_rules`,
                                      `sync_column_mappings`

  Jobs                                `sync_jobs`, `sync_job_executions`

  Checkpoints                         `sync_checkpoints`

  Events                              `sync_events`, `processed_events`

  Conflicts                           `sync_conflicts`

  History                             `sync_runs`, `sync_run_items`

  Operations                          `worker_heartbeats`, `audit_logs`
  -----------------------------------------------------------------------

## 8.2 Konvensi Database

-   Primary key internal menggunakan UUID.
-   Timestamp menggunakan `TIMESTAMPTZ`.
-   Nama tabel dan kolom menggunakan `snake_case`.
-   Status menggunakan string terkontrol atau PostgreSQL enum.
-   JSONB digunakan untuk metadata vendor-specific.
-   Foreign key dan indeks harus didefinisikan dalam migration.
-   Tabel event besar harus memiliki kebijakan retensi dan partitioning.
-   Secret dipisahkan dari data koneksi umum.

## 8.3 Tabel `db_connections`

``` sql
CREATE TABLE db_connections (
    id UUID PRIMARY KEY,
    connection_name VARCHAR(150) NOT NULL,
    db_type VARCHAR(30) NOT NULL,
    host VARCHAR(255) NOT NULL,
    port INTEGER NOT NULL,
    database_name VARCHAR(255) NOT NULL,
    username VARCHAR(255) NOT NULL,
    ssl_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    connection_options JSONB,
    status VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN',
    last_tested_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_db_connections_db_type
    ON db_connections(db_type);
```

Nilai `db_type` yang diizinkan: `MYSQL`, `POSTGRESQL`, `SQLSERVER`.

Password tidak disimpan pada tabel ini.

## 8.4 Tabel `connection_secrets`

``` sql
CREATE TABLE connection_secrets (
    id UUID PRIMARY KEY,
    connection_id UUID NOT NULL
        REFERENCES db_connections(id) ON DELETE CASCADE,
    encrypted_password TEXT NOT NULL,
    encryption_key_version VARCHAR(30) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    rotated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX ux_connection_secrets_connection
    ON connection_secrets(connection_id);
```

Implementasi produksi harus mendukung rotasi kunci dan akses secret
melalui service khusus.

## 8.5 Tabel `sync_groups`

``` sql
CREATE TABLE sync_groups (
    id UUID PRIMARY KEY,
    group_name VARCHAR(150) NOT NULL,
    description TEXT,
    sync_direction VARCHAR(30) NOT NULL,
    sync_mode VARCHAR(30) NOT NULL,
    conflict_strategy VARCHAR(30) NOT NULL DEFAULT 'MANUAL',
    status VARCHAR(30) NOT NULL DEFAULT 'DRAFT',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_sync_groups_status
    ON sync_groups(status);
```

Contoh nilai:

-   `sync_direction`: `A_TO_B`, `B_TO_A`, `BIDIRECTIONAL`
-   `sync_mode`: `SCHEDULED`, `CDC`, `HYBRID`
-   `conflict_strategy`: `MANUAL`, `LAST_WRITE_WINS`, `A_PRIORITY`,
    `B_PRIORITY`
-   `status`: `DRAFT`, `VALIDATING`, `ACTIVE`, `PAUSED`, `ERROR`,
    `STOPPED`

Mode `HYBRID` hanya boleh diaktifkan jika ownership perubahan dan
deduplication antara dua engine telah dirancang. Jangan menjalankan
Scheduled dan CDC atas perubahan yang sama tanpa aturan yang jelas.

## 8.6 Tabel `sync_endpoints`

``` sql
CREATE TABLE sync_endpoints (
    id UUID PRIMARY KEY,
    sync_group_id UUID NOT NULL
        REFERENCES sync_groups(id) ON DELETE CASCADE,
    connection_id UUID NOT NULL
        REFERENCES db_connections(id),
    endpoint_role VARCHAR(20) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_endpoint_role
        CHECK (endpoint_role IN ('A', 'B')),

    CONSTRAINT ux_group_endpoint_role
        UNIQUE(sync_group_id, endpoint_role),

    CONSTRAINT ux_group_connection
        UNIQUE(sync_group_id, connection_id)
);
```

Application service harus memastikan sebuah group aktif memiliki tepat
satu endpoint A dan satu endpoint B.

## 8.7 Tabel `schema_snapshots`

``` sql
CREATE TABLE schema_snapshots (
    id UUID PRIMARY KEY,
    connection_id UUID NOT NULL
        REFERENCES db_connections(id),
    schema_name VARCHAR(150) NOT NULL,
    table_name VARCHAR(150) NOT NULL,
    schema_hash VARCHAR(128) NOT NULL,
    schema_definition JSONB NOT NULL,
    discovered_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_schema_snapshots_connection_table
    ON schema_snapshots(connection_id, schema_name, table_name);
```

Snapshot menyimpan metadata struktur, bukan isi seluruh data tabel.

## 8.8 Tabel `sync_table_rules`

``` sql
CREATE TABLE sync_table_rules (
    id UUID PRIMARY KEY,
    sync_group_id UUID NOT NULL
        REFERENCES sync_groups(id) ON DELETE CASCADE,

    source_schema VARCHAR(150) NOT NULL,
    source_table VARCHAR(150) NOT NULL,
    target_schema VARCHAR(150) NOT NULL,
    target_table VARCHAR(150) NOT NULL,

    sync_a_to_b BOOLEAN NOT NULL DEFAULT TRUE,
    sync_b_to_a BOOLEAN NOT NULL DEFAULT TRUE,

    sync_insert BOOLEAN NOT NULL DEFAULT TRUE,
    sync_update BOOLEAN NOT NULL DEFAULT TRUE,
    sync_delete BOOLEAN NOT NULL DEFAULT TRUE,

    initial_sync_status VARCHAR(30) NOT NULL DEFAULT 'PENDING',
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ux_group_source_table
        UNIQUE(sync_group_id, source_schema, source_table)
);
```

Untuk pemetaan yang benar-benar dua arah, implementasi harus menyimpan
identitas tabel pada A dan B secara eksplisit. Nama `source` dan
`target` di atas merupakan baseline awal dan perlu diperluas menjadi
`endpoint_a_schema`, `endpoint_a_table`, `endpoint_b_schema`, dan
`endpoint_b_table` sebelum migration produksi final.

## 8.9 Tabel `sync_column_mappings`

``` sql
CREATE TABLE sync_column_mappings (
    id UUID PRIMARY KEY,
    table_rule_id UUID NOT NULL
        REFERENCES sync_table_rules(id) ON DELETE CASCADE,
    endpoint_a_column VARCHAR(150) NOT NULL,
    endpoint_b_column VARCHAR(150) NOT NULL,
    data_type_strategy VARCHAR(50),
    is_primary_key BOOLEAN NOT NULL DEFAULT FALSE,
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ux_column_mapping
        UNIQUE(table_rule_id, endpoint_a_column, endpoint_b_column)
);
```

## 8.10 Tabel `sync_checkpoints`

``` sql
CREATE TABLE sync_checkpoints (
    id UUID PRIMARY KEY,
    sync_group_id UUID NOT NULL
        REFERENCES sync_groups(id) ON DELETE CASCADE,
    table_rule_id UUID NOT NULL
        REFERENCES sync_table_rules(id) ON DELETE CASCADE,
    direction VARCHAR(20) NOT NULL,
    capture_position JSONB,
    durable_position JSONB,
    applied_position JSONB,
    last_success_at TIMESTAMPTZ,
    status VARCHAR(30) NOT NULL DEFAULT 'PENDING',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ux_checkpoint_table_direction
        UNIQUE(table_rule_id, direction)
);
```

`capture_position`, `durable_position`, dan `applied_position` harus
dibedakan:

-   Capture: posisi perubahan telah dibaca dari sumber.
-   Durable: event telah tersimpan dengan aman di antrean.
-   Applied: event telah berhasil diterapkan ke tujuan.

Jangan memajukan posisi applied hanya karena event berhasil dibaca.

## 8.11 Tabel `sync_events`

``` sql
CREATE TABLE sync_events (
    id UUID PRIMARY KEY,
    event_id UUID NOT NULL UNIQUE,

    sync_group_id UUID NOT NULL
        REFERENCES sync_groups(id),
    table_rule_id UUID NOT NULL
        REFERENCES sync_table_rules(id),

    source_endpoint_id UUID NOT NULL
        REFERENCES sync_endpoints(id),
    direction VARCHAR(20) NOT NULL,

    operation VARCHAR(20) NOT NULL,
    primary_key JSONB NOT NULL,
    payload JSONB,
    source_position JSONB,

    origin_event_id UUID,
    schema_version INTEGER NOT NULL DEFAULT 1,

    status VARCHAR(30) NOT NULL DEFAULT 'PENDING',
    retry_count INTEGER NOT NULL DEFAULT 0,
    next_retry_at TIMESTAMPTZ,
    error_code VARCHAR(100),
    error_message TEXT,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at TIMESTAMPTZ
);

CREATE INDEX ix_sync_events_pending
    ON sync_events(status, next_retry_at, created_at);

CREATE INDEX ix_sync_events_group_created
    ON sync_events(sync_group_id, created_at);
```

Untuk skala besar, pertimbangkan partitioning berdasarkan waktu,
retensi, dan pemisahan payload besar ke storage yang sesuai. Payload
harus dilindungi karena dapat berisi data bisnis sensitif.

## 8.12 Tabel `processed_events`

``` sql
CREATE TABLE processed_events (
    id UUID PRIMARY KEY,
    event_id UUID NOT NULL UNIQUE,
    destination_endpoint_id UUID NOT NULL
        REFERENCES sync_endpoints(id),
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    result_hash VARCHAR(128)
);
```

Tabel ini menjadi salah satu mekanisme deduplication. Retensinya harus
lebih panjang daripada kemungkinan event replay.

## 8.13 Tabel `sync_conflicts`

``` sql
CREATE TABLE sync_conflicts (
    id UUID PRIMARY KEY,
    sync_group_id UUID NOT NULL
        REFERENCES sync_groups(id),
    table_rule_id UUID NOT NULL
        REFERENCES sync_table_rules(id),

    primary_key JSONB NOT NULL,
    endpoint_a_value JSONB,
    endpoint_b_value JSONB,

    endpoint_a_version JSONB,
    endpoint_b_version JSONB,

    conflict_type VARCHAR(50) NOT NULL,
    status VARCHAR(30) NOT NULL DEFAULT 'PENDING',

    resolution_strategy VARCHAR(50),
    resolved_value JSONB,
    resolved_by UUID,
    resolved_at TIMESTAMPTZ,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

## 8.14 Tabel `sync_runs` dan `sync_run_items`

`sync_runs` menyimpan satu eksekusi sinkronisasi. `sync_run_items`
menyimpan rincian per tabel atau batch.

Contoh kolom `sync_runs`:

-   `id`
-   `sync_group_id`
-   `run_type`
-   `status`
-   `started_at`
-   `finished_at`
-   `records_read`
-   `records_inserted`
-   `records_updated`
-   `records_deleted`
-   `records_failed`
-   `error_summary`

Contoh kolom `sync_run_items`:

-   `id`
-   `sync_run_id`
-   `table_rule_id`
-   `batch_number`
-   `status`
-   `records_read`
-   `records_written`
-   `records_failed`
-   `started_at`
-   `finished_at`
-   `error_message`

## 8.15 Tabel Operasional

`worker_heartbeats` minimal berisi:

-   `worker_id`
-   `worker_type`
-   `hostname`
-   `status`
-   `last_heartbeat_at`
-   `metadata`

`audit_logs` minimal berisi:

-   `id`
-   `user_id`
-   `action`
-   `resource_type`
-   `resource_id`
-   `before_value`
-   `after_value`
-   `ip_address`
-   `created_at`

Audit log harus memiliki kebijakan retensi dan tidak boleh menyimpan
password, token, atau secret.

------------------------------------------------------------------------

# 9. Desain Connector Database

## 9.1 Connector Abstraction

Setiap connector harus mengimplementasikan kontrak umum agar Sync Engine
tidak bergantung pada vendor tertentu.

Contoh interface konseptual:

``` csharp
public interface IDatabaseConnector
{
    DatabaseType DatabaseType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DatabaseSchema>> GetSchemasAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TableMetadata>> GetTablesAsync(
        ConnectionProfile profile,
        string schema,
        CancellationToken cancellationToken);

    Task<TableSchema> GetTableSchemaAsync(
        ConnectionProfile profile,
        string schema,
        string table,
        CancellationToken cancellationToken);

    Task<IAsyncEnumerable<RowData>> ReadRowsAsync(
        ConnectionProfile profile,
        ReadQuery query,
        CancellationToken cancellationToken);

    Task<WriteResult> ApplyChangesAsync(
        ConnectionProfile profile,
        IReadOnlyList<ChangeOperation> operations,
        CancellationToken cancellationToken);
}
```

Signature final dapat disesuaikan ketika implementasi. Interface harus
membedakan operasi metadata, snapshot read, incremental capture, dan
target apply bila perilaku vendor berbeda.

## 9.2 Implementasi Connector

  Database     Driver .NET
  ------------ --------------------------
  MySQL        MySqlConnector
  PostgreSQL   Npgsql
  SQL Server   Microsoft.Data.SqlClient

Connector bertanggung jawab atas:

-   Connection lifecycle.
-   Schema discovery.
-   Metadata normalization.
-   Parameterized SQL.
-   Transaction handling pada satu database.
-   Data type conversion.
-   Pagination/streaming.
-   Error classification.
-   Vendor-specific CDC prerequisites.

## 9.3 Connector Registry

Gunakan dependency injection dan registry berdasarkan `DatabaseType`.
Hindari switch statement vendor yang tersebar di seluruh application
layer.

------------------------------------------------------------------------

# 10. Desain Synchronization Engine

## 10.1 Event Model

Semua perubahan dinormalisasi menjadi model internal.

``` json
{
  "eventId": "d61b5bb1-0a00-4d21-94e5-68dd64a2f041",
  "syncGroupId": "group-uuid",
  "sourceEndpoint": "A",
  "table": {
    "schema": "public",
    "name": "employees"
  },
  "operation": "UPDATE",
  "primaryKey": {
    "id": 1001
  },
  "before": {
    "name": "Andi"
  },
  "after": {
    "name": "Budi"
  },
  "sourceMetadata": {
    "transactionId": "source-transaction-id",
    "position": "vendor-specific-position"
  },
  "occurredAt": "2026-11-02T14:00:00Z",
  "originEventId": "original-event-uuid",
  "schemaVersion": 1
}
```

Model tersebut adalah format internal DBSync, bukan format native
Debezium.

## 10.2 Pipeline

``` mermaid
flowchart TD
    A[Capture Change] --> B[Normalize Event]
    B --> C[Validate Schema]
    C --> D[Persist Event Durably]
    D --> E[Deduplicate]
    E --> F[Check Provenance]
    F --> G[Check Conflict]
    G --> H{Conflict?}
    H -->|Yes| I[Conflict Queue]
    H -->|No| J[Apply Target Transaction]
    J --> K[Verify / Record Result]
    K --> L[Mark Applied]
    L --> M[Advance Applied Checkpoint]
```

Checkpoint capture tidak boleh bergerak melewati perubahan yang belum
diamankan ke durable storage. Untuk Kafka, offset commit harus mengikuti
strategi persistensi dan pemrosesan yang menjamin event tidak hilang
ketika worker berhenti.

## 10.3 Event Status

  Status          Makna
  --------------- --------------------
  `PENDING`       Menunggu diproses
  `PROCESSING`    Sedang diproses
  `SUCCESS`       Berhasil
  `RETRY`         Menunggu retry
  `FAILED`        Gagal
  `CONFLICT`      Konflik terdeteksi
  `SKIPPED`       Sengaja dilewati
  `DEAD_LETTER`   Gagal permanen

Transisi status harus dibatasi oleh state machine agar event tidak
berubah ke status yang tidak valid.

## 10.4 Idempotent Apply

Sebelum menerapkan event:

1.  Periksa `event_id` pada `processed_events`.
2.  Jika sudah diproses pada tujuan, jangan ulangi efeknya.
3.  Jika belum, mulai transaksi pada database tujuan.
4.  Terapkan perubahan menggunakan primary key dan parameterized SQL.
5.  Catat idempotency marker sedekat mungkin dalam transaksi yang sama.
6.  Commit transaksi.
7.  Perbarui status dan checkpoint pada storage kontrol.

Untuk database tujuan, marker deduplication idealnya berada pada
database tujuan dan commit bersama perubahan record. Jika marker hanya
berada di PostgreSQL konfigurasi dan target database terpisah, masih ada
crash window antara commit data target dan commit marker. Karena itu,
desain produksi harus memiliki strategi idempotent upsert atau marker di
target agar replay aman.

## 10.5 Ordering

-   Pertahankan urutan event untuk key/record yang sama.
-   Jangan mengasumsikan urutan global antar tabel.
-   Kafka partition key sebaiknya berdasarkan synchronization group,
    table, dan primary key sesuai kebutuhan ordering.
-   Transaksi sumber yang mengubah beberapa tabel memerlukan strategi
    konsistensi jika relasi lintas tabel harus dipertahankan.

------------------------------------------------------------------------

# 11. Initial Synchronization

Initial sync memindahkan baseline data dari satu endpoint ke endpoint
lain.

## 11.1 Tahapan

1.  Validasi koneksi kedua endpoint.
2.  Validasi schema dan table mapping.
3.  Validasi primary key.
4.  Validasi tipe data dan constraint.
5.  Tentukan snapshot consistency strategy.
6.  Mulai capture perubahan atau tetapkan posisi CDC.
7.  Baca snapshot secara batch.
8.  Tulis data ke tujuan.
9.  Proses perubahan yang terjadi selama snapshot.
10. Jalankan data validation.
11. Tandai initial sync selesai.

## 11.2 Snapshot Consistency

Snapshot dan CDC harus memiliki hubungan posisi yang konsisten. Strategi
spesifik berbeda menurut database dan connector.

Jangan menjalankan full table scan tanpa mekanisme untuk menangkap
perubahan yang terjadi selama scan karena dapat menghasilkan data yang
tertinggal atau tidak konsisten.

## 11.3 Mode Penulisan

  -----------------------------------------------------------------------
  Mode                    Keterangan              Rekomendasi
  ----------------------- ----------------------- -----------------------
  Insert only             Gagal jika key sudah    Kasus khusus
                          ada                     

  Upsert                  Insert atau update      Default
                          berdasarkan key         

  Replace table           Mengganti isi tabel     Berisiko tinggi;
                          tujuan                  konfirmasi dan backup
                                                  wajib
  -----------------------------------------------------------------------

Default V1 adalah **Upsert**.

Replace table harus memerlukan konfirmasi eksplisit, pemeriksaan
dependensi foreign key, backup atau rencana pemulihan, serta validasi
target. Jangan menghapus data tujuan secara otomatis.

## 11.4 Progress

Progress per tabel:

-   Total rows jika dapat dihitung tanpa biaya berlebihan.
-   Rows read.
-   Rows written.
-   Rows skipped.
-   Rows failed.
-   Current batch.
-   Start time.
-   Estimated completion jika estimasi cukup andal.

Jangan menampilkan persentase palsu jika total record tidak diketahui.

------------------------------------------------------------------------

# 12. Scheduled Incremental Synchronization

## 12.1 Mekanisme Change Detection

Urutan preferensi:

1.  Native change tracking atau change data mechanism yang sesuai.
2.  Kolom `updated_at` yang dijamin selalu diperbarui.
3.  Tombstone table/outbox untuk DELETE.
4.  Snapshot comparison sebagai fallback terbatas.

`updated_at` saja tidak cukup untuk mendeteksi DELETE. Sistem
membutuhkan tombstone, audit table, CDC, atau mekanisme lain yang dapat
diandalkan.

## 12.2 Contoh Konfigurasi

  Pengaturan            Default awal
  --------------------- ------------------------------
  Interval              5 menit
  Batch size            500
  Max parallel tables   2
  Retry                 3
  Retry delay           Exponential backoff + jitter
  Command timeout       60 detik
  Conflict strategy     Manual
  Validation            Enabled

Nilai harus dapat diubah berdasarkan kapasitas dan karakteristik
database.

## 12.3 Checkpoint

Checkpoint terpisah untuk:

-   Setiap synchronization group.
-   Setiap table rule.
-   Setiap arah sinkronisasi.
-   Setiap mekanisme capture.

Contoh:

``` text
Group A-B
├── employees
│   ├── A_TO_B
│   └── B_TO_A
├── departments
│   ├── A_TO_B
│   └── B_TO_A
└── attendance
    ├── A_TO_B
    └── B_TO_A
```

Nilai checkpoint dapat berupa timestamp, sequence, LSN, binlog
coordinates, atau token vendor-specific.

------------------------------------------------------------------------

# 13. Real-Time CDC

## 13.1 Arsitektur

``` mermaid
flowchart TD
    DB[Source Database] --> D[Debezium Kafka Connect]
    D --> K[(Apache Kafka)]
    K --> C[DBSync CDC Consumer]
    C --> N[Event Normalizer]
    N --> V[Schema and Rule Validator]
    V --> R[Conflict / Provenance]
    R --> W[Target Writer]
    W --> T[Target Database]
```

Debezium bertugas menangkap perubahan dari sumber. DBSync tetap
bertanggung jawab atas normalisasi, mapping, deduplication, konflik, dan
penulisan ke database tujuan.

## 13.2 MySQL CDC

Prasyarat umum:

-   Binary logging aktif.
-   Format dan row image binlog sesuai connector.
-   User mempunyai privilege yang diperlukan.
-   Binlog retention cukup untuk downtime yang direncanakan.
-   Server ID dan konfigurasi connector tidak bentrok.

Periksa versi MySQL dan dokumentasi Debezium yang digunakan sebelum
menetapkan parameter final.

## 13.3 PostgreSQL CDC

Prasyarat umum:

-   `wal_level = logical`.
-   Logical replication slot.
-   Publication sesuai tabel yang dipilih.
-   User memiliki privilege yang dibutuhkan.
-   WAL retention dan replication slot dipantau.

Replication slot yang tertinggal dapat menahan WAL dan menghabiskan
disk. Monitoring slot lag wajib tersedia.

## 13.4 SQL Server CDC

Prasyarat umum:

-   CDC diaktifkan pada database.
-   CDC diaktifkan pada tabel terpilih.
-   SQL Server Agent dan capture job berjalan sesuai kebutuhan.
-   Retention dan cleanup job dipantau.
-   User connector memiliki hak yang diperlukan.

Ketersediaan fitur dapat bergantung pada edisi dan versi SQL Server.

## 13.5 Kafka Design

Pertimbangkan:

-   Topic per group atau per sumber sesuai skala.
-   Partition key untuk ordering per record.
-   Replication factor sesuai jumlah broker.
-   Retention berdasarkan recovery objective.
-   Dead-letter topic atau mekanisme dead-letter terpisah.
-   Schema evolution dan versioning.
-   Monitoring consumer lag.
-   TLS dan authentication.

Untuk development, Kafka dapat dijalankan melalui Docker Compose.
Production memerlukan persistent volume, resource sizing, monitoring,
backup, dan prosedur recovery.

## 13.6 CDC Offset

Offset Kafka dan checkpoint aplikasi harus dikelola secara konsisten.

Strategi yang disarankan:

1.  Consumer membaca event.
2.  Event dipersistenkan ke durable event store atau diproses secara
    idempotent.
3.  Target apply berhasil.
4.  Status hasil tercatat.
5.  Offset di-commit sesuai jaminan durability yang dipilih.

Jangan commit offset sebelum event aman diproses atau dipersistenkan.

------------------------------------------------------------------------

# 14. Bidirectional Synchronization dan Loop Prevention

## 14.1 Risiko Loop

Contoh:

1.  User mengubah data di A.
2.  CDC A menangkap event.
3.  DBSync menerapkan perubahan ke B.
4.  Perubahan di B tertangkap CDC B.
5.  DBSync berpotensi mengirim perubahan itu kembali ke A.

## 14.2 Pencegahan

Gunakan kombinasi:

-   Event ID unik.
-   Origin event ID.
-   Source endpoint.
-   Sync group ID.
-   Processed event registry.
-   Target-side idempotency marker.
-   Provenance metadata yang dapat dikaitkan dengan perubahan.
-   Aturan routing event.

Jangan hanya mengandalkan session variable karena CDC membaca
transaction log dan perubahan dapat diproses oleh koneksi berbeda.

## 14.3 Provenance

Setiap event yang diteruskan harus mempertahankan asal perubahan. Ketika
perubahan yang berasal dari DBSync tertangkap kembali, sistem
mengenalinya sebagai event turunan dan tidak meneruskan kembali secara
tidak terbatas.

Jika vendor tidak menyediakan cara yang andal untuk mengaitkan metadata
asal ke perubahan target, gunakan strategi deduplication tambahan yang
diuji terhadap crash, retry, dan event replay.

## 14.4 Konflik Siklus Antar Group

Jika A-B dan B-C aktif, perubahan dapat merambat ke C. V1 harus
membatasi topologi tersebut atau menerapkan global provenance registry
dan routing policy yang teruji. Pairwise bukan berarti bebas membuat
semua kombinasi tanpa risiko.

------------------------------------------------------------------------

# 15. Conflict Detection dan Resolution

## 15.1 Contoh Konflik

Record dengan primary key yang sama diubah pada kedua database sebelum
perubahan dari satu sisi diterapkan ke sisi lainnya.

  Field          Endpoint A   Endpoint B
  -------------- ------------ ------------
  Employee ID    1001         1001
  Name           Andi         Budi
  Updated time   10:00        10:01
  Version        5            6

Timestamp tidak selalu cukup untuk menentukan pemenang, terutama jika
jam server tidak tersinkronisasi atau presisi berbeda.

## 15.2 Strategi

  -----------------------------------------------------------------------
  Strategi                            Perilaku
  ----------------------------------- -----------------------------------
  Manual                              Administrator memilih hasil

  Last Write Wins                     Memilih versi berdasarkan metadata
                                      waktu/versi yang tepercaya

  A Priority                          Endpoint A menjadi otoritas

  B Priority                          Endpoint B menjadi otoritas
  -----------------------------------------------------------------------

Default: `MANUAL`.

## 15.3 Resolusi Manual

UI harus menampilkan:

-   Group.
-   Tabel.
-   Primary key.
-   Nilai A.
-   Nilai B.
-   Waktu perubahan.
-   Source position.
-   Jenis konflik.
-   Pilihan resolusi.
-   User yang menyelesaikan.
-   Hasil akhir.

Keputusan resolusi harus dicatat sebagai event yang dapat diaudit dan
disebarkan ke kedua sisi sesuai kebijakan.

------------------------------------------------------------------------

# 16. Data Type Mapping

Cross-database sync membutuhkan normalisasi tipe data.

## 16.1 Mapping Awal

  -----------------------------------------------------------------------------------------
  Logical Type      MySQL                PostgreSQL              SQL Server
  ----------------- -------------------- ----------------------- --------------------------
  Integer           INT/BIGINT           INTEGER/BIGINT          INT/BIGINT

  Decimal           DECIMAL              NUMERIC                 DECIMAL

  Boolean           TINYINT(1) / BOOLEAN BOOLEAN                 BIT
                    convention                                   

  Text              VARCHAR/TEXT         VARCHAR/TEXT            VARCHAR/NVARCHAR

  Date              DATE                 DATE                    DATE

  Timestamp         DATETIME/TIMESTAMP   TIMESTAMP/TIMESTAMPTZ   DATETIME2/DATETIMEOFFSET

  Binary            BLOB                 BYTEA                   VARBINARY

  JSON              JSON                 JSON/JSONB              NVARCHAR JSON convention
  -----------------------------------------------------------------------------------------

Mapping harus mempertimbangkan precision, scale, timezone, collation,
encoding, unsigned integer, dan batas panjang.

## 16.2 Tipe Khusus

-   UUID.
-   ENUM.
-   SET.
-   ARRAY.
-   JSON/JSONB.
-   Spatial/geography.
-   XML.
-   Large object.
-   Computed/generated column.
-   Identity/auto increment.

Tipe yang belum didukung harus menghasilkan validation warning/error.
Jangan melakukan cast diam-diam yang berpotensi kehilangan presisi.

## 16.3 Primary Key

-   Primary key wajib pada V1.
-   Composite primary key harus didukung.
-   Key mapping harus stabil.
-   Auto increment/identity di target tidak boleh mengambil alih key
    sumber jika key harus dipertahankan.
-   Generated column tidak boleh ditulis jika database tujuan
    mengelolanya sendiri.

------------------------------------------------------------------------

# 17. API Specification

Base URL:

``` text
/api/v1
```

Semua API menggunakan JSON kecuali endpoint download/export. API harus
menggunakan HTTPS pada deployment production.

## 17.1 Authentication

  Method   Endpoint          Fungsi
  -------- ----------------- ---------------
  POST     `/auth/login`     Login
  POST     `/auth/refresh`   Refresh token
  POST     `/auth/logout`    Logout
  GET      `/auth/me`        Profil user

## 17.2 Connections

  Method   Endpoint                                    Fungsi
  -------- ------------------------------------------- ----------------
  GET      `/connections`                              Daftar koneksi
  POST     `/connections`                              Tambah koneksi
  GET      `/connections/{id}`                         Detail koneksi
  PUT      `/connections/{id}`                         Update koneksi
  DELETE   `/connections/{id}`                         Hapus koneksi
  POST     `/connections/{id}/test`                    Test koneksi
  GET      `/connections/{id}/schemas`                 Daftar schema
  GET      `/connections/{id}/tables`                  Daftar tabel
  GET      `/connections/{id}/tables/{table}/schema`   Struktur tabel

Contoh request:

``` json
{
  "connectionName": "Database HRGA",
  "dbType": "POSTGRESQL",
  "host": "192.168.1.100",
  "port": 5432,
  "databaseName": "hrga",
  "username": "dbsync_user",
  "password": "********",
  "sslEnabled": true
}
```

Password hanya diterima melalui HTTPS, tidak dikembalikan pada response,
dan tidak boleh muncul di log.

## 17.3 Synchronization Groups

  Method   Endpoint                       Fungsi
  -------- ------------------------------ ---------------
  GET      `/sync-groups`                 Daftar group
  POST     `/sync-groups`                 Membuat group
  GET      `/sync-groups/{id}`            Detail group
  PUT      `/sync-groups/{id}`            Update group
  DELETE   `/sync-groups/{id}`            Hapus group
  POST     `/sync-groups/{id}/validate`   Validasi
  POST     `/sync-groups/{id}/activate`   Aktifkan
  POST     `/sync-groups/{id}/pause`      Pause
  POST     `/sync-groups/{id}/resume`     Resume
  POST     `/sync-groups/{id}/stop`       Stop

## 17.4 Table Mapping

  Method   Endpoint                               Fungsi
  -------- -------------------------------------- ------------------
  GET      `/sync-groups/{id}/available-tables`   Tabel tersedia
  POST     `/sync-groups/{id}/discover-schema`    Discovery schema
  GET      `/sync-groups/{id}/table-rules`        Daftar rule
  POST     `/sync-groups/{id}/table-rules`        Tambah tabel
  PUT      `/table-rules/{id}`                    Ubah rule
  DELETE   `/table-rules/{id}`                    Hapus rule
  POST     `/table-rules/{id}/validate`           Validasi tabel
  GET      `/table-rules/{id}/column-mapping`     Mapping kolom
  PUT      `/table-rules/{id}/column-mapping`     Ubah mapping

## 17.5 Synchronization

  Method   Endpoint                           Fungsi
  -------- ---------------------------------- --------------
  POST     `/sync-groups/{id}/initial-sync`   Initial sync
  POST     `/sync-groups/{id}/sync-now`       Sync manual
  GET      `/sync-groups/{id}/status`         Status
  GET      `/sync-groups/{id}/progress`       Progress
  GET      `/sync-runs`                       Riwayat
  GET      `/sync-runs/{id}`                  Detail run
  POST     `/sync-runs/{id}/cancel`           Cancel

Job panjang harus mengembalikan HTTP `202 Accepted` dan `jobId`/`runId`.
Client mengambil progress melalui polling atau SignalR.

## 17.6 Monitoring

  Method   Endpoint                                Fungsi
  -------- --------------------------------------- ----------------------
  GET      `/monitoring/dashboard`                 Dashboard
  GET      `/monitoring/events`                    Daftar event
  GET      `/monitoring/conflicts`                 Konflik
  POST     `/monitoring/conflicts/{id}/resolve`    Resolusi konflik
  GET      `/monitoring/dead-letters`              Event gagal permanen
  POST     `/monitoring/dead-letters/{id}/retry`   Retry
  GET      `/monitoring/workers`                   Worker status
  GET      `/audit-logs`                           Audit

## 17.7 Standar Response

Sukses:

``` json
{
  "success": true,
  "message": "Operation accepted",
  "data": {
    "jobId": "uuid",
    "status": "QUEUED"
  },
  "traceId": "trace-id"
}
```

Error:

``` json
{
  "success": false,
  "message": "Validation failed",
  "errors": [
    {
      "code": "PRIMARY_KEY_REQUIRED",
      "field": "table",
      "message": "Table must have a supported primary key."
    }
  ],
  "traceId": "trace-id"
}
```

Gunakan HTTP status code yang tepat, pagination, filtering, sorting,
correlation ID, dan API versioning.

------------------------------------------------------------------------

# 18. Frontend Specification

Frontend menggunakan Nuxt, Vue 3, TypeScript, dan Bootstrap 5.

## 18.1 Menu Utama

``` text
Dashboard
│
├── Database Connections
│   ├── Connection List
│   ├── Add Connection
│   └── Connection Test
│
├── Synchronization
│   ├── Synchronization Groups
│   ├── Create Group
│   ├── Table Selection
│   ├── Schema Mapping
│   └── Group Settings
│
├── Jobs
│   ├── Initial Sync
│   ├── Scheduled Jobs
│   └── Running Jobs
│
├── Monitoring
│   ├── Live Monitoring
│   ├── Event Queue
│   ├── Conflict Management
│   ├── Failed Events
│   └── Worker Status
│
├── History
│   ├── Sync History
│   └── Audit Logs
│
└── Administration
    ├── Users
    ├── Roles
    └── System Settings
```

## 18.2 Dashboard

Dashboard minimal menampilkan:

-   Total connections.
-   Connected/disconnected databases.
-   Active synchronization groups.
-   Scheduled jobs.
-   CDC status.
-   Events processed.
-   Pending events.
-   Failed events.
-   Conflicts.
-   Recent synchronization activity.
-   Worker health.

## 18.3 Create Synchronization Group

Wizard:

1.  Group information.
2.  Select Endpoint A.
3.  Select Endpoint B.
4.  Test both connections.
5.  Discover schema.
6.  Select tables.
7.  Configure column mapping.
8.  Select direction.
9.  Select sync mode.
10. Configure conflict strategy.
11. Validate.
12. Review and activate.

## 18.4 Table Selection

Tampilan perlu menampilkan:

-   Nama tabel A.
-   Nama tabel B.
-   Status struktur.
-   Primary key.
-   Jumlah kolom.
-   Foreign key.
-   Checkbox pemilihan.
-   Arah sinkronisasi.
-   Status validasi.

Tabel tanpa primary key atau dengan tipe data yang tidak didukung tidak
boleh diaktifkan tanpa solusi eksplisit.

## 18.5 Conflict Screen

Tampilkan nilai kedua sisi, perubahan, primary key, metadata versi, dan
keputusan resolusi. Tindakan resolve harus memerlukan izin yang sesuai.

## 18.6 Real-Time UI

Gunakan SignalR untuk update status worker, progress, dan monitoring.
SignalR bukan sumber kebenaran; jika koneksi browser terputus, data
tetap dapat diambil kembali dari API.

------------------------------------------------------------------------

# 19. Security Architecture

## 19.1 Authentication dan RBAC

  Role            Hak
  --------------- --------------------------------
  Super Admin     Seluruh fungsi
  Administrator   Koneksi, group, konfigurasi
  Operator        Menjalankan dan memonitor sync
  Viewer          Melihat dashboard dan riwayat
  Auditor         Melihat audit dan log

Hak akses harus diterapkan di backend, bukan hanya menyembunyikan menu
frontend.

## 19.2 Secret Encryption

-   Enkripsi secret menggunakan mekanisme authenticated encryption.
-   Master key disimpan di luar database.
-   Mendukung key versioning dan rotation.
-   Password tidak ditampilkan kembali.
-   Secret tidak masuk log, exception, telemetry, atau response API.
-   Backup key dilakukan secara aman dan terpisah.
-   Restore diuji secara berkala.

ASP.NET Core Data Protection dapat digunakan untuk skenario yang sesuai.
Untuk kebutuhan secret lintas host atau rotasi terkelola, pertimbangkan
Vault atau secret manager. Jangan menyimpan key enkripsi di repository
atau image container.

## 19.3 Network Security

-   HTTPS untuk akses browser.
-   TLS untuk koneksi database jika tersedia.
-   Private network untuk Kafka dan PostgreSQL internal.
-   Firewall hanya membuka port yang diperlukan.
-   Jangan membuka database target ke internet publik.
-   Gunakan VPN atau worker di jaringan database jika dibutuhkan.

## 19.4 Database Privileges

Gunakan akun khusus DBSync, bukan root, postgres superuser, atau SQL
Server `sa`.

  Aktivitas          Hak minimum
  ------------------ ----------------------------------------------
  Schema discovery   Read metadata
  Snapshot read      SELECT
  Target apply       SELECT, INSERT, UPDATE, DELETE sesuai rule
  MySQL CDC          Privilege binlog yang diperlukan
  PostgreSQL CDC     Replication/logical decoding yang diperlukan
  SQL Server CDC     Hak membaca CDC sesuai dokumentasi
  DDL                Tidak diberikan secara default

## 19.5 Audit

Catat:

-   Login dan logout.
-   Pembuatan/perubahan koneksi.
-   Aktivasi/pause group.
-   Perubahan table mapping.
-   Initial sync.
-   Resolusi konflik.
-   Retry dead letter.
-   Perubahan user dan role.
-   Perubahan konfigurasi keamanan.

Audit tidak boleh merekam password atau token.

------------------------------------------------------------------------

# 20. Reliability, Recovery, dan Observability

## 20.1 Retry

Gunakan exponential backoff dengan jitter. Bedakan:

-   Transient errors: network timeout, temporary database unavailable.
-   Permanent errors: invalid column, unsupported type, permission
    denied.
-   Conflict errors: perubahan bersamaan.
-   Configuration errors: koneksi atau schema tidak valid.

Permanent errors tidak boleh di-retry tanpa perubahan kondisi atau
intervensi operator.

## 20.2 Dead Letter

Event masuk dead letter ketika:

-   Retry habis.
-   Payload tidak valid.
-   Mapping tidak tersedia.
-   Tipe data tidak kompatibel.
-   Target menolak perubahan secara permanen.
-   Konflik membutuhkan tindakan manual.

Operator harus dapat melihat penyebab, memperbaiki konfigurasi, dan
mengulang event dengan audit trail.

## 20.3 Worker Recovery

Ketika worker berhenti:

1.  Lease worker dianggap kedaluwarsa.
2.  Worker baru mengambil job setelah memastikan tidak ada pemilik
    aktif.
3.  Event yang statusnya `PROCESSING` dievaluasi kembali.
4.  Idempotency mencegah efek ganda.
5.  Checkpoint dilanjutkan dari posisi durable.
6.  Status run diperbarui.

Gunakan distributed lease/lock dengan expiry dan fencing token jika
beberapa worker dapat mengambil pekerjaan yang sama.

## 20.4 Monitoring Metrics

-   Events captured.
-   Events persisted.
-   Events applied.
-   Events failed.
-   Queue depth.
-   Consumer lag.
-   CDC latency.
-   Retry count.
-   Dead-letter count.
-   Worker heartbeat.
-   Database connectivity.
-   Replication slot lag.
-   Binlog retention.
-   SQL Server CDC capture latency.
-   Disk usage.
-   Sync throughput.

## 20.5 Backup dan Disaster Recovery

Backup minimal:

1.  PostgreSQL configuration database.
2.  Master encryption key.
3.  Kafka configuration dan metadata yang diperlukan.
4.  Connector configuration.
5.  Dokumentasi checkpoint dan recovery.
6.  Secret manager backup sesuai kebijakan organisasi.

RPO dan RTO harus ditentukan berdasarkan kebutuhan bisnis. Uji restore
secara berkala, bukan hanya memastikan file backup terbentuk.

------------------------------------------------------------------------

# 21. Deployment Architecture

## 21.1 Development

Gunakan Docker Compose untuk:

-   PostgreSQL internal.
-   Kafka.
-   Kafka Connect/Debezium.
-   Nginx bila dibutuhkan.

Frontend dan .NET dapat dijalankan langsung pada Windows selama
development atau di container.

## 21.2 Komponen Container

``` text
DBSync Platform
├── dbsync-frontend
│   └── Nuxt
├── dbsync-api
│   └── ASP.NET Core
├── dbsync-worker
│   └── .NET Worker
├── dbsync-postgres
│   └── Configuration Database
├── dbsync-kafka
│   └── Event Streaming
├── dbsync-kafka-connect
│   └── Debezium Connectors
└── dbsync-proxy
    └── Nginx
```

## 21.3 Port Development

  Service                                          Port
  ------------------ ----------------------------------
  Nuxt                                             3000
  ASP.NET Core API     5000 atau port HTTPS development
  PostgreSQL                                       5432
  Kafka Connect                                    8083
  Kafka                         Internal Docker network
  Nginx                                          80/443

Port adalah contoh development dan harus disesuaikan dengan konfigurasi
aktual. Kafka dan PostgreSQL tidak boleh diekspos ke publik pada
production.

## 21.4 Production

-   Gunakan HTTPS.
-   Gunakan private network.
-   Persistenkan volume database dan Kafka.
-   Pisahkan secret dari environment file biasa.
-   Terapkan resource limit.
-   Aktifkan health checks.
-   Gunakan centralized logging.
-   Siapkan backup dan restore.
-   Batasi akses administrator.
-   Pantau disk dan retensi transaction log.

## 21.5 Distributed Worker

Untuk database yang tersebar di cabang atau jaringan privat:

-   Central server mengelola UI dan konfigurasi.
-   Worker ditempatkan di jaringan yang dapat menjangkau database.
-   Worker melakukan koneksi keluar yang aman ke central server.
-   Hindari membuka port database ke internet.
-   Gunakan autentikasi worker dan sertifikat bila sesuai.
-   Konfigurasi secret dapat dikirim dengan mekanisme aman dan dibatasi.

------------------------------------------------------------------------

# 22. Testing Strategy

## 22.1 Compatibility Matrix

Semua kombinasi wajib diuji:

  Source       Destination
  ------------ -------------
  MySQL        MySQL
  MySQL        PostgreSQL
  MySQL        SQL Server
  PostgreSQL   MySQL
  PostgreSQL   PostgreSQL
  PostgreSQL   SQL Server
  SQL Server   MySQL
  SQL Server   PostgreSQL
  SQL Server   SQL Server

## 22.2 Functional Testing

-   INSERT.
-   UPDATE.
-   DELETE.
-   Bulk insert.
-   Composite primary key.
-   Foreign key.
-   Unique constraint.
-   NULL.
-   Unicode.
-   Decimal precision.
-   Date/time dan timezone.
-   JSON.
-   Binary.
-   Generated/identity column.

## 22.3 Failure Testing

-   Source database mati.
-   Target database mati.
-   Jaringan putus.
-   Worker restart.
-   Kafka restart.
-   Duplicate event.
-   Event out of order.
-   Schema berubah.
-   Disk penuh.
-   Credential kedaluwarsa.
-   CDC retention terlampaui.
-   Konflik perubahan bersamaan.
-   Initial sync berhenti di tengah.

## 22.4 Data Integrity

Validasi:

-   Jumlah record.
-   Primary key coverage.
-   Checksum per batch.
-   Insert/update/delete count.
-   Record yang tertinggal.
-   Record konflik.
-   Nilai decimal dan timestamp.
-   Foreign key consistency.

Untuk tabel besar, lakukan validasi per rentang key atau partisi.

## 22.5 Test Layers

  Jenis              Tujuan
  ------------------ ---------------------------------------
  Unit test          Domain dan business rules
  Connector test     Perilaku driver
  Integration test   Interaksi database nyata
  Contract test      Konsistensi connector interface
  End-to-end         Alur UI/API/worker
  Load test          Throughput dan latency
  Security test      Authentication, authorization, secret
  Recovery test      Restart dan replay

Gunakan xUnit dan Testcontainers untuk integration testing jika
lingkungan CI mendukung Docker.

------------------------------------------------------------------------

# 23. Roadmap Implementasi

## Phase 1 --- Project Foundation

-   Membuat repository.
-   Membuat solution .NET.
-   Membuat frontend Nuxt.
-   Membuat PostgreSQL internal.
-   Menyiapkan migration.
-   Authentication dan RBAC.
-   Database Connection Manager.
-   Connector abstraction.
-   Implementasi tiga connector.
-   Schema discovery.
-   Table mapping.

**Exit criteria:** aplikasi dapat login, menyimpan koneksi terenkripsi,
mengetes koneksi ke tiga vendor, dan membaca struktur tabel.

## Phase 2 --- Scheduled Sync

-   Initial snapshot.
-   Batch processing.
-   Incremental change detection.
-   Checkpoint.
-   Bidirectional event processing.
-   Idempotent apply.
-   Retry dan dead letter.
-   Conflict detection.
-   Data validation.

**Exit criteria:** perubahan INSERT, UPDATE, DELETE dapat disinkronkan
sesuai kemampuan change detection yang telah divalidasi, dan worker
dapat restart tanpa kehilangan data.

## Phase 3 --- CDC

-   Kafka dan Kafka Connect.
-   Debezium MySQL.
-   Debezium PostgreSQL.
-   Debezium SQL Server.
-   CDC consumer.
-   Event normalization.
-   Provenance.
-   Offset management.
-   Target writers.

**Exit criteria:** event CDC dapat ditangkap, dipersistenkan,
diterapkan, dan dipulihkan setelah restart.

## Phase 4 --- Monitoring dan Security

-   Dashboard.
-   SignalR.
-   Audit.
-   Conflict UI.
-   Worker monitoring.
-   Metrics.
-   Alerting.
-   Secret rotation.
-   Security review.

## Phase 5 --- Production Release

-   Integration test penuh.
-   Load test.
-   Failure test.
-   Backup/restore.
-   Docker deployment.
-   Windows/Linux deployment guide.
-   User manual.
-   Release checklist.

------------------------------------------------------------------------

# 24. Acceptance Criteria

DBSync V1 dapat dinyatakan siap untuk pilot production jika seluruh
kondisi berikut terpenuhi.

## 24.1 Connection

-   [ ] MySQL dapat dihubungkan.
-   [ ] PostgreSQL dapat dihubungkan.
-   [ ] SQL Server dapat dihubungkan.
-   [ ] Test connection menampilkan hasil yang jelas.
-   [ ] Password terenkripsi.
-   [ ] Password tidak muncul pada log atau API response.

## 24.2 Configuration

-   [ ] Group mempunyai tepat dua endpoint.
-   [ ] Tabel dapat ditemukan otomatis.
-   [ ] Administrator dapat memilih tabel tertentu.
-   [ ] Primary key divalidasi.
-   [ ] Schema mismatch ditampilkan.
-   [ ] Arah sinkronisasi dapat dikonfigurasi.

## 24.3 Synchronization

-   [ ] Initial sync hanya menjalankan tabel terpilih.
-   [ ] Batch processing berjalan.
-   [ ] INSERT tersinkronisasi.
-   [ ] UPDATE tersinkronisasi.
-   [ ] DELETE tersinkronisasi.
-   [ ] Bidirectional berjalan.
-   [ ] Duplicate event aman.
-   [ ] Loop prevention diuji.
-   [ ] Checkpoint durable.
-   [ ] Worker restart dapat recovery.

## 24.4 Monitoring

-   [ ] Progress terlihat.
-   [ ] Riwayat tersimpan.
-   [ ] Error dapat ditelusuri.
-   [ ] Retry berfungsi.
-   [ ] Dead letter dapat diperiksa.
-   [ ] Konflik dapat diselesaikan.
-   [ ] Audit tersedia.

## 24.5 Production

-   [ ] HTTPS aktif.
-   [ ] Role dan permission diuji.
-   [ ] Backup dan restore diuji.
-   [ ] Master key aman.
-   [ ] Database internal tidak terbuka ke publik.
-   [ ] Kafka dan CDC monitoring aktif.
-   [ ] Dokumentasi deployment tersedia.
-   [ ] Failure test lulus.

------------------------------------------------------------------------

# 25. Risiko dan Mitigasi

  -----------------------------------------------------------------------
  Risiko                  Dampak                  Mitigasi
  ----------------------- ----------------------- -----------------------
  Primary key tidak       Record sulit            Tolak tabel pada V1
  tersedia                diidentifikasi          

  Schema berbeda          Gagal apply             Schema validation dan
                                                  mapping

  Event duplikat          Efek ganda              Idempotency

  Loop A-B-A              Event berulang          Provenance dan
                                                  deduplication

  Konflik bersamaan       Data tertimpa           Conflict resolver

  Worker crash            Job terhenti            Durable checkpoint dan
                                                  recovery

  Binlog/WAL hilang       CDC gap                 Retention monitoring
                                                  dan resnapshot

  Disk penuh              Kafka/DB berhenti       Disk alert dan
                                                  retention

  Password bocor          Akses ilegal            Secret encryption dan
                                                  masking

  Clock berbeda           LWW salah               Version/source ordering
                                                  yang tepercaya

  DELETE tidak terdeteksi Data tujuan tertinggal  CDC/tombstone

  Cross-vendor type loss  Data berubah            Type mapping dan
                                                  validation

  Foreign key order       Insert gagal            Dependency ordering

  Multi-group cycle       Propagasi berulang      Topology validation
  -----------------------------------------------------------------------

------------------------------------------------------------------------

# 26. Keputusan Arsitektur

Keputusan berikut menjadi baseline implementasi DBSync V1.

    No. Area               Keputusan
  ----- ------------------ -----------------------------------------------
      1 Database support   MySQL, PostgreSQL, SQL Server
      2 Topology           Pairwise
      3 Endpoint           Dua database per group
      4 Direction          A→B, B→A, Bidirectional
      5 Initial Sync       Hanya tabel terpilih
      6 Engine             Scheduled + Real-Time CDC
      7 Backend            ASP.NET Core / .NET 10
      8 Frontend           Nuxt / Vue 3 / TypeScript
      9 Internal DB        PostgreSQL
     10 CDC                Debezium
     11 Streaming          Apache Kafka
     12 Scheduler          Quartz.NET
     13 Real-time UI       SignalR
     14 ORM                Entity Framework Core untuk metadata
     15 Data access        Native SQL melalui connector
     16 Default conflict   Manual
     17 Event guarantee    At-least-once + idempotency
     18 Deployment         Docker Compose development, server production
     19 Security           RBAC, encrypted secrets, TLS
     20 Schema change      Manual/controlled, bukan auto DDL

Keputusan yang masih harus divalidasi saat implementasi:

-   Versi minimum masing-masing database.
-   Strategi snapshot konsisten per connector.
-   Detail offset dan checkpoint.
-   Strategi target-side idempotency marker.
-   Retensi event dan CDC log.
-   Kapasitas Kafka dan worker.
-   Strategi topology/provenance lintas group.
-   RPO/RTO dan kebijakan backup.

------------------------------------------------------------------------

# 27. Glosarium

  Istilah       Arti
  ------------- --------------------------------------
  API           Application Programming Interface
  CDC           Change Data Capture
  DDL           Data Definition Language
  DML           Data Manipulation Language
  WAL           Write-Ahead Log
  LSN           Log Sequence Number
  Binlog        MySQL Binary Log
  Kafka         Distributed event streaming platform
  Debezium      Change Data Capture platform
  Checkpoint    Posisi proses terakhir
  Snapshot      Pembacaan data konsisten
  Upsert        Insert atau update
  Idempotency   Aman diproses berulang
  Dead Letter   Event gagal permanen
  Provenance    Asal-usul event
  RBAC          Role-Based Access Control
  RPO           Recovery Point Objective
  RTO           Recovery Time Objective
  TLS           Transport Layer Security
  P95           Persentil ke-95 pengukuran

------------------------------------------------------------------------

# 28. Referensi Teknis

Dokumentasi resmi yang menjadi rujukan implementasi:

## .NET dan ASP.NET Core

-   https://dotnet.microsoft.com/en-us/platform/support/policy
-   https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview
-   https://learn.microsoft.com/en-us/aspnet/core/

## Database Drivers

-   MySqlConnector: https://mysqlconnector.net/
-   Npgsql: https://www.npgsql.org/doc/
-   Microsoft.Data.SqlClient:
    https://learn.microsoft.com/en-us/sql/connect/ado-net/introduction-microsoft-data-sqlclient-namespace

## CDC dan Streaming

-   Debezium: https://debezium.io/documentation/reference/stable/
-   Apache Kafka: https://kafka.apache.org/documentation/
-   Kafka Connect: https://kafka.apache.org/documentation/#connect

## Database CDC

-   MySQL: https://dev.mysql.com/doc/
-   PostgreSQL Logical Replication:
    https://www.postgresql.org/docs/current/logical-replication.html
-   SQL Server CDC:
    https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-data-capture-sql-server

## Frontend

-   Nuxt: https://nuxt.com/docs
-   Vue: https://vuejs.org/guide/
-   TypeScript: https://www.typescriptlang.org/docs/

## Infrastruktur dan Testing

-   Docker Compose: https://docs.docker.com/compose/
-   Nginx: https://nginx.org/en/docs/
-   xUnit: https://xunit.net/
-   Testcontainers for .NET: https://dotnet.testcontainers.org/

------------------------------------------------------------------------

# Penutup

DBSync dirancang sebagai platform sinkronisasi database lintas vendor
yang dapat dikembangkan secara bertahap, dimulai dari pengelolaan
koneksi dan schema discovery, dilanjutkan dengan scheduled
synchronization, kemudian real-time CDC.

Keberhasilan aplikasi tidak hanya diukur dari kemampuan memindahkan
data, tetapi juga dari kemampuan menjaga konsistensi, mencegah
duplikasi, mendeteksi konflik, pulih setelah kegagalan, dan menyediakan
jejak audit yang dapat diperiksa.

**Urutan implementasi yang direkomendasikan:**

1.  Project Foundation.
2.  Database Connection Manager.
3.  Schema Discovery dan Table Mapping.
4.  Initial Synchronization.
5.  Scheduled Incremental Synchronization.
6.  Bidirectional Processing dan Conflict Management.
7.  Real-Time CDC.
8.  Monitoring, Security, dan Production Hardening.

Dokumen ini merupakan baseline spesifikasi DBSync V1.0. Detail
implementasi yang bergantung pada perilaku vendor harus dikonfirmasi
melalui integration test sebelum dinyatakan siap production.


note: buatkan juga 1 file dokumentasi untuk setting aplikasi step by step secara detail sampai benar2 bisa jalan semua aplikasinya