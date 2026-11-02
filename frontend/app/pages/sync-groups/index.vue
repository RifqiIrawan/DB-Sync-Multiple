<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const groups = ref<any[] | null>(null); const status = ref(''); const search = ref(''); const running = ref<Set<string>>(new Set())
async function load() {
  groups.value = await api.get('/sync-groups')
  try { const d = await api.get('/monitoring/dashboard', undefined, true); running.value = new Set(d.runningRuns.map((r: any) => r.syncGroupId)) } catch { /* abaikan */ }
}
usePolling(load, 5000)

const chips = [['', 'Semua'], ['ACTIVE', 'Aktif'], ['PAUSED', 'Dijeda'], ['DRAFT', 'Draft'], ['ERROR', 'Error'], ['STOPPED', 'Berhenti']]
const shown = computed(() => (groups.value ?? []).filter(g => (!status.value || g.status === status.value) && (!search.value || g.groupName.toLowerCase().includes(search.value.toLowerCase()))))
const count = (s: string) => (groups.value ?? []).filter(g => !s || g.status === s).length

async function act(g: any, action: string, label: string) {
  if (action === 'stop' && !(await confirm.ask('Hentikan grup?', `Grup "${g.groupName}" dihentikan dan job yang antre dibatalkan.`, true))) return
  try { await api.post(`/sync-groups/${g.id}/${action}`); toast.success(`${label} berhasil`); await load() } catch { /* toast otomatis */ }
}
async function syncNow(g: any) { try { await api.post(`/sync-groups/${g.id}/sync-now`); toast.success('Sinkronisasi diantrekan', g.groupName); await load() } catch { /* */ } }
async function remove(g: any) {
  if (!(await confirm.ask('Hapus grup?', `Grup "${g.groupName}" beserta konfigurasi tabel, riwayat, dan event akan dihapus permanen. Data pada database A/B tidak berubah.`, true))) return
  await api.del(`/sync-groups/${g.id}`); toast.success('Grup dihapus'); await load()
}
const modeLabel = (m: string) => m === 'CDC' ? 'Real-time CDC' : m === 'SCHEDULED' ? 'Terjadwal' : m
const dirLabel = (d: string) => d === 'A_TO_B' ? 'A → B' : d === 'B_TO_A' ? 'B → A' : 'Dua arah'
</script>
<template>
  <div>
    <CommonPageHeader title="Grup Sinkronisasi" subtitle="Setiap grup menghubungkan tepat dua database (Endpoint A dan B)" icon="diagram-2">
      <NuxtLink v-if="auth.canManage.value" to="/sync-groups/new" class="btn btn-primary"><i class="bi bi-plus-lg"></i> Buat grup</NuxtLink>
    </CommonPageHeader>

    <div class="d-flex gap-2 flex-wrap align-items-center mb-3">
      <button v-for="c in chips" :key="c[0]" class="btn btn-sm" :class="status === c[0] ? 'btn-primary' : 'btn-light'" @click="status = c[0]!">{{ c[1] }} <span class="opacity-75">{{ count(c[0]!) }}</span></button>
      <div class="flex-grow-1"></div>
      <div class="input-group" style="max-width: 260px"><span class="input-group-text"><i class="bi bi-search"></i></span><input v-model="search" class="form-control form-control-sm" placeholder="Cari grup…"></div>
    </div>

    <div v-if="!groups" class="row g-3"><div v-for="i in 3" :key="i" class="col-lg-6 col-xl-4"><div class="card"><CommonSkeleton :rows="4" /></div></div></div>
    <div v-else-if="!groups.length" class="card"><CommonEmptyState icon="diagram-2" title="Belum ada grup sinkronisasi" text="Buat grup pertama Anda untuk mulai menyinkronkan dua database. Wizard akan memandu Anda langkah demi langkah.">
      <NuxtLink v-if="auth.canManage.value" to="/sync-groups/new" class="btn btn-primary"><i class="bi bi-magic"></i> Mulai wizard</NuxtLink></CommonEmptyState></div>
    <div v-else-if="!shown.length" class="card"><CommonEmptyState icon="funnel" title="Tidak ada hasil" text="Ubah filter status atau kata kunci pencarian." /></div>

    <div v-else class="row g-3 rise">
      <div v-for="g in shown" :key="g.id" class="col-lg-6 col-xl-4">
        <div class="card lift group-card h-100"><div class="card-body d-flex flex-column gap-3">
          <div class="d-flex justify-content-between align-items-start gap-2">
            <div style="min-width: 0"><NuxtLink :to="`/sync-groups/${g.id}`" class="gname text-body stretched-link-off">{{ g.groupName }}</NuxtLink>
              <div class="help text-truncate">{{ g.description || 'Tanpa deskripsi' }}</div></div>
            <CommonStatusBadge :status="running.has(g.id) ? 'RUNNING' : g.status" />
          </div>
          <SynchronizationFlowDiagram :a="g.endpointA" :b="g.endpointB" :direction="g.syncDirection" :running="running.has(g.id)" :active="g.status === 'ACTIVE'" />
          <div class="d-flex flex-wrap gap-2">
            <span class="dbchip"><i class="bi bi-lightning-charge"></i>{{ modeLabel(g.syncMode) }}<template v-if="g.syncMode === 'SCHEDULED'"> · {{ Math.round(g.scheduleIntervalSeconds / 60) }} mnt</template></span>
            <span class="dbchip"><i class="bi bi-arrow-left-right"></i>{{ dirLabel(g.syncDirection) }}</span>
            <span class="dbchip"><i class="bi bi-table"></i>{{ g.tableCount }} tabel</span>
          </div>
          <div class="d-flex gap-2 mt-auto pt-1 flex-wrap">
            <NuxtLink :to="`/sync-groups/${g.id}`" class="btn btn-sm btn-primary flex-grow-1">Buka <i class="bi bi-arrow-right"></i></NuxtLink>
            <template v-if="auth.canOperate.value">
              <button v-if="g.status === 'ACTIVE'" class="btn btn-sm btn-outline-secondary" title="Sinkronkan sekarang" @click="syncNow(g)"><i class="bi bi-arrow-repeat"></i></button>
              <button v-if="g.status === 'ACTIVE'" class="btn btn-sm btn-outline-warning" title="Jeda" @click="act(g, 'pause', 'Jeda')"><i class="bi bi-pause-fill"></i></button>
              <button v-if="g.status === 'PAUSED'" class="btn btn-sm btn-outline-success" title="Lanjutkan" @click="act(g, 'resume', 'Lanjutkan')"><i class="bi bi-play-fill"></i></button>
            </template>
            <template v-if="auth.canManage.value">
              <button v-if="['DRAFT', 'ERROR', 'STOPPED'].includes(g.status)" class="btn btn-sm btn-outline-success" @click="act(g, 'activate', 'Aktivasi')">Aktifkan</button>
              <button v-if="['ACTIVE', 'PAUSED', 'ERROR'].includes(g.status)" class="btn btn-sm btn-outline-secondary" title="Hentikan" @click="act(g, 'stop', 'Hentikan')"><i class="bi bi-stop-fill"></i></button>
              <button v-if="!['ACTIVE', 'PAUSED'].includes(g.status)" class="btn btn-sm btn-outline-danger" title="Hapus" @click="remove(g)"><i class="bi bi-trash"></i></button>
            </template>
          </div>
        </div></div>
      </div>
    </div>
  </div>
</template>
