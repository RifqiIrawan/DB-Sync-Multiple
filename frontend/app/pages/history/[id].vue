<script setup lang="ts">
const id = useRoute().params.id as string
const api = useApi(); const r = ref<any>(null); const events = ref<any>({ items: [], total: 0 }); const page = ref(1)
async function load() { r.value = await api.get(`/sync-runs/${id}`) }
async function loadEvents() { events.value = await api.get('/monitoring/events', { page: page.value, pageSize: 20 }) }
onMounted(load)
</script>
<template>
  <div v-if="r">
    <CommonPageHeader :title="`Run — ${r.groupName}`" :subtitle="r.id"><NuxtLink to="/history" class="btn btn-light">‹ Riwayat</NuxtLink></CommonPageHeader>
    <div class="row g-3 mb-3">
      <div class="col-6 col-md-2" v-for="x in [['Dibaca', r.recordsRead], ['Insert', r.recordsInserted], ['Update', r.recordsUpdated], ['Delete', r.recordsDeleted], ['Gagal', r.recordsFailed], ['Konflik', r.conflictsDetected]]" :key="x[0] as string">
        <div class="card"><div class="card-body py-2"><div class="stat fs-4">{{ fmtNum(x[1] as number) }}</div><div class="stat-label">{{ x[0] }}</div></div></div></div>
    </div>
    <div class="card"><div class="card-header">Detail per tabel — <CommonStatusBadge :status="r.detail.status" /> <span class="small text-muted ms-2">{{ r.detail.runType }} · {{ fmtDate(r.detail.startedAt) }} → {{ fmtDate(r.detail.finishedAt) }}</span></div>
      <div class="alert alert-danger m-3 py-2 small" v-if="r.detail.errorSummary">{{ r.detail.errorSummary }}</div>
      <div class="table-responsive"><table class="table table-sm align-middle mb-0">
        <thead><tr><th>Tabel</th><th>Status</th><th class="text-end">Dibaca</th><th class="text-end">Ditulis</th><th class="text-end">Dilewati</th><th class="text-end">Gagal</th><th>Durasi</th><th>Catatan</th></tr></thead>
        <tbody><tr v-for="i in r.detail.items" :key="i.id"><td class="mono">{{ i.tableLabel }}</td><td><CommonStatusBadge :status="i.status" /></td>
          <td class="text-end">{{ fmtNum(i.recordsRead) }}</td><td class="text-end">{{ fmtNum(i.recordsWritten) }}</td><td class="text-end">{{ fmtNum(i.recordsSkipped) }}</td><td class="text-end">{{ fmtNum(i.recordsFailed) }}</td>
          <td class="small">{{ fmtDuration(i.startedAt, i.finishedAt) }}</td><td class="small text-danger">{{ i.errorMessage }}</td></tr></tbody></table></div></div>
  </div>
</template>
