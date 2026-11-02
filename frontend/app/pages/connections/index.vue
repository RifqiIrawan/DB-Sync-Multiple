<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const page = ref(1); const size = 15; const search = ref(''); const dbType = ref('')
const list = ref<any>({ items: [], total: 0 })
const loading = ref(false)

async function load() {
  loading.value = true
  try { list.value = await api.get('/connections', { page: page.value, pageSize: size, search: search.value || undefined, dbType: dbType.value || undefined }) }
  finally { loading.value = false }
}
watch([page, dbType], load); watch(search, () => { page.value = 1; load() })
onMounted(load)

const defaultPorts: Record<string, number> = { MYSQL: 3306, POSTGRESQL: 5432, SQLSERVER: 1433 }
const blank = () => ({ id: '', connectionName: '', dbType: 'MYSQL', host: '', port: 3306, databaseName: '', username: '', password: '', sslEnabled: false, trustServerCertificate: true })
const form = ref<any>(blank()); const showForm = ref(false); const saving = ref(false); const testResult = ref<any>(null); const testing = ref(false)
function openNew() { form.value = blank(); testResult.value = null; showForm.value = true }
function openEdit(c: any) { form.value = { ...c, password: '', trustServerCertificate: c.connectionOptions?.trustServerCertificate !== 'false' }; testResult.value = null; showForm.value = true }
watch(() => form.value.dbType, (t) => { if (!form.value.id) form.value.port = defaultPorts[t] ?? 0 })

