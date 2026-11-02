<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast()
const groups = ref<any[]>([]); const d = ref<any>(null)
async function load() {
  groups.value = await api.get('/sync-groups', undefined, true)
  d.value = await api.get('/monitoring/dashboard', undefined, true)
}
usePolling(load, 3000)
const scheduled = computed(() => groups.value.filter(g => g.syncMode === 'SCHEDULED'))
async function syncNow(g: any) { try { await api.post(`/sync-groups/${g.id}/sync-now`); toast.success('Sync diantrekan'); await load() } catch { /* */ } }
</script>
<template>
  <div>
    <CommonPageHeader title="Job & Jadwal" icon="clock-history" subtitle="Initial sync, jadwal incremental, dan job yang sedang berjalan" />
    <div class="card mb-3"><div class="card-header">Running / Queued Jobs</div>
      <div class="card-body" v-if="d?.runningRuns?.length">
        <div v-for="r in d.runningRuns" :key="r.id" class="mb-3">
          <div class="d-flex justify-content-between"><NuxtLink :to="`/sync-groups/${r.syncGroupId}`"><strong>{{ r.groupName }}</strong></NuxtLink><span><span class="badge text-bg-light border me-1">{{ r.runType }}</span><CommonStatusBadge :status="r.status" /></span></div>
          <div v-for="(i, k) in r.items" :key="k" class="small d-flex justify-content-between"><span class="mono">{{ i.tableLabel }}</span><span>{{ fmtNum(i.recordsRead) }}<template v-if="i.totalRows"> / {{ fmtNum(i.totalRows) }}</template> · {{ i.status }}</span></div>
        </div>
      </div>
      <div class="card-body text-muted" v-else>Tidak ada job berjalan.</div></div>

    <div class="card"><div class="card-header">Scheduled Groups</div>
      <div class="table-responsive"><table class="table table-sm align-middle mb-0">
        <thead><tr><th>Group</th><th>Interval</th><th>Status</th><th>Tabel</th><th></th></tr></thead>
        <tbody>
          <tr v-for="g in scheduled" :key="g.id">
            <td><NuxtLink :to="`/sync-groups/${g.id}`">{{ g.groupName }}</NuxtLink></td><td>{{ Math.round(g.scheduleIntervalSeconds / 60) }} menit</td>
            <td><CommonStatusBadge :status="g.status" /></td><td>{{ g.tableCount }}</td>
            <td class="text-end"><NuxtLink :to="`/sync-groups/${g.id}`" class="btn btn-sm btn-outline-secondary me-1">Initial sync</NuxtLink>
              <button v-if="auth.canOperate.value && g.status !== 'STOPPED'" class="btn btn-sm btn-outline-primary" @click="syncNow(g)">Sync now</button></td>
          </tr>
          <tr v-if="!scheduled.length"><td colspan="5" class="text-center text-muted py-3">Tidak ada group bermode SCHEDULED.</td></tr>
        </tbody></table></div></div>
  </div>
</template>
