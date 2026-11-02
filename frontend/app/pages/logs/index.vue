<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const tab = ref<'events' | 'dead'>('events')
const f = ref({ status: '', groupId: '', operation: '', from: '', to: '' })
const groups = ref<any[]>([]); const page = ref(1); const size = 25
const list = ref<any>({ items: [], total: 0 }); const detail = ref<any>(null)

async function load() {
  if (tab.value === 'events')
    list.value = await api.get('/monitoring/events', { status: f.value.status || undefined, groupId: f.value.groupId || undefined, operation: f.value.operation || undefined,
      from: f.value.from ? new Date(f.value.from).toISOString() : undefined, to: f.value.to ? new Date(f.value.to).toISOString() : undefined, page: page.value, pageSize: size })
  else list.value = await api.get('/monitoring/dead-letters', { groupId: f.value.groupId || undefined, page: page.value, pageSize: size })
}
onMounted(async () => { groups.value = await api.get('/sync-groups'); load() })
watch([tab, page], load); watch(f, () => { page.value = 1; load() }, { deep: true })
usePolling(load, 8000)
async function retry(e: any) { await api.post(`/monitoring/dead-letters/${e.id}/retry`); toast.success('Event dijadwalkan ulang'); await load() }
async function retryAll() {
  if (!(await confirm.ask('Coba ulang semua dead letter?', `${fmtNum(list.value.total)} event${f.value.groupId ? ' pada grup terpilih' : ''} dikembalikan ke antrean. Lakukan ini setelah penyebab error diperbaiki; bila penyebabnya belum teratasi, event akan gagal lagi.`))) return
  const r = await api.post('/monitoring/dead-letters/retry-all', { groupId: f.value.groupId || null })
  toast.success(`${r.retried} event dijadwalkan ulang`, 'Akan diproses pada siklus sinkronisasi berikutnya.'); await load()
}
async function show(e: any) { detail.value = await api.get(`/monitoring/events/${e.id}`) }
</script>
<template>
  <div>
    <CommonPageHeader title="Antrean Event & Gagal" icon="list-check" subtitle="Setiap perubahan tercatat; gagal permanen masuk dead letter sehingga tidak hilang diam-diam" />
    <ul class="nav nav-tabs mb-3">
      <li class="nav-item"><a class="nav-link" :class="{ active: tab === 'events' }" href="#" @click.prevent="tab = 'events'; page = 1">Semua event</a></li>
      <li class="nav-item"><a class="nav-link" :class="{ active: tab === 'dead' }" href="#" @click.prevent="tab = 'dead'; page = 1">Dead letter / Failed</a></li>
    </ul>
    <div class="card mb-3"><div class="card-body d-flex gap-2 flex-wrap">
      <select v-model="f.groupId" class="form-select form-select-sm" style="width: 200px"><option value="">Semua group</option><option v-for="g in groups" :key="g.id" :value="g.id">{{ g.groupName }}</option></select>
      <button v-if="tab === 'dead' && list.total > 0 && auth.canOperate.value" class="btn btn-sm btn-primary ms-auto" @click="retryAll"><i class="bi bi-arrow-repeat"></i> Coba ulang semua ({{ fmtNum(list.total) }})</button>
      <template v-if="tab === 'events'">
        <select v-model="f.status" class="form-select form-select-sm" style="width: 150px"><option value="">Semua status</option><option v-for="s in ['PENDING', 'PROCESSING', 'SUCCESS', 'RETRY', 'FAILED', 'CONFLICT', 'SKIPPED', 'DEAD_LETTER']" :key="s">{{ s }}</option></select>
        <select v-model="f.operation" class="form-select form-select-sm" style="width: 130px"><option value="">Semua operasi</option><option>INSERT</option><option>UPDATE</option><option>DELETE</option></select>
        <input v-model="f.from" type="datetime-local" class="form-control form-control-sm" style="width: 200px"><input v-model="f.to" type="datetime-local" class="form-control form-control-sm" style="width: 200px">
      </template>
    </div></div>
    <div class="card"><div class="table-responsive"><table class="table table-sm table-hover align-middle mb-0">
      <thead><tr><th>Waktu</th><th>Tabel</th><th>Arah</th><th>Operasi</th><th>PK</th><th>Status</th><th>Retry</th><th>Error</th><th></th></tr></thead>
      <tbody>
        <tr v-for="e in list.items" :key="e.id">
          <td class="small">{{ fmtDate(e.createdAt) }}</td><td>{{ e.table }}</td><td class="small">{{ e.direction }}</td><td>{{ e.operation }}</td><td class="mono small">{{ e.pkKey }}</td>
          <td><CommonStatusBadge :status="e.status ?? 'DEAD_LETTER'" /></td><td>{{ e.retryCount }}</td>
          <td class="small text-danger text-truncate" style="max-width: 260px" :title="e.errorMessage">{{ e.errorCode }} {{ e.errorMessage }}</td>
          <td class="text-nowrap text-end"><button class="btn btn-sm btn-link" @click="show(e)">detail</button>
            <button v-if="tab === 'dead' && auth.canOperate.value" class="btn btn-sm btn-outline-primary" @click="retry(e)">Retry</button></td>
        </tr>
        <tr v-if="!list.items.length"><td colspan="9" class="text-center text-muted py-4">Tidak ada data.</td></tr>
      </tbody></table></div><div class="px-3 pb-2"><CommonPager v-model:page="page" :page-size="size" :total="list.total" /></div></div>

    <div v-if="detail" class="modal-backdrop-custom" @click.self="detail = null"><div class="modal-panel wide">
      <div class="p-3 border-bottom fw-semibold">Event {{ detail.operation }} · {{ detail.status }}</div>
      <div class="p-3"><div class="small text-muted mb-1">Payload</div><pre class="json">{{ JSON.stringify(detail.payload, null, 2) }}</pre>
        <div class="small text-muted mb-1">Primary key</div><pre class="json">{{ JSON.stringify(detail.primaryKey, null, 2) }}</pre>
        <div v-if="detail.errorMessage" class="alert alert-danger small mb-0">{{ detail.errorCode }}: {{ detail.errorMessage }}</div></div>
      <div class="p-3 border-top text-end"><button class="btn btn-light" @click="detail = null">Tutup</button></div></div></div>
  </div>
</template>
