<script setup lang="ts">
const route = useRoute(); const id = route.params.id as string
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const g = ref<any>(null); const st = ref<any>(null); const prog = ref<any>(null); const cdc = ref<any>(null); const rules = ref<any[]>([])
const tab = ref('overview'); const report = ref<any>(null); const busy = ref(false)
const edit = ref<any>(null)

async function load() {
  g.value = await api.get(`/sync-groups/${id}`)
  st.value = await api.get(`/sync-groups/${id}/status`, undefined, true)
  prog.value = await api.get(`/sync-groups/${id}/progress`, undefined, true)
  rules.value = await api.get(`/sync-groups/${id}/table-rules`)
  if (!edit.value) edit.value = { groupName: g.value.groupName, description: g.value.description, syncDirection: g.value.syncDirection, syncMode: g.value.syncMode, conflictStrategy: g.value.conflictStrategy,
    endpointAConnectionId: g.value.endpointA.connectionId, endpointBConnectionId: g.value.endpointB.connectionId, scheduleIntervalSeconds: g.value.scheduleIntervalSeconds, batchSize: g.value.batchSize, maxRetries: g.value.maxRetries, maxParallelTables: g.value.maxParallelTables, commandTimeoutSeconds: g.value.commandTimeoutSeconds }
}
usePolling(load, 3000)
async function loadCdc() { cdc.value = await api.get(`/sync-groups/${id}/cdc`) }
watch(tab, t => { if (t === 'cdc') loadCdc() })