function payload() {
  const f = form.value
  return { connectionName: f.connectionName, dbType: f.dbType, host: f.host, port: Number(f.port), databaseName: f.databaseName, username: f.username,
    password: f.password || null, sslEnabled: f.sslEnabled, connectionOptions: f.dbType === 'SQLSERVER' ? { trustServerCertificate: String(f.trustServerCertificate) } : null }
}
async function testForm() {
  testing.value = true; testResult.value = null
  try {
    if (form.value.password) testResult.value = await api.post('/connections/test', payload())
    else if (form.value.id) testResult.value = await api.post(`/connections/${form.value.id}/test`)
    else toast.warn('Isi password untuk menguji koneksi baru')
  } finally { testing.value = false }
}
async function save() {
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
    <CommonPageHeader title="Database Connections" subtitle="MySQL, PostgreSQL, dan SQL Server. Password disimpan terenkripsi (AES-256-GCM).">
      <button v-if="auth.canManage.value" class="btn btn-primary" @click="openNew"><i class="bi bi-plus-lg"></i> Add Connection</button>
    </CommonPageHeader>
    <div class="card">
      <div class="card-body pb-0 d-flex gap-2">
        <input v-model.lazy="search" class="form-control form-control-sm" style="max-width: 280px" placeholder="Cari nama / host...">
        <select v-model="dbType" class="form-select form-select-sm" style="max-width: 180px"><option value="">Semua tipe</option><option>MYSQL</option><option>POSTGRESQL</option><option>SQLSERVER</option></select>
      </div>
      <div class="table-responsive p-3"><table class="table table-hover table-sm align-middle mb-0">
        <thead><tr><th>Nama</th><th>Tipe</th><th>Host : Port</th><th>Database</th><th>User</th><th>Status</th><th>Terakhir diuji</th><th>Dipakai</th><th></th></tr></thead>
        <tbody>
          <tr v-for="c in list.items" :key="c.id">
            <td><strong>{{ c.connectionName }}</strong></td><td><span class="badge text-bg-light border">{{ c.dbType }}</span></td>
            <td class="mono">{{ c.host }}:{{ c.port }}</td><td>{{ c.databaseName }}</td><td>{{ c.username }}</td>
            <td><CommonStatusBadge :status="c.status" /><div class="small text-muted text-truncate" style="max-width: 200px" :title="c.lastTestMessage">{{ c.status === 'DISCONNECTED' ? c.lastTestMessage : '' }}</div></td>
            <td class="small">{{ fmtDate(c.lastTestedAt) }}</td><td>{{ c.usedByGroups }} group</td>
            <td class="text-end text-nowrap">
              <button v-if="auth.canOperate.value" class="btn btn-sm btn-outline-primary me-1" @click="testRow(c)"><i class="bi bi-plug"></i> Test</button>
              <template v-if="auth.canManage.value">
                <button class="btn btn-sm btn-outline-secondary me-1" @click="openEdit(c)"><i class="bi bi-pencil"></i></button>
                <button class="btn btn-sm btn-outline-danger" @click="remove(c)"><i class="bi bi-trash"></i></button>
              </template>
            </td>
          </tr>
          <tr v-if="!list.items.length && !loading"><td colspan="9" class="text-center text-muted py-4">Belum ada koneksi.</td></tr>
        </tbody>
      </table>
      <CommonPager v-model:page="page" :page-size="size" :total="list.total" /></div>
    </div>

    <div v-if="showForm" class="modal-backdrop-custom" @click.self="showForm = false">
      <form class="modal-panel" @submit.prevent="save">
        <div class="p-3 border-bottom fw-semibold">{{ form.id ? 'Edit' : 'Add' }} Connection</div>
        <div class="p-3 row g-3">
          <div class="col-12"><label class="form-label">Nama koneksi</label><input v-model="form.connectionName" class="form-control" required></div>
          <div class="col-md-4"><label class="form-label">Tipe database</label>
            <select v-model="form.dbType" class="form-select"><option value="MYSQL">MySQL / MariaDB</option><option value="POSTGRESQL">PostgreSQL</option><option value="SQLSERVER">SQL Server</option></select></div>
          <div class="col-md-5"><label class="form-label">Host</label><input v-model="form.host" class="form-control" required></div>
          <div class="col-md-3"><label class="form-label">Port</label><input v-model.number="form.port" type="number" class="form-control" required></div>
          <div class="col-md-6"><label class="form-label">Database</label><input v-model="form.databaseName" class="form-control" required></div>
          <div class="col-md-6"><label class="form-label">Username</label><input v-model="form.username" class="form-control" required autocomplete="off"></div>
          <div class="col-12"><label class="form-label">Password <span v-if="form.id" class="text-muted small">(kosongkan bila tidak diubah)</span></label>
            <input v-model="form.password" type="password" class="form-control" :required="!form.id" autocomplete="new-password"></div>
          <div class="col-md-6 form-check ms-3"><input id="ssl" v-model="form.sslEnabled" type="checkbox" class="form-check-input"><label for="ssl" class="form-check-label">Gunakan SSL/TLS</label></div>
          <div v-if="form.dbType === 'SQLSERVER'" class="col-md-5 form-check"><input id="tsc" v-model="form.trustServerCertificate" type="checkbox" class="form-check-input"><label for="tsc" class="form-check-label">Trust server certificate</label></div>
          <div class="col-12" v-if="testResult"><div class="alert py-2 mb-0" :class="testResult.success ? 'alert-success' : 'alert-danger'">
            <strong>{{ testResult.success ? 'Berhasil' : 'Gagal' }}</strong> — {{ testResult.message }} <span v-if="testResult.serverVersion" class="mono">({{ testResult.serverVersion }})</span></div></div>
        </div>
        <div class="p-3 border-top d-flex justify-content-between">
          <button type="button" class="btn btn-outline-primary" :disabled="testing" @click="testForm"><i class="bi bi-plug"></i> {{ testing ? 'Menguji...' : 'Test Connection' }}</button>
          <div class="d-flex gap-2"><button type="button" class="btn btn-light" @click="showForm = false">Batal</button><button class="btn btn-primary" :disabled="saving">Simpan</button></div>
        </div>
      </form>
    </div>
  </div>
</template>
