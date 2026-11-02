<script setup lang="ts">
const api = useApi(); const toast = useToast(); const confirm = useConfirm(); const auth = useAuth()
const users = ref<any[]>([]); const roles = ref<any[]>([]); const form = ref<any>(null)
async function load() { [users.value, roles.value] = await Promise.all([api.get('/users'), api.get('/roles')]) }
onMounted(load)
const roleHelp: Record<string, string> = { SuperAdmin: 'Seluruh fungsi', Administrator: 'Koneksi, group, konfigurasi', Operator: 'Menjalankan & memonitor sync', Viewer: 'Melihat dashboard & riwayat', Auditor: 'Melihat audit & log' }
function openNew() { form.value = { id: '', username: '', email: '', password: '', roles: ['Viewer'], isActive: true } }
function openEdit(u: any) { form.value = { id: u.id, username: u.username, email: u.email, password: '', roles: [...u.roles], isActive: u.isActive } }
function toggleRole(r: string) { const i = form.value.roles.indexOf(r); i >= 0 ? form.value.roles.splice(i, 1) : form.value.roles.push(r) }
async function save() {
  if (form.value.id) await api.put(`/users/${form.value.id}`, { email: form.value.email, isActive: form.value.isActive, roles: form.value.roles, newPassword: form.value.password || null })
  else await api.post('/users', { username: form.value.username, email: form.value.email, password: form.value.password, roles: form.value.roles })
  toast.success('User disimpan'); form.value = null; await load()
}
async function remove(u: any) { if (await confirm.ask('Hapus user?', u.username, true)) { await api.del(`/users/${u.id}`); await load() } }
</script>
<template>
  <div>
    <CommonPageHeader title="Users & Roles" subtitle="Hak akses ditegakkan di backend (RBAC), bukan sekadar menyembunyikan menu."><button class="btn btn-primary" @click="openNew"><i class="bi bi-plus-lg"></i> Add User</button></CommonPageHeader>
    <div class="row g-3">
      <div class="col-lg-8"><div class="card"><div class="table-responsive"><table class="table table-sm align-middle mb-0">
        <thead><tr><th>User</th><th>Email</th><th>Role</th><th>Status</th><th>Login terakhir</th><th></th></tr></thead>
        <tbody><tr v-for="u in users" :key="u.id"><td><strong>{{ u.username }}</strong></td><td>{{ u.email }}</td><td><span v-for="r in u.roles" :key="r" class="badge text-bg-light border me-1">{{ r }}</span></td>
          <td><CommonStatusBadge :status="u.isActive ? 'ACTIVE' : 'STOPPED'" /></td><td class="small">{{ fmtDate(u.lastLoginAt) }}</td>
          <td class="text-end"><button class="btn btn-sm btn-outline-secondary me-1" @click="openEdit(u)"><i class="bi bi-pencil"></i></button><button class="btn btn-sm btn-outline-danger" :disabled="u.id === auth.user.value?.id" @click="remove(u)"><i class="bi bi-trash"></i></button></td></tr></tbody></table></div></div></div>
      <div class="col-lg-4"><div class="card"><div class="card-header">Role</div><ul class="list-group list-group-flush"><li v-for="r in roles" :key="r.id" class="list-group-item"><strong>{{ r.name }}</strong><div class="small text-muted">{{ roleHelp[r.name] }}</div></li></ul></div></div>
    </div>
    <div v-if="form" class="modal-backdrop-custom" @click.self="form = null"><form class="modal-panel" @submit.prevent="save">
      <div class="p-3 border-bottom fw-semibold">{{ form.id ? 'Edit' : 'Tambah' }} user</div>
      <div class="p-3 row g-3">
        <div class="col-md-6"><label class="form-label">Username</label><input v-model="form.username" class="form-control" :disabled="!!form.id" required></div>
        <div class="col-md-6"><label class="form-label">Email</label><input v-model="form.email" type="email" class="form-control" required></div>
        <div class="col-12"><label class="form-label">Password <span v-if="form.id" class="small text-muted">(kosong = tidak diubah)</span> <span class="small text-muted">min. 10 karakter</span></label><input v-model="form.password" type="password" class="form-control" :required="!form.id" minlength="10" autocomplete="new-password"></div>
        <div class="col-12"><label class="form-label">Role</label><div class="d-flex flex-wrap gap-3"><div v-for="r in roles" :key="r.id" class="form-check"><input :id="'r' + r.name" class="form-check-input" type="checkbox" :checked="form.roles.includes(r.name)" @change="toggleRole(r.name)"><label :for="'r' + r.name" class="form-check-label">{{ r.name }}</label></div></div></div>
        <div class="col-12 form-check ms-3" v-if="form.id"><input id="act" v-model="form.isActive" class="form-check-input" type="checkbox"><label for="act" class="form-check-label">Aktif</label></div>
      </div>
      <div class="p-3 border-top text-end"><button type="button" class="btn btn-light me-2" @click="form = null">Batal</button><button class="btn btn-primary">Simpan</button></div></form></div>
  </div>
</template>
