<script setup lang="ts">
const api = useApi(); const toast = useToast()
const steps = ['Informasi & Endpoint', 'Pilih Tabel', 'Mapping Kolom', 'Arah, Mode & Konflik', 'Validasi', 'Review & Aktifkan']
const step = ref(0)
const conns = ref<any[]>([])
const f = ref<any>({ groupName: '', description: '', endpointAConnectionId: '', endpointBConnectionId: '', syncDirection: 'BIDIRECTIONAL', syncMode: 'SCHEDULED', conflictStrategy: 'MANUAL', scheduleIntervalMinutes: 5, batchSize: 500, maxRetries: 3, maxParallelTables: 2 })
const tests = ref<Record<string, any>>({})
const groupId = ref<string>('')
const rules = ref<any[]>([])
const report = ref<any>(null); const busy = ref(false)

onMounted(async () => { conns.value = (await api.get('/connections', { pageSize: 200 })).items })
const connOf = (id: string) => conns.value.find(c => c.id === id)

type EpKey = 'endpointAConnectionId' | 'endpointBConnectionId'
// koneksi diuji OTOMATIS begitu dipilih; hasilnya tampil besar di kartu endpoint
async function testEndpoint(key: EpKey) {
  const id = f.value[key]
  if (!id) { tests.value[key] = null; return }
  tests.value[key] = { busy: true }
  try { tests.value[key] = await api.post(`/connections/${id}/test`, undefined, true) }
  catch (e: any) { tests.value[key] = { success: false, message: e?.data?.message ?? 'Tidak dapat menguji koneksi (API tidak terjangkau?)' } }
}
watch(() => f.value.endpointAConnectionId, () => { if (!groupId.value) testEndpoint('endpointAConnectionId') })
watch(() => f.value.endpointBConnectionId, () => { if (!groupId.value) testEndpoint('endpointBConnectionId') })

// daftar syarat untuk lanjut dari langkah 1 (ditampilkan sebagai checklist + alasan tombol nonaktif)
const checks = computed(() => {
  const a = f.value.endpointAConnectionId, b = f.value.endpointBConnectionId
  const st = (k: EpKey) => tests.value[k]
  return [
    { ok: !!f.value.groupName.trim(), text: 'Nama grup diisi', todo: 'Isi nama grup' },
    { ok: !!a && st('endpointAConnectionId')?.success === true, text: 'Endpoint A dipilih dan terhubung', todo: !a ? 'Pilih Endpoint A' : st('endpointAConnectionId')?.busy ? 'Menunggu tes Endpoint A' : 'Perbaiki koneksi Endpoint A' },
    { ok: !!b && st('endpointBConnectionId')?.success === true, text: 'Endpoint B dipilih dan terhubung', todo: !b ? 'Pilih Endpoint B' : st('endpointBConnectionId')?.busy ? 'Menunggu tes Endpoint B' : 'Perbaiki koneksi Endpoint B' },
    { ok: !!a && !!b && a !== b, text: 'Endpoint A dan B berbeda', todo: 'Pilih dua database yang berbeda' },
  ]
})
const missing = computed(() => checks.value.filter(c => !c.ok).map(c => c.todo))
const stepHelp = [
  'Beri nama grup lalu pilih dua database (A dan B). Koneksi diuji otomatis; setelah keduanya berstatus Terhubung, klik Lanjut.',
  'Centang tabel yang ingin disinkronkan, lalu klik tombol “Tambahkan … tabel terpilih” di bawah daftar (atau “Pilih semua yang memenuhi syarat”). Tabel yang sudah ditambahkan muncul di daftar “Tabel yang disinkronkan”. Setelah itu klik Lanjut.',
  'Kolom bernama sama sudah dipetakan otomatis. Anda hanya perlu mengubahnya bila nama kolom berbeda di kedua database; jika tidak, langsung klik Lanjut.',
  'Tentukan arah sinkronisasi, mode (terjadwal atau real-time CDC), dan cara menangani konflik. Nilai bawaan aman untuk memulai.',
  'DBSync memeriksa struktur tabel, tipe data, dan koneksi. Baris “error” harus diperbaiki; “warning” boleh diabaikan bila Anda paham dampaknya.',
  'Periksa ringkasan, lalu klik “Aktifkan grup”. Setelah aktif, jalankan Initial Sync dari halaman detail grup untuk menyalin data awal.',
]
function body() {
  const x = f.value
  return { groupName: x.groupName, description: x.description, syncDirection: x.syncDirection, syncMode: x.syncMode, conflictStrategy: x.conflictStrategy,
    endpointAConnectionId: x.endpointAConnectionId, endpointBConnectionId: x.endpointBConnectionId, scheduleIntervalSeconds: Math.max(1, x.scheduleIntervalMinutes) * 60,
    batchSize: x.batchSize, maxRetries: x.maxRetries, maxParallelTables: x.maxParallelTables }
}

