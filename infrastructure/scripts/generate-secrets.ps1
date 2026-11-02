# Membuat master key (AES-256) dan JWT signing key yang kuat untuk DBSync.
$bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$jwt = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($jwt)
"DBSYNC_MASTER_KEY=$([Convert]::ToBase64String($bytes))"
"DBSYNC_JWT_KEY=$([Convert]::ToBase64String($jwt))"
"# Simpan di secret manager / .env di luar repository. Hilangnya master key = password koneksi tidak bisa didekripsi."