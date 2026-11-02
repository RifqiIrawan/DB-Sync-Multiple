<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const groups = ref<any[]>([]); const status = ref(''); const search = ref('')
async function load() { groups.value = await api.get('/sync-groups', { status: status.value || undefined, search: search.value || undefined }) }
watch([status, search], load); usePolling(load, 5000)

async function act(g: any, action: string, label: string) {
  if (action === 'stop' && !(await confirm.ask('Stop group?', `Group "${g.groupName}" dihentikan dan job yang antre dibatalkan.`, true))) return
  try { await api.post(`/sync-groups/${g.id}/${action}`); toast.success(`${label} berhasil`); await load() } catch { /* toast otomatis */ }
}
async function remove(g: any) {
  if (!(await confirm.ask('Hapus group?', `Group "${g.groupName}" beserta konfigurasi tabel, riwayat, dan event akan dihapus permanen. Data pada database A/B tidak berubah.`, true))) return
  await api.del(`/sync-groups/${g.id}`); toast.success('Group dihapus'); await load()
}
const arrow = (d: string) => d === 'A_TO_B' ? '→' : d === 'B_TO_A' ? '←' : '⇄'
</script>
<template>
  <div>
    <CommonPageHeader title="Synchronization Groups" subtitle="Setiap group menghubungkan tepat dua database (Endpoint A dan B).">
      <NuxtLink v-if="auth.canManage.value" to="/sync-groups/new" class="btn btn-primary"><i class="bi bi-plus-lg"></i> Create Group</NuxtLink>
    </CommonPageHeader>
    <div class="card">
      <div class="card-body pb-0 d-flex gap-2">
        <input v-model.lazy="search" class="form-control form-control-sm" style="max-width: 260px" placeholder="Cari group...">
        <select v-model="status" class="form-select form-select-sm" style="max-width: 180px"><option value="">Semua status</option><option>DRAFT</option><option>ACTIVE</option><option>PAUSED</option><option>ERROR</option><option>STOPPED</option></select>
      </div>
      <div class="table-responsive p-3"><table class="table table-hover table-sm align-middle mb-0">
        <thead><tr><th>Group</th><th>Endpoint A</th><th></th><th>Endpoint B</th><th>Mode</th><th>Konflik</th><th>Tabel</th><th>Status</th><th></th></tr></thead>
        <tbody>
          <tr v-for="g in groups" :key="g.id">
            <td><NuxtLink :to="`/sync-groups/${g.id}`"><strong>{{ g.groupName }}</strong></NuxtLink><div class="small text-muted">{{ g.description }}</div></td>
            <td>{{ g.endpointA?.connectionName }}<div class="small text-muted">{{ g.endpointA?.dbType }}</div></td>
            <td class="fs-5 text-primary">{{ arrow(g.syncDirection) }}</td>
            <td>{{ g.endpointB?.connectionName }}<div class="small text-muted">{{ g.endpointB?.dbType }}</div></td>
            <td><span class="badge text-bg-light border">{{ g.syncMode }}</span><div class="small text-muted" v-if="g.syncMode === 'SCHEDULED'">tiap {{ Math.round(g.scheduleIntervalSeconds / 60) }} mnt</div></td>
            <td class="small">{{ g.conflictStrategy }}</td><td>{{ g.tableCount }}</td>
            <td><CommonStatusBadge :status="g.status" /></td>
            <td class="text-end text-nowrap">
              <template v-if="auth.canOperate.value">
                <button v-if="g.status === 'ACTIVE'" class="btn btn-sm btn-outline-warning me-1" @click="act(g, 'pause', 'Pause')"><i class="bi bi-pause-fill"></i></button>
                <button v-if="g.status === 'PAUSED'" class="btn btn-sm btn-outline-success me-1" @click="act(g, 'resume', 'Resume')"><i class="bi bi-play-fill"></i></button>
              </template>
              <template v-if="auth.canManage.value">
                <button v-if="['DRAFT', 'ERROR', 'STOPPED'].includes(g.status)" class="btn btn-sm btn-outline-success me-1" @click="act(g, 'activate', 'Aktivasi')">Activate</button>
                <button v-if="['ACTIVE', 'PAUSED', 'ERROR'].includes(g.status)" class="btn btn-sm btn-outline-secondary me-1" @click="act(g, 'stop', 'Stop')"><i class="bi bi-stop-fill"></i></button>
                <button v-if="!['ACTIVE', 'PAUSED'].includes(g.status)" class="btn btn-sm btn-outline-danger" @click="remove(g)"><i class="bi bi-trash"></i></button>
              </template>
            </td>
          </tr>
          <tr v-if="!groups.length"><td colspan="9" class="text-center text-muted py-4">Belum ada group.</td></tr>
        </tbody>
      </table></div>
    </div>
  </div>
</template>
