<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const page = ref(1); const size = 15; const search = ref(''); const dbType = ref('')
const list = ref<any>({ items: [], total: 0 })
const loading = ref(true)

async function load() {
  try { list.value = await api.get('/connections', { page: page.value, pageSize: size, search: search.value || undefined, dbType: dbType.value || undefined }) }
  finally { loading.value = false }
}
watch([page, dbType], load); watch(search, () => { page.value = 1; load() })
onMounted(load)

const defaultPorts: Record<string, number> = { MYSQL: 3306, POSTGRESQL: 5432, SQLSERVER: 1433 }
const typeLabel: Record<string, string> = { MYSQL: 'MySQL / MariaDB', POSTGRESQL: 'PostgreSQL', SQLSERVER: 'SQL Server' }
const blank = () => ({ id: '', connectionName: '', dbType: 'MYSQL', host: '', port: 3306, databaseName: '', username: '', password: '', clearPassword: false, sslEnabled: false, trustServerCertificate: true, cdcHost: '', cdcPort: '', extraOptions: {} as Record<string, string> })
const form = ref<any>(blank()); const showForm = ref(false); const saving = ref(false); const testResult = ref<any>(null); const testing = ref(false)
const showAdvanced = ref(false)

// ---- daftar database dari server (dropdown) ----
const dbs = ref<string[] | null>(null); const dbLoading = ref(false); const dbError = ref(''); const manualDb = ref(false)
let dbTimer: any; let dbSeq = 0
// cukup host + port + username: password boleh kosong (sebagian server tanpa password)
const canListDbs = computed(() => !!(form.value.host && form.value.port && form.value.username))
async function loadDatabases(silent = true) {
  if (!canListDbs.value) { if (!silent) toast.warn('Isi host, port, dan username dulu'); return }
  const seq = ++dbSeq; dbLoading.value = true; dbError.value = ''
  try {
    const f = form.value
    const r = await api.post<string[]>('/connections/databases', { dbType: f.dbType, host: f.host, port: Number(f.port), username: f.username, password: f.id ? (f.clearPassword ? '' : (f.password || null)) : f.password,
      sslEnabled: f.sslEnabled, connectionOptions: optionsPayload(), connectionId: f.id || null }, true)
    if (seq !== dbSeq) return
    dbs.value = r; manualDb.value = false
    if (r.length === 1 && !f.databaseName) f.databaseName = r[0]!
    if (!silent) toast.success(`${r.length} database ditemukan`)
  } catch (e: any) {
    if (seq !== dbSeq) return
    dbs.value = null
    dbError.value = e?.data?.errors?.[0]?.message ?? 'Tidak dapat membaca daftar database. Anda masih bisa mengetik nama database manual.'
  } finally { if (seq === dbSeq) dbLoading.value = false }
}
// otomatis muat setelah field koneksi terisi (tunda 800 ms agar tidak menembak server tiap ketikan)
watch(() => [form.value.dbType, form.value.host, form.value.port, form.value.username, form.value.password, form.value.clearPassword, form.value.sslEnabled, form.value.trustServerCertificate], () => {
  if (!showForm.value) return
  clearTimeout(dbTimer); dbs.value = null; dbError.value = ''
  if (canListDbs.value) dbTimer = setTimeout(() => loadDatabases(true), 800)
})
watch(() => form.value.dbType, (t) => { if (!form.value.id) form.value.port = defaultPorts[t] ?? 0 })

function openNew() { form.value = blank(); testResult.value = null; dbs.value = null; dbError.value = ''; manualDb.value = false; showAdvanced.value = false; showForm.value = true }
function openEdit(c: any) {
  const o = c.connectionOptions ?? {}
  const { cdcHost = '', cdcPort = '', trustServerCertificate, ...extra } = o
  form.value = { ...c, password: '', clearPassword: false, trustServerCertificate: trustServerCertificate !== 'false', cdcHost, cdcPort, extraOptions: extra }
  testResult.value = null; dbs.value = null; dbError.value = ''; manualDb.value = false; showAdvanced.value = !!(cdcHost || cdcPort); showForm.value = true
  loadDatabases(true)
}

