# DBSync Platform

Aplikasi web untuk sinkronisasi database lintas vendor (MySQL/MariaDB, PostgreSQL, SQL Server) dengan mode **Scheduled Incremental** dan **Real-Time CDC** (Debezium + Kafka). Implementasi dari `DBSync_Technical_Specification_v1.0.md`.

- `backend/` — ASP.NET Core / .NET 10 (API, Worker, SyncEngine, Scheduler, Connectors) + `tests/`
- `frontend/` — Nuxt 4 / Vue 3 / TypeScript / Bootstrap 5
- `infrastructure/` — Docker Compose (PostgreSQL, Kafka, Debezium, demo DB, Nginx), skrip
- `database/` — seed demo
- **`PANDUAN_SETUP.md` — panduan lengkap langkah demi langkah** (mulai dari sini)

Ringkas (development):
```powershell
# 1) PostgreSQL konfigurasi: user & db "dbsync" (lihat panduan 3.1)
cd backend; dotnet run --project src/DBSync.API      # http://localhost:18080
cd frontend; npm install; npm run dev                # http://localhost:13000   (admin / Admin#12345 — hanya dev)
```
Dokumen teknis tambahan ada di `docs/`.