const active = computed(() => ['QUEUED', 'RUNNING', 'CANCELLING'].includes(prog.value?.status))
async function act(path: string, label: string, danger = false, msg = '') {
  if (danger && !(await confirm.ask(label + '?', msg, true))) return
  busy.value = true
  try { const r = await api.post(`/sync-groups/${id}/${path}`); toast.success(label + ' berhasil'); await load(); return r } catch { /* toast */ } finally { busy.value = false }
}
const initOpts = ref({ sourceRole: 'A', restart: false, selected: [] as string[] })
async function startInitial() {
  const needs = rules.value.filter(r => r.isEnabled && (initOpts.value.selected.length === 0 || initOpts.value.selected.includes(r.id)))
  if (!(await confirm.ask('Jalankan Initial Sync?', `Menyalin baseline ${needs.length} tabel dari Endpoint ${initOpts.value.sourceRole} ke ${initOpts.value.sourceRole === 'A' ? 'B' : 'A'} (mode UPSERT; tidak ada penghapusan data tujuan).${initOpts.value.restart ? '\nMulai ULANG dari awal (checkpoint diabaikan).' : ''}`))) return
  busy.value = true
  try {
    await api.post(`/sync-groups/${id}/initial-sync`, { sourceRole: initOpts.value.sourceRole, restart: initOpts.value.restart, tableRuleIds: initOpts.value.selected.length ? initOpts.value.selected : null })
    toast.info('Initial sync diantrekan'); tab.value = 'jobs'; await load()
  } finally { busy.value = false }
}
async function validate() { busy.value = true; try { report.value = await api.post(`/sync-groups/${id}/validate`) } finally { busy.value = false } }
async function cancelRun() { if (prog.value && await confirm.ask('Batalkan job?', '', true)) { await api.post(`/sync-runs/${prog.value.id}/cancel`); await load() } }
async function saveSettings() { await api.put(`/sync-groups/${id}`, edit.value); toast.success('Pengaturan disimpan'); await load() }
async function setupCdc() { const r = await api.post(`/sync-groups/${id}/cdc/setup`); toast.success('Connector Debezium dibuat/diperbarui', r.connectors.join(', ')); await loadCdc() }
const locked = computed(() => ['ACTIVE', 'PAUSED'].includes(g.value?.status) && false)
</script>
<template>
  <div v-if="g">
    <CommonPageHeader :title="g.groupName" :subtitle="g.description || `${g.endpointA?.connectionName} ${g.syncDirection === 'A_TO_B' ? '→' : g.syncDirection === 'B_TO_A' ? '←' : '⇄'} ${g.endpointB?.connectionName}`">
      <CommonStatusBadge :status="g.status" />
      <template v-if="auth.canOperate.value">
        <button v-if="g.status === 'ACTIVE'" class="btn btn-sm btn-outline-warning" @click="act('pause', 'Pause')"><i class="bi bi-pause-fill"></i> Pause</button>
        <button v-if="g.status === 'PAUSED'" class="btn btn-sm btn-outline-success" @click="act('resume', 'Resume')"><i class="bi bi-play-fill"></i> Resume</button>
      </template>
      <template v-if="auth.canManage.value">
        <button v-if="['DRAFT', 'ERROR', 'STOPPED'].includes(g.status)" class="btn btn-sm btn-success" :disabled="busy" @click="act('activate', 'Aktivasi')">Activate</button>
        <button v-if="['ACTIVE', 'PAUSED', 'ERROR'].includes(g.status)" class="btn btn-sm btn-outline-secondary" @click="act('stop', 'Stop', true, 'Group dihentikan; job antre dibatalkan.')"><i class="bi bi-stop-fill"></i> Stop</button>
      </template>
    </CommonPageHeader>

    <ul class="nav nav-tabs mb-3">
      <li class="nav-item" v-for="t in [['overview', 'Overview'], ['tables', 'Tables'], ['jobs', 'Jobs'], ['settings', 'Settings'], ['cdc', 'CDC']]" :key="t[0]">
        <a class="nav-link" :class="{ active: tab === t[0] }" href="#" @click.prevent="tab = t[0]!">{{ t[1] }}</a></li>
    </ul>

    <!-- OVERVIEW -->
    <div v-if="tab === 'overview'" class="row g-3">
      <div class="col-lg-6"><div class="card h-100"><div class="card-header">Konfigurasi</div><div class="card-body">
        <dl class="row mb-0">
          <dt class="col-5">Endpoint A</dt><dd class="col-7">{{ g.endpointA?.connectionName }} <span class="badge text-bg-light border">{{ g.endpointA?.dbType }}</span></dd>
          <dt class="col-5">Endpoint B</dt><dd class="col-7">{{ g.endpointB?.connectionName }} <span class="badge text-bg-light border">{{ g.endpointB?.dbType }}</span></dd>
          <dt class="col-5">Arah</dt><dd class="col-7">{{ g.syncDirection }}</dd>
          <dt class="col-5">Mode</dt><dd class="col-7">{{ g.syncMode }} <span v-if="g.syncMode === 'SCHEDULED'" class="text-muted">· tiap {{ Math.round(g.scheduleIntervalSeconds / 60) }} menit</span></dd>
          <dt class="col-5">Strategi konflik</dt><dd class="col-7">{{ g.conflictStrategy }}</dd>
          <dt class="col-5">Batch / Retry / Paralel</dt><dd class="col-7">{{ g.batchSize }} / {{ g.maxRetries }} / {{ g.maxParallelTables }}</dd>
        </dl></div></div></div>
      <div class="col-lg-6"><div class="card h-100"><div class="card-header">Status</div><div class="card-body">
        <div class="row text-center g-2 mb-3">
          <div class="col-3" v-for="k in ['SUCCESS', 'RETRY', 'DEAD_LETTER', 'CONFLICT']" :key="k"><div class="stat fs-4">{{ st?.events?.[k] ?? 0 }}</div><div class="stat-label">{{ k }}</div></div>
        </div>
        <div class="small">Run terakhir: <template v-if="st?.lastRun"><CommonStatusBadge :status="st.lastRun.status" /> {{ st.lastRun.runType }} · {{ fmtDate(st.lastRun.finishedAt ?? st.lastRun.queuedAt) }}
          <div class="text-danger" v-if="st.lastRun.errorSummary">{{ st.lastRun.errorSummary }}</div></template><span v-else class="text-muted">belum ada</span></div>
        <div class="small mt-2" v-if="st?.pendingConflicts"><NuxtLink to="/conflicts" class="text-danger"><i class="bi bi-exclamation-diamond"></i> {{ st.pendingConflicts }} konflik menunggu resolusi</NuxtLink></div>
        <hr><div class="small fw-semibold mb-1">Tabel</div>
        <div v-for="t in st?.tables ?? []" :key="t.id" class="d-flex justify-content-between small"><span>{{ t.endpointATable }}</span><span><CommonStatusBadge :status="t.initialSyncStatus" /></span></div>
      </div></div></div>
      <div class="col-12" v-if="auth.canManage.value"><div class="card"><div class="card-header d-flex justify-content-between">Validasi group <button class="btn btn-sm btn-outline-primary" :disabled="busy" @click="validate">Jalankan validasi</button></div>
        <div class="card-body" v-if="report"><CommonStatusBadge :status="report.status" />
          <table class="table table-sm mt-2 mb-0"><tbody><tr v-for="(i, k) in report.issues" :key="k"><td style="width: 80px"><span class="badge" :class="i.severity === 'error' ? 'text-bg-danger' : 'text-bg-warning'">{{ i.severity }}</span></td><td class="mono small" style="width: 200px">{{ i.code }}</td><td>{{ i.message }}</td></tr></tbody></table></div></div></div>
    </div>

    <!-- TABLES -->
    <SynchronizationTableSelector v-if="tab === 'tables'" :group-id="id" :locked="g.status === 'ACTIVE' && active" @changed="load" />

    <!-- JOBS -->
    <div v-if="tab === 'jobs'" class="row g-3">
      <div class="col-lg-5" v-if="auth.canOperate.value">
        <div class="card mb-3"><div class="card-header">Initial Sync</div><div class="card-body">
          <p class="small text-muted">Memindahkan baseline data awal. Hanya tabel terpilih. Dapat dilanjutkan dari checkpoint bila worker restart.</p>
          <div class="mb-2"><label class="form-label">Sumber data</label>
            <select v-model="initOpts.sourceRole" class="form-select form-select-sm"><option value="A">Endpoint A → B</option><option value="B">Endpoint B → A</option></select></div>
          <div class="mb-2"><label class="form-label">Tabel (kosong = semua tabel terpilih)</label>
            <select v-model="initOpts.selected" multiple class="form-select form-select-sm" size="5"><option v-for="r in rules" :key="r.id" :value="r.id">{{ r.endpointATable }} — {{ r.initialSyncStatus }}</option></select></div>
          <div class="form-check mb-3"><input id="rs" v-model="initOpts.restart" class="form-check-input" type="checkbox"><label for="rs" class="form-check-label">Mulai ulang dari awal (abaikan checkpoint)</label></div>
          <button class="btn btn-primary w-100" :disabled="busy || active" @click="startInitial"><i class="bi bi-cloud-arrow-up"></i> Jalankan Initial Sync</button>
        </div></div>
        <div class="card"><div class="card-header">Sinkronisasi manual</div><div class="card-body">
          <p class="small text-muted">Jalankan satu siklus incremental sekarang (mendeteksi INSERT/UPDATE/DELETE dan konflik).</p>
          <button class="btn btn-outline-primary w-100" :disabled="busy || active || g.status === 'STOPPED'" @click="act('sync-now', 'Sync now')"><i class="bi bi-arrow-repeat"></i> Sync Now</button>
        </div></div>
      </div>
      <div :class="auth.canOperate.value ? 'col-lg-7' : 'col-12'">
        <div class="card"><div class="card-header d-flex justify-content-between">Progress<button v-if="active && auth.canOperate.value" class="btn btn-sm btn-outline-danger" @click="cancelRun">Batalkan</button></div>
          <div class="card-body" v-if="prog">
            <div class="mb-2"><CommonStatusBadge :status="prog.status" /> <span class="badge text-bg-light border">{{ prog.runType }}</span> <span class="small text-muted">{{ fmtDate(prog.startedAt ?? prog.queuedAt) }} · {{ fmtDuration(prog.startedAt, prog.finishedAt) }}</span></div>
            <div class="alert alert-danger py-2 small" v-if="prog.errorSummary">{{ prog.errorSummary }}</div>
            <div v-for="i in prog.items" :key="i.id" class="mb-3">
              <div class="d-flex justify-content-between"><strong class="small">{{ i.tableLabel }}</strong><CommonStatusBadge :status="i.status" /></div>
              <div class="progress my-1" style="height: 8px" v-if="i.percent != null"><div class="progress-bar" :class="i.status === 'RUNNING' ? 'progress-bar-striped progress-bar-animated' : ''" :style="{ width: i.percent + '%' }"></div></div>
              <div class="small text-muted">dibaca {{ fmtNum(i.recordsRead) }}<template v-if="i.totalRows != null && prog.runType === 'INITIAL'"> dari {{ fmtNum(i.totalRows) }} ({{ i.percent }}%)</template>
                · ditulis {{ fmtNum(i.recordsWritten) }} · dilewati {{ fmtNum(i.recordsSkipped) }} · gagal {{ fmtNum(i.recordsFailed) }} <template v-if="i.batchNumber">· batch {{ i.batchNumber }}</template></div>
              <div class="small text-danger" v-if="i.errorMessage">{{ i.errorMessage }}</div>
            </div>
          </div>
          <div class="card-body text-muted" v-else>Belum ada job.</div></div>
      </div>
    </div>

    <!-- SETTINGS -->
    <div v-if="tab === 'settings' && edit" class="card"><div class="card-body row g-3">
      <div class="col-md-6"><label class="form-label">Nama</label><input v-model="edit.groupName" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-md-6"><label class="form-label">Deskripsi</label><input v-model="edit.description" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-md-4"><label class="form-label">Arah</label><select v-model="edit.syncDirection" class="form-select" :disabled="!auth.canManage.value"><option value="A_TO_B">A → B</option><option value="B_TO_A">B → A</option><option value="BIDIRECTIONAL">Dua arah</option></select></div>
      <div class="col-md-4"><label class="form-label">Mode <span class="text-muted small">(tetap saat ACTIVE/PAUSED)</span></label><select v-model="edit.syncMode" class="form-select" :disabled="!auth.canManage.value"><option value="SCHEDULED">SCHEDULED</option><option value="CDC">CDC</option></select></div>
      <div class="col-md-4"><label class="form-label">Konflik</label><select v-model="edit.conflictStrategy" class="form-select" :disabled="!auth.canManage.value"><option>MANUAL</option><option>A_PRIORITY</option><option>B_PRIORITY</option><option>LAST_WRITE_WINS</option></select></div>
      <div class="col-md-3"><label class="form-label">Interval (detik, ≥60)</label><input v-model.number="edit.scheduleIntervalSeconds" type="number" min="60" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-md-3"><label class="form-label">Batch size</label><input v-model.number="edit.batchSize" type="number" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-md-2"><label class="form-label">Retry</label><input v-model.number="edit.maxRetries" type="number" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-md-2"><label class="form-label">Paralel</label><input v-model.number="edit.maxParallelTables" type="number" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-md-2"><label class="form-label">Timeout (dtk)</label><input v-model.number="edit.commandTimeoutSeconds" type="number" class="form-control" :disabled="!auth.canManage.value"></div>
      <div class="col-12" v-if="auth.canManage.value"><button class="btn btn-primary" @click="saveSettings">Simpan pengaturan</button></div>
    </div></div>

    <!-- CDC -->
    <div v-if="tab === 'cdc'">
      <div class="alert alert-secondary" v-if="g.syncMode !== 'CDC'">Group ini bermode {{ g.syncMode }}. Ubah ke CDC pada tab Settings (saat DRAFT/STOPPED) untuk memakai Debezium.</div>
      <div v-if="cdc && !cdc.enabled" class="alert alert-warning">CDC dinonaktifkan di konfigurasi backend (<code>Cdc:Enabled=false</code>).</div>
      <div v-else-if="cdc" class="row g-3">
        <div class="col-md-6" v-for="e in cdc.endpoints" :key="e.role"><div class="card h-100"><div class="card-header">Endpoint {{ e.role }} — {{ e.connection }}</div><div class="card-body">
          <div class="mb-2">Connector: <CommonStatusBadge :status="e.connector?.state ?? 'NOT CREATED'" /> <span class="small text-muted" v-if="e.connector">{{ e.connector.name }}</span></div>
          <div class="small text-danger mb-2" v-if="e.error">{{ e.error }}</div>
          <div class="small text-danger mb-2" v-if="e.connector?.trace" style="max-height: 80px; overflow: auto">{{ e.connector.trace.slice(0, 400) }}</div>
          <div class="fw-semibold small">Prasyarat</div>
          <ul class="list-unstyled small mb-0"><li v-for="c in e.prerequisites ?? []" :key="c.name"><i class="bi" :class="c.ok ? 'bi-check-circle-fill text-success' : 'bi-x-circle-fill text-danger'"></i> <span class="mono">{{ c.name }}</span> — <span class="text-muted">{{ c.detail }}</span></li></ul>
        </div></div></div>
        <div class="col-12" v-if="auth.canManage.value && g.syncMode === 'CDC'"><button class="btn btn-primary" @click="setupCdc">Buat / perbarui connector Debezium</button> <button class="btn btn-outline-secondary" @click="loadCdc">Refresh</button></div>
      </div>
    </div>
  </div>
  <div v-else class="text-muted">Memuat...</div>
</template>
