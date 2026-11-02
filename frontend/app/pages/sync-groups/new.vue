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

async function testEndpoint(key: 'endpointAConnectionId' | 'endpointBConnectionId') {
  const id = f.value[key]; if (!id) return
  tests.value[key] = { busy: true }
  tests.value[key] = await api.post(`/connections/${id}/test`)
}
const bothOk = computed(() => tests.value.endpointAConnectionId?.success && tests.value.endpointBConnectionId?.success)
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
      if (!f.value.groupName || !f.value.endpointAConnectionId || !f.value.endpointBConnectionId) return toast.warn('Lengkapi nama group dan kedua endpoint')
      if (f.value.endpointAConnectionId === f.value.endpointBConnectionId) return toast.warn('Endpoint A dan B harus berbeda')
      if (!bothOk.value) return toast.warn('Test kedua koneksi hingga berhasil')
      if (!groupId.value) { groupId.value = (await api.post('/sync-groups', body())).id; toast.info('Group dibuat sebagai DRAFT') }
      else await api.put(`/sync-groups/${groupId.value}`, body())
    }
    if (step.value === 1) {
      rules.value = await api.get(`/sync-groups/${groupId.value}/table-rules`)
      if (!rules.value.length) return toast.warn('Pilih minimal satu tabel')
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
    <CommonPageHeader title="Create Synchronization Group" subtitle="Wizard: endpoint → tabel → mapping → arah/mode → validasi → aktivasi" />
    <div class="d-flex gap-3 mb-3 flex-wrap">
      <div v-for="(s, i) in steps" :key="s" class="d-flex align-items-center gap-2">
        <span class="step-pill" :class="{ active: i === step, done: i < step }">{{ i < step ? '✓' : i + 1 }}</span><span :class="i === step ? 'fw-semibold' : 'text-muted'">{{ s }}</span>
      </div>
    </div>

    <div class="card"><div class="card-body">
      <!-- 1 -->
      <div v-show="step === 0" class="row g-3">
        <div class="col-md-6"><label class="form-label">Nama group</label><input v-model="f.groupName" class="form-control" placeholder="mis. HRGA-CENTRAL"></div>
        <div class="col-md-6"><label class="form-label">Deskripsi</label><input v-model="f.description" class="form-control"></div>
        <div class="col-md-6" v-for="(k, idx) in (['endpointAConnectionId', 'endpointBConnectionId'] as const)" :key="k">
          <label class="form-label">Endpoint {{ idx === 0 ? 'A' : 'B' }}</label>
          <div class="input-group">
            <select v-model="f[k]" class="form-select" :disabled="!!groupId" @change="tests[k] = null">
              <option value="">— pilih koneksi —</option>
              <option v-for="c in conns" :key="c.id" :value="c.id" :disabled="c.id === f[idx === 0 ? 'endpointBConnectionId' : 'endpointAConnectionId']">{{ c.connectionName }} ({{ c.dbType }} · {{ c.databaseName }})</option>
            </select>
            <button class="btn btn-outline-primary" :disabled="!f[k]" @click="testEndpoint(k)">Test</button>
          </div>
          <div v-if="tests[k]" class="small mt-1" :class="tests[k].success ? 'text-success' : 'text-danger'">{{ tests[k].busy ? 'Menguji...' : (tests[k].success ? '✓ Terhubung ' + (tests[k].serverVersion ?? '') : '✗ ' + tests[k].message) }}</div>
        </div>
        <div class="col-12 text-muted small" v-if="!conns.length">Belum ada koneksi. <NuxtLink to="/connections">Tambahkan dulu</NuxtLink>.</div>
      </div>
      <!-- 2 -->
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
        <button v-if="step < 5" class="btn btn-primary" :disabled="busy" @click="next">Lanjut ›</button>
        <button v-else class="btn btn-success" :disabled="busy || errors.length > 0" @click="activate"><i class="bi bi-rocket-takeoff"></i> Aktifkan group</button>
      </div>
    </div></div>
  </div>
</template>