async function next() {
  busy.value = true
  try {
    if (step.value === 0) {
      if (missing.value.length) return
      if (!groupId.value) { groupId.value = (await api.post('/sync-groups', body())).id; toast.info('Grup dibuat sebagai draft', 'Anda bisa melanjutkannya nanti dari menu Grup Sinkronisasi.') }
      else await api.put(`/sync-groups/${groupId.value}`, body())
    }
    if (step.value === 1) {
      rules.value = await api.get(`/sync-groups/${groupId.value}/table-rules`)
      if (!rules.value.length) return toast.warn('Belum ada tabel yang ditambahkan', 'Centang tabel, lalu klik tombol "Tambahkan … tabel terpilih" di bawah daftar.')
      await api.post(`/sync-groups/${groupId.value}/discover-schema`)
    }
    if (step.value === 3) await api.put(`/sync-groups/${groupId.value}`, body())
    if (step.value === 3) report.value = await api.post(`/sync-groups/${groupId.value}/validate`)
    step.value++
  } finally { busy.value = false }
}
async function revalidate() { busy.value = true; try { report.value = await api.post(`/sync-groups/${groupId.value}/validate`) } finally { busy.value = false } }
async function activate() {
  busy.value = true
  try { await api.post(`/sync-groups/${groupId.value}/activate`); toast.success('Group aktif'); await navigateTo(`/sync-groups/${groupId.value}`) } finally { busy.value = false }
}
const errors = computed(() => (report.value?.issues ?? []).filter((i: any) => i.severity === 'error'))
</script>
<template>
  <div>
    <CommonPageHeader title="Buat Grup Sinkronisasi" icon="magic" subtitle="Ikuti 6 langkah: database → tabel → mapping → arah & mode → validasi → aktifkan" />
    <div class="d-flex gap-3 mb-3 flex-wrap">
      <div v-for="(s, i) in steps" :key="s" class="d-flex align-items-center gap-2">
        <span class="step-pill" :class="{ active: i === step, done: i < step }">{{ i < step ? '✓' : i + 1 }}</span><span :class="i === step ? 'fw-semibold' : 'text-muted'">{{ s }}</span>
      </div>
    </div>

    <div class="alert alert-info d-flex gap-2 align-items-start"><i class="bi bi-lightbulb mt-1"></i><div><strong>Langkah {{ step + 1 }} dari {{ steps.length }}:</strong> {{ stepHelp[step] }}</div></div>

    <div class="card"><div class="card-body">
      <!-- 1 -->
      <div v-show="step === 0" class="row g-3">
        <div class="col-md-6"><label class="form-label">Nama grup</label><input v-model="f.groupName" class="form-control" placeholder="mis. HRGA-CENTRAL"></div>
        <div class="col-md-6"><label class="form-label">Deskripsi (opsional)</label><input v-model="f.description" class="form-control"></div>

        <div class="col-lg-6" v-for="(k, idx) in (['endpointAConnectionId', 'endpointBConnectionId'] as const)" :key="k">
          <div class="card h-100" :style="{ borderTop: `4px solid var(--${idx === 0 ? 'a' : 'b'})` }"><div class="card-body">
            <div class="d-flex align-items-center gap-2 mb-2"><span class="ep" :class="idx === 0 ? 'ep-a' : 'ep-b'">{{ idx === 0 ? 'A' : 'B' }}</span>
              <strong>Endpoint {{ idx === 0 ? 'A' : 'B' }}</strong><span class="help">{{ idx === 0 ? 'database pertama / sumber awal' : 'database kedua / tujuan awal' }}</span></div>
            <select v-model="f[k]" class="form-select" :disabled="!!groupId">
              <option value="">— pilih koneksi —</option>
              <option v-for="c in conns" :key="c.id" :value="c.id" :disabled="c.id === f[idx === 0 ? 'endpointBConnectionId' : 'endpointAConnectionId']">{{ c.connectionName }} ({{ c.dbType }} · {{ c.databaseName }})</option>
            </select>

            <div class="mt-3" style="min-height: 64px">
              <div v-if="!f[k]" class="help"><i class="bi bi-arrow-up"></i> Pilih koneksi untuk mulai — pengujian berjalan otomatis.</div>
              <div v-else-if="tests[k]?.busy" class="alert alert-info py-2 mb-0 d-flex align-items-center gap-2"><span class="spinner-border spinner-border-sm"></span> Menguji koneksi…</div>
              <div v-else-if="tests[k]?.success" class="alert alert-success py-2 mb-0"><i class="bi bi-check-circle-fill me-1"></i><strong>Terhubung</strong> <span class="mono ms-1">{{ tests[k].serverVersion }}</span></div>
              <div v-else-if="tests[k]" class="alert alert-danger py-2 mb-0">
                <div><i class="bi bi-x-circle-fill me-1"></i><strong>Gagal terhubung</strong></div>
                <div class="small mt-1" style="word-break: break-word">{{ tests[k].message }}</div>
                <div class="d-flex gap-2 mt-2"><button type="button" class="btn btn-sm btn-light" @click="testEndpoint(k)"><i class="bi bi-arrow-repeat"></i> Coba lagi</button>
                  <NuxtLink to="/connections" target="_blank" class="btn btn-sm btn-light"><i class="bi bi-wrench"></i> Perbaiki di menu Koneksi ↗</NuxtLink></div>
              </div>
              <button v-if="f[k] && tests[k]?.success" type="button" class="btn btn-link btn-sm p-0 mt-1" @click="testEndpoint(k)"><i class="bi bi-arrow-repeat"></i> uji ulang</button>
            </div>
          </div></div>
        </div>

        <div class="col-12" v-if="!conns.length"><div class="alert alert-warning mb-0"><i class="bi bi-exclamation-triangle me-1"></i> Belum ada koneksi database. <NuxtLink to="/connections" class="fw-semibold">Tambahkan koneksi dulu</NuxtLink>, lalu kembali ke sini.</div></div>

        <div class="col-12"><div class="d-flex flex-wrap gap-3 p-3 rounded" style="background: var(--card-2); border: 1px dashed var(--line)">
          <span class="help fw-semibold align-self-center">Syarat untuk lanjut:</span>
          <span v-for="c in checks" :key="c.text" class="status" :class="c.ok ? 's-ok' : 's-mute'"><i :class="`bi bi-${c.ok ? 'check-circle-fill' : 'circle'}`"></i> {{ c.text }}</span>
        </div></div>
      </div>      <!-- 2 -->
      <div v-if="step === 1"><SynchronizationTableSelector :group-id="groupId" /></div>
      <!-- 3 -->
      <div v-if="step === 2">
        <p class="text-muted">Kolom bernama sama dipetakan otomatis. Anda dapat menyesuaikan nama kolom per tabel:</p>
        <ul class="list-group"><li v-for="r in rules" :key="r.id" class="list-group-item d-flex justify-content-between align-items-center">
          <span><strong>{{ r.endpointATable }}</strong> <CommonStatusBadge :status="r.validationStatus" /></span>
          <NuxtLink :to="`/table-mapping/${r.id}`" target="_blank" class="btn btn-sm btn-outline-secondary">Edit mapping kolom ↗</NuxtLink></li></ul>
      </div>
      <!-- 4 -->
      <div v-if="step === 3" class="row g-3">
        <div class="col-md-4"><label class="form-label">Arah sinkronisasi</label>
          <select v-model="f.syncDirection" class="form-select"><option value="A_TO_B">A → B</option><option value="B_TO_A">B → A</option><option value="BIDIRECTIONAL">Dua arah (⇄)</option></select></div>
        <div class="col-md-4"><label class="form-label">Mode</label>
          <select v-model="f.syncMode" class="form-select"><option value="SCHEDULED">Scheduled Incremental</option><option value="CDC">Real-Time CDC (Debezium + Kafka)</option></select></div>
        <div class="col-md-4"><label class="form-label">Strategi konflik</label>
          <select v-model="f.conflictStrategy" class="form-select"><option value="MANUAL">Manual (default)</option><option value="A_PRIORITY">A Priority</option><option value="B_PRIORITY">B Priority</option><option value="LAST_WRITE_WINS">Last Write Wins (butuh kolom updated_at)</option></select></div>
        <template v-if="f.syncMode === 'SCHEDULED'">
          <div class="col-md-3"><label class="form-label">Interval (menit, min. 1)</label><input v-model.number="f.scheduleIntervalMinutes" type="number" min="1" class="form-control"></div>
        </template>
        <div class="col-md-3"><label class="form-label">Batch size</label><input v-model.number="f.batchSize" type="number" min="1" max="10000" class="form-control"></div>
        <div class="col-md-3"><label class="form-label">Retry otomatis</label><input v-model.number="f.maxRetries" type="number" min="0" class="form-control"></div>
        <div class="col-md-3"><label class="form-label">Tabel paralel</label><input v-model.number="f.maxParallelTables" type="number" min="1" class="form-control"></div>
        <div class="col-12"><div class="alert alert-info py-2 small mb-0" v-if="f.syncMode === 'CDC'">Mode CDC membutuhkan Kafka + Kafka Connect/Debezium berjalan dan prasyarat CDC pada kedua database (binlog / wal_level=logical / SQL Server CDC). Prasyarat diperiksa saat validasi. Lihat PANDUAN_SETUP.md.</div></div>
      </div>
      <!-- 5 -->
      <div v-if="step === 4">
        <div class="d-flex justify-content-between mb-2"><h6 class="mb-0">Hasil validasi group</h6><button class="btn btn-sm btn-outline-primary" :disabled="busy" @click="revalidate">Validasi ulang</button></div>
        <div v-if="report">
          <div class="mb-2">Status: <CommonStatusBadge :status="report.status" /></div>
          <table class="table table-sm"><thead><tr><th>Level</th><th>Kode</th><th>Pesan</th></tr></thead><tbody>
            <tr v-for="(i, k) in report.issues" :key="k"><td><span class="badge" :class="i.severity === 'error' ? 'text-bg-danger' : 'text-bg-warning'">{{ i.severity }}</span></td><td class="mono small">{{ i.code }}</td><td>{{ i.message }}</td></tr>
            <tr v-if="!report.issues.length"><td colspan="3" class="text-success">Semua pemeriksaan lulus.</td></tr></tbody></table>
        </div>
      </div>
      <!-- 6 -->
      <div v-if="step === 5">
        <dl class="row mb-3">
          <dt class="col-sm-3">Group</dt><dd class="col-sm-9">{{ f.groupName }}</dd>
          <dt class="col-sm-3">Endpoint A</dt><dd class="col-sm-9">{{ connOf(f.endpointAConnectionId)?.connectionName }} ({{ connOf(f.endpointAConnectionId)?.dbType }})</dd>
          <dt class="col-sm-3">Endpoint B</dt><dd class="col-sm-9">{{ connOf(f.endpointBConnectionId)?.connectionName }} ({{ connOf(f.endpointBConnectionId)?.dbType }})</dd>
          <dt class="col-sm-3">Arah / Mode</dt><dd class="col-sm-9">{{ f.syncDirection }} · {{ f.syncMode }} · konflik {{ f.conflictStrategy }}</dd>
          <dt class="col-sm-3">Tabel</dt><dd class="col-sm-9">{{ rules.map(r => r.endpointATable).join(', ') }}</dd>
        </dl>
        <div class="alert alert-warning small">Setelah aktif, jalankan <strong>Initial Sync</strong> dari halaman detail group. Tabel baru dilewati oleh sinkronisasi terjadwal sampai initial sync selesai.</div>
        <div v-if="errors.length" class="alert alert-danger small">Masih ada {{ errors.length }} error validasi. Kembali ke langkah sebelumnya.</div>
      </div>
    </div>
    <div class="card-footer bg-white d-flex justify-content-between">
      <button class="btn btn-light" :disabled="step === 0 || busy" @click="step--">‹ Kembali</button>
      <div class="d-flex gap-2">
        <NuxtLink v-if="groupId" :to="`/sync-groups/${groupId}`" class="btn btn-outline-secondary">Simpan sebagai draft</NuxtLink>
        <span v-if="step === 0 && missing.length" class="help align-self-center me-1"><i class="bi bi-info-circle"></i> Belum bisa lanjut: {{ missing.join(' · ') }}</span>
        <button v-if="step < 5" class="btn btn-primary" :disabled="busy || (step === 0 && missing.length > 0)" @click="next"><span v-if="busy" class="spinner-border spinner-border-sm"></span> Lanjut <i class="bi bi-arrow-right"></i></button>
        <button v-else class="btn btn-success" :disabled="busy || errors.length > 0" @click="activate"><i class="bi bi-rocket-takeoff"></i> Aktifkan group</button>
      </div>
    </div></div>
  </div>
</template>
