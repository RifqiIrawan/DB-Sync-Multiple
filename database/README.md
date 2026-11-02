Skema database konfigurasi dibuat otomatis oleh API/Worker saat start (EF Core `EnsureCreated` + seeding role & admin).
Untuk produksi dengan perubahan skema berkelanjutan, ganti ke EF Core Migrations (`dotnet ef migrations add`) sebelum rilis berikutnya.
`seeds/` berisi database demo untuk uji; `scripts/` untuk skrip operasional.