function optionsPayload() {
  const f = form.value; const o: Record<string, string> = { ...(f.extraOptions ?? {}) }
  if (f.dbType === 'SQLSERVER') o.trustServerCertificate = String(f.trustServerCertificate)
  if (f.cdcHost) o.cdcHost = f.cdcHost
  if (f.cdcPort) o.cdcPort = String(f.cdcPort)
  return o
}
function payload() {
  const f = form.value
  return { connectionName: f.connectionName, dbType: f.dbType, host: f.host, port: Number(f.port), databaseName: f.databaseName, username: f.username,
    // buat baru: kosong = password kosong. Edit: kosong = tidak diubah, kecuali dicentang 'setel password kosong'
    password: f.id ? (f.clearPassword ? '' : (f.password || null)) : f.password, sslEnabled: f.sslEnabled, connectionOptions: optionsPayload() }
}
async function testForm() {
  testing.value = true; testResult.value = null
  try {
    // password tidak diubah pada koneksi tersimpan -> uji memakai password tersimpan; selain itu uji dengan isian form
    if (form.value.id && !form.value.password && !form.value.clearPassword) testResult.value = await api.post(`/connections/${form.value.id}/test`)
    else testResult.value = await api.post('/connections/test', payload())
  } finally { testing.value = false }
}
async function save() {
  if (!form.value.databaseName) return toast.warn('Pilih atau isi nama database')
  saving.value = true
  try {
    if (form.value.id) await api.put(`/connections/${form.value.id}`, payload()); else await api.post('/connections', payload())
    toast.success('Koneksi disimpan'); showForm.value = false; await load()
  } finally { saving.value = false }
}
async function testRow(c: any) { const r = await api.post(`/connections/${c.id}/test`); r.success ? toast.success(c.connectionName, `Terhubung · ${r.serverVersion ?? ''}`) : toast.error(c.connectionName, r.message); await load() }
async function remove(c: any) {
  if (!(await confirm.ask('Hapus koneksi?', `Koneksi "${c.connectionName}" akan dihapus.`, true))) return
  await api.del(`/connections/${c.id}`); toast.success('Koneksi dihapus'); await load()
}
</script>
<template>
  <div>
    <CommonPageHeader title="Koneksi Database" icon="hdd-network" subtitle="MySQL, PostgreSQL, dan SQL Server. Password disimpan terenkripsi (AES-256-GCM).">
      <button v-if="auth.canManage.value" class="btn btn-primary" @click="openNew"><i class="bi bi-plus-lg"></i> Tambah koneksi</button>
    </CommonPageHeader>
    <div class="card">
      <div class="card-body pb-0 d-flex gap-2 flex-wrap">
        <div class="input-group" style="max-width: 300px"><span class="input-group-text"><i class="bi bi-search"></i></span><input v-model.lazy="search" class="form-control form-control-sm" placeholder="Cari nama / host..."></div>
        <select v-model="dbType" class="form-select form-select-sm" style="max-width: 190px"><option value="">Semua tipe</option><option value="MYSQL">MySQL / MariaDB</option><option value="POSTGRESQL">PostgreSQL</option><option value="SQLSERVER">SQL Server</option></select>
      </div>
      <div class="table-responsive p-3"><table class="table table-hover table-sm align-middle mb-0">
        <thead><tr><th>Nama</th><th>Tipe</th><th>Host : Port</th><th>Database</th><th>User</th><th>Status</th><th>Terakhir diuji</th><th>Dipakai</th><th></th></tr></thead>
        <tbody>
          <tr v-for="c in list.items" :key="c.id">
            <td><strong>{{ c.connectionName }}</strong></td><td><span class="dbchip">{{ typeLabel[c.dbType] ?? c.dbType }}</span></td>
            <td class="mono">{{ c.host }}:{{ c.port }}</td><td>{{ c.databaseName }}</td><td>{{ c.username }}</td>
            <td><CommonStatusBadge :status="c.status" /><div class="small text-muted text-truncate" style="max-width: 200px" :title="c.lastTestMessage">{{ c.status === 'DISCONNECTED' ? c.lastTestMessage : '' }}</div></td>
            <td class="small">{{ fmtDate(c.lastTestedAt) }}</td><td>{{ c.usedByGroups }} grup</td>
            <td class="text-end text-nowrap">
              <button v-if="auth.canOperate.value" class="btn btn-sm btn-outline-primary me-1" @click="testRow(c)"><i class="bi bi-plug"></i> Test</button>
              <template v-if="auth.canManage.value">
                <button class="btn btn-sm btn-outline-secondary me-1" title="Edit" aria-label="Edit" @click="openEdit(c)"><i class="bi bi-pencil"></i></button>
                <button class="btn btn-sm btn-outline-danger" title="Hapus" aria-label="Hapus" @click="remove(c)"><i class="bi bi-trash"></i></button>
              </template>
            </td>
          </tr>
          <tr v-if="loading && !list.items.length"><td colspan="9"><CommonSkeleton :rows="3" /></td></tr>
          <tr v-if="!list.items.length && !loading"><td colspan="9"><CommonEmptyState icon="hdd-network" title="Belum ada koneksi database" text="Tambahkan koneksi MySQL, PostgreSQL, atau SQL Server. Setelah host dan kredensial terisi, daftar database dimuat otomatis.">
            <button v-if="auth.canManage.value" class="btn btn-primary" @click="openNew"><i class="bi bi-plus-lg"></i> Tambah koneksi</button></CommonEmptyState></td></tr>
        </tbody>
      </table>
      <CommonPager v-model:page="page" :page-size="size" :total="list.total" /></div>
    </div>

    <div v-if="showForm" class="modal-backdrop-custom" @click.self="showForm = false">
      <form class="modal-panel" @submit.prevent="save">
        <div class="p-3 border-bottom fw-semibold">{{ form.id ? 'Edit' : 'Tambah' }} koneksi</div>
        <div class="p-3 row g-3">
          <div class="col-12"><label class="form-label">Nama koneksi</label><input v-model="form.connectionName" class="form-control" required placeholder="mis. HRGA Produksi"></div>
          <div class="col-md-4"><label class="form-label">Tipe database</label>
            <select v-model="form.dbType" class="form-select"><option value="MYSQL">MySQL / MariaDB</option><option value="POSTGRESQL">PostgreSQL</option><option value="SQLSERVER">SQL Server</option></select></div>
          <div class="col-md-5"><label class="form-label">Host</label><input v-model="form.host" class="form-control" required placeholder="localhost atau 192.168.1.10"></div>
          <div class="col-md-3"><label class="form-label">Port</label><input v-model.number="form.port" type="number" class="form-control" required></div>
          <div class="col-md-6"><label class="form-label">Username</label><input v-model="form.username" class="form-control" required autocomplete="off"></div>
          <div class="col-md-6"><label class="form-label">Password <span v-if="form.id" class="text-muted fw-normal">(kosong = tidak diubah)</span></label>
            <input v-model="form.password" type="password" class="form-control" :disabled="form.clearPassword" :placeholder="form.id ? '••••••••' : 'kosong jika tanpa password'" autocomplete="new-password">
            <div v-if="form.id" class="form-check mt-1"><input id="clrpw" v-model="form.clearPassword" type="checkbox" class="form-check-input" @change="form.password = ''"><label for="clrpw" class="form-check-label help">Setel password menjadi kosong</label></div></div>

          <div class="col-12">
            <label class="form-label d-flex justify-content-between">
              <span>Database</span>
              <span class="fw-normal">
                <span v-if="dbLoading" class="text-muted"><span class="spinner-border spinner-border-sm me-1"></span>memuat daftar…</span>
                <a v-else-if="dbs" href="#" @click.prevent="manualDb = !manualDb">{{ manualDb ? 'pilih dari daftar' : 'ketik manual' }}</a>
              </span>
            </label>
            <div class="input-group">
              <select v-if="dbs && !manualDb" v-model="form.databaseName" class="form-select" required>
                <option value="" disabled>— pilih database ({{ dbs.length }} tersedia) —</option>
                <option v-if="form.databaseName && !dbs.includes(form.databaseName)" :value="form.databaseName">{{ form.databaseName }} (tersimpan)</option>
                <option v-for="d in dbs" :key="d" :value="d">{{ d }}</option>
              </select>
              <input v-else v-model="form.databaseName" class="form-control" required placeholder="nama database">
              <button type="button" class="btn btn-outline-primary" :disabled="dbLoading" title="Ambil daftar database dari server (klik untuk memuat ulang)" @click="loadDatabases(false)"><i class="bi bi-arrow-repeat" :class="{ spin: dbLoading }"></i> {{ dbLoading ? 'Memuat…' : (dbs ? 'Muat ulang' : 'Muat database') }}</button>
            </div>
            <div class="help mt-1" v-if="dbError"><i class="bi bi-info-circle"></i> {{ dbError }}</div>
            <div class="help mt-1" v-else-if="!dbs && !dbLoading">Isi host, port, dan username — daftar database dimuat otomatis dari server (password boleh kosong).</div>
          </div>

          <div class="col-12 d-flex gap-4 flex-wrap">
            <div class="form-check"><input id="ssl" v-model="form.sslEnabled" type="checkbox" class="form-check-input"><label for="ssl" class="form-check-label">Gunakan SSL/TLS</label></div>
            <div v-if="form.dbType === 'SQLSERVER'" class="form-check"><input id="tsc" v-model="form.trustServerCertificate" type="checkbox" class="form-check-input"><label for="tsc" class="form-check-label">Trust server certificate</label></div>
          </div>

          <div class="col-12"><a href="#" class="small" @click.prevent="showAdvanced = !showAdvanced"><i :class="`bi bi-chevron-${showAdvanced ? 'down' : 'right'}`"></i> Lanjutan: alamat untuk Debezium (CDC)</a>
            <div v-if="showAdvanced" class="row g-2 mt-1">
              <div class="col-md-8"><input v-model="form.cdcHost" class="form-control form-control-sm" placeholder="Host dari sisi Debezium (mis. nama container)"></div>
              <div class="col-md-4"><input v-model="form.cdcPort" class="form-control form-control-sm" placeholder="Port"></div>
              <div class="col-12 help">Isi hanya jika Debezium (Kafka Connect) menjangkau database lewat alamat berbeda dari DBSync, mis. di jaringan Docker. Kosong = pakai host/port di atas.</div></div></div>

          <div class="col-12" v-if="testResult"><div class="alert py-2 mb-0" :class="testResult.success ? 'alert-success' : 'alert-danger'">
            <strong>{{ testResult.success ? 'Berhasil' : 'Gagal' }}</strong> — {{ testResult.message }} <span v-if="testResult.serverVersion" class="mono">({{ testResult.serverVersion }})</span></div></div>
        </div>
        <div class="p-3 border-top d-flex justify-content-between">
          <button type="button" class="btn btn-outline-primary" :disabled="testing" @click="testForm"><i class="bi bi-plug"></i> {{ testing ? 'Menguji…' : 'Test koneksi' }}</button>
          <div class="d-flex gap-2"><button type="button" class="btn btn-light" @click="showForm = false">Batal</button><button class="btn btn-primary" :disabled="saving">Simpan</button></div>
        </div>
      </form>
    </div>
  </div>
</template>
<style scoped>
.spin { animation: spin 0.8s linear infinite; display: inline-block; }
@keyframes spin { to { transform: rotate(360deg); } }
</style>
